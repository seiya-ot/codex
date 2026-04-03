# WORK HISTORY

最終更新: 2026-04-02 (JST)

このドキュメントは、`C:\Users\seiya-ot\work\iga\codex` で実施した作業を時系列でまとめた記録です。

## 1. 初期要件の整理

- 対象マニュアル配下ページを網羅的に読み込み、API リクエスト検証のためのワークベンチを構築する方針を確定。
- ユーザー入力は日本語リクエストを前提とし、実行時に `Tenant URL` と `Access Token` を指定できる構成に決定。

## 2. マニュアル収集・API カタログ化

- 収集対象:
  - `https://manual.iij.jp/iga/igaapi_reference/`
  - `https://manual.iij.jp/iga/manual/`
  - `https://manual.iij.jp/iga/saas_federation_sample_manual/`
  - `https://developer.yesod.io/reference/api/index.html`
- 収集結果:
  - 総ページ数: `140`
  - 内訳: `31 + 54 + 54 + 1`
  - 抽出 API 数: `39` (ユニーク)
- 主要成果物:
  - [manual-catalog.json](C:/Users/seiya-ot/work/iga/codex/Data/manual-catalog.json)
  - [ManualCrawler.cs](C:/Users/seiya-ot/work/iga/codex/Services/ManualCrawler.cs)
  - [ManualCatalogStore.cs](C:/Users/seiya-ot/work/iga/codex/Services/ManualCatalogStore.cs)
- 補足:
  - YESOD 側は取得不安定なため、既知 API のフォールバックを導入。

## 3. API 検証ワークベンチ実装

- バックエンド:
  - .NET 8 Web アプリを新規実装。
  - 日本語リクエストから API 候補を返す `/api/resolve` を実装。
  - 任意 API 実行 `/api/execute` を実装。
  - カタログ要約 `/api/overview`、カバレッジ計画 `/api/coverage-plan` を実装。
- フロントエンド:
  - `Tenant URL / Access Token / Japanese request / Method / Path / Variables / Headers / Body` を入力可能な UI を実装。
- 主要ファイル:
  - [Program.cs](C:/Users/seiya-ot/work/iga/codex/Program.cs)
  - [RequestResolver.cs](C:/Users/seiya-ot/work/iga/codex/Services/RequestResolver.cs)
  - [RequestExecutor.cs](C:/Users/seiya-ot/work/iga/codex/Services/RequestExecutor.cs)
  - [CoveragePlanner.cs](C:/Users/seiya-ot/work/iga/codex/Services/CoveragePlanner.cs)
  - [index.html](C:/Users/seiya-ot/work/iga/codex/wwwroot/index.html)
  - [app.js](C:/Users/seiya-ot/work/iga/codex/wwwroot/app.js)

## 4. Git/GitHub 運用

- 作業ブランチ: `codex/api-verification-workbench`
- 初期コミット: `90a5e8b Add API verification workbench`
- GitHub リポジトリ: `https://github.com/seiya-ot/codex`
- `main` への統合を実施 (複数回の merge を含む)。

## 5. `/api/execute` の 500 エラー修正

- 問題:
  - 接続失敗時に `HttpRequestException` が未処理で 500 を返していた。
- 修正:
  - `network_error / timeout / validation_error / invalid_binary_body` などの分類レスポンスへ変更。
  - エラーメッセージと実行ノートを構造化して返すように改修。
- 主要ファイル:
  - [RequestExecutor.cs](C:/Users/seiya-ot/work/iga/codex/Services/RequestExecutor.cs)
  - [ApiRequestModels.cs](C:/Users/seiya-ot/work/iga/codex/Models/ApiRequestModels.cs)
  - [app.js](C:/Users/seiya-ot/work/iga/codex/wwwroot/app.js)

## 6. リクエストボディ判定・自動生成

- 追加:
  - API とメソッドに応じて body の要否判定とテンプレート生成を行う `RequestBodyPlanner` を実装。
  - `/api/body-plan` を追加。
- 対応例:
  - `GET`: body なし
  - `POST /api/.../query`: JSON テンプレート
  - `POST /api/.../import`: import 用 JSON テンプレート
  - `PUT /api/.../avatar`: multipart テンプレート
- 主要ファイル:
  - [RequestBodyPlanner.cs](C:/Users/seiya-ot/work/iga/codex/Services/RequestBodyPlanner.cs)
  - [Program.cs](C:/Users/seiya-ot/work/iga/codex/Program.cs)
  - [ApiRequestModels.cs](C:/Users/seiya-ot/work/iga/codex/Models/ApiRequestModels.cs)
  - [RequestExecutor.cs](C:/Users/seiya-ot/work/iga/codex/Services/RequestExecutor.cs)

## 7. デバッグ表示強化・候補再検索

- 追加:
  - 実行履歴に API リクエスト全文を表示:
    - `METHOD PATH HTTP/1.1`
    - `Host / Authorization / Content-Type / custom headers`
    - body (multipart プレビューを含む)
  - 日本語リクエスト入力時に候補 API を自動再検索 (debounce + IME 対応)。
- 主要ファイル:
  - [RequestExecutor.cs](C:/Users/seiya-ot/work/iga/codex/Services/RequestExecutor.cs)
  - [ApiRequestModels.cs](C:/Users/seiya-ot/work/iga/codex/Models/ApiRequestModels.cs)
  - [app.js](C:/Users/seiya-ot/work/iga/codex/wwwroot/app.js)

## 8. memberAssets の成功時サンプル表示

- 背景:
  - 実環境接続に失敗した場合でも、UI で成功レスポンスの構造確認を可能にするため。
- 追加:
  - `successExample` を `/api/execute` レスポンスに追加。
  - `GET /api/v24.10/memberAssets` のマニュアル準拠サンプル JSON を生成。
  - 実リクエスト失敗時に UI へ success example を併記表示。
- 主要ファイル:
  - [SuccessExamplePlanner.cs](C:/Users/seiya-ot/work/iga/codex/Services/SuccessExamplePlanner.cs)
  - [RequestExecutor.cs](C:/Users/seiya-ot/work/iga/codex/Services/RequestExecutor.cs)
  - [ApiRequestModels.cs](C:/Users/seiya-ot/work/iga/codex/Models/ApiRequestModels.cs)
  - [Program.cs](C:/Users/seiya-ot/work/iga/codex/Program.cs)
  - [app.js](C:/Users/seiya-ot/work/iga/codex/wwwroot/app.js)

## 9. 実環境タイムアウト調査

- 調査結果:
  - DNS 解決は成功 (`i-test03.int.igms.iij.jp -> 34.144.240.242`)。
  - ただし TCP 443 接続が確立せず timeout。
  - `example.com:443` も同様に timeout したため、対象 API 固有ではない。
  - 実行ユーザーは `TP-IIJ1261\CodexSandboxOffline`、ログオンユーザーは `seiya-ot` でセッション差分あり。
- 結論:
  - タイムアウト原因は API 仕様やトークンではなく、実行セッション側のネットワーク到達性。

## 10. `seiya-ot` セッション前提の起動方式へ変更

- 変更内容:
  - `seiya-ot` 対話セッションでの起動専用スクリプトを追加。
  - `.user-session-build` 出力を使う方式へ切替 (`bin/Debug` ロック競合を回避)。
  - `CodexSandboxOffline` 実行時は明示エラーにするガードを追加。
- 追加/更新ファイル:
  - [Start-Workbench.ps1](C:/Users/seiya-ot/work/iga/codex/scripts/Start-Workbench.ps1)
  - [Start-Workbench.cmd](C:/Users/seiya-ot/work/iga/codex/scripts/Start-Workbench.cmd)
  - [Refresh-Manuals.cmd](C:/Users/seiya-ot/work/iga/codex/scripts/Refresh-Manuals.cmd)
  - [README.md](C:/Users/seiya-ot/work/iga/codex/README.md)
  - [.gitignore](C:/Users/seiya-ot/work/iga/codex/.gitignore)

## 11. ExecutionPolicy エラー記録 (2026-04-02)

- 事象:
  - `.\scripts\Start-Workbench.ps1` 実行時に `PSSecurityException` (未署名スクリプト)。
- 対処:
  - 直接 `.ps1` を実行せず、以下の起動経路を使用:
  - `.\scripts\Start-Workbench.cmd`
  - `powershell.exe -ExecutionPolicy Bypass -File .\scripts\Start-Workbench.ps1`

## 12. 補足（未コミット差分）

- ローカルで `Data/manual-catalog.json` の未コミット差分が発生するケースあり。
- 大きな自動生成差分のため、必要な変更のみを選択してコミットする運用を継続。

## 13. 2026-04-03 起動スクリプト構文エラー修正

- 事象:
  - `.\scripts\Start-Workbench.ps1` 実行時に PowerShell の parse error が発生。
  - エラー位置は `".dotnet-home"` 付近だが、実際にはファイル内の非 ASCII 文字列が Windows PowerShell 側で崩れて解釈されていた。
- 対応:
  - [Start-Workbench.ps1](C:/Users/seiya-ot/work/iga/codex/scripts/Start-Workbench.ps1) を ASCII のみで再作成。
  - エラーメッセージ文字列を英語に変更し、実行時のエンコーディング依存を除去。
- 検証:
  - `powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\Start-Workbench.ps1 -NoBuild`
  - 上記で parse error は解消し、想定どおり `CodexSandboxOffline` ガードメッセージが返ることを確認。

## 14. 2026-04-03 外部接続タイムアウト対策 (proxy 指定対応)

- 背景:
  - `i-test03.int.igms.iij.jp` への接続がタイムアウトし、環境依存の proxy 要件をリクエスト単位で制御できなかった。
- 変更内容:
  - `/api/execute` 入力へ以下を追加:
    - `proxyUrl`
    - `bypassSystemProxy`
    - `useDefaultProxyCredentials`
    - `timeoutSeconds`
  - 実行結果へ以下を追加:
    - `proxyMode`
    - `proxyUrl` (実際に使った値)
  - `RequestExecutor` を再構成し、`SocketsHttpHandler` で以下を切替可能にした:
    - system proxy 利用
    - explicit proxy 利用
    - proxy 無効化
  - UI に `Proxy URL / Timeout (sec) / Proxy mode` 入力を追加し、localStorage に保存。
- 対応ファイル:
  - [ApiRequestModels.cs](C:/Users/seiya-ot/work/iga/codex/Models/ApiRequestModels.cs)
  - [RequestExecutor.cs](C:/Users/seiya-ot/work/iga/codex/Services/RequestExecutor.cs)
  - [index.html](C:/Users/seiya-ot/work/iga/codex/wwwroot/index.html)
  - [app.js](C:/Users/seiya-ot/work/iga/codex/wwwroot/app.js)
- 検証:
  - `dotnet build --configfile NuGet.Config -o .verify-build /p:UseAppHost=false` 成功
  - `/api/execute` で `proxyMode = explicit` と `proxyUrl` がレスポンスへ反映されることを確認
