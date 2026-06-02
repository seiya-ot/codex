using System.Text.RegularExpressions;

namespace Codex.ApiVerificationWorkbench.Services;

public sealed class AttributeIdCatalog
{
    private static readonly Regex IdPattern = new("^[A-Za-z0-9_-]{10,}$", RegexOptions.Compiled);
    private static readonly Regex SplitPattern = new("[\\s\\-_/#+]+", RegexOptions.Compiled);

    private readonly string _filePath;
    private readonly object _sync = new();
    private IReadOnlyList<AttributeIdEntry> _cachedEntries = [];

    public AttributeIdCatalog(IHostEnvironment environment)
    {
        _filePath = Path.Combine(environment.ContentRootPath, "iga_assetId.csv");
    }

    public string? GetDefaultAttributeId()
    {
        return GetEntries().FirstOrDefault()?.Id;
    }

    public bool ContainsAttributeId(string? attributeId)
    {
        if (string.IsNullOrWhiteSpace(attributeId))
        {
            return false;
        }

        var normalized = attributeId.Trim();
        return GetEntries().Any(entry => string.Equals(entry.Id, normalized, StringComparison.OrdinalIgnoreCase));
    }

    public AttributeIdInference? InferFromRequestText(string? requestText)
    {
        if (string.IsNullOrWhiteSpace(requestText))
        {
            return null;
        }

        var normalizedRequest = ManualCrawler.NormalizeForSearch(requestText);
        if (string.IsNullOrWhiteSpace(normalizedRequest))
        {
            return null;
        }

        var entries = GetEntries();
        if (entries.Count == 0)
        {
            return null;
        }

        AttributeIdEntry? best = null;
        var bestScore = 0;

        foreach (var entry in entries)
        {
            var score = ScoreEntry(normalizedRequest, requestText, entry);
            if (score <= 0)
            {
                continue;
            }

            if (score > bestScore)
            {
                best = entry;
                bestScore = score;
            }
        }

        if (best is null || bestScore < 120)
        {
            return null;
        }

        return new AttributeIdInference(best.Id, best.Name, bestScore);
    }

    private IReadOnlyList<AttributeIdEntry> GetEntries()
    {
        if (_cachedEntries.Count > 0)
        {
            return _cachedEntries;
        }

        lock (_sync)
        {
            if (_cachedEntries.Count > 0)
            {
                return _cachedEntries;
            }

            var loaded = LoadEntriesSafe();
            if (loaded.Count > 0)
            {
                _cachedEntries = loaded;
                return _cachedEntries;
            }

            return loaded;
        }
    }

    private IReadOnlyList<AttributeIdEntry> LoadEntriesSafe()
    {
        try
        {
            return LoadEntriesCore();
        }
        catch (IOException)
        {
            return _cachedEntries;
        }
        catch (UnauthorizedAccessException)
        {
            return _cachedEntries;
        }
    }

    private IReadOnlyList<AttributeIdEntry> LoadEntriesCore()
    {
        if (!File.Exists(_filePath))
        {
            return [];
        }

        var results = new List<AttributeIdEntry>();
        var index = 0;

        using var stream = new FileStream(
            _filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);

        while (!reader.EndOfStream)
        {
            var line = reader.ReadLine();
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var columns = line.Split(',');
            if (columns.Length < 2)
            {
                continue;
            }

            var id = columns[0].Trim().Trim('"');
            var name = columns[1].Trim().Trim('"');
            if (!IdPattern.IsMatch(id))
            {
                continue;
            }

            var normalizedName = string.IsNullOrWhiteSpace(name) ? string.Empty : ManualCrawler.NormalizeForSearch(name);
            var keywords = BuildKeywords(name);
            results.Add(new AttributeIdEntry(index++, id, name, normalizedName, keywords));
        }

        return results
            .GroupBy(entry => entry.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderBy(entry => entry.Index).First())
            .OrderBy(entry => entry.Index)
            .ToList();
    }

    private static int ScoreEntry(string normalizedRequest, string rawRequest, AttributeIdEntry entry)
    {
        var score = 0;
        if (!string.IsNullOrWhiteSpace(entry.NormalizedName))
        {
            if (normalizedRequest.Contains(entry.NormalizedName, StringComparison.OrdinalIgnoreCase))
            {
                score += 260 + entry.NormalizedName.Length * 3;
            }
            else if (entry.NormalizedName.Contains(normalizedRequest, StringComparison.OrdinalIgnoreCase))
            {
                score += 80;
            }
        }

        if (!string.IsNullOrWhiteSpace(entry.Name) &&
            rawRequest.Contains(entry.Name, StringComparison.OrdinalIgnoreCase))
        {
            score += 220 + Math.Min(60, entry.Name.Length * 2);
        }

        var keywordHits = 0;
        foreach (var keyword in entry.Keywords)
        {
            if (normalizedRequest.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                keywordHits++;
            }
        }

        score += keywordHits * 35;
        return score;
    }

    private static IReadOnlyList<string> BuildKeywords(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        var keywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in SplitPattern.Split(value))
        {
            var normalized = ManualCrawler.NormalizeForSearch(part);
            if (normalized.Length >= 2)
            {
                keywords.Add(normalized);
            }
        }

        var whole = ManualCrawler.NormalizeForSearch(value);
        if (whole.Length >= 2)
        {
            keywords.Add(whole);
        }

        return keywords.ToList();
    }

    private sealed record AttributeIdEntry(int Index, string Id, string Name, string NormalizedName, IReadOnlyList<string> Keywords);
}

public sealed record AttributeIdInference(string AttributeId, string AttributeName, int Score);
