using System.Text.RegularExpressions;
using Codex.ApiVerificationWorkbench.Models;

namespace Codex.ApiVerificationWorkbench.Services;

public sealed partial class RequestResolver
{
    private readonly ManualCatalogStore _catalogStore;

    public RequestResolver(ManualCatalogStore catalogStore)
    {
        _catalogStore = catalogStore;
    }

    public ResolveRequestResponse Resolve(ResolveRequestInput input)
    {
        var catalog = _catalogStore.Current;
        var response = new ResolveRequestResponse();

        if (!string.IsNullOrWhiteSpace(input.OperationId))
        {
            var operation = catalog.ApiOperations.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, input.OperationId, StringComparison.OrdinalIgnoreCase));

            if (operation is not null)
            {
                response.Candidates.Add(new ResolvedCandidate
                {
                    Operation = operation,
                    Score = 10_000,
                    Reasons = ["operationId が明示されています。"]
                });
                response.NormalizedQuery = ManualCrawler.NormalizeForSearch(input.RequestText ?? string.Empty);
                response.MethodHint = operation.Method;
                response.PathHint = operation.Path;
                return response;
            }
        }

        var queryText = string.Join(" ", new[]
        {
            input.RequestText?.Trim(),
            input.ExplicitMethod?.Trim(),
            input.ExplicitPath?.Trim()
        }.Where(value => !string.IsNullOrWhiteSpace(value)));

        response.NormalizedQuery = ManualCrawler.NormalizeForSearch(queryText);
        response.MethodHint = ParseMethodHint(input.ExplicitMethod, input.RequestText);
        response.PathHint = ParsePathHint(input.ExplicitPath, input.RequestText);

        if (string.IsNullOrWhiteSpace(queryText))
        {
            return response;
        }

        var grams = BuildNGrams(response.NormalizedQuery, 2, 3);
        var queryTerms = ExtractQueryTerms(queryText);
        var candidates = new List<ResolvedCandidate>();

        foreach (var sourceOperation in catalog.ApiOperations)
        {
            var operation = EnsureOptionalQueryParameters(sourceOperation);
            var score = 0;
            var reasons = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (!string.IsNullOrWhiteSpace(response.MethodHint) &&
                string.Equals(operation.Method, response.MethodHint, StringComparison.OrdinalIgnoreCase))
            {
                score += ResolverScoring.MethodHintMatch;
                reasons.Add($"HTTP メソッド {response.MethodHint} が一致しました。");
            }

            if (!string.IsNullOrWhiteSpace(response.PathHint))
            {
                if (string.Equals(operation.Path, response.PathHint, StringComparison.OrdinalIgnoreCase))
                {
                    score += ResolverScoring.PathExactMatch;
                    reasons.Add("パスが完全一致しました。");
                }
                else if (operation.Path.Contains(response.PathHint, StringComparison.OrdinalIgnoreCase) ||
                         response.PathHint.Contains(operation.Path, StringComparison.OrdinalIgnoreCase))
                {
                    score += ResolverScoring.PathPartialMatch;
                    reasons.Add("パスの一部が一致しました。");
                }
            }

            foreach (var alias in operation.Aliases)
            {
                var normalizedAlias = ManualCrawler.NormalizeForSearch(alias);
                if (string.IsNullOrWhiteSpace(normalizedAlias))
                {
                    continue;
                }

                if (response.NormalizedQuery.Contains(normalizedAlias, StringComparison.OrdinalIgnoreCase))
                {
                    score += ResolverScoring.AliasExactMatchBase + normalizedAlias.Length;
                    reasons.Add($"「{alias}」に一致しました。");
                }
                else if (normalizedAlias.Contains(response.NormalizedQuery, StringComparison.OrdinalIgnoreCase))
                {
                    score += ResolverScoring.AliasContainsQuery;
                    reasons.Add($"候補側の別名「{alias}」が入力を内包しています。");
                }
            }

            foreach (var term in queryTerms)
            {
                if (operation.SearchKeywords.Any(keyword => keyword.Contains(term, StringComparison.OrdinalIgnoreCase)))
                {
                    score += Math.Min(
                        ResolverScoring.QueryTermMatchMax,
                        ResolverScoring.QueryTermMatchBase + (term.Length * ResolverScoring.QueryTermLengthMultiplier));
                    reasons.Add($"キーワード「{term}」に一致しました。");
                }
            }

            var searchable = string.Join(' ', operation.SearchKeywords);
            var gramHits = grams.Count(gram => searchable.Contains(gram, StringComparison.OrdinalIgnoreCase));
            if (gramHits > 0)
            {
                score += gramHits * ResolverScoring.NGramHit;
                reasons.Add($"検索文字片が {gramHits} 件一致しました。");
            }

            if (score <= 0)
            {
                continue;
            }

            if (operation.OptionalQueryParameters.Count > 0)
            {
                reasons.Add($"Optional query parameters: {string.Join(", ", operation.OptionalQueryParameters.Keys.OrderBy(key => key, StringComparer.OrdinalIgnoreCase))}");
            }

            candidates.Add(new ResolvedCandidate
            {
                Operation = operation,
                Score = score,
                Reasons = reasons.ToList()
            });
        }

        response.Candidates = candidates
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.Operation.Path, StringComparer.OrdinalIgnoreCase)
            .Take(Math.Clamp(input.Top, 1, 20))
            .ToList();

        return response;
    }

    private static string? ParseMethodHint(string? explicitMethod, string? requestText)
    {
        var source = $"{explicitMethod} {requestText}";
        var match = MethodHintRegex().Match(source);
        return match.Success ? match.Groups[1].Value.ToUpperInvariant() : null;
    }

    private static string? ParsePathHint(string? explicitPath, string? requestText)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            return explicitPath.Trim();
        }

        var match = PathHintRegex().Match(requestText ?? string.Empty);
        return match.Success ? match.Value.Trim() : null;
    }

    private static HashSet<string> BuildNGrams(string normalizedText, int minLength, int maxLength)
    {
        var grams = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(normalizedText))
        {
            return grams;
        }

        for (var size = minLength; size <= maxLength; size++)
        {
            if (normalizedText.Length < size)
            {
                continue;
            }

            for (var index = 0; index <= normalizedText.Length - size; index++)
            {
                grams.Add(normalizedText.Substring(index, size));
            }
        }

        return grams;
    }

    private static List<string> ExtractQueryTerms(string queryText)
    {
        var cleaned = queryText;
        var fillers = new[]
        {
            "してください", "したい", "して", "します", "した", "を", "の", "に", "で", "は", "が", "と", "から", "まで",
            "API", "api", "実行", "取得", "一覧", "照会", "検索", "更新"
        };

        foreach (var filler in fillers)
        {
            cleaned = cleaned.Replace(filler, " ", StringComparison.OrdinalIgnoreCase);
        }

        cleaned = QueryTermSeparatorsRegex().Replace(cleaned, " ");
        var terms = cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();

        foreach (var term in terms.ToList())
        {
            AddDerivedTerm(terms, term, "履歴");
            AddDerivedTerm(terms, term, "従業員");
            AddDerivedTerm(terms, term, "メンバー");
            AddDerivedTerm(terms, term, "権限");
            AddDerivedTerm(terms, term, "組織");
            AddDerivedTerm(terms, term, "グループ");
            AddDerivedTerm(terms, term, "プロジェクト");
            AddDerivedTerm(terms, term, "アバター");
        }

        return terms
            .Select(ManualCrawler.NormalizeForSearch)
            .Where(term => term.Length >= 2)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static ApiOperation EnsureOptionalQueryParameters(ApiOperation operation)
    {
        if (operation.OptionalQueryParameters.Count > 0)
        {
            return operation;
        }

        var inferred = InferOptionalQueryParameters(operation.Method, operation.Path);
        if (inferred.Count == 0)
        {
            return operation;
        }

        operation.OptionalQueryParameters = inferred;
        return operation;
    }

    private static Dictionary<string, string> InferOptionalQueryParameters(string method, string path)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase))
        {
            return values;
        }

        if (string.Equals(path, "/api/v24.10/memberAssets", StringComparison.OrdinalIgnoreCase))
        {
            values["assetId"] = "{assetId}";
            values["date"] = "{date}";
        }

        return values;
    }

    private static void AddDerivedTerm(ICollection<string> terms, string source, string needle)
    {
        if (!source.Contains(needle, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        terms.Add(needle);
        var remaining = source.Replace(needle, string.Empty, StringComparison.OrdinalIgnoreCase).Trim();
        if (remaining.Length >= 2)
        {
            terms.Add(remaining);
        }
    }

    [GeneratedRegex("\\b(GET|POST|PUT|DELETE|PATCH)\\b", RegexOptions.IgnoreCase)]
    private static partial Regex MethodHintRegex();

    [GeneratedRegex("/api/[^\\s\"']+", RegexOptions.IgnoreCase)]
    private static partial Regex PathHintRegex();

    [GeneratedRegex("[=:/{}\"'.,，、。()（）\\[\\]\\s]+")]
    private static partial Regex QueryTermSeparatorsRegex();

    private static class ResolverScoring
    {
        public const int MethodHintMatch = 120;
        public const int PathExactMatch = 1_000;
        public const int PathPartialMatch = 250;
        public const int AliasExactMatchBase = 180;
        public const int AliasContainsQuery = 90;
        public const int QueryTermMatchMax = 220;
        public const int QueryTermMatchBase = 40;
        public const int QueryTermLengthMultiplier = 30;
        public const int NGramHit = 4;
    }
}
