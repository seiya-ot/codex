using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Codex.ApiVerificationWorkbench.Models;

namespace Codex.ApiVerificationWorkbench.Services;

public sealed partial class ManualCrawler
{
    private readonly HttpClient _httpClient;

    public ManualCrawler(HttpClient httpClient)
    {
        _httpClient = httpClient;
        _httpClient.Timeout = TimeSpan.FromSeconds(60);
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("CodexApiVerificationWorkbench/1.0");
    }

    public async Task<CatalogData> BuildCatalogAsync(CancellationToken cancellationToken)
    {
        var roots = GetManualRoots();
        var fetchedPages = new List<FetchedPage>();

        foreach (var root in roots)
        {
            try
            {
                if (root.SinglePage)
                {
                    fetchedPages.Add(await FetchPageAsync(root, root.RootUrl, cancellationToken));
                }
                else
                {
                    fetchedPages.AddRange(await CrawlRootAsync(root, cancellationToken));
                }
            }
            catch when (string.Equals(root.Id, "yesod-api-reference", StringComparison.OrdinalIgnoreCase))
            {
                fetchedPages.Add(BuildFallbackYesodPage(root));
            }
        }

        var manualPages = fetchedPages.Select(page => page.Page).ToList();
        var apiOperations = new Dictionary<string, ApiOperation>(StringComparer.OrdinalIgnoreCase);

        foreach (var page in fetchedPages.Where(page => page.Root.Id == "iij-api-reference"))
        {
            foreach (var operation in ExtractIijOperations(page))
            {
                apiOperations[operation.Id] = operation;
            }
        }

        foreach (var page in fetchedPages.Where(page => page.Root.Id == "yesod-api-reference"))
        {
            foreach (var operation in ExtractYesodOperations(page))
            {
                apiOperations.TryAdd(operation.Id, operation);
            }
        }

        EnrichAliases(apiOperations.Values);

        return new CatalogData
        {
            Metadata = new CatalogMetadata
            {
                GeneratedAtUtc = DateTimeOffset.UtcNow,
                PageCounts = manualPages.GroupBy(page => page.ManualId)
                    .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase),
                Manuals = roots.Select(root => new ManualDescriptor
                {
                    Id = root.Id,
                    Name = root.Name,
                    RootUrl = root.RootUrl
                }).ToList()
            },
            ManualPages = manualPages.OrderBy(page => page.ManualName).ThenBy(page => page.Url).ToList(),
            ApiOperations = apiOperations.Values.OrderBy(operation => operation.ManualName).ThenBy(operation => operation.Path).ThenBy(operation => operation.Method).ToList()
        };
    }

    public static string NormalizeForSearch(string value)
    {
        return Regex.Replace(value.ToLowerInvariant(), "[\\s　_\\-/:.()\\[\\]{}]+", string.Empty);
    }

    private async Task<List<FetchedPage>> CrawlRootAsync(ManualRoot root, CancellationToken cancellationToken)
    {
        var pages = new List<FetchedPage>();
        var queue = new Queue<string>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        queue.Enqueue(root.RootUrl);

        while (queue.Count > 0)
        {
            var url = queue.Dequeue();
            if (!visited.Add(url))
            {
                continue;
            }

            var page = await FetchPageAsync(root, url, cancellationToken);
            pages.Add(page);

            foreach (var link in page.Page.LinkedUrls)
            {
                if (!visited.Contains(link))
                {
                    queue.Enqueue(link);
                }
            }
        }

        return pages;
    }

    private async Task<FetchedPage> FetchPageAsync(ManualRoot root, string url, CancellationToken cancellationToken)
    {
        var html = await FetchHtmlAsync(url, cancellationToken);
        var plainText = ExtractPlainText(html);
        var page = new ManualPage
        {
            Id = $"{root.Id}:{url}",
            ManualId = root.Id,
            ManualName = root.Name,
            Url = url,
            Title = ExtractTitle(html),
            PlainText = plainText,
            Excerpt = plainText.Length > 420 ? plainText[..420] + "..." : plainText,
            LinkedUrls = ExtractLinks(url, html, root).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            Tags = root.Tags.ToList()
        };

        return new FetchedPage(root, page, html);
    }

    private async Task<string> FetchHtmlAsync(string url, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();

        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        var encoding = TryGetEncoding(response.Content.Headers.ContentType?.CharSet)
            ?? DetectEncodingFromMeta(bytes)
            ?? Encoding.UTF8;
        return encoding.GetString(bytes);
    }

    private static Encoding? TryGetEncoding(string? charset)
    {
        if (string.IsNullOrWhiteSpace(charset))
        {
            return null;
        }

        try
        {
            return Encoding.GetEncoding(charset.Trim('"'));
        }
        catch
        {
            return null;
        }
    }

    private static Encoding? DetectEncodingFromMeta(byte[] bytes)
    {
        var utf8 = Encoding.UTF8.GetString(bytes);
        var match = Regex.Match(utf8, "<meta[^>]+charset=\"?(?<charset>[A-Za-z0-9_\\-]+)\"?", RegexOptions.IgnoreCase);
        return match.Success ? TryGetEncoding(match.Groups["charset"].Value) : null;
    }

    private static IEnumerable<ApiOperation> ExtractIijOperations(FetchedPage page)
    {
        var operations = new Dictionary<string, ApiOperation>(StringComparer.OrdinalIgnoreCase);
        var foundStructured = false;

        foreach (Match rowMatch in TableRowRegex().Matches(page.Html))
        {
            var rowText = CleanupText(WebUtility.HtmlDecode(TagRegex().Replace(rowMatch.Value, " ")));
            var methodMatch = HttpMethodRegex().Match(rowText);
            var pathMatches = ApiPathRegex().Matches(rowText);

            if (!methodMatch.Success || pathMatches.Count == 0)
            {
                continue;
            }

            foundStructured = true;
            foreach (Match pathMatch in pathMatches)
            {
                AddOperation(operations, page, methodMatch.Value.ToUpperInvariant(), pathMatch.Value, page.Page.Title);
            }
        }

        if (!foundStructured)
        {
            foreach (Match match in InlineMethodAndPathRegex().Matches(ExtractPlainText(page.Html)))
            {
                AddOperation(
                    operations,
                    page,
                    match.Groups["method"].Value.ToUpperInvariant(),
                    match.Groups["path"].Value,
                    page.Page.Title);
            }
        }

        return operations.Values;
    }

    private static IEnumerable<ApiOperation> ExtractYesodOperations(FetchedPage page)
    {
        var operations = new Dictionary<string, ApiOperation>(StringComparer.OrdinalIgnoreCase);

        foreach (Match match in InlineMethodAndPathRegex().Matches(page.Html))
        {
            var path = NormalizePath(match.Groups["path"].Value);
            if (string.IsNullOrWhiteSpace(path) || path.Contains("&#x27;", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            AddOperation(
                operations,
                page,
                match.Groups["method"].Value.ToUpperInvariant(),
                path,
                GetYesodSummary(path));
        }

        return operations.Values;
    }

    private static void AddOperation(
        IDictionary<string, ApiOperation> operations,
        FetchedPage page,
        string method,
        string rawPath,
        string summary)
    {
        var path = NormalizePath(rawPath);
        if (string.Equals(path, "/api/v22.10/members/", StringComparison.OrdinalIgnoreCase))
        {
            if (summary.Contains("履歴", StringComparison.OrdinalIgnoreCase))
            {
                path = "/api/v22.10/members/{memberId}/histories";
            }
            else if (summary.Contains("アバター", StringComparison.OrdinalIgnoreCase))
            {
                path = "/api/v22.10/members/{memberId}/avatar";
            }
        }

        if (summary.Contains("アバター", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase) &&
            path.EndsWith("/avatar", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        var id = $"{method}:{path}";
        operations[id] = new ApiOperation
        {
            Id = id,
            ManualId = page.Root.Id,
            ManualName = page.Root.Name,
            SourcePageUrl = page.Page.Url,
            SourcePageTitle = page.Page.Title,
            Summary = summary,
            Method = method,
            Path = path,
            Category = InferCategory(path, summary),
            Version = ExtractVersion(path),
            Aliases = BuildInitialAliases(summary, path),
            PathParameters = ExtractPathParameters(path),
            SampleContentType = InferSampleContentType(path),
            Notes = BuildOperationNotes(page.Root.Name, path),
            RequiresManualFixture = RequiresManualFixture(path, method)
        };
    }

    private static IEnumerable<string> ExtractLinks(string currentUrl, string html, ManualRoot root)
    {
        var current = new Uri(currentUrl);

        foreach (Match match in HrefRegex().Matches(html))
        {
            var rawHref = WebUtility.HtmlDecode(match.Groups["href"].Value).Trim();
            if (string.IsNullOrWhiteSpace(rawHref) || rawHref.StartsWith('#'))
            {
                continue;
            }

            Uri absolute;
            try
            {
                absolute = new Uri(current, rawHref);
            }
            catch
            {
                continue;
            }

            var url = absolute.AbsoluteUri;
            if (root.SinglePage)
            {
                if (string.Equals(url, root.RootUrl, StringComparison.OrdinalIgnoreCase))
                {
                    yield return url;
                }

                continue;
            }

            if (!url.StartsWith(root.RootUrl, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!url.EndsWith(".html", StringComparison.OrdinalIgnoreCase) &&
                !url.EndsWith("/", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            yield return url;
        }
    }

    private static string ExtractTitle(string html)
    {
        var match = Regex.Match(html, "<title>(?<title>.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        return match.Success ? CleanupText(match.Groups["title"].Value) : "Untitled";
    }

    private static string ExtractPlainText(string html)
    {
        var text = ScriptRegex().Replace(html, " ");
        text = StyleRegex().Replace(text, " ");
        text = TagRegex().Replace(text, " ");
        text = CleanupText(WebUtility.HtmlDecode(text));
        return text.Length > 12000 ? text[..12000] : text;
    }

    private static string CleanupText(string value) => Regex.Replace(value, "\\s+", " ").Trim();

    private static void EnrichAliases(IEnumerable<ApiOperation> operations)
    {
        foreach (var operation in operations)
        {
            var aliases = new HashSet<string>(operation.Aliases, StringComparer.OrdinalIgnoreCase)
            {
                operation.Summary,
                operation.SourcePageTitle,
                operation.Path,
                operation.Method,
                operation.Category
            };

            if (operation.Summary.Contains("メンバー", StringComparison.OrdinalIgnoreCase))
            {
                aliases.Add("従業員");
                aliases.Add("社員");
            }

            if (operation.Summary.Contains("権限", StringComparison.OrdinalIgnoreCase))
            {
                aliases.Add("ロール");
                aliases.Add("役割");
            }

            if (IsQueryEndpoint(operation.Path))
            {
                aliases.Add("検索");
                aliases.Add("クエリ検索");
            }

            if (operation.Path.Contains("import", StringComparison.OrdinalIgnoreCase))
            {
                aliases.Add("取り込み");
            }

            if (operation.Path.Contains("export", StringComparison.OrdinalIgnoreCase))
            {
                aliases.Add("書き出し");
            }

            operation.Aliases = aliases.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToList();
            operation.SearchKeywords = aliases
                .Select(NormalizeForSearch)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    private static FetchedPage BuildFallbackYesodPage(ManualRoot root)
    {
        const string url = "https://developer.yesod.io/reference/api/index.html";
        var lines = string.Join('\n', GetYesodFallbackOperations().Select(operation => $"{operation.method} {operation.path}"));
        var page = new ManualPage
        {
            Id = $"{root.Id}:{url}",
            ManualId = root.Id,
            ManualName = root.Name,
            Url = url,
            Title = root.Name,
            PlainText = lines,
            Excerpt = "developer.yesod.io への直接接続に失敗したため、既知の YESOD API 経路情報からフォールバック生成したページです。",
            LinkedUrls = [],
            Tags = root.Tags.ToList()
        };

        return new FetchedPage(root, page, lines);
    }

    private static List<ManualRoot> GetManualRoots()
    {
        return
        [
            new ManualRoot(
                "iij-api-reference",
                "IIJ IDガバナンス管理サービス APIリファレンス",
                "https://manual.iij.jp/iga/igaapi_reference/",
                false,
                ["iij", "api", "reference"]),
            new ManualRoot(
                "iij-service-manual",
                "IIJ IDガバナンス管理サービス マニュアル",
                "https://manual.iij.jp/iga/manual/",
                false,
                ["iij", "manual"]),
            new ManualRoot(
                "iij-saas-federation-sample",
                "IIJ SaaS連携サンプルマニュアル",
                "https://manual.iij.jp/iga/saas_federation_sample_manual/",
                false,
                ["iij", "saas", "federation"]),
            new ManualRoot(
                "yesod-api-reference",
                "YESOD API",
                "https://developer.yesod.io/reference/api/index.html",
                true,
                ["yesod", "api", "reference"])
        ];
    }

    private static string NormalizePath(string rawPath)
    {
        var path = WebUtility.HtmlDecode(rawPath)
            .Replace("<従業員のID>", "{memberId}", StringComparison.OrdinalIgnoreCase)
            .Replace("<memberId>", "{memberId}", StringComparison.OrdinalIgnoreCase)
            .Replace("<taskId>", "{taskId}", StringComparison.OrdinalIgnoreCase)
            .Trim();

        path = Regex.Replace(path, "\\s+", string.Empty);
        path = Regex.Replace(path, "<[^>]+>", "{id}");

        if (Regex.IsMatch(path, "^/api/v\\d+\\.\\d+/members/[^/]+/avatar$", RegexOptions.IgnoreCase))
        {
            path = Regex.Replace(path, "/members/[^/]+/avatar", "/members/{memberId}/avatar", RegexOptions.IgnoreCase);
        }

        return path;
    }

    private static string ExtractVersion(string path)
    {
        var match = Regex.Match(path, "/api/(?<version>v\\d+\\.\\d+)/", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups["version"].Value : string.Empty;
    }

    private static string InferCategory(string path, string summary)
    {
        if (path.Contains("bigquery", StringComparison.OrdinalIgnoreCase))
        {
            return "BigQuery";
        }

        if (path.Contains("query", StringComparison.OrdinalIgnoreCase))
        {
            return "Query";
        }

        if (path.Contains("authorit", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("memberAssets", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("inventory-accounts", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("sync-assets", StringComparison.OrdinalIgnoreCase))
        {
            return "Access Control";
        }

        if (path.Contains("import", StringComparison.OrdinalIgnoreCase) ||
            summary.Contains("インポート", StringComparison.OrdinalIgnoreCase))
        {
            return "Import";
        }

        if (summary.Contains("履歴", StringComparison.OrdinalIgnoreCase))
        {
            return "History";
        }

        return "Directory";
    }

    private static List<string> BuildInitialAliases(string summary, string path)
    {
        var aliases = new List<string> { summary, path };
        foreach (var segment in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            aliases.Add(segment);
        }

        return aliases;
    }

    private static List<ParameterTemplate> ExtractPathParameters(string path)
    {
        var parameters = new List<ParameterTemplate>();
        foreach (Match match in Regex.Matches(path, "{(?<name>[^{}]+)}"))
        {
            parameters.Add(new ParameterTemplate
            {
                Name = match.Groups["name"].Value,
                Kind = "path",
                Required = true,
                Placeholder = "{" + match.Groups["name"].Value + "}",
                Description = $"{match.Groups["name"].Value} path parameter"
            });
        }

        return parameters;
    }

    private static string? InferSampleContentType(string path)
    {
        if (path.Contains("import", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("export", StringComparison.OrdinalIgnoreCase))
        {
            return "text/plain";
        }

        if (path.Contains("avatar", StringComparison.OrdinalIgnoreCase))
        {
            return "application/octet-stream";
        }

        return "application/json";
    }

    private static List<string> BuildOperationNotes(string manualName, string path)
    {
        var notes = new List<string> { $"Source: {manualName}" };

        if (path.Contains("import", StringComparison.OrdinalIgnoreCase))
        {
            notes.Add("CSVまたは業務データの投入形式を事前に確認してください。");
        }

        if (path.Contains("avatar", StringComparison.OrdinalIgnoreCase))
        {
            notes.Add("バイナリや multipart/form-data の扱いが必要な場合があります。");
        }

        if (IsQueryEndpoint(path))
        {
            notes.Add("クエリ条件の JSON ボディが必要です。");
        }

        return notes;
    }

    private static bool RequiresManualFixture(string path, string method)
    {
        if (method is "POST" or "PUT" or "PATCH")
        {
            return true;
        }

        return path.Contains("export", StringComparison.OrdinalIgnoreCase) ||
               path.Contains("{", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetYesodSummary(string path)
    {
        return path switch
        {
            "/api/v20.06/groupTree" => "グループAPI",
            "/api/v21.07/attributeLabel" => "属性一覧取得",
            "/api/v21.07/groups/import" => "組織・会社・事業所・プロジェクトのインポート",
            "/api/v21.07/groups/importAndApply" => "組織・会社・事業所・プロジェクトのインポート(同時承認)",
            "/api/v21.07/members/import" => "従業員情報のインポート",
            "/api/v21.07/members/importAndApply" => "従業員情報のインポート(同時承認)",
            "/api/v21.07/transactions/import" => "トランザクションインポート",
            "/api/v21.07/transactions/importAndApply" => "トランザクションインポート(同時承認)",
            "/api/v22.10/authorities" => "権限セットの照会",
            "/api/v22.10/authorityTasks" => "タスク照会API",
            "/api/v22.10/authorityTasks/{taskId}" => "タスクの完了書き戻し",
            "/api/v22.10/memberAuthorities" => "メンバーに関連づく業務アセットと役割の照会",
            "/api/v22.10/members/{memberId}/avatar" => "メンバーアバター画像更新 API",
            "/api/v22.10/members/{memberId}/histories" => "メンバー履歴 API",
            "/api/v22.10/organizations" => "組織 API",
            "/api/v22.10/projectAuthorities" => "プロジェクトに関連づく業務アセットと役割の照会",
            "/api/v24.10/memberAssets" => "業務アセットに割り当てられているメンバの照会",
            "/api/v24.10/query" => "クエリ",
            "/api/v25.04/bigquery/members" => "BigQuery用メンバー履歴 API",
            "/api/v25.04/options" => "項目オプションAPI",
            "/api/v25.04/sync-assets" => "同期する項目をYESODからSaaSに同期",
            "/api/v25.10/bigquery/groups" => "BigQuery用グループ履歴 API",
            "/api/v25.10/memberAssets/export" => "業務アセットに割り当てられているメンバーのCSVエクスポート",
            "/api/v25.10/memberAssetsDelta/export" => "業務アセットに割り当てられているメンバーの差分CSVエクスポート",
            "/api/v26.04/bigquery/attributes" => "BigQuery用属性一覧 API",
            "/api/v26.04/bigquery/options" => "BigQuery用項目オプション一覧 API",
            "/api/v26.04/inventory-accounts" => "棚卸しアカウント一覧の照会",
            "/api/v26.04/projects" => "プロジェクト API",
            _ => path
        };
    }

    private static bool IsQueryEndpoint(string path)
    {
        return Regex.IsMatch(path, "(?i)(^|/)query($|/)");
    }

    private static IReadOnlyList<(string method, string path)> GetYesodFallbackOperations()
    {
        return
        [
            ("GET", "/api/v20.06/groupTree"),
            ("GET", "/api/v21.07/attributeLabel"),
            ("POST", "/api/v21.07/groups/import"),
            ("POST", "/api/v21.07/groups/importAndApply"),
            ("POST", "/api/v21.07/members/import"),
            ("POST", "/api/v21.07/members/importAndApply"),
            ("POST", "/api/v21.07/transactions/import"),
            ("POST", "/api/v21.07/transactions/importAndApply"),
            ("GET", "/api/v22.10/authorities"),
            ("GET", "/api/v22.10/authorityTasks"),
            ("PATCH", "/api/v22.10/authorityTasks/{taskId}"),
            ("GET", "/api/v22.10/memberAuthorities"),
            ("PUT", "/api/v22.10/members/{memberId}/avatar"),
            ("GET", "/api/v22.10/members/{memberId}/histories"),
            ("GET", "/api/v22.10/organizations"),
            ("GET", "/api/v22.10/projectAuthorities"),
            ("GET", "/api/v24.10/memberAssets"),
            ("POST", "/api/v24.10/query"),
            ("GET", "/api/v25.04/bigquery/members"),
            ("GET", "/api/v25.04/options"),
            ("POST", "/api/v25.04/sync-assets"),
            ("GET", "/api/v25.10/bigquery/groups"),
            ("POST", "/api/v25.10/memberAssets/export"),
            ("POST", "/api/v25.10/memberAssetsDelta/export"),
            ("GET", "/api/v26.04/bigquery/attributes"),
            ("GET", "/api/v26.04/bigquery/options"),
            ("GET", "/api/v26.04/inventory-accounts"),
            ("GET", "/api/v26.04/projects")
        ];
    }

    [GeneratedRegex("<a[^>]+href=\"(?<href>[^\"]+)\"", RegexOptions.IgnoreCase)]
    private static partial Regex HrefRegex();

    [GeneratedRegex("<script\\b[^>]*>.*?</script>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ScriptRegex();

    [GeneratedRegex("<style\\b[^>]*>.*?</style>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex StyleRegex();

    [GeneratedRegex("<[^>]+>", RegexOptions.Singleline)]
    private static partial Regex TagRegex();

    [GeneratedRegex("<tr\\b[^>]*>.*?</tr>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex TableRowRegex();

    [GeneratedRegex("\\b(GET|POST|PUT|DELETE|PATCH)\\b", RegexOptions.IgnoreCase)]
    private static partial Regex HttpMethodRegex();

    [GeneratedRegex("/api/[^\\s\"'<>]+", RegexOptions.IgnoreCase)]
    private static partial Regex ApiPathRegex();

    [GeneratedRegex("(?<method>GET|POST|PUT|DELETE|PATCH).{0,160}?(?<path>/api/[^\\s\"'<>]+)", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex InlineMethodAndPathRegex();

    private sealed record ManualRoot(string Id, string Name, string RootUrl, bool SinglePage, IReadOnlyList<string> Tags);
    private sealed record FetchedPage(ManualRoot Root, ManualPage Page, string Html);
}
