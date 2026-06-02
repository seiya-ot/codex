using System.Text.Json;
using Codex.ApiVerificationWorkbench.Models;

namespace Codex.ApiVerificationWorkbench.Services;

public sealed class ManualCatalogStore
{
    private readonly IWebHostEnvironment _environment;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<ManualCatalogStore> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public ManualCatalogStore(
        IWebHostEnvironment environment,
        IHttpClientFactory httpClientFactory,
        ILogger<ManualCatalogStore> logger,
        ILoggerFactory loggerFactory)
    {
        _environment = environment;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _loggerFactory = loggerFactory;
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
        var previous = Current;
        var temporaryPath = Path.Combine(CatalogDirectory, $"{Path.GetFileName(CatalogPath)}.{Guid.NewGuid():N}.tmp");

        Directory.CreateDirectory(CatalogDirectory);

        try
        {
            var crawler = new ManualCrawler(
                _httpClientFactory.CreateClient(nameof(ManualCrawler)),
                _loggerFactory.CreateLogger<ManualCrawler>());
            var catalog = await crawler.BuildCatalogAsync(cancellationToken);

            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 8192,
                FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(stream, catalog, _jsonOptions, cancellationToken);
            }

            File.Move(temporaryPath, CatalogPath, overwrite: true);
            Current = catalog;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Current = previous;
            DeleteTemporaryCatalog(temporaryPath);
            _logger.LogInformation("Manual catalog refresh was canceled. Keeping the previous catalog.");
            throw;
        }
        catch (Exception exception)
        {
            Current = previous;
            DeleteTemporaryCatalog(temporaryPath);
            _logger.LogError(exception, "Manual catalog refresh failed. Keeping the previous catalog.");
            throw;
        }
    }

    private void DeleteTemporaryCatalog(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed to delete temporary manual catalog file {Path}.", path);
        }
    }
}
