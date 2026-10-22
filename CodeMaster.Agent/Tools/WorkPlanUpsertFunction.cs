using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodeMaster.Agent.Contracts;
using CodeMaster.Agent.Serialization;
using CodeMaster.Agent.Services;
using Microsoft.Extensions.AI;

namespace CodeMaster.Agent.Tools;

/// <summary>
/// Validate create/update identity before the typed function binder converts Int64 fields.
/// Both server and LocalAgent use this boundary; no database state or version is invented here.
/// </summary>
internal sealed class WorkPlanUpsertFunction(AIFunction declaration, Func<UpsertAiProjectWorkPlanRequest, Task<string>> execute)
    : DelegatingAIFunction(declaration)
{
    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!arguments.TryGetValue("input", out var value) || value == null)
            return Invalid("missing_plan_input", "$.input", "请在 input 对象中提交完整的任务计划。", "resubmit_plan");

        UpsertAiProjectWorkPlanRequest request;
        try
        {
            var element = value is JsonElement json ? json : JsonSerializer.SerializeToElement(value, AgentJsonSerializer.Options);
            if (element.ValueKind != JsonValueKind.Object)
                return Invalid("invalid_plan_input", "$.input", "input 必须是 JSON 对象。", "resubmit_plan");

            // Property matching follows the DTO's case-insensitive JSON contract.
            // Reject duplicates instead of letting the binder pick a different identity.
            var properties = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in element.EnumerateObject())
            {
                if (!properties.TryAdd(property.Name, property.Value))
                    return Invalid("duplicate_plan_property", "$.input", "计划对象包含重复属性，请合并后重新提交。", "resubmit_plan");
            }

            var updating = properties.TryGetValue("planId", out var planId) && planId.ValueKind != JsonValueKind.Null;
            if (updating && !TryPositiveInt64(planId, out _))
                return Invalid("invalid_plan_id", "$.planId", "更新计划必须使用 get_project_work_plan 返回的正整数 planId；新建时省略 planId。", "get_project_work_plan");

            long version = 0;
            if (updating && (!properties.TryGetValue("expectedStructureVersion", out var expected) || !TryPositiveInt64(expected, out version)))
                return Invalid("invalid_plan_version", "$.expectedStructureVersion",
                    "更新计划必须使用 get_project_work_plan 返回的当前整数 structureVersion（例如 1 或字符串 \"1\"）。项目概况的 projectStructureHash、哈希、小数和科学计数法都不是计划版本。",
                    "get_project_work_plan");

            var normalized = new JsonObject();
            foreach (var (key, property) in properties)
            {
                // New plans have no prior revision. Discard only this irrelevant
                // update precondition, leaving stages/fields/relations intact.
                if (key.Equals("expectedStructureVersion", StringComparison.OrdinalIgnoreCase)) continue;
                normalized[key] = JsonNode.Parse(property.GetRawText());
            }
            if (updating) normalized["expectedStructureVersion"] = version;
            request = normalized.Deserialize<UpsertAiProjectWorkPlanRequest>(AgentJsonSerializer.Options)
                ?? throw new JsonException("计划对象不能为空。");
        }
        catch (JsonException exception)
        {
            return Invalid("invalid_plan_payload", exception.Path ?? "$.input",
                AiRequestFailure.Sanitize(exception.Message), "resubmit_plan");
        }
        catch (FormatException)
        {
            return Invalid("invalid_plan_payload", "$.input", "计划中的编号必须为范围内的十进制整数或整数字符串；请保留原计划内容并修正该字段。", "resubmit_plan");
        }

        cancellationToken.ThrowIfCancellationRequested();
        // Ownership, stale-version checks, permissions, task validation and
        // automatic execution stay in the existing application service.
        return await execute(request);
    }

    private static bool TryPositiveInt64(JsonElement value, out long result)
    {
        result = 0;
        return value.ValueKind switch
        {
            JsonValueKind.Number => value.TryGetInt64(out result) && result > 0,
            JsonValueKind.String => long.TryParse(value.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out result) && result > 0,
            _ => false
        };
    }

    private static string Invalid(string code, string path, string diagnostic, string action) =>
        JsonSerializer.Serialize(new
        {
            success = false, code, path, diagnostic,
            recovery = new { action, preservePlanContent = true },
            message = "项目方案尚未保存，请修正参数后重试；不要删减已确认的业务范围。"
        }, AgentJsonSerializer.Options);
}
