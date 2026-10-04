using System.Collections.Concurrent;
using CodeMaster.Domain.Entities.Ai;

namespace CodeMaster.Agent.Services;

/// <summary>
/// In-memory IAgentEventStore. Used by tests and as the no-op fallback when
/// no persistence is registered. Maintains idempotency via (request_id, sequence).
/// </summary>
internal sealed class InMemoryAgentEventStore : IAgentEventStore
{
    private readonly ConcurrentDictionary<(string requestId, int sequence), AiAgentEvent> _events =
        new ConcurrentDictionary<(string, int), AiAgentEvent>();

    public Task<AiAgentEvent> RecordAsync(AgentEventRecord record, CancellationToken cancellationToken = default)
    {
        var sequence = record.Sequence ?? 0;
        var key = (record.RequestId, sequence);
        if (sequence > 0 && _events.ContainsKey(key))
            return Task.FromResult(_events[key]);

        var entity = new AiAgentEvent
        {
            Id = _events.Count + 1,
            ConversationId = record.ConversationId,
            UserId = record.UserId,
            ProjectId = record.ProjectId,
            RequestId = record.RequestId,
            TurnId = record.TurnId,
            StepId = record.StepId,
            PlanId = record.PlanId,
            StageKey = record.StageKey,
            TaskKey = record.TaskKey,
            Sequence = sequence,
            EventType = record.EventType,
            Status = record.Status,
            ErrorCode = record.ErrorCode,
            Message = record.Message,
            PayloadJson = record.PayloadJson,
            TenantId = 0,
        };
        _events[key] = entity;
        return Task.FromResult(entity);
    }
}
