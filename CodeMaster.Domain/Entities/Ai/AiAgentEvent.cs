using CodeMaster.Core.Entities;
using SqlSugar;

namespace CodeMaster.Domain.Entities.Ai;

/// <summary>
/// Persistent Agent turn / step / tool lifecycle event. Records the fact-of-event
/// so SSE-replay-only flows (web refresh, WebApi restart) can recover the timeline.
/// Schema follows doc §7.5: turnId/stepId/planId/stageKey/taskKey/sequence/eventType
/// all carry the canonical tuple, indexed by (conversation_id, request_id, sequence)
/// so retries cannot create duplicates.
/// </summary>
[SugarTable("sys_ai_agent_event")]
public class AiAgentEvent : EntityBaseWithTenant
{
    [SugarColumn(ColumnName = "conversation_id", IsNullable = false)]
    public long ConversationId { get; set; }

    [SugarColumn(ColumnName = "user_id", IsNullable = false)]
    public long UserId { get; set; }

    [SugarColumn(ColumnName = "project_id", IsNullable = false)]
    public long ProjectId { get; set; }

    [SugarColumn(ColumnName = "request_id", Length = 64, IsNullable = false)]
    public string RequestId { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "turn_id", Length = 64, IsNullable = true)]
    public string? TurnId { get; set; }

    [SugarColumn(ColumnName = "step_id", Length = 64, IsNullable = true)]
    public string? StepId { get; set; }

    [SugarColumn(ColumnName = "plan_id", IsNullable = true)]
    public long? PlanId { get; set; }

    [SugarColumn(ColumnName = "stage_key", Length = 64, IsNullable = true)]
    public string? StageKey { get; set; }

    [SugarColumn(ColumnName = "task_key", Length = 100, IsNullable = true)]
    public string? TaskKey { get; set; }

    [SugarColumn(ColumnName = "sequence", IsNullable = false)]
    public int Sequence { get; set; }

    [SugarColumn(ColumnName = "event_type", Length = 50, IsNullable = false)]
    public string EventType { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "status", Length = 30, IsNullable = true)]
    public string? Status { get; set; }

    [SugarColumn(ColumnName = "error_code", Length = 60, IsNullable = true)]
    public string? ErrorCode { get; set; }

    [SugarColumn(ColumnName = "message", Length = 2000, IsNullable = true)]
    public string? Message { get; set; }

    [SugarColumn(ColumnName = "payload_json", Length = 4000, IsNullable = true)]
    public string? PayloadJson { get; set; }
}
