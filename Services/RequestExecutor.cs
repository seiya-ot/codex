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
    private readonly RequestBodyPlanner _requestBodyPlanner;

    public RequestExecutor(IHttpClientFactory httpClientFactory, RequestBodyPlanner requestBodyPlanner)
    {
        _httpClientFactory = httpClientFactory;
        _requestBodyPlanner = requestBodyPlanner;
    }

    public async Task<ExecuteResponse> ExecuteAsync(ExecuteRequestInput input, ResolveRequestResponse resolved)
    {
        var selectedOperation = SelectOperation(input, resolved);
        var method = (input.Method ?? selectedOperation?.Method ?? "GET").Trim().ToUpperInvariant();
        var path = (input.Path ?? selectedOperation?.Path ?? string.Empty).Trim();
        var stopwatch = Stopwatch.StartNew();
        Uri? finalUri = null;
        BodyPlanResponse? plan = null;

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
                throw new InvalidOperationException("実行する API パスを指定してください。");
            }

            path = ApplyVariables(path, input.Variables);
            finalUri = new Uri(baseUri, path.TrimStart('/'));

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
                RequestContentType = plan.ContentType,
                RequestBodyFormat = plan.BodyFormat,
                RequestBody = preparedBody,
                BodyRequired = plan.BodyRequired,
                BodySource = plan.BodySource,
                Notes = BuildExecutionNotes(selectedOperation, path, preparedBody, plan)
            };
        }
        catch (Exception exception)
        {
            stopwatch.Stop();
            return BuildErrorResponse(method, finalUri, input, selectedOperation, plan, stopwatch.ElapsedMilliseconds, exception);
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

    private static HttpContent CreateHttpContent(string body, string contentType, string bodyFormat)
    {
        if (string.Equals(bodyFormat, "base64", StringComparison.OrdinalIgnoreCase))
        {
            if (ContainsPlaceholder(body))
            {
                throw new InvalidOperationException("Base64 body に未置換のプレースホルダがあります。");
            }

            var bytes = Convert.FromBase64String(body.Trim());
            var binaryContent = new ByteArrayContent(bytes);
            binaryContent.Headers.ContentType = new MediaTypeHeaderValue(contentType);
            return binaryContent;
        }

        if (string.Equals(bodyFormat, "multipart", StringComparison.OrdinalIgnoreCase))
        {
            return CreateMultipartContent(body);
        }

        var content = new StringContent(body, Encoding.UTF8);
        content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        return content;
    }

    private static HttpContent CreateMultipartContent(string body)
    {
        using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
        var multipart = new MultipartFormDataContent();

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
            throw new InvalidOperationException($"{property.Name}.base64 に未置換のプレースホルダがあります。");
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
            notes.Add("Path に未置換のプレースホルダがあります。Variables JSON を確認してください。");
        }

        if (!string.IsNullOrWhiteSpace(preparedBody) && ContainsPlaceholder(preparedBody))
        {
            notes.Add("Body に未置換のプレースホルダがあります。必要なら Variables JSON または body を編集してください。");
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
        long elapsedMilliseconds,
        Exception exception)
    {
        var preparedBody = plan is null ? string.Empty : ApplyVariables(plan.Body ?? string.Empty, input.Variables);
        var resolvedPath = finalUri?.AbsolutePath ?? input.Path ?? operation?.Path ?? string.Empty;
        var notes = BuildExecutionNotes(operation, resolvedPath, preparedBody, plan);
        var errorType = "unexpected_error";
        var errorMessage = "想定外のエラーが発生しました。";

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
                errorMessage = "body の形式が不正です。";
                notes.Add(formatException.Message);
                break;
            case TaskCanceledException:
                errorType = "timeout";
                errorMessage = "接続または応答がタイムアウトしました。対象テナントへの到達性を確認してください。";
                break;
            case HttpRequestException httpRequestException:
                errorType = "network_error";
                errorMessage = BuildHttpRequestErrorMessage(finalUri, httpRequestException);
                break;
        }

        notes.Add($"Exception: {exception.GetType().Name}");

        if (finalUri is not null && finalUri.Host.Contains(".int.", StringComparison.OrdinalIgnoreCase))
        {
            notes.Add("対象ホストは社内ネットワークや VPN の接続が必要な可能性があります。");
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
            BodyRequired = plan?.BodyRequired ?? false,
            BodySource = plan?.BodySource ?? "none",
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

    private static bool ContainsPlaceholder(string value)
    {
        return Regex.IsMatch(value, "\\{[A-Za-z0-9_]+\\}");
    }
}
