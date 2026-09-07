using System.Text.Json;
using System.Text.RegularExpressions;
using Codex.ApiVerificationWorkbench.Models;

namespace Codex.ApiVerificationWorkbench.Services;

/// <summary>
/// API 実行失敗の診断情報を、認証情報を除外してローカルに保持する。
/// </summary>
public sealed partial class ApiErrorLogStore
{
    private const int DefaultMaximumEntries = 200;
    private const int MaximumResponseBodyLength = 65_536;
    private readonly IWebHostEnvironment _environment;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ApiErrorLogStore> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public ApiErrorLogStore(
        IWebHostEnvironment environment,
        IConfiguration configuration,
        ILogger<ApiErrorLogStore> logger)
    {
        _environment = environment;
        _configuration = configuration;
        _logger = logger;
    }

    private string LogDirectory => Path.Combine(_environment.ContentRootPath, "Data");
    private string LogPath => Path.Combine(LogDirectory, "api-error-log.json");
    private int MaximumEntries => Math.Clamp(
        _configuration.GetValue<int?>("ErrorLog:MaximumEntries") ?? DefaultMaximumEntries,
        1,
        1_000);

    /// <summary>
    /// 失敗した API 実行を保存する。保存に失敗しても実行結果の返却を妨げない。
    /// </summary>
    public async Task RecordIfFailedAsync(ExecuteResponse response, CancellationToken cancellationToken = default)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        try
        {
            await _gate.WaitAsync(cancellationToken);
            try
            {
                var entries = await LoadAsync(cancellationToken);
                entries.Insert(0, CreateEntry(response));
                if (entries.Count > MaximumEntries)
                {
                    entries.RemoveRange(MaximumEntries, entries.Count - MaximumEntries);
                }

                await SaveAsync(entries, cancellationToken);
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Failed to persist an API execution error.");
        }
    }

    /// <summary>
    /// 保存済みの API 実行エラーを新しい順で返す。
    /// </summary>
    public async Task<IReadOnlyList<ApiErrorLogEntry>> GetEntriesAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return await LoadAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 指定したエラー記録を削除し、削除できたかを返す。
    /// </summary>
    public async Task<bool> DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var entries = await LoadAsync(cancellationToken);
            var removed = entries.RemoveAll(entry => string.Equals(entry.Id, id, StringComparison.OrdinalIgnoreCase));
            if (removed == 0)
            {
                return false;
            }

            await SaveAsync(entries, cancellationToken);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// すべてのエラー記録を削除する。
    /// </summary>
    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await SaveAsync([], cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<List<ApiErrorLogEntry>> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(LogPath))
        {
            return [];
        }

        await using var stream = File.OpenRead(LogPath);
        return await JsonSerializer.DeserializeAsync<List<ApiErrorLogEntry>>(stream, _jsonOptions, cancellationToken) ?? [];
    }

    private async Task SaveAsync(List<ApiErrorLogEntry> entries, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(LogDirectory);
        var temporaryPath = Path.Combine(LogDirectory, $"{Path.GetFileName(LogPath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 8192,
                FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(stream, entries, _jsonOptions, cancellationToken);
            }

            File.Move(temporaryPath, LogPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static ApiErrorLogEntry CreateEntry(ExecuteResponse response)
    {
        return new ApiErrorLogEntry
        {
            Id = Guid.NewGuid().ToString("N"),
            OccurredAtUtc = DateTimeOffset.UtcNow,
            Method = response.Method,
            FinalUrl = RemoveQueryAndFragment(response.FinalUrl),
            StatusCode = response.StatusCode,
            ErrorType = response.ErrorType,
            ErrorMessage = RedactText(response.ErrorMessage),
            ElapsedMilliseconds = response.ElapsedMilliseconds,
            UsedOperationId = response.UsedOperationId,
            UsedOperationSummary = response.UsedOperationSummary,
            RequestContentType = response.RequestContentType,
            RequestBodyFormat = response.RequestBodyFormat,
            RequestHeaders = RedactHeaders(response.RequestHeaders),
            ResponseHeaders = RedactHeaders(response.ResponseHeaders),
            ResponseBody = RedactText(Truncate(response.ResponseBody, MaximumResponseBodyLength)) ?? string.Empty,
            ProxyMode = response.ProxyMode,
            ProxyUrl = RedactProxyUrl(response.ProxyUrl),
            Notes = response.Notes.Select(note => RedactText(note) ?? string.Empty).ToList()
        };
    }

    private static Dictionary<string, string[]> RedactHeaders(IReadOnlyDictionary<string, string[]> headers)
    {
        return headers.ToDictionary(
            pair => pair.Key,
            pair => IsSensitiveHeader(pair.Key)
                ? ["[REDACTED]"]
                : pair.Value.Select(value => RedactText(value) ?? string.Empty).ToArray(),
            StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsSensitiveHeader(string name)
    {
        return name.Contains("authorization", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("cookie", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("api-key", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("token", StringComparison.OrdinalIgnoreCase);
    }

    private static string RemoveQueryAndFragment(string url)
    {
        return Uri.TryCreate(url, UriKind.Absolute, out var uri)
            ? uri.GetLeftPart(UriPartial.Path)
            : url;
    }

    private static string? RedactProxyUrl(string? proxyUrl)
    {
        if (string.IsNullOrWhiteSpace(proxyUrl) || !Uri.TryCreate(proxyUrl, UriKind.Absolute, out var uri))
        {
            return proxyUrl;
        }

        var builder = new UriBuilder(uri)
        {
            UserName = string.Empty,
            Password = string.Empty,
            Query = string.Empty,
            Fragment = string.Empty
        };
        return builder.Uri.GetLeftPart(UriPartial.Path);
    }

    private static string Truncate(string value, int maximumLength)
    {
        return value.Length <= maximumLength ? value : $"{value[..maximumLength]}\n[TRUNCATED]";
    }

    private static string? RedactText(string? value)
    {
        return value is null ? null : SecretJsonValueRegex().Replace(BearerTokenRegex().Replace(value, "Bearer [REDACTED]"), "$1[REDACTED]$3");
    }

    [GeneratedRegex("(?i)Bearer\\s+[A-Za-z0-9._~+/-]+=*")]
    private static partial Regex BearerTokenRegex();

    [GeneratedRegex("(?i)(\\\"(?:access_?token|token|password|secret|api_?key)\\\"\\s*:\\s*\\\")[^\\\"]*(\\\")")]
    private static partial Regex SecretJsonValueRegex();
}
