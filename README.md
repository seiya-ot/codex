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

PowerShell:

```powershell
$env:DOTNET_CLI_HOME="$PWD/.dotnet-home"
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT='1'
$env:NUGET_PACKAGES="$PWD/.nuget/packages"
$env:APPDATA="$PWD/.appdata"
dotnet run
```

ブラウザで `http://localhost:5000` または表示された URL を開いてください。

## マニュアル再読込

```powershell
$env:DOTNET_CLI_HOME="$PWD/.dotnet-home"
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT='1'
$env:NUGET_PACKAGES="$PWD/.nuget/packages"
$env:APPDATA="$PWD/.appdata"
dotnet run -- --refresh-manuals
```

## 補足

- 日本語解決は候補提示を主目的にしており、精度が足りない場合は Method / Path を直接編集してください。
- `POST` / `PUT` / `PATCH` 系や import/export 系 API は、テナント固有の実データや手動フィクスチャが必要になる前提です。
- 任意実行モードではカタログに無いパスも直接指定できます。
