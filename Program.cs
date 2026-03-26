using Codex.ApiVerificationWorkbench.Models;
using Codex.ApiVerificationWorkbench.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
    options.SerializerOptions.WriteIndented = true;
});

builder.Services.AddHttpClient();
builder.Services.AddSingleton<ManualCatalogStore>();
builder.Services.AddSingleton<RequestResolver>();
builder.Services.AddSingleton<RequestBodyPlanner>();
builder.Services.AddSingleton<RequestExecutor>();
builder.Services.AddSingleton<CoveragePlanner>();

var app = builder.Build();

var catalogStore = app.Services.GetRequiredService<ManualCatalogStore>();

if (args.Contains("--refresh-manuals", StringComparer.OrdinalIgnoreCase))
{
    await catalogStore.RefreshAsync();
    return;
}

await catalogStore.EnsureLoadedAsync();

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/api/overview", (ManualCatalogStore store) =>
{
    var catalog = store.Current;
    return Results.Ok(new
    {
        generatedAtUtc = catalog.Metadata.GeneratedAtUtc,
        totalManualPages = catalog.ManualPages.Count,
        totalOperations = catalog.ApiOperations.Count,
        pageCounts = catalog.Metadata.PageCounts,
        manuals = catalog.Metadata.Manuals
    });
});

app.MapGet("/api/catalog", (ManualCatalogStore store) =>
{
    return Results.Ok(store.Current.ApiOperations
        .OrderBy(operation => operation.ManualName)
        .ThenBy(operation => operation.Path)
        .ThenBy(operation => operation.Method));
});

app.MapPost("/api/resolve", (ResolveRequestInput input, RequestResolver resolver) =>
{
    var response = resolver.Resolve(input);
    return Results.Ok(response);
});

app.MapPost("/api/execute", async (ExecuteRequestInput input, RequestResolver resolver, RequestExecutor executor) =>
{
    var resolved = resolver.Resolve(new ResolveRequestInput
    {
        OperationId = input.OperationId,
        RequestText = input.RequestText,
        ExplicitMethod = input.Method,
        ExplicitPath = input.Path,
        Top = 5
    });

    var response = await executor.ExecuteAsync(input, resolved);
    return Results.Ok(response);
});

app.MapPost("/api/body-plan", (BodyPlanInput input, RequestResolver resolver, RequestBodyPlanner planner) =>
{
    var resolved = resolver.Resolve(new ResolveRequestInput
    {
        OperationId = input.OperationId,
        RequestText = input.RequestText,
        ExplicitMethod = input.Method,
        ExplicitPath = input.Path,
        Top = 5
    });

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

    var response = planner.BuildPlan(input, selectedOperation);
    return Results.Ok(response);
});

app.MapPost("/api/coverage-plan", (CoveragePlanInput input, CoveragePlanner planner) =>
{
    var response = planner.Build(input);
    return Results.Ok(response);
});

app.MapPost("/api/admin/refresh-manuals", async (ManualCatalogStore store) =>
{
    await store.RefreshAsync();
    return Results.Ok(new { message = "manual catalog refreshed" });
});

app.Run();
