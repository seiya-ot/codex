using System.Diagnostics;
using System.Net.Http.Headers;
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
        if (string.IsNullOrWhiteSpace(input.BaseUrl))
        {
            throw new InvalidOperationException("Tenant URL is required.");
        }

        if (!Uri.TryCreate(AppendSlashIfNeeded(input.BaseUrl.Trim()), UriKind.Absolute, out var baseUri))
        {
            throw new InvalidOperationException("Tenant URL is invalid.");
        }

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

        if (string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidOperationException("Path could not be resolved.");
        }

        path = ApplyVariables(path, input.Variables);
        var finalUri = new Uri(baseUri, path.TrimStart('/'));

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
        var stopwatch = Stopwatch.StartNew();
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
}
