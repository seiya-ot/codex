# API Verification Workbench

IIJ IDガバナンス管理サービスと YESOD API マニュアルを巡回し、API カタログ化したうえで、
テナント URL とアクセストークンを指定して日本語入力から API 候補を引き当てて実行できる検証用ワークベンチです。

## 構成

- `Data/manual-catalog.json`
  - 4 系統のマニュアルを巡回して保存したカタログです。
  - 現在の収集件数は `140` ページ、抽出 API は `39` 件です。
- `Services/ManualCrawler.cs`
  - 配下ページを再帰巡回し、ページ本文と API 経路を抽出します。
  - `developer.yesod.io` への直接接続に失敗した場合は YESOD API の既知経路へフォールバックします。
- `Services/RequestResolver.cs`
  - 日本語入力、HTTP メソッド、パスから候補 API をスコアリングして返します。
- `Services/RequestExecutor.cs`
  - テナント URL、アクセストークン、任意パス、変数展開を使って実リクエストを送信します。
- `Services/CoveragePlanner.cs`
  - カタログ API に対して、`ready` / `needs_input` / `manual_fixture` の観点で網羅検証の準備状況を出します。
- `wwwroot/`
  - 単一ページの UI です。接続設定、日本語リクエスト、候補選択、直接編集、実行結果、検証プランをまとめています。

## 起動

社内テナントへ接続する場合は、CodexSandboxOffline ではなく `seiya-ot` の対話セッションで起動してください。  
このリポジトリでは `dotnet run` の代わりに、ユーザーセッション用ビルド出力 `.user-session-build` を使う起動スクリプトを用意しています。

Explorer から実行する場合:

```text
scripts\Start-Workbench.cmd
```

PowerShell から実行する場合:

```powershell
.\scripts\Start-Workbench.ps1
```

既定では `http://127.0.0.1:5205` で起動します。別ポートにしたい場合は次のように実行します。

```powershell
.\scripts\Start-Workbench.ps1 -Url http://127.0.0.1:5300
```

この方式にしている理由:

- `seiya-ot` セッションの proxy / VPN / 社内ネットワーク設定をそのまま引き継げる
- `CodexSandboxOffline` で起動したバックエンドから社内テナントへ接続できない問題を避けられる
- `.user-session-build` に出力するため、`bin/Debug` のロック競合を避けられる

## マニュアル再読込

Explorer:

```text
scripts\Refresh-Manuals.cmd
```

PowerShell:

```powershell
.\scripts\Start-Workbench.ps1 -RefreshManuals
```

## 補足

- 日本語解決は候補提示を主目的にしており、精度が足りない場合は Method / Path を直接編集してください。
- `POST` / `PUT` / `PATCH` 系や import/export 系 API は、テナント固有の実データや手動フィクスチャが必要になる前提です。
- 任意実行モードではカタログに無いパスも直接指定できます。
