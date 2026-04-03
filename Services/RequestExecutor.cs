using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Codex.ApiVerificationWorkbench.Models;

namespace Codex.ApiVerificationWorkbench.Services;

public sealed class RequestExecutor
{
    private readonly RequestBodyPlanner _requestBodyPlanner;
    private readonly SuccessExamplePlanner _successExamplePlanner;

    public RequestExecutor(
        RequestBodyPlanner requestBodyPlanner,
        SuccessExamplePlanner successExamplePlanner)
    {
        _requestBodyPlanner = requestBodyPlanner;
        _successExamplePlanner = successExamplePlanner;
    }

    public async Task<ExecuteResponse> ExecuteAsync(ExecuteRequestInput input, ResolveRequestResponse resolved)
    {
        var selectedOperation = SelectOperation(input, resolved);
        var method = (input.Method ?? selectedOperation?.Method ?? "GET").Trim().ToUpperInvariant();
        var path = (input.Path ?? selectedOperation?.Path ?? string.Empty).Trim();
        var stopwatch = Stopwatch.StartNew();
        Uri? finalUri = null;
        BodyPlanResponse? plan = null;
        SuccessExampleResponse? successExample = null;
        var runtimeNotes = new List<string>();
        var proxyMode = "system";
        string? effectiveProxyUrl = null;

        try
        {
            if (string.IsNullOrWhiteSpace(input.BaseUrl))
            {
                throw new InvalidOperationException("テナント URL を入力してください。");
            }

            if (!Uri.TryCreate(AppendSlashIfNeeded(input.BaseUrl.Trim()), UriKind.Absolute, out var baseUri))
            {
                throw new InvalidOperationException("テナント URL の形式が正しくありません。");
            }

            if (string.IsNullOrWhiteSpace(path))
            {
                throw new InvalidOperationException("実行する API パスを入力してください。");
            }

            path = ApplyVariables(path, input.Variables);
            finalUri = new Uri(baseUri, path.TrimStart('/'));
            successExample = _successExamplePlanner.Build(selectedOperation, method, finalUri);

            plan = _requestBodyPlanner.BuildPlan(new BodyPlanInput
            {
                OperationId = input.OperationId,
                RequestText = input.RequestText,
                BaseUrl = input.BaseUrl,
                Method = method,
                Path = path,
                ContentType = input.ContentType,
                BodyFormat = input.BodyFormat,
                Body = input.Body,
                Variables = input.Variables
            }, selectedOperation);

            var preparedBody = ApplyVariables(plan.Body ?? string.Empty, input.Variables);

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

            if (plan.ShouldSendBody)
            {
                request.Content = CreateHttpContent(preparedBody, plan.ContentType, plan.BodyFormat);
            }

            var requestHeaders = BuildHeadersSnapshot(request);
            var requestDebugText = BuildRequestDebugText(method, finalUri, requestHeaders, preparedBody, plan);

            using var client = CreateHttpClient(input, out proxyMode, out effectiveProxyUrl, out var clientNotes);
            runtimeNotes.AddRange(clientNotes);

            using var response = await client.SendAsync(request);
            stopwatch.Stop();

            var responseBody = await response.Content.ReadAsStringAsync();
            var responseHeaders = response.Headers
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
                ResponseHeaders = responseHeaders,
                UsedOperationId = selectedOperation?.Id,
                UsedOperationSummary = selectedOperation?.Summary,
                ErrorType = null,
                ErrorMessage = null,
                RequestContentType = plan.ContentType,
                RequestBodyFormat = plan.BodyFormat,
                RequestBody = preparedBody,
                RequestDebugText = requestDebugText,
                RequestHeaders = requestHeaders,
                ProxyMode = proxyMode,
                ProxyUrl = effectiveProxyUrl,
                BodyRequired = plan.BodyRequired,
                BodySource = plan.BodySource,
                Notes = MergeNotes(BuildExecutionNotes(selectedOperation, path, preparedBody, plan), runtimeNotes),
                SuccessExample = successExample
            };
        }
        catch (Exception exception)
        {
            stopwatch.Stop();
            return BuildErrorResponse(
                method,
                finalUri,
                input,
                selectedOperation,
                plan,
                successExample,
                runtimeNotes,
                proxyMode,
                effectiveProxyUrl,
                stopwatch.ElapsedMilliseconds,
                exception);
        }
    }

    private static ApiOperation? SelectOperation(ExecuteRequestInput input, ResolveRequestResponse resolved)
    {
        var selectedOperation = resolved.Candidates.FirstOrDefault()?.Operation;
        if (selectedOperation is null)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(input.Path) &&
            !string.Equals(selectedOperation.Path, input.Path, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(input.Method) &&
            !string.Equals(selectedOperation.Method, input.Method, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return selectedOperation;
    }

    private static string AppendSlashIfNeeded(string baseUrl)
    {
        return baseUrl.EndsWith('/') ? baseUrl : baseUrl + "/";
    }

    private static HttpClient CreateHttpClient(
        ExecuteRequestInput input,
        out string proxyMode,
        out string? effectiveProxyUrl,
        out List<string> notes)
    {
        notes = [];
        proxyMode = "system";
        effectiveProxyUrl = null;

        var timeoutSeconds = Math.Clamp(input.TimeoutSeconds <= 0 ? 30 : input.TimeoutSeconds, 5, 180);
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli
        };

        if (input.BypassSystemProxy)
        {
            handler.UseProxy = false;
            proxyMode = "disabled";
            notes.Add("Proxy disabled for this request.");
        }
        else if (!string.IsNullOrWhiteSpace(input.ProxyUrl))
        {
            if (!Uri.TryCreate(input.ProxyUrl.Trim(), UriKind.Absolute, out var proxyUri))
            {
                throw new InvalidOperationException("Proxy URL の形式が正しくありません。");
            }

            var proxy = new WebProxy(proxyUri);
            if (input.UseDefaultProxyCredentials)
            {
                proxy.Credentials = CredentialCache.DefaultCredentials;
            }

            handler.UseProxy = true;
            handler.Proxy = proxy;
            proxyMode = "explicit";
            effectiveProxyUrl = proxyUri.ToString();
            notes.Add($"Using explicit proxy: {proxyUri.Host}:{proxyUri.Port}");
        }
        else
        {
            handler.UseProxy = true;
            proxyMode = "system";
            notes.Add("Using system proxy settings.");
        }

        notes.Add($"HTTP timeout: {timeoutSeconds}s");

        return new HttpClient(handler, disposeHandler: true)
        {
            Timeout = TimeSpan.FromSeconds(timeoutSeconds)
        };
    }

    private static HttpContent CreateHttpContent(string body, string contentType, string bodyFormat)
    {
        if (string.Equals(bodyFormat, "base64", StringComparison.OrdinalIgnoreCase))
        {
            if (ContainsPlaceholder(body))
            {
                throw new InvalidOperationException("Base64 body に未設定プレースホルダが残っています。");
            }

            var bytes = Convert.FromBase64String(body.Trim());
            var content = new ByteArrayContent(bytes);
            content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
            return content;
        }

        if (string.Equals(bodyFormat, "multipart", StringComparison.OrdinalIgnoreCase))
        {
            return CreateMultipartContent(body);
        }

        var text = new StringContent(body, Encoding.UTF8);
        text.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        return text;
    }

    private static HttpContent CreateMultipartContent(string body)
    {
        using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
        var boundary = "----CodexBoundary" + Guid.NewGuid().ToString("N");
        var multipart = new MultipartFormDataContent(boundary);

        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (TryCreateFilePart(property, out var fileContent, out var fileName))
            {
                multipart.Add(fileContent, property.Name, fileName);
                continue;
            }

            if (property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
            {
                var json = property.Value.GetRawText();
                multipart.Add(new StringContent(json, Encoding.UTF8, "application/json"), property.Name);
                continue;
            }

            multipart.Add(new StringContent(property.Value.ToString(), Encoding.UTF8), property.Name);
        }

        return multipart;
    }

    private static bool TryCreateFilePart(JsonProperty property, out ByteArrayContent fileContent, out string fileName)
    {
        fileContent = null!;
        fileName = string.Empty;

        if (property.Value.ValueKind != JsonValueKind.Object ||
            !property.Value.TryGetProperty("base64", out var base64Property))
        {
            return false;
        }

        var base64 = base64Property.GetString() ?? string.Empty;
        if (ContainsPlaceholder(base64))
        {
            throw new InvalidOperationException($"{property.Name}.base64 に未設定プレースホルダが残っています。");
        }

        var bytes = Convert.FromBase64String(base64.Trim());
        fileContent = new ByteArrayContent(bytes);
        fileName = property.Value.TryGetProperty("fileName", out var fileNameProperty)
            ? fileNameProperty.GetString() ?? $"{property.Name}.bin"
            : $"{property.Name}.bin";

        var contentType = property.Value.TryGetProperty("contentType", out var contentTypeProperty)
            ? contentTypeProperty.GetString()
            : null;
        fileContent.Headers.ContentType = MediaTypeHeaderValue.Parse(string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType);
        return true;
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

    private static Dictionary<string, string[]> BuildHeadersSnapshot(HttpRequestMessage request)
    {
        var contentHeaders = request.Content?.Headers ?? Enumerable.Empty<KeyValuePair<string, IEnumerable<string>>>();
        return request.Headers
            .Concat(contentHeaders)
            .Append(new KeyValuePair<string, IEnumerable<string>>("Host", new[] { request.RequestUri?.Authority ?? string.Empty }))
            .GroupBy(header => header.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.SelectMany(value => value.Value).ToArray(), StringComparer.OrdinalIgnoreCase);
    }

    private static string BuildRequestDebugText(
        string method,
        Uri finalUri,
        IReadOnlyDictionary<string, string[]> requestHeaders,
        string preparedBody,
        BodyPlanResponse? plan)
    {
        var lines = new List<string>
        {
            $"{method} {finalUri.PathAndQuery} HTTP/1.1"
        };

        foreach (var header in requestHeaders
                     .OrderBy(header => string.Equals(header.Key, "Host", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                     .ThenBy(header => header.Key, StringComparer.OrdinalIgnoreCase))
        {
            foreach (var value in header.Value)
            {
                lines.Add($"{header.Key}: {value}");
            }
        }

        var bodyPreview = BuildRequestBodyPreview(preparedBody, plan, requestHeaders);
        if (!string.IsNullOrWhiteSpace(bodyPreview))
        {
            lines.Add(string.Empty);
            lines.Add(bodyPreview);
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static string BuildRequestBodyPreview(
        string preparedBody,
        BodyPlanResponse? plan,
        IReadOnlyDictionary<string, string[]> requestHeaders)
    {
        if (plan is null || !plan.ShouldSendBody || string.IsNullOrWhiteSpace(preparedBody))
        {
            return string.Empty;
        }

        if (string.Equals(plan.BodyFormat, "multipart", StringComparison.OrdinalIgnoreCase))
        {
            return BuildMultipartPreview(preparedBody, requestHeaders);
        }

        return preparedBody;
    }

    private static string BuildMultipartPreview(string preparedBody, IReadOnlyDictionary<string, string[]> requestHeaders)
    {
        using var document = JsonDocument.Parse(preparedBody);
        var boundary = ExtractBoundary(requestHeaders) ?? "----CodexBoundary";
        var lines = new List<string>();

        foreach (var property in document.RootElement.EnumerateObject())
        {
            lines.Add($"--{boundary}");

            if (property.Value.ValueKind == JsonValueKind.Object &&
                property.Value.TryGetProperty("base64", out var base64Property))
            {
                var fileName = property.Value.TryGetProperty("fileName", out var fileNameProperty)
                    ? fileNameProperty.GetString() ?? $"{property.Name}.bin"
                    : $"{property.Name}.bin";
                var contentType = property.Value.TryGetProperty("contentType", out var contentTypeProperty)
                    ? contentTypeProperty.GetString() ?? "application/octet-stream"
                    : "application/octet-stream";

                lines.Add($"Content-Disposition: form-data; name=\"{property.Name}\"; filename=\"{fileName}\"");
                lines.Add($"Content-Type: {contentType}");
                lines.Add(string.Empty);
                lines.Add(base64Property.GetString() ?? string.Empty);
            }
            else
            {
                lines.Add($"Content-Disposition: form-data; name=\"{property.Name}\"");
                lines.Add(string.Empty);
                lines.Add(property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array
                    ? property.Value.GetRawText()
                    : property.Value.ToString());
            }
        }

        lines.Add($"--{boundary}--");
        return string.Join(Environment.NewLine, lines);
    }

    private static string? ExtractBoundary(IReadOnlyDictionary<string, string[]> requestHeaders)
    {
        if (!requestHeaders.TryGetValue("Content-Type", out var contentTypeValues))
        {
            return null;
        }

        var contentType = contentTypeValues.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(contentType))
        {
            return null;
        }

        var match = Regex.Match(contentType, "boundary=(?:\"(?<boundary>[^\"]+)\"|(?<boundary>[^;]+))", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups["boundary"].Value : null;
    }

    private static List<string> BuildExecutionNotes(ApiOperation? operation, string path, string preparedBody, BodyPlanResponse? plan)
    {
        var notes = new List<string>();

        if (operation is not null)
        {
            notes.Add($"Resolved operation: {operation.Summary}");
        }

        if (plan is not null)
        {
            notes.AddRange(plan.Notes);
        }

        if (Regex.IsMatch(path, "{[^{}]+}"))
        {
            notes.Add("Path contains unresolved placeholders. Provide values in Variables JSON.");
        }

        if (!string.IsNullOrWhiteSpace(preparedBody) && ContainsPlaceholder(preparedBody))
        {
            notes.Add("Body contains unresolved placeholders. Provide values in Variables JSON or edit Body.");
        }

        return notes
            .Where(note => !string.IsNullOrWhiteSpace(note))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static ExecuteResponse BuildErrorResponse(
        string method,
        Uri? finalUri,
        ExecuteRequestInput input,
        ApiOperation? operation,
        BodyPlanResponse? plan,
        SuccessExampleResponse? successExample,
        IReadOnlyCollection<string> runtimeNotes,
        string proxyMode,
        string? effectiveProxyUrl,
        long elapsedMilliseconds,
        Exception exception)
    {
        var preparedBody = plan is null ? string.Empty : ApplyVariables(plan.Body ?? string.Empty, input.Variables);
        var resolvedPath = finalUri?.AbsolutePath ?? input.Path ?? operation?.Path ?? string.Empty;
        var notes = MergeNotes(BuildExecutionNotes(operation, resolvedPath, preparedBody, plan), runtimeNotes);
        var errorType = "unexpected_error";
        var errorMessage = "An unexpected error occurred.";

        switch (exception)
        {
            case InvalidOperationException invalidOperationException:
                errorType = "validation_error";
                errorMessage = invalidOperationException.Message;
                break;
            case FormatException formatException:
                errorType = plan?.BodyFormat.Equals("base64", StringComparison.OrdinalIgnoreCase) == true ||
                            plan?.BodyFormat.Equals("multipart", StringComparison.OrdinalIgnoreCase) == true
                    ? "invalid_binary_body"
                    : "invalid_format";
                errorMessage = "Request body format is invalid.";
                notes.Add(formatException.Message);
                break;
            case TaskCanceledException:
                errorType = "timeout";
                errorMessage = $"接続先 {(finalUri?.Host ?? "unknown host")} への接続がタイムアウトしました。";
                break;
            case HttpRequestException httpRequestException:
                errorType = "network_error";
                errorMessage = BuildHttpRequestErrorMessage(finalUri, httpRequestException);
                break;
        }

        notes.Add($"Exception: {exception.GetType().Name}");

        if (finalUri is not null && finalUri.Host.Contains(".int.", StringComparison.OrdinalIgnoreCase))
        {
            notes.Add("Internal hosts may require corporate network, VPN, or proxy configuration.");
        }

        if (string.Equals(errorType, "timeout", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(proxyMode, "system", StringComparison.OrdinalIgnoreCase))
        {
            notes.Add("If your network requires a proxy, set Proxy URL in request settings.");
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
            RequestContentType = plan?.ContentType ?? input.ContentType ?? string.Empty,
            RequestBodyFormat = plan?.BodyFormat ?? input.BodyFormat,
            RequestBody = preparedBody,
            RequestDebugText = BuildFallbackRequestDebugText(method, finalUri, input, preparedBody, plan),
            RequestHeaders = BuildFallbackRequestHeaders(finalUri, input, plan),
            ProxyMode = proxyMode,
            ProxyUrl = effectiveProxyUrl,
            BodyRequired = plan?.BodyRequired ?? false,
            BodySource = plan?.BodySource ?? "none",
            Notes = notes,
            SuccessExample = successExample
        };
    }

    private static Dictionary<string, string[]> BuildFallbackRequestHeaders(
        Uri? finalUri,
        ExecuteRequestInput input,
        BodyPlanResponse? plan)
    {
        var headers = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["Host"] = new List<string> { finalUri?.Authority ?? string.Empty }
        };

        if (!string.IsNullOrWhiteSpace(input.AccessToken))
        {
            headers["Authorization"] = new List<string> { $"Bearer {input.AccessToken.Trim()}" };
        }

        foreach (var header in input.Headers)
        {
            if (string.Equals(header.Key, "Authorization", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            headers[header.Key] = new List<string> { header.Value };
        }

        if (!string.IsNullOrWhiteSpace(plan?.ContentType) && plan.ShouldSendBody)
        {
            headers["Content-Type"] = new List<string> { plan.ContentType };
        }
        else if (!string.IsNullOrWhiteSpace(input.ContentType))
        {
            headers["Content-Type"] = new List<string> { input.ContentType };
        }

        return headers.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray(), StringComparer.OrdinalIgnoreCase);
    }

    private static string BuildFallbackRequestDebugText(
        string method,
        Uri? finalUri,
        ExecuteRequestInput input,
        string preparedBody,
        BodyPlanResponse? plan)
    {
        var uri = finalUri ?? new Uri("http://localhost/");
        var headers = BuildFallbackRequestHeaders(finalUri, input, plan);
        return BuildRequestDebugText(method, uri, headers, preparedBody, plan);
    }

    private static string BuildHttpRequestErrorMessage(Uri? finalUri, HttpRequestException exception)
    {
        if (exception.InnerException is SocketException socketException)
        {
            return socketException.SocketErrorCode switch
            {
                SocketError.TimedOut => $"接続先 {(finalUri?.Host ?? "unknown host")} への接続がタイムアウトしました。",
                SocketError.HostNotFound => $"接続先 {(finalUri?.Host ?? "unknown host")} の名前解決に失敗しました。",
                SocketError.ConnectionRefused => $"接続先 {(finalUri?.Host ?? "unknown host")} が接続を拒否しました。",
                _ => $"接続先 {(finalUri?.Host ?? "unknown host")} への接続に失敗しました。"
            };
        }

        return exception.Message;
    }

    private static List<string> MergeNotes(IEnumerable<string> baseNotes, IEnumerable<string> runtimeNotes)
    {
        return baseNotes
            .Concat(runtimeNotes)
            .Where(note => !string.IsNullOrWhiteSpace(note))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool ContainsPlaceholder(string value)
    {
        return Regex.IsMatch(value, "\\{[A-Za-z0-9_]+\\}");
    }
}
