using System.Text.Json;
using Codex.ApiVerificationWorkbench.Models;

namespace Codex.ApiVerificationWorkbench.Services;

public sealed class SuccessExamplePlanner
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public SuccessExampleResponse? Build(ApiOperation? operation, string method, Uri? finalUri)
    {
        var normalizedMethod = string.IsNullOrWhiteSpace(method)
            ? "GET"
            : method.Trim().ToUpperInvariant();
        var normalizedPath = finalUri?.AbsolutePath ?? operation?.Path ?? string.Empty;

        if (string.IsNullOrWhiteSpace(normalizedPath))
        {
            return null;
        }

        if (string.Equals(normalizedMethod, "GET", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(normalizedPath, "/api/v24.10/memberAssets", StringComparison.OrdinalIgnoreCase))
        {
            return BuildMemberAssetsExample(finalUri);
        }

        return null;
    }

    private static SuccessExampleResponse BuildMemberAssetsExample(Uri? finalUri)
    {
        var queryParameters = ParseQueryParameters(finalUri);
        var assetId = queryParameters.TryGetValue("assetId", out var requestedAssetId) &&
                      !string.IsNullOrWhiteSpace(requestedAssetId)
            ? requestedAssetId
            : "asset-001";
        var assetName = string.Equals(assetId, "asset-001", StringComparison.OrdinalIgnoreCase)
            ? "Google Workspace"
            : $"Business Asset {assetId}";
        var responseBody = new
        {
            members = new[]
            {
                new
                {
                    date = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd"),
                    id = "member-0001",
                    type = "member",
                    name = "山田 太郎",
                    email = "taro.yamada@example.jp",
                    identificationNumber = "A000123",
                    companies = new[]
                    {
                        new
                        {
                            id = "company-001",
                            type = "company",
                            name = "IIJ Sample Company",
                            code = "COMP001",
                            depth = 1
                        }
                    },
                    organizations = new[]
                    {
                        new
                        {
                            id = "org-001",
                            type = "organization",
                            name = "Corporate IT",
                            code = "ORG001",
                            depth = 2
                        }
                    },
                    offices = new[]
                    {
                        new
                        {
                            id = "office-001",
                            type = "office",
                            name = "Tokyo Head Office",
                            code = "OFF001",
                            depth = 3
                        }
                    },
                    projects = new[]
                    {
                        new
                        {
                            id = "project-001",
                            type = "project",
                            name = "Identity Modernization",
                            code = "PRJ001",
                            depth = 4
                        }
                    },
                    assets = new[]
                    {
                        new
                        {
                            id = assetId,
                            type = "asset",
                            name = assetName,
                            attributes = new Dictionary<string, string>
                            {
                                ["employeeCode"] = "A000123",
                                ["displayName"] = "山田 太郎",
                                ["primaryEmail"] = "taro.yamada@example.jp"
                            },
                            allocations = new[]
                            {
                                new
                                {
                                    id = "allocation-001",
                                    name = "標準利用者",
                                    items = new[]
                                    {
                                        new
                                        {
                                            id = "allocation-item-001",
                                            name = "一般ユーザー",
                                            code = "general-user"
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
        };

        var notes = new List<string>
        {
            "マニュアル記載の memberAssets レスポンス項目に基づく成功時サンプルです。",
            "実データではないため、メンバー数・属性名・割当内容は対象テナントの実レスポンスと異なります。"
        };

        if (!queryParameters.ContainsKey("assetId"))
        {
            notes.Add("assetId を未指定にした例です。実運用では複数業務アセット分の members が返る場合があります。");
        }

        return new SuccessExampleResponse
        {
            StatusCode = 200,
            ContentType = "application/json",
            Body = JsonSerializer.Serialize(responseBody, JsonOptions),
            Source = "manual_schema",
            Notes = notes
        };
    }

    private static Dictionary<string, string> ParseQueryParameters(Uri? finalUri)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (finalUri is null || string.IsNullOrWhiteSpace(finalUri.Query))
        {
            return values;
        }

        var segments = finalUri.Query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var segment in segments)
        {
            var parts = segment.Split('=', 2);
            var key = Uri.UnescapeDataString(parts[0]);
            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            var value = parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : string.Empty;
            values[key] = value;
        }

        return values;
    }
}
