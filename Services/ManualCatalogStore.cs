using System.Text.Json;
using Codex.ApiVerificationWorkbench.Models;

namespace Codex.ApiVerificationWorkbench.Services;

public sealed class ManualCatalogStore
{
    private readonly IWebHostEnvironment _environment;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public ManualCatalogStore(IWebHostEnvironment environment, IHttpClientFactory httpClientFactory)
    {
        _environment = environment;
        _httpClientFactory = httpClientFactory;
    }

    public CatalogData Current { get; private set; } = new();

    private string CatalogDirectory => Path.Combine(_environment.ContentRootPath, "Data");
    private string CatalogPath => Path.Combine(CatalogDirectory, "manual-catalog.json");

    public async Task EnsureLoadedAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (Current.ApiOperations.Count > 0)
            {
                return;
            }

            if (!File.Exists(CatalogPath))
            {
                await RefreshInternalAsync(cancellationToken);
                return;
            }

            await using var stream = File.OpenRead(CatalogPath);
            var catalog = await JsonSerializer.DeserializeAsync<CatalogData>(stream, _jsonOptions, cancellationToken);
            Current = catalog ?? new CatalogData();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await RefreshInternalAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task RefreshInternalAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(CatalogDirectory);

        var crawler = new ManualCrawler(_httpClientFactory.CreateClient(nameof(ManualCrawler)));
        var catalog = await crawler.BuildCatalogAsync(cancellationToken);

        await using var stream = File.Create(CatalogPath);
        await JsonSerializer.SerializeAsync(stream, catalog, _jsonOptions, cancellationToken);
        Current = catalog;
    }
}
