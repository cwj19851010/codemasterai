using CodeMaster.Core.Entities;
using SqlSugar;

namespace CodeMaster.Domain.Entities.Ai;

/// <summary>
/// Audited Agent tool invocation and approval state.
/// </summary>
[SugarTable("sys_ai_tool_execution")]
public class AiToolExecution : EntityBaseWithTenant
{
    [SugarColumn(ColumnName = "conversation_id", IsNullable = false)]
    public long ConversationId { get; set; }

    [SugarColumn(ColumnName = "user_id", IsNullable = false)]
    public long UserId { get; set; }

    [SugarColumn(ColumnName = "project_id", IsNullable = false)]
    public long ProjectId { get; set; }

    [SugarColumn(ColumnName = "work_plan_id", IsNullable = true)]
    public long? WorkPlanId { get; set; }

    [SugarColumn(ColumnName = "work_batch_id", IsNullable = true)]
    public long? WorkBatchId { get; set; }

    [SugarColumn(ColumnName = "work_task_id", IsNullable = true)]
    public long? WorkTaskId { get; set; }

    [SugarColumn(ColumnName = "request_id", Length = 64, IsNullable = true)]
    public string? RequestId { get; set; }

    [SugarColumn(ColumnName = "input_hash", Length = 64, IsNullable = true)]
    public string? InputHash { get; set; }

    [SugarColumn(ColumnName = "tool_name", Length = 100, IsNullable = false)]
    public string ToolName { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "input_json", Length = 8000, IsNullable = true)]
    public string? InputJson { get; set; }

    /// <summary>
    /// Gzip-compressed UTF-8 tool input. New executions use this provider-neutral binary column;
    /// InputJson remains as a compact summary and as a legacy payload fallback.
    /// </summary>
    [SugarColumn(ColumnName = "input_payload", IsNullable = true)]
    public byte[]? InputPayload { get; set; }

    [SugarColumn(ColumnName = "output_json", Length = 8000, IsNullable = true)]
    public string? OutputJson { get; set; }

    /// <summary>
    /// Gzip-compressed UTF-8 execution output. OutputJson remains available for legacy records.
    /// </summary>
    [SugarColumn(ColumnName = "output_payload", IsNullable = true)]
    public byte[]? OutputPayload { get; set; }

    [SugarColumn(ColumnName = "status", Length = 30, IsNullable = false)]
    public string Status { get; set; } = "PendingApproval";

    [SugarColumn(ColumnName = "approved_at", IsNullable = true)]
    public DateTime? ApprovedAt { get; set; }

    [SugarColumn(ColumnName = "completed_at", IsNullable = true)]
    public DateTime? CompletedAt { get; set; }

    [SugarColumn(ColumnName = "error_message", Length = 2000, IsNullable = true)]
    public string? ErrorMessage { get; set; }
}
