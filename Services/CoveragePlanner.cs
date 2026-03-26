using Codex.ApiVerificationWorkbench.Models;

namespace Codex.ApiVerificationWorkbench.Services;

public sealed class CoveragePlanner
{
    private readonly ManualCatalogStore _catalogStore;

    public CoveragePlanner(ManualCatalogStore catalogStore)
    {
        _catalogStore = catalogStore;
    }

    public CoveragePlanResponse Build(CoveragePlanInput input)
    {
        var operations = _catalogStore.Current.ApiOperations.AsEnumerable();
        if (input.OperationIds is { Count: > 0 })
        {
            var selected = new HashSet<string>(input.OperationIds, StringComparer.OrdinalIgnoreCase);
            operations = operations.Where(operation => selected.Contains(operation.Id));
        }

        var items = new List<CoveragePlanItem>();

        foreach (var operation in operations.OrderBy(operation => operation.Path).ThenBy(operation => operation.Method))
        {
            var reasons = new List<string>();
            var status = "ready";

            foreach (var parameter in operation.PathParameters)
            {
                if (!input.Variables.TryGetValue(parameter.Name, out var value) || string.IsNullOrWhiteSpace(value))
                {
                    status = "needs_input";
                    reasons.Add($"path parameter `{parameter.Name}` が不足しています。");
                }
            }

            if (operation.RequiresManualFixture)
            {
                status = status == "needs_input" ? status : "manual_fixture";
                reasons.Add("実データまたは手動フィクスチャを用意して検証する前提です。");
            }

            items.Add(new CoveragePlanItem
            {
                OperationId = operation.Id,
                Summary = operation.Summary,
                Method = operation.Method,
                Path = operation.Path,
                Status = status,
                Reasons = reasons
            });
        }

        return new CoveragePlanResponse
        {
            TotalOperations = items.Count,
            ReadyCount = items.Count(item => item.Status == "ready"),
            NeedsInputCount = items.Count(item => item.Status == "needs_input"),
            ManualFixtureCount = items.Count(item => item.Status == "manual_fixture"),
            Items = items
        };
    }
}
