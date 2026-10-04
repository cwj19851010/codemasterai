using System.Collections.Generic;
using CodeMaster.Agent.Contracts;

namespace CodeMaster.Agent.Services;

/// <summary>
/// Outcome of one turn. Distinct result so callers (legacy service, tests,
/// future direct callers) can route on it without needing the same DTO.
/// </summary>
public sealed record AgentTurnResult(
    AgentTurnOutcome Outcome,
    long ConversationId,
    string? FinalText,
    AiMessageDto? AssistantMessage,
    List<AiToolExecutionDto> ToolExecutions,
    long? PlanId,
    bool PlanSaved,
    int ModelCallCount,
    int? AttemptCount)
{
    public static AgentTurnResult NotImplemented(long conversationId) =>
        new(AgentTurnOutcome.NotImplemented, conversationId, null, null,
            new List<AiToolExecutionDto>(), null, false, 0, null);
}

public enum AgentTurnOutcome
{
    /// <summary>Turn completed with a final assistant reply and tool results.</summary>
    Completed = 0,
    /// <summary>Turn paused waiting for user input.</summary>
    WaitingUser = 1,
    /// <summary>Turn failed; finalText contains the error description.</summary>
    Failed = 2,
    /// <summary>Turn cancelled.</summary>
    Cancelled = 3,
    /// <summary>Runner is not yet wired up — caller should fall through to legacy service.</summary>
    NotImplemented = 4,
}
