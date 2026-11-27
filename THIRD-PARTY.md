# 第三者ソフトウェア

Windows ZIP の `ThirdPartyLicenses/` には、ビルドで解決したパッケージに含まれるライセンスと著作権表示を、パッケージ名・バージョン別に収録します。

リポジトリ内の Web バンドルの許諾文と著作権表示は [THIRD-PARTY-WEB.txt](THIRD-PARTY-WEB.txt) にあります。

| ソフトウェア | 区分 |
| --- | --- |
| three 0.185.1、@pixiv/three-vrm と関連モジュール 3.5.5 | Web バンドル。MIT。各パッケージの著作権表示と許諾文を同梱 |
| esbuild 0.28.1 | MIT。ビルド時に使用 |
| .NET ランタイムと System.Numerics.Tensors | Microsoft のライセンスと第三者通知を同梱 |
| Windows App SDK のランタイム DLL | パッケージ固有の Microsoft ライセンスと第三者通知が適用 |
| WebView2 SDK | SDK に付属するライセンスと通知を同梱。Evergreen Runtime は Microsoft が別途配布 |
| Microsoft.Windows.SDK.NET.Ref | WinRT プロジェクション DLL に [Windows SDK ライセンス](https://learn.microsoft.com/en-us/legal/windows-sdk/license) と [再配布リスト](https://learn.microsoft.com/en-us/legal/windows-sdk/redist) が適用 |
| Windows SDK BuildTools、.NET テスト用パッケージ | ビルド・テスト時に使用 |

Windows App SDK 2.7.0 のライセンス第 3 条には、アプリのビルド出力に配置されるコードの再配布許可と条件が記載されています。Windows SDK ライセンス第 2 条には、再配布可能なファイルと配布条件が記載されています。配布元は、各条項に沿った利用者向け条件を適用する必要があります。
