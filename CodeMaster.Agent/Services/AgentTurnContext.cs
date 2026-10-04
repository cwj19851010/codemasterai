using CodeMaster.Agent.Contracts;

namespace CodeMaster.Agent.Services;

/// <summary>
/// Inputs the runner needs for one turn. Wraps existing conversation + message
/// state so the runner stays decoupled from the SQL repos.
/// </summary>
public sealed record AgentTurnContext(
    long ConversationId,
    long ProjectId,
    long ProviderId,
    string RequestId,
    string Content,
    string SystemInstruction,
    System.Collections.Generic.IReadOnlyList<AiMessageDto> History,
    bool RequiresPlan,
    string WorkflowMode);
