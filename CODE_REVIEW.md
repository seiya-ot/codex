# コードレビュー (2026-05-22)

## 全体的な印象

.NET 8 Minimal API で構築されたAPI検証ワークベンチ。構造は明瞭で、責務分離も適切。

## 良い点 ✅

- **責務分離**: Services 層が `Resolver` / `Executor` / `Planner` に分かれており見通しが良い
- **Nullable 有効化** + Immutable モデル (`sealed class`)
- **プロキシ / タイムアウト** の柔軟な設定
- **日本語ローカライズ** されたエラーメッセージ・ノート

## 改善提案 ⚠️

| # | 重要度 | 箇所 | 内容 |
|---|--------|------|------|
| 1 | 高 | `RequestExecutor.CreateHttpClient` | `HttpClient` を毎回 `new` しているが `IHttpClientFactory` が DI 登録済み。ソケット枯渇のリスクあり |
| 2 | 高 | `RequestResolver` | `Regex.Match` を毎呼び出しで新規コンパイル。`[GeneratedRegex]` or `static Regex` にすべき |
| 3 | 中 | `Program.cs` | エンドポイント定義が肥大化。`/api/body-plan` 等のロジックを Endpoint クラスか拡張メソッドに分離推奨 |
| 4 | 中 | `ManualCatalogStore` | `SemaphoreSlim` が `Dispose` されていない (Singleton なので実害は小さいが) |
| 5 | 低 | `SuccessExamplePlanner` | ハードコードされたモック例 (`/api/v24.10/memberAssets`) — スケールしない設計。将来データ駆動に |
| 6 | 低 | `.gitignore` | `appsettings.*.json` (機密含む可能性) や `Data/manual-catalog.json` (大容量生成物) が除外されていない |
| 7 | 低 | テスト | ユニットテストが存在しない |

## セキュリティ

- `AccessToken` がログや `RequestDebugText` に含まれうる → 本番利用時はマスキング推奨
- `input.ProxyUrl` のバリデーションは最低限あるが、SSRF 対策は追加検討の余地あり

## 次のアクション候補

1. `IHttpClientFactory` を活用するよう `RequestExecutor` をリファクタリング
2. `RequestResolver` 内の正規表現を `[GeneratedRegex]` に置き換え
3. エンドポイント定義を `Endpoints/` ディレクトリに分離
4. ユニットテストプロジェクトの追加
