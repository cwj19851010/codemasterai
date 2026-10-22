using System.Text.Json;
using CodeMaster.Agent.Contracts;

namespace CodeMaster.Agent.Services;

/// <summary>
/// Conversation-scoped Agent preferences. <see cref="AgentMode"/> is the authoritative
/// source for how a turn is routed: it is chosen explicitly by the user in the UI and
/// persisted here, so the server never has to infer intent from message keywords.
/// </summary>
internal sealed record AiWorkflowPreferences(
    string WorkflowMode,
    bool AutoApproveApprovals,
    string AgentMode = AiAgentModes.Requirements,
    DateTime? LastGeneratedAt = null)
{
    public bool ShouldAutoApprove => AiWorkflowModes.ShouldAutoApprove(WorkflowMode, AutoApproveApprovals);

    /// <summary>True when the user has explicitly authorized code generation.</summary>
    public bool IsCodeMode => AiAgentModes.IsCode(AgentMode);
}

internal static class AiWorkflowPreferencesStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static AiWorkflowPreferences Default { get; } =
        new(AiWorkflowModes.Default, false, AiAgentModes.Requirements);

    public static AiWorkflowPreferences NewConversationDefault { get; } =
        new(AiWorkflowModes.Default, false, AiAgentModes.Requirements);

    public static AiWorkflowPreferences Normalize(string? workflowMode, bool autoApproveApprovals)
        => new(AiWorkflowModes.Normalize(workflowMode), autoApproveApprovals, AiAgentModes.Requirements);

    public static AiWorkflowPreferences Normalize(
        string? workflowMode,
        bool autoApproveApprovals,
        string? agentMode)
        => new(AiWorkflowModes.Normalize(workflowMode), autoApproveApprovals, AiAgentModes.Normalize(agentMode));

    public static AiWorkflowPreferences Read(string? serialized)
    {
        if (string.IsNullOrWhiteSpace(serialized))
            return Default;

        try
        {
            var stored = JsonSerializer.Deserialize<StoredPreferences>(serialized, JsonOptions);
            return new AiWorkflowPreferences(
                AiWorkflowModes.Normalize(stored?.WorkflowMode),
                stored?.AutoApproveApprovals ?? false,
                AiAgentModes.Normalize(stored?.AgentMode),
                stored?.LastGeneratedAt);
        }
        catch (JsonException)
        {
            return Default;
        }
    }

    /// <summary>
    /// Reads the persisted preferences and returns a copy switched to <paramref name="agentMode"/>.
    /// </summary>
    public static AiWorkflowPreferences WithAgentMode(string? serialized, string? agentMode)
    {
        var current = Read(serialized);
        return current with { AgentMode = AiAgentModes.Normalize(agentMode) };
    }

    public static string Write(AiWorkflowPreferences preferences)
        => JsonSerializer.Serialize(new StoredPreferences
        {
            WorkflowMode = AiWorkflowModes.Normalize(preferences.WorkflowMode),
            AgentMode = AiAgentModes.Normalize(preferences.AgentMode),
            AutoApproveApprovals = preferences.AutoApproveApprovals,
            LastGeneratedAt = preferences.LastGeneratedAt
        }, JsonOptions);

    private sealed class StoredPreferences
    {
        public string? WorkflowMode { get; set; }
        public string? AgentMode { get; set; }
        public bool AutoApproveApprovals { get; set; }
        public DateTime? LastGeneratedAt { get; set; }
    }
}
