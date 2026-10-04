using System.Text.Json;
using CodeMaster.Agent.Contracts;

namespace CodeMaster.Agent.Services;

/// <summary>
/// Per-turn state machine. Extracts the model-call + tool-execute + auto-continue
/// loop out of AiConversationService so it can be reused, tested, and observed
/// independently. Phase 1 ships the skeleton; later phases wire it up.
/// </summary>
public sealed class AgentTurnRunner
{
    public AgentTurnRunner()
    {
    }

    /// <summary>
    /// Run a single agent turn: model call → tool calls → plan compile → auto-continue.
    /// Returns the aggregated result. Publishes events through the supplied delegate
    /// so SSE / database persistence can subscribe.
    /// </summary>
    public Task<AgentTurnResult> RunAsync(
        AgentTurnContext context,
        Func<AiChatStreamEvent, CancellationToken, ValueTask>? publishAsync,
        CancellationToken cancellationToken)
    {
        // Phase 1: identity implementation. The real work continues to live in
        // AiConversationService.SendStreamingAsync. We return a marker result so
        // callers can decide whether to fall through to the legacy path.
        return Task.FromResult(AgentTurnResult.NotImplemented(context.ConversationId));
    }
}
