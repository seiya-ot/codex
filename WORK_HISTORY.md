# Work History

このファイルは、2026-03-26 時点までに本リポジトリで実施した作業履歴を、判断理由と補足コメント付きでまとめたものです。

## 1. 初期要件整理

- ユーザ要求:
  - IIJ/YESOD の複数マニュアル配下ページを読み込み、掲載 API を網羅的に検証できるシステムを作る。
  - 日本語で実行したい内容を入力すると、候補 API を解決して実行結果を返せるようにする。
  - テナント URL とアクセストークンを入力できるようにする。
- コメント:
  - 単なる API クライアントではなく、マニュアル由来の API カタログと日本語解決機構を持つ検証ワークベンチとして設計した。

## 2. マニュアル収集とカタログ化

- 対象マニュアル:
  - `https://manual.iij.jp/iga/igaapi_reference/`
  - `https://manual.iij.jp/iga/manual/`
  - `https://manual.iij.jp/iga/saas_federation_sample_manual/`
  - `https://developer.yesod.io/reference/api/index.html`
- 実施内容:
  - 巡回取得ロジックを実装し、配下ページの本文・リンク・API 記述を収集した。
  - 収集結果を [manual-catalog.json](C:/Users/seiya-ot/work/iga/codex/Data/manual-catalog.json) に保存する仕組みを作成した。
  - 収集件数は `140` ページ、抽出したユニーク API は `39` 件。
  - 内訳は `31 + 54 + 54 + 1` ページ。
- 関連ファイル:
  - [ManualCrawler.cs](C:/Users/seiya-ot/work/iga/codex/Services/ManualCrawler.cs)
  - [ManualCatalogStore.cs](C:/Users/seiya-ot/work/iga/codex/Services/ManualCatalogStore.cs)
  - [manual-catalog.json](C:/Users/seiya-ot/work/iga/codex/Data/manual-catalog.json)
- コメント:
  - `developer.yesod.io` は取得が不安定だったため、YESOD 側はフォールバック用の既知 API 一覧も実装した。
  - 以後の API 解決と検証プラン生成は、このカタログを基礎データとして利用している。

## 3. 初期ワークベンチ実装

- 実施内容:
  - .NET 8 の Web アプリとして検証ワークベンチを新規実装した。
  - 日本語入力から API 候補を解決する機能を実装した。
  - テナント URL、アクセストークン、Method、Path、Variables、Headers を指定して API を実行する機能を実装した。
  - API ごとの実行準備状態を `ready / needs_input / manual_fixture` で返す検証プラン機能を実装した。
  - Web UI から一連の操作ができるようにした。
- 関連ファイル:
  - [Program.cs](C:/Users/seiya-ot/work/iga/codex/Program.cs)
  - [RequestResolver.cs](C:/Users/seiya-ot/work/iga/codex/Services/RequestResolver.cs)
  - [RequestExecutor.cs](C:/Users/seiya-ot/work/iga/codex/Services/RequestExecutor.cs)
  - [CoveragePlanner.cs](C:/Users/seiya-ot/work/iga/codex/Services/CoveragePlanner.cs)
  - [index.html](C:/Users/seiya-ot/work/iga/codex/wwwroot/index.html)
  - [app.js](C:/Users/seiya-ot/work/iga/codex/wwwroot/app.js)
  - [README.md](C:/Users/seiya-ot/work/iga/codex/README.md)
- コメント:
  - 候補解決に失敗した場合でも、Method / Path 直接入力で実行できるようにして、検証用途を阻害しない設計にした。
  - UI は検証作業を優先し、候補選択、実行、カバレッジ確認が同一画面で完結する構成にした。

## 4. 初回ビルドとローカル確認

- 実施内容:
  - `dotnet build --configfile NuGet.Config` を通した。
  - ローカル起動後に `/api/overview` の疎通を確認した。
  - 日本語入力 `従業員履歴を取得したい` から履歴 API が上位候補に出ることを確認した。
  - `/api/execute` で自己呼び出しの実行確認をした。
- コメント:
  - PowerShell の表示で日本語が文字化けする箇所はあったが、ビルドと API 実行結果が正常だったため、ファイル自体は UTF-8 と判断した。

## 5. Git 初期化と GitHub 連携

- 実施内容:
  - 作業ブランチ `codex/api-verification-workbench` を作成した。
  - 初回コミット `90a5e8b Add API verification workbench` を作成した。
  - GitHub リポジトリ `https://github.com/seiya-ot/codex` に push した。
- コメント:
  - 当初は remote URL が不正で push に失敗したが、正しいリポジトリ URL 確認後に push した。

## 6. main への統合

- 実施内容:
  - `codex/api-verification-workbench` を `main` にマージした。
  - 履歴が別ルートだったため、README の競合だけ解消して統合した。
  - その後 `origin/main` へ push した。
- 関連コミット:
  - `e8f488c`
  - `00bcaf9`
- コメント:
  - 初期状態のリポジトリ履歴と作業履歴が分岐していたため、単純 fast-forward ではなく merge で統合した。

## 7. `/api/execute` 500 エラーの修正

- きっかけ:
  - 内部ホスト接続時に `HttpRequestException` が未処理のまま伝播し、`/api/execute` が HTTP 500 を返していた。
- 実施内容:
  - ネットワークエラー、タイムアウト、Base64 不正、入力不正を補足して構造化レスポンスを返すようにした。
  - 接続先が `.int.` ドメインの場合は VPN / 社内ネットワーク注意メモを返すようにした。
  - UI 側でも生スタックトレースではなく、日本語エラー要約を表示するようにした。
- 関連ファイル:
  - [RequestExecutor.cs](C:/Users/seiya-ot/work/iga/codex/Services/RequestExecutor.cs)
  - [ApiRequestModels.cs](C:/Users/seiya-ot/work/iga/codex/Models/ApiRequestModels.cs)
  - [app.js](C:/Users/seiya-ot/work/iga/codex/wwwroot/app.js)
- 関連コミット:
  - `f122aec Handle execute network failures gracefully`
- コメント:
  - これは挙動改善ではなく、障害時の API 契約を安定させる修正。検証ツールでは「失敗内容を返せる」ことが重要と判断した。

## 8. リクエストボディの要否判定と自動生成

- きっかけ:
  - ユーザ要求として、「リクエストボディは必要に応じて不要か必要かを判断し、必要な場合は生成すること」が追加された。
- 実施内容:
  - request body 判定とテンプレート生成を行う [RequestBodyPlanner.cs](C:/Users/seiya-ot/work/iga/codex/Services/RequestBodyPlanner.cs) を追加した。
  - `/api/body-plan` API を追加し、UI から body の要否と自動生成結果を確認できるようにした。
  - 実行時もサーバ側で body を自動生成できるようにして、画面に実際の送信 body を返すようにした。
  - `GET` は原則 body なし、`query`、`import`、`authorityTasks PATCH`、`sync-assets`、`memberAssets export`、`avatar multipart` はテンプレートを自動生成するようにした。
  - multipart の avatar 更新では `fileBase64` 未指定時に明確な検証エラーを返すようにした。
- 関連ファイル:
  - [RequestBodyPlanner.cs](C:/Users/seiya-ot/work/iga/codex/Services/RequestBodyPlanner.cs)
  - [RequestExecutor.cs](C:/Users/seiya-ot/work/iga/codex/Services/RequestExecutor.cs)
  - [Program.cs](C:/Users/seiya-ot/work/iga/codex/Program.cs)
  - [ApiRequestModels.cs](C:/Users/seiya-ot/work/iga/codex/Models/ApiRequestModels.cs)
  - [app.js](C:/Users/seiya-ot/work/iga/codex/wwwroot/app.js)
  - [index.html](C:/Users/seiya-ot/work/iga/codex/wwwroot/index.html)
- 関連コミット:
  - `083a66b Generate request bodies when required`
  - `0ba2de7 Merge branch 'codex/api-verification-workbench'`
- コメント:
  - カタログ JSON を全面更新せず、実行時プランナー方式にしたのは、既存データを壊さずに追加でき、直接 Method / Path 入力にも適用できるため。
  - YESOD 側は詳細仕様の取得が限定的だったため、テンプレートは「安全な最小構造」を返す方針にした。

## 9. 実施した検証

- ビルド:
  - `dotnet build --configfile NuGet.Config`
- ローカル API 確認:
  - `/api/overview`
  - `/api/body-plan`
  - `/api/execute`
- 確認済みシナリオ:
  - `GET /api/v22.10/memberAuthorities` は body なしと判定される。
  - `POST /api/v22.10/query` は JSON body が自動生成される。
  - `POST /api/v21.07/members/import` は import 用 JSON テンプレートが生成される。
  - `PUT /api/v22.10/members/{memberId}/avatar` は multipart テンプレートが生成され、`fileBase64` 未指定時に validation error が返る。
  - 到達不能ホストへの `/api/execute` は 500 ではなく `network_error` として返る。
- コメント:
  - バックグラウンド起動が環境依存で不安定だったため、一部確認は PowerShell job で起動してその場で API を叩く形で実施した。

## 10. Git 履歴の要約

- 主要コミット:
  - `90a5e8b Add API verification workbench`
  - `f122aec Handle execute network failures gracefully`
  - `083a66b Generate request bodies when required`
- main 側の主要マージ:
  - `e8f488c Merge branch 'codex/api-verification-workbench'`
  - `00bcaf9 Merge branch 'codex/api-verification-workbench'`
  - `0ba2de7 Merge branch 'codex/api-verification-workbench'`
- コメント:
  - 機能単位でコミットを分け、`main` にはマージコミットで統合した。
  - この履歴ファイル自体は、上記作業を後追いで記録するためのドキュメント追加である。

## 11. 現時点の補足事項

- 未コミット差分:
  - [manual-catalog.json](C:/Users/seiya-ot/work/iga/codex/Data/manual-catalog.json)
- コメント:
  - これは生成日時差分を含むローカル変更で、今回の履歴ドキュメント追加や PR 作成の対象には含めていない。
  - もしカタログ再生成結果も確定版として残すなら、別途レビューしてコミットするのが安全。
