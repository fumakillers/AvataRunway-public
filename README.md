# AvataRunway

Windows 用の VRM ビューアーです。VRM 0.x と 1.0 のプレビュー、メタ情報表示、カメラ操作、表情・ポーズの調整、表情パックとポーズの入出力に対応しています。対応する表情パックを読み込んだモデルは別名で保存できます。

## 起動と保存先

Windows x64 と Microsoft Edge WebView2 Evergreen Runtime が必要です。起動を確認した環境は Windows 11 x64（ビルド 26200）です。他の OS ビルドでの動作は未検証です。Runtime がない場合は、Microsoft の配布ページから Evergreen Runtime をインストールしてアプリを起動し直してください。[WebView2 をダウンロード](https://developer.microsoft.com/microsoft-edge/webview2/#download-section)

1. Release の ZIP と `.sha256` を取得し、PowerShell の `Get-FileHash <ZIPのパス> -Algorithm SHA256` で照合します。初回版はアプリ本体のコード署名なしを予定しています。
2. ZIP 全体をユーザーが書き込めるフォルダーに展開し、`AvataRunway.exe` を起動します。移動するときはアプリを終了してフォルダー全体を移動します。
3. 設定は `app/config/settings.json`、ログは `app/logs/`、WebView2 のユーザーデータは `app/temp/webview2/` に保存します。ログと設定には VRM のパスが記録される場合があります。
4. 削除するときはアプリを終了して、展開したフォルダーを削除します。別の場所へ保存したファイルと、システムに導入した WebView2 Runtime は個別に管理してください。

書き込めない場所では起動時にエラーを表示します。移動後、前回の VRM を参照できない場合はモデルを開き直してください。表情パックを取り込めるかは対象モデルの構造に依存します。

## ビルド

Windows x64、PowerShell 7.4 以降、.NET SDK 11.0.100、Node.js 24.22.3、pnpm 11.25.0 を使います。

```powershell
./tools/Build-Release.ps1 -Version 0.1.0 -BuildOnly
```

Web 依存をロックファイルどおりにインストールし、Web テスト、バンドル再生成、.NET Release テスト、Windows x64 の自己完結 publish を行います。出力先は実行ごとに作成する `artifacts/release-*/publish/` です。再配布条件の確認後は `-BuildOnly` を外すと、第三者ライセンス文書付き ZIP と SHA-256 チェックサムを生成できます。

AvataRunway の独自コードは [MIT ライセンス](LICENSE) です。第三者ソフトウェアについては [THIRD-PARTY.md](THIRD-PARTY.md) を参照してください。
