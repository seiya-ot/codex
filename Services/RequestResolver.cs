using System.Text.RegularExpressions;
using Codex.ApiVerificationWorkbench.Models;

namespace Codex.ApiVerificationWorkbench.Services;

public sealed class RequestResolver
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

        foreach (var operation in catalog.ApiOperations)
        {
            var score = 0;
            var reasons = new List<string>();

            if (!string.IsNullOrWhiteSpace(response.MethodHint) &&
                string.Equals(operation.Method, response.MethodHint, StringComparison.OrdinalIgnoreCase))
            {
                score += 120;
                reasons.Add($"HTTP メソッド {response.MethodHint} が一致しました。");
            }

            if (!string.IsNullOrWhiteSpace(response.PathHint))
            {
                if (string.Equals(operation.Path, response.PathHint, StringComparison.OrdinalIgnoreCase))
                {
                    score += 1_000;
                    reasons.Add("パスが完全一致しました。");
                }
                else if (operation.Path.Contains(response.PathHint, StringComparison.OrdinalIgnoreCase) ||
                         response.PathHint.Contains(operation.Path, StringComparison.OrdinalIgnoreCase))
                {
                    score += 250;
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
                    score += 180 + normalizedAlias.Length;
                    reasons.Add($"「{alias}」に一致しました。");
                }
                else if (normalizedAlias.Contains(response.NormalizedQuery, StringComparison.OrdinalIgnoreCase))
                {
                    score += 90;
                    reasons.Add($"候補側の別名「{alias}」が入力を内包しています。");
                }
            }

            foreach (var term in queryTerms)
            {
                if (operation.SearchKeywords.Any(keyword => keyword.Contains(term, StringComparison.OrdinalIgnoreCase)))
                {
                    score += Math.Min(220, 40 + (term.Length * 30));
                    reasons.Add($"キーワード「{term}」に一致しました。");
                }
            }

            var searchable = string.Join(' ', operation.SearchKeywords);
            var gramHits = grams.Count(gram => searchable.Contains(gram, StringComparison.OrdinalIgnoreCase));
            if (gramHits > 0)
            {
                score += gramHits * 4;
                reasons.Add($"検索文字片が {gramHits} 件一致しました。");
            }

            if (score <= 0)
            {
                continue;
            }

            candidates.Add(new ResolvedCandidate
            {
                Operation = operation,
                Score = score,
                Reasons = reasons.Distinct(StringComparer.OrdinalIgnoreCase).ToList()
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
        var match = Regex.Match(source, "\\b(GET|POST|PUT|DELETE|PATCH)\\b", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value.ToUpperInvariant() : null;
    }

    private static string? ParsePathHint(string? explicitPath, string? requestText)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            return explicitPath.Trim();
        }

        var match = Regex.Match(requestText ?? string.Empty, "/api/[^\\s\"']+", RegexOptions.IgnoreCase);
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

        cleaned = Regex.Replace(cleaned, "[=:/{}\"'.,，、。()（）\\[\\]\\s]+", " ");
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
}
