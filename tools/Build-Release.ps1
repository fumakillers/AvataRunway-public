#Requires -Version 7.4
param(
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string] $Version = '0.1.0',
    [switch] $BuildOnly
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'

if (!$IsWindows -or [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture -ne 'X64') {
    throw 'Run this script on Windows x64.'
}

Push-Location (Join-Path $PSScriptRoot '..')
try {
    dotnet --version
    node --version
    pnpm --version
    Push-Location WebClient
    try {
        pnpm install --frozen-lockfile
        pnpm test
        pnpm run build
    }
    finally {
        Pop-Location
    }

    dotnet test AvataRunway.Core.Tests/AvataRunway.Core.Tests.csproj -c Release
    $run = New-Item -ItemType Directory "artifacts/release-$Version-$([guid]::NewGuid().ToString('N'))" -Force
    $publish = Join-Path $run.FullName 'publish'
    $restoreAssets = Join-Path $run.FullName 'project.assets.json'
    dotnet publish AvataRunway.csproj -c Release -r win-x64 -p:Platform=x64 --self-contained true -p:Version=$Version `
        -p:DebugType=None -p:DebugSymbols=false -p:CopyOutputSymbolsToPublishDirectory=false -p:AllowedReferenceRelatedFileExtensions=.xml -o $publish
    Copy-Item obj/project.assets.json $restoreAssets

    $expectedAppFiles = @('web/viewer-bundle.js', 'web/viewer.html')
    $app = Join-Path $publish 'app'
    $actualAppFiles = Get-ChildItem $app -File -Force -Recurse | ForEach-Object {
        [IO.Path]::GetRelativePath($app, $_.FullName).Replace('\', '/')
    }
    if ((Compare-Object $expectedAppFiles @($actualAppFiles)) -or
        (Get-ChildItem $publish -File -Force -Recurse | Where-Object Extension -In '.pdb','.vrm','.glb','.log','.pfx','.pem','.key') -or
        !(Test-Path (Join-Path $publish 'AvataRunway.exe')) -or
        !(Test-Path (Join-Path $publish 'coreclr.dll')) -or
        !(Test-Path (Join-Path $publish 'WebView2Loader.dll'))) {
        throw "Unexpected publish layout in $publish."
    }
    Write-Output "Publish: $publish"
    if ($BuildOnly) { return }

    $assets = Get-Content $restoreAssets -Raw | ConvertFrom-Json -AsHashtable
    $packages = @($assets.libraries.Values | Where-Object { $_.type -eq 'package' } | ForEach-Object { $_.path })
    $packages += @($assets.project.frameworks.Values.downloadDependencies | ForEach-Object {
        if ($_.version -notmatch '^\[([^,]+), \1\]$') {
            throw "Unexpected runtime pack version range for $($_.name)."
        }
        "$($_.name.ToLowerInvariant())/$($Matches[1])"
    })
    $notices = Join-Path $publish 'ThirdPartyLicenses'
    foreach ($package in ($packages | Sort-Object -Unique)) {
        $folder = $assets.packageFolders.Keys | ForEach-Object { Join-Path $_ $package } |
            Where-Object { Test-Path -LiteralPath $_ -PathType Container } | Select-Object -First 1
        if (!$folder) { throw "Package missing: $package" }
        $documents = @(Get-ChildItem -LiteralPath $folder -File | Where-Object Name -Match '^(license|notice|third.?party|copying|redist)')
        foreach ($document in $documents) {
            if ($document.Name -match '^licen[sc]e' -and
                (Select-String -LiteralPath $document.FullName -Pattern 'MICROSOFT WINDOWS APP SDK ENGINEERING PREVIEW' -Quiet)) {
                throw "Redistribution review required for $package (Engineering Preview terms). Build output: $publish"
            }
            $destination = New-Item -ItemType Directory (Join-Path "$notices/NuGet" $package) -Force
            Copy-Item -LiteralPath $document.FullName -Destination $destination.FullName
        }
    }
    $webPackages = Get-ChildItem WebClient/node_modules/.pnpm -Directory |
        ForEach-Object { Get-ChildItem (Join-Path $_.FullName 'node_modules') -Directory -ErrorAction SilentlyContinue } |
        ForEach-Object {
            if ($_.Name.StartsWith('@')) { Get-ChildItem $_.FullName -Directory } else { $_ }
        } | Where-Object { !$_.LinkType -and (Test-Path (Join-Path $_.FullName 'package.json')) }
    foreach ($package in $webPackages) {
        $metadata = Get-Content (Join-Path $package.FullName 'package.json') -Raw | ConvertFrom-Json
        $documents = @(Get-ChildItem -LiteralPath $package.FullName -File | Where-Object Name -Match '^(license|notice|copying)')
        if (!$documents -and $metadata.name -notlike '@esbuild/*') {
            throw "License text missing for $($metadata.name)@$($metadata.version)."
        }
        if ($documents) {
            $destination = New-Item -ItemType Directory "$notices/npm/$($metadata.name)/$($metadata.version)" -Force
            Copy-Item -LiteralPath $documents.FullName -Destination $destination.FullName
        }
    }
    if (!(Test-Path "$notices/npm/three") -or !(Test-Path "$notices/npm/@pixiv/three-vrm")) {
        throw 'Missing Web bundle license notices.'
    }
    Copy-Item README.md,THIRD-PARTY.md,THIRD-PARTY-WEB.txt -Destination $publish
    if (Test-Path LICENSE) { Copy-Item LICENSE -Destination $publish }
    $zipName = "AvataRunway-$Version-win-x64.zip"
    $zip = Join-Path $run.FullName $zipName
    [IO.Compression.ZipFile]::CreateFromDirectory($publish, $zip)
    $hash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
    Set-Content -LiteralPath "$zip.sha256" -Value "$hash  $zipName" -Encoding ascii
    Write-Output "$zip`n$hash"
}
finally {
    Pop-Location
}
