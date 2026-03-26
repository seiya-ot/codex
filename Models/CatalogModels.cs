namespace Codex.ApiVerificationWorkbench.Models;

public sealed class CatalogData
{
    public CatalogMetadata Metadata { get; set; } = new();
    public List<ManualPage> ManualPages { get; set; } = [];
    public List<ApiOperation> ApiOperations { get; set; } = [];
}

public sealed class CatalogMetadata
{
    public DateTimeOffset GeneratedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public Dictionary<string, int> PageCounts { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<ManualDescriptor> Manuals { get; set; } = [];
}

public sealed class ManualDescriptor
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string RootUrl { get; set; } = string.Empty;
}

public sealed class ManualPage
{
    public string Id { get; set; } = string.Empty;
    public string ManualId { get; set; } = string.Empty;
    public string ManualName { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string PlainText { get; set; } = string.Empty;
    public string Excerpt { get; set; } = string.Empty;
    public List<string> LinkedUrls { get; set; } = [];
    public List<string> Tags { get; set; } = [];
}

public sealed class ApiOperation
{
    public string Id { get; set; } = string.Empty;
    public string ManualId { get; set; } = string.Empty;
    public string ManualName { get; set; } = string.Empty;
    public string SourcePageUrl { get; set; } = string.Empty;
    public string SourcePageTitle { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public string Method { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public List<string> Aliases { get; set; } = [];
    public List<string> SearchKeywords { get; set; } = [];
    public List<ParameterTemplate> PathParameters { get; set; } = [];
    public string? SampleBody { get; set; }
    public string? SampleContentType { get; set; }
    public List<string> Notes { get; set; } = [];
    public bool RequiresManualFixture { get; set; }
}

public sealed class ParameterTemplate
{
    public string Name { get; set; } = string.Empty;
    public string Kind { get; set; } = "path";
    public bool Required { get; set; } = true;
    public string Placeholder { get; set; } = string.Empty;
    public string? Description { get; set; }
}
