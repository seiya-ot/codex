using System.Text.Json;
using System.Text.RegularExpressions;
using Codex.ApiVerificationWorkbench.Models;

namespace Codex.ApiVerificationWorkbench.Services;

public sealed partial class RequestBodyPlanner
{
    private readonly AttributeIdCatalog _attributeIdCatalog;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public RequestBodyPlanner(AttributeIdCatalog attributeIdCatalog)
    {
        _attributeIdCatalog = attributeIdCatalog;
    }

    public BodyPlanResponse BuildPlan(BodyPlanInput input, ApiOperation? operation)
    {
        var method = (input.Method ?? operation?.Method ?? "GET").Trim().ToUpperInvariant();
        var path = (input.Path ?? operation?.Path ?? string.Empty).Trim();
        var notes = new List<string>();

        if (string.IsNullOrWhiteSpace(path))
        {
            return new BodyPlanResponse
            {
                Method = method,
                Path = path,
                ContentType = input.ContentType?.Trim() ?? "application/json",
                BodyFormat = string.IsNullOrWhiteSpace(input.BodyFormat) ? "json" : input.BodyFormat,
                BodyRequired = false,
                ShouldSendBody = !string.IsNullOrWhiteSpace(input.Body),
                BodyGenerated = false,
                BodySource = string.IsNullOrWhiteSpace(input.Body) ? "none" : "user",
                Body = ApplyDefaultAttributeId(input.Body ?? string.Empty, input.RequestText, input.Variables, notes),
                Notes = ["Path が未指定のため、body の自動判定を行いませんでした。"]
            };
        }

        var tenantProfile = DetectTenantProfile(input.BaseUrl);
        var template = DetermineTemplate(method, path, tenantProfile);
        var hasUserBody = !string.IsNullOrWhiteSpace(input.Body);

        var contentType = template.ContentType;
        if (hasUserBody && !string.IsNullOrWhiteSpace(input.ContentType))
        {
            contentType = input.ContentType.Trim();
        }

        var bodyFormat = template.BodyFormat;
        if (hasUserBody && !string.IsNullOrWhiteSpace(input.BodyFormat))
        {
            bodyFormat = input.BodyFormat.Trim();
        }

        if (hasUserBody)
        {
            notes.AddRange(template.Notes);
            notes.Add("入力済みの body をそのまま利用します。");

            return new BodyPlanResponse
            {
                Method = method,
                Path = path,
                ContentType = contentType,
                BodyFormat = bodyFormat,
                BodyRequired = template.BodyRequired,
                ShouldSendBody = true,
                BodyGenerated = false,
                BodySource = "user",
                Body = ApplyDefaultAttributeId(input.Body ?? string.Empty, input.RequestText, input.Variables, notes),
                Notes = notes
            };
        }

        if (!template.BodyRequired)
        {
            notes.AddRange(template.Notes);

            return new BodyPlanResponse
            {
                Method = method,
                Path = path,
                ContentType = contentType,
                BodyFormat = bodyFormat,
                BodyRequired = false,
                ShouldSendBody = false,
                BodyGenerated = false,
                BodySource = "none",
                Body = string.Empty,
                Notes = notes
            };
        }

        var body = template.BodyFactory();
        body = ApplyVariables(body, input.Variables);
        body = ApplyDefaultAttributeId(body, input.RequestText, input.Variables, notes);
        notes.AddRange(template.Notes);
        notes.Add("必須項目を含むテンプレート body を自動生成しました。必要に応じて置換してください。");

        return new BodyPlanResponse
        {
            Method = method,
            Path = path,
            ContentType = contentType,
            BodyFormat = bodyFormat,
            BodyRequired = true,
            ShouldSendBody = true,
            BodyGenerated = true,
            BodySource = "generated",
            Body = body,
            Notes = notes
        };
    }

    private string ApplyDefaultAttributeId(
        string body,
        string? requestText,
        IReadOnlyDictionary<string, string> variables,
        ICollection<string> notes)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return body;
        }

        var hasAttributeIdPlaceholder = ContainsPlaceholder(body, "attributeId");
        var hasAttributeIdJsonField = body.Contains("\"attributeId\"", StringComparison.OrdinalIgnoreCase);
        if (!hasAttributeIdPlaceholder && !hasAttributeIdJsonField)
        {
            return body;
        }

        if (variables.TryGetValue("attributeId", out var attributeId) && !string.IsNullOrWhiteSpace(attributeId))
        {
            var normalizedAttributeId = attributeId.Trim();
            if (_attributeIdCatalog.ContainsAttributeId(normalizedAttributeId))
            {
                body = ApplyVariables(body, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["attributeId"] = normalizedAttributeId
                });
                if (hasAttributeIdJsonField)
                {
                    body = EnsureJsonAttributeIdFromCsv(body, normalizedAttributeId, notes);
                }

                return body;
            }

            notes.Add($"Variables attributeId is not found in iga_assetId.csv. fallback to csv value: {normalizedAttributeId}");
        }

        var inferred = _attributeIdCatalog.InferFromRequestText(requestText);
        if (inferred is not null)
        {
            notes.Add($"attributeId inferred from request text: {inferred.AttributeName} -> {inferred.AttributeId}");
            body = ApplyVariables(body, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["attributeId"] = inferred.AttributeId
            });
            if (hasAttributeIdJsonField)
            {
                body = EnsureJsonAttributeIdFromCsv(body, inferred.AttributeId, notes);
            }

            return body;
        }

        var defaultAttributeId = _attributeIdCatalog.GetDefaultAttributeId();
        if (string.IsNullOrWhiteSpace(defaultAttributeId))
        {
            return body;
        }

        notes.Add($"attributeId を iga_assetId.csv から自動補完しました: {defaultAttributeId}");
        body = ApplyVariables(body, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["attributeId"] = defaultAttributeId
        });
        if (hasAttributeIdJsonField)
        {
            body = EnsureJsonAttributeIdFromCsv(body, defaultAttributeId, notes);
        }

        return body;
    }

    private string EnsureJsonAttributeIdFromCsv(string body, string csvAttributeId, ICollection<string> notes)
    {
        var replacedAny = false;
        var changedAny = false;

        var replaced = Regex.Replace(
            body,
            "\"attributeId\"\\s*:\\s*\"(?<value>[^\"]*)\"",
            match =>
            {
                replacedAny = true;
                var current = match.Groups["value"].Value;
                var isPlaceholder = current.Contains("{attributeId}", StringComparison.OrdinalIgnoreCase) ||
                                    current.Contains("<attributeId>", StringComparison.OrdinalIgnoreCase);
                if (string.IsNullOrWhiteSpace(current) || isPlaceholder || !_attributeIdCatalog.ContainsAttributeId(current))
                {
                    changedAny = true;
                    return match.Value.Replace(current, csvAttributeId, StringComparison.Ordinal);
                }

                return match.Value;
            },
            RegexOptions.IgnoreCase);

        if (replacedAny && changedAny)
        {
            notes.Add($"request body attributeId replaced by csv value: {csvAttributeId}");
        }

        return replaced;
    }

    private static bool ContainsPlaceholder(string template, string variableName)
    {
        return template.Contains("{" + variableName + "}", StringComparison.OrdinalIgnoreCase) ||
               template.Contains("<" + variableName + ">", StringComparison.OrdinalIgnoreCase);
    }

    private static string ApplyVariables(string template, IReadOnlyDictionary<string, string> variables)
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

    private static BodyTemplate DetermineTemplate(string method, string path, string tenantProfile)
    {
        if (method is "GET" or "DELETE" or "HEAD" or "OPTIONS")
        {
            return new BodyTemplate(false, "application/json", "json", static () => string.Empty)
            {
                Notes = [$"{method} {path} は request body なしで実行します。"]
            };
        }

        if (IsAvatarEndpoint(path))
        {
            return new BodyTemplate(true, "multipart/form-data", "multipart", BuildAvatarTemplate)
            {
                Notes =
                [
                    "アバター更新 API は multipart/form-data の file パートが必要です。",
                    "Variables JSON に fileBase64 / changeDate を指定するとテンプレートの置換に利用できます。"
                ]
            };
        }

        if (IsQueryEndpoint(path))
        {
            return new BodyTemplate(true, "application/json", "json", BuildQueryTemplate)
            {
                Notes =
                [
                    "クエリ API は query オブジェクトを含む JSON body が必要です。",
                    "attributeId は Variables JSON または body 編集で置き換えてください。"
                ]
            };
        }

        if (IsAuthorityTaskPatch(method, path))
        {
            return new BodyTemplate(true, "application/json", "json", BuildAuthorityTaskTemplate)
            {
                Notes =
                [
                    "タスク更新 API は status を含む JSON body が必要です。",
                    "taskStatus には done などの値を設定してください。"
                ]
            };
        }

        if (IsSyncAssetsEndpoint(path))
        {
            return new BodyTemplate(true, "application/json", "json", BuildSyncAssetsTemplate)
            {
                Notes =
                [
                    "sync-assets API は同期対象を表す JSON body が必要です。",
                    "assetId を Variables JSON または body 編集で置き換えてください。"
                ]
            };
        }

        if (IsMemberAssetsDeltaExportEndpoint(path))
        {
            return new BodyTemplate(true, "application/json", "json", BuildMemberAssetsDeltaExportTemplate)
            {
                Notes =
                [
                    "差分 CSV export API は対象 asset と比較日を表す JSON body が必要です。",
                    "beforeSystemDate / afterSystemDate などを Variables JSON で指定できます。"
                ]
            };
        }

        if (IsMemberAssetsExportEndpoint(path))
        {
            return new BodyTemplate(true, "application/json", "json", BuildMemberAssetsExportTemplate)
            {
                Notes =
                [
                    "CSV export API は対象 asset を表す JSON body が必要です。",
                    "assetId と date を Variables JSON または body 編集で置き換えてください。"
                ]
            };
        }

        if (IsTransactionsImportEndpoint(path))
        {
            return new BodyTemplate(true, "application/json", "json", BuildTransactionImportTemplate)
            {
                Notes =
                [
                    "トランザクション import API は options オブジェクトを含む JSON body が必要です。",
                    "mapping や changeDate を Variables JSON で置き換えてください。"
                ]
            };
        }

        if (IsImportEndpoint(path))
        {
            return new BodyTemplate(true, "application/json", "json", BuildImportTemplate)
            {
                Notes =
                [
                    tenantProfile == "yesod"
                        ? "YESOD import API のサンプルは JSON 形式で生成しました。必要に応じて multipart 形式に切り替えてください。"
                        : "IIJ import API は csv と options を含む JSON body を想定しています。",
                    "csvData と mapping を Variables JSON または body 編集で置き換えてください。"
                ]
            };
        }

        if (method is "POST" or "PUT" or "PATCH")
        {
            return new BodyTemplate(false, "application/json", "json", static () => string.Empty)
            {
                Notes = [$"{method} {path} の body スキーマを特定できなかったため、自動生成は行いませんでした。"]
            };
        }

        return new BodyTemplate(false, "application/json", "json", static () => string.Empty)
        {
            Notes = [$"{method} {path} は request body なしで実行します。"]
        };
    }

    private static string BuildQueryTemplate()
    {
        return Serialize(new
        {
            query = new
            {
                type = "AttributeQuery",
                condition = new
                {
                    attributeId = "{attributeId}",
                    comparisonOperator = "ISNOTNULL",
                    referenceIds = Array.Empty<string>()
                },
                onlyLatestData = false
            },
            attributeSelector = new[] { "{attributeId}" },
            groupAttributeSelector = Array.Empty<string>()
        });
    }

    private static string BuildImportTemplate()
    {
        return Serialize(new
        {
            csv = "{csvData}",
            options = new
            {
                mapping = "{mapping}",
                applicationName = "API検証",
                changeDate = "{changeDate}"
            }
        });
    }

    private static string BuildTransactionImportTemplate()
    {
        return Serialize(new
        {
            options = new
            {
                mapping = "{mapping}",
                applicationName = "API検証",
                changeDate = "{changeDate}"
            }
        });
    }

    private static string BuildAuthorityTaskTemplate()
    {
        return Serialize(new
        {
            status = "{taskStatus}"
        });
    }

    private static string BuildSyncAssetsTemplate()
    {
        return Serialize(new
        {
            assetId = "{assetId}",
            force = false
        });
    }

    private static string BuildMemberAssetsExportTemplate()
    {
        return Serialize(new
        {
            assetId = "{assetId}",
            date = "{date}",
            csvOptions = new
            {
                delimiter = "COMMA",
                header = true,
                lineEnding = "CRLF",
                encoding = "UTF-8"
            }
        });
    }

    private static string BuildMemberAssetsDeltaExportTemplate()
    {
        return Serialize(new
        {
            assetId = "{assetId}",
            beforeSystemDate = "{beforeSystemDate}",
            afterSystemDate = "{afterSystemDate}",
            beforeValidDate = "{beforeValidDate}",
            afterValidDate = "{afterValidDate}",
            changeTypeFilter = new[] { "added", "updated", "removed" },
            csvOptions = new
            {
                delimiter = "COMMA",
                header = true,
                lineEnding = "CRLF",
                encoding = "UTF-8"
            }
        });
    }

    private static string BuildAvatarTemplate()
    {
        return Serialize(new
        {
            changeDate = "{changeDate}",
            file = new
            {
                fileName = "avatar.png",
                contentType = "image/png",
                base64 = "{fileBase64}"
            }
        });
    }

    private static string Serialize<T>(T value)
    {
        return JsonSerializer.Serialize(value, JsonOptions);
    }

    private static bool IsQueryEndpoint(string path)
    {
        return QueryEndpointRegex().IsMatch(path);
    }

    private static bool IsImportEndpoint(string path)
    {
        return path.Contains("/import", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsTransactionsImportEndpoint(string path)
    {
        return path.Contains("/transactions/import", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsAvatarEndpoint(string path)
    {
        return path.EndsWith("/avatar", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsAuthorityTaskPatch(string method, string path)
    {
        return string.Equals(method, "PATCH", StringComparison.OrdinalIgnoreCase) &&
               path.Contains("/authorityTasks/", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSyncAssetsEndpoint(string path)
    {
        return path.EndsWith("/sync-assets", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsMemberAssetsExportEndpoint(string path)
    {
        return path.EndsWith("/memberAssets/export", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsMemberAssetsDeltaExportEndpoint(string path)
    {
        return path.EndsWith("/memberAssetsDelta/export", StringComparison.OrdinalIgnoreCase);
    }

    private static string DetectTenantProfile(string baseUrl)
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri))
        {
            return "unknown";
        }

        if (uri.Host.Contains("yesod.io", StringComparison.OrdinalIgnoreCase))
        {
            return "yesod";
        }

        if (uri.Host.Contains("igms.iij.jp", StringComparison.OrdinalIgnoreCase) ||
            uri.Host.EndsWith(".iij.jp", StringComparison.OrdinalIgnoreCase))
        {
            return "iij";
        }

        return "unknown";
    }

    private sealed record BodyTemplate(bool BodyRequired, string ContentType, string BodyFormat, Func<string> BodyFactory)
    {
        public List<string> Notes { get; init; } = [];
    }

    [GeneratedRegex("(^|/)query($|/)", RegexOptions.IgnoreCase)]
    private static partial Regex QueryEndpointRegex();
}
