namespace Codex.ApiVerificationWorkbench.Models;

public sealed class ResolveRequestInput
{
    public string? OperationId { get; set; }
    public string? RequestText { get; set; }
    public string? ExplicitMethod { get; set; }
    public string? ExplicitPath { get; set; }
    public int Top { get; set; } = 8;
}

public sealed class ResolveRequestResponse
{
    public string NormalizedQuery { get; set; } = string.Empty;
    public string? MethodHint { get; set; }
    public string? PathHint { get; set; }
    public List<ResolvedCandidate> Candidates { get; set; } = [];
}

public sealed class ResolvedCandidate
{
    public ApiOperation Operation { get; set; } = new();
    public int Score { get; set; }
    public List<string> Reasons { get; set; } = [];
}

public sealed class ExecuteRequestInput
{
    public string? OperationId { get; set; }
    public string? RequestText { get; set; }
    public string BaseUrl { get; set; } = string.Empty;
    public string AccessToken { get; set; } = string.Empty;
    public string? Method { get; set; }
    public string? Path { get; set; }
    public string? ContentType { get; set; }
    public string BodyFormat { get; set; } = "json";
    public string? Body { get; set; }
    public Dictionary<string, string> Variables { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> Headers { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class ExecuteResponse
{
    public string Method { get; set; } = string.Empty;
    public string FinalUrl { get; set; } = string.Empty;
    public int StatusCode { get; set; }
    public bool IsSuccessStatusCode { get; set; }
    public long ElapsedMilliseconds { get; set; }
    public string ResponseBody { get; set; } = string.Empty;
    public Dictionary<string, string[]> ResponseHeaders { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string? UsedOperationId { get; set; }
    public string? UsedOperationSummary { get; set; }
    public List<string> Notes { get; set; } = [];
}

public sealed class CoveragePlanInput
{
    public List<string>? OperationIds { get; set; }
    public Dictionary<string, string> Variables { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class CoveragePlanResponse
{
    public int TotalOperations { get; set; }
    public int ReadyCount { get; set; }
    public int NeedsInputCount { get; set; }
    public int ManualFixtureCount { get; set; }
    public List<CoveragePlanItem> Items { get; set; } = [];
}

public sealed class CoveragePlanItem
{
    public string OperationId { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public string Method { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public List<string> Reasons { get; set; } = [];
}
