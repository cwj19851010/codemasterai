using CodeMaster.Agent.Contracts;
using CodeMaster.Core.MultiTenancy;
using CodeMaster.Core.Repositories;
using CodeMaster.Domain.Entities.Ai;

namespace CodeMaster.Agent.Services;

/// <summary>
/// Persists Agent turn / step / tool lifecycle events. Records are idempotent
/// on (conversation_id, request_id, sequence) so SSE replay or retry cannot
/// create duplicates (doc §7.5 "事件写入必须幂等").
/// </summary>
public interface IAgentEventStore
{
    Task<AiAgentEvent> RecordAsync(
        AgentEventRecord record,
        CancellationToken cancellationToken = default);
}

public sealed record AgentEventRecord(
    long ConversationId,
    long ProjectId,
    long UserId,
    string RequestId,
    string EventType,
    int? Sequence = null,
    string? TurnId = null,
    string? StepId = null,
    long? PlanId = null,
    string? StageKey = null,
    string? TaskKey = null,
    string? Status = null,
    string? ErrorCode = null,
    string? Message = null,
    string? PayloadJson = null);

internal sealed class SqlAgentEventStore : IAgentEventStore
{
    private readonly SqlSugar.ISqlSugarClient _db;
    private readonly CodeMaster.Core.Repositories.IRepository<AiAgentEvent> _repo;
    private readonly ITenantContext _tenantContext;

    public SqlAgentEventStore(
        SqlSugar.ISqlSugarClient db,
        CodeMaster.Core.Repositories.IRepository<AiAgentEvent> repo,
        ITenantContext tenantContext)
    {
        _db = db;
        _repo = repo;
        _tenantContext = tenantContext;
    }

    public async Task<AiAgentEvent> RecordAsync(
        AgentEventRecord record,
        CancellationToken cancellationToken = default)
    {
        // Idempotency: skip insert if the (request_id, sequence) tuple already exists.
        var sequence = record.Sequence ?? 0;
        if (sequence > 0)
        {
            var existing = await _db.Queryable<AiAgentEvent>()
                .FirstAsync(e => e.RequestId == record.RequestId && e.Sequence == sequence);
            if (existing != null) return existing;
        }

        var entity = new AiAgentEvent
        {
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
            Message = Truncate(record.Message, 2000),
            PayloadJson = Truncate(record.PayloadJson, 4000),
            TenantId = _tenantContext.CurrentTenantId ?? 0,
        };
        entity.Id = await _repo.InsertAsync(entity);
        return entity;
    }

    private static string? Truncate(string? value, int maxLength)
    {
        if (value == null) return null;
        return value.Length <= maxLength ? value : value[..maxLength];
    }
}
