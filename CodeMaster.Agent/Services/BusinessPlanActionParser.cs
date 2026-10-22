using System.Text.Json;
using CodeMaster.Agent.Contracts;
using CodeMaster.Agent.Serialization;
using CodeMaster.Agent.Tools;

namespace CodeMaster.Agent.Services;

/// <summary>
/// Parses the model's structured business-plan fallback when a provider returns
/// JSON text instead of emitting the submit_business_plan tool call.
/// </summary>
internal static class BusinessPlanActionParser
{
    public static bool TryParse(string? text, out BusinessPlanDefinition definition)
    {
        definition = new BusinessPlanDefinition();
        if (string.IsNullOrWhiteSpace(text))
            return false;

        foreach (var candidate in Candidates(text))
        {
            try
            {
                using var document = JsonDocument.Parse(candidate);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                    continue;

                if (root.TryGetProperty("action", out var action) &&
                    action.ValueKind == JsonValueKind.String &&
                    !string.Equals(action.GetString(), CodeMasterAgentToolCatalog.SubmitBusinessPlan, StringComparison.Ordinal))
                    continue;

                var payload = root;
                if (root.TryGetProperty("businessPlan", out var nested) && nested.ValueKind == JsonValueKind.Object)
                    payload = nested;

                var parsed = payload.Deserialize<BusinessPlanDefinition>(AgentJsonSerializer.Options);
                if (parsed == null || !HasBusinessContent(parsed))
                    continue;

                definition = parsed;
                return true;
            }
            catch (JsonException)
            {
                // Try the next fenced/direct candidate. Plain prose is not a
                // successful action and must continue through normal recovery.
            }
        }

        return false;
    }

    private static bool HasBusinessContent(BusinessPlanDefinition definition)
        => definition.Modules.Count > 0 ||
           definition.Entities.Count > 0 ||
           definition.Views.Count > 0 ||
           definition.Relations.Count > 0 ||
           definition.Pages.Count > 0;

    private static IEnumerable<string> Candidates(string text)
    {
        var trimmed = text.Trim();
        yield return trimmed;

        var fenceStart = trimmed.IndexOf("```json", StringComparison.OrdinalIgnoreCase);
        if (fenceStart >= 0)
        {
            var start = fenceStart + 7;
            var end = trimmed.IndexOf("```", start, StringComparison.Ordinal);
            if (end > start)
                yield return trimmed[start..end].Trim();
        }
        else
        {
            fenceStart = trimmed.IndexOf("```", StringComparison.Ordinal);
            if (fenceStart >= 0)
            {
                var start = trimmed.IndexOf('\n', fenceStart);
                var end = start >= 0 ? trimmed.IndexOf("```", start + 1, StringComparison.Ordinal) : -1;
                if (end > start)
                    yield return trimmed[(start + 1)..end].Trim();
            }
        }
    }
}
