using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Codex.ApiVerificationWorkbench.Models;

namespace Codex.ApiVerificationWorkbench.Services;

public sealed class RequestExecutor
{
    private readonly IHttpClientFactory _httpClientFactory;

    public RequestExecutor(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    public async Task<ExecuteResponse> ExecuteAsync(ExecuteRequestInput input, ResolveRequestResponse resolved)
    {
        var selectedOperation = resolved.Candidates.FirstOrDefault()?.Operation;
        if (selectedOperation is not null)
        {
            if (!string.IsNullOrWhiteSpace(input.Path) &&
                !string.Equals(selectedOperation.Path, input.Path, StringComparison.OrdinalIgnoreCase))
            {
                selectedOperation = null;
            }

            if (selectedOperation is not null &&
                !string.IsNullOrWhiteSpace(input.Method) &&
                !string.Equals(selectedOperation.Method, input.Method, StringComparison.OrdinalIgnoreCase))
            {
                selectedOperation = null;
            }
        }

        var method = (input.Method ?? selectedOperation?.Method ?? "GET").Trim().ToUpperInvariant();
        var path = (input.Path ?? selectedOperation?.Path ?? string.Empty).Trim();
        var stopwatch = Stopwatch.StartNew();
        Uri? finalUri = null;

        try
        {
            if (string.IsNullOrWhiteSpace(input.BaseUrl))
            {
                throw new InvalidOperationException("テナント URL を入力してください。");
            }

            if (!Uri.TryCreate(AppendSlashIfNeeded(input.BaseUrl.Trim()), UriKind.Absolute, out var baseUri))
            {
                throw new InvalidOperationException("テナント URL の形式が不正です。");
            }

            if (string.IsNullOrWhiteSpace(path))
            {
                throw new InvalidOperationException("実行する API パスを特定できませんでした。");
            }

            path = ApplyVariables(path, input.Variables);
            finalUri = new Uri(baseUri, path.TrimStart('/'));

            using var request = new HttpRequestMessage(new HttpMethod(method), finalUri);

            if (!string.IsNullOrWhiteSpace(input.AccessToken))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", input.AccessToken.Trim());
            }

            foreach (var header in input.Headers)
            {
                if (string.Equals(header.Key, "Authorization", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                request.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            var contentType = input.ContentType ?? selectedOperation?.SampleContentType ?? "application/json";
            var body = ApplyVariables(input.Body ?? string.Empty, input.Variables);
            if (method is "POST" or "PUT" or "PATCH" || !string.IsNullOrWhiteSpace(body))
            {
                request.Content = CreateHttpContent(body, contentType, input.BodyFormat);
            }

            using var client = _httpClientFactory.CreateClient(nameof(RequestExecutor));
            client.Timeout = TimeSpan.FromSeconds(30);

            using var response = await client.SendAsync(request);
            stopwatch.Stop();

            var responseBody = await response.Content.ReadAsStringAsync();
            var headers = response.Headers
                .Concat(response.Content.Headers)
                .GroupBy(header => header.Key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.SelectMany(value => value.Value).ToArray(), StringComparer.OrdinalIgnoreCase);

            return new ExecuteResponse
            {
                Method = method,
                FinalUrl = finalUri.ToString(),
                StatusCode = (int)response.StatusCode,
                IsSuccessStatusCode = response.IsSuccessStatusCode,
                ElapsedMilliseconds = stopwatch.ElapsedMilliseconds,
                ResponseBody = FormatResponseBody(responseBody),
                ResponseHeaders = headers,
                UsedOperationId = selectedOperation?.Id,
                UsedOperationSummary = selectedOperation?.Summary,
                Notes = BuildExecutionNotes(selectedOperation, input)
            };
        }
        catch (Exception exception)
        {
            stopwatch.Stop();
            return BuildErrorResponse(method, finalUri, input, selectedOperation, stopwatch.ElapsedMilliseconds, exception);
        }
    }

    private static string AppendSlashIfNeeded(string baseUrl)
    {
        return baseUrl.EndsWith('/') ? baseUrl : baseUrl + "/";
    }

    private static HttpContent CreateHttpContent(string body, string contentType, string bodyFormat)
    {
        if (string.Equals(bodyFormat, "base64", StringComparison.OrdinalIgnoreCase))
        {
            var bytes = Convert.FromBase64String(body.Trim());
            var binaryContent = new ByteArrayContent(bytes);
            binaryContent.Headers.ContentType = new MediaTypeHeaderValue(contentType);
            return binaryContent;
        }

        var content = new StringContent(body, Encoding.UTF8);
        content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        return content;
    }

    private static string ApplyVariables(string template, IDictionary<string, string> variables)
    {
        if (string.IsNullOrWhiteSpace(template) || variables.Count == 0)
        {
            return template;
        }

        var result = template;
        foreach (var pair in variables.Where(pair => !string.IsNullOrWhiteSpace(pair.Value)))
        {
            result = result.Replace("{" + pair.Key + "}", pair.Value, StringComparison.OrdinalIgnoreCase);
            result = result.Replace("<" + pair.Key + ">", pair.Value, StringComparison.OrdinalIgnoreCase);
        }

        return result;
    }

    private static string FormatResponseBody(string responseBody)
    {
        if (string.IsNullOrWhiteSpace(responseBody))
        {
            return string.Empty;
        }

        try
        {
            using var document = JsonDocument.Parse(responseBody);
            return JsonSerializer.Serialize(document, new JsonSerializerOptions { WriteIndented = true });
        }
        catch
        {
            return responseBody;
        }
    }

    private static List<string> BuildExecutionNotes(ApiOperation? operation, ExecuteRequestInput input)
    {
        var notes = new List<string>();

        if (operation is not null)
        {
            notes.Add($"Resolved operation: {operation.Summary}");
        }

        if (Regex.IsMatch(input.Path ?? operation?.Path ?? string.Empty, "{[^{}]+}"))
        {
            notes.Add("未置換のパスパラメータが残っていないか確認してください。");
        }

        if (string.Equals(input.BodyFormat, "base64", StringComparison.OrdinalIgnoreCase))
        {
            notes.Add("body は Base64 デコード後のバイナリとして送信しました。");
        }

        return notes;
    }

    private static ExecuteResponse BuildErrorResponse(
        string method,
        Uri? finalUri,
        ExecuteRequestInput input,
        ApiOperation? operation,
        long elapsedMilliseconds,
        Exception exception)
    {
        var notes = BuildExecutionNotes(operation, input);
        var errorType = "unexpected_error";
        var errorMessage = "予期しないエラーが発生しました。";

        switch (exception)
        {
            case InvalidOperationException invalidOperationException:
                errorType = "validation_error";
                errorMessage = invalidOperationException.Message;
                break;
            case FormatException formatException when string.Equals(input.BodyFormat, "base64", StringComparison.OrdinalIgnoreCase):
                errorType = "invalid_base64";
                errorMessage = "Base64 body の形式が不正です。";
                notes.Add(formatException.Message);
                break;
            case TaskCanceledException:
                errorType = "timeout";
                errorMessage = "接続または応答がタイムアウトしました。対象テナントへの疎通を確認してください。";
                break;
            case HttpRequestException httpRequestException:
                errorType = "network_error";
                errorMessage = BuildHttpRequestErrorMessage(finalUri, httpRequestException);
                break;
        }

        notes.Add($"Exception: {exception.GetType().Name}");

        if (finalUri is not null && finalUri.Host.Contains(".int.", StringComparison.OrdinalIgnoreCase))
        {
            notes.Add("対象ホストは内部向けドメインに見えます。VPN や社内ネットワーク接続が必要な可能性があります。");
        }

        return new ExecuteResponse
        {
            Method = method,
            FinalUrl = finalUri?.ToString() ?? string.Empty,
            StatusCode = 0,
            IsSuccessStatusCode = false,
            ElapsedMilliseconds = elapsedMilliseconds,
            ResponseBody = string.Empty,
            ResponseHeaders = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase),
            UsedOperationId = operation?.Id,
            UsedOperationSummary = operation?.Summary,
            ErrorType = errorType,
            ErrorMessage = errorMessage,
            Notes = notes
        };
    }

    private static string BuildHttpRequestErrorMessage(Uri? finalUri, HttpRequestException exception)
    {
        if (exception.InnerException is SocketException socketException)
        {
            return socketException.SocketErrorCode switch
            {
                SocketError.TimedOut => $"接続先 {finalUri?.Host ?? "unknown host"} への接続がタイムアウトしました。",
                SocketError.HostNotFound => $"接続先 {finalUri?.Host ?? "unknown host"} の名前解決に失敗しました。",
                SocketError.ConnectionRefused => $"接続先 {finalUri?.Host ?? "unknown host"} が接続を拒否しました。",
                _ => $"接続先 {finalUri?.Host ?? "unknown host"} への接続に失敗しました。"
            };
        }

        return exception.Message;
    }
}
