using CodeMaster.Core.Entities;
using SqlSugar;

namespace CodeMaster.Domain.Entities.Ai;

[SugarTable("sys_ai_project_work_batch")]
public class AiProjectWorkBatch : EntityBaseWithTenant
{
    [SugarColumn(ColumnName = "plan_id", IsNullable = false)]
    public long PlanId { get; set; }

    [SugarColumn(ColumnName = "stage_id", IsNullable = true)]
    public long? StageId { get; set; }

    [SugarColumn(ColumnName = "tool_execution_id", IsNullable = true)]
    public long? ToolExecutionId { get; set; }

    [SugarColumn(ColumnName = "batch_key", Length = 64, IsNullable = false)]
    public string BatchKey { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "title", Length = 200, IsNullable = false)]
    public string Title { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "summary", Length = 2000, IsNullable = true)]
    public string? Summary { get; set; }

    [SugarColumn(ColumnName = "status", IsNullable = false)]
    public AiProjectWorkBatchStatus Status { get; set; } = AiProjectWorkBatchStatus.Draft;

    [SugarColumn(ColumnName = "structure_version", IsNullable = false)]
    public long StructureVersion { get; set; }

    [SugarColumn(ColumnName = "input_hash", Length = 64, IsNullable = false)]
    public string InputHash { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "execution_location", IsNullable = false)]
    public AiProjectWorkExecutionLocation ExecutionLocation { get; set; } = AiProjectWorkExecutionLocation.Client;

    [SugarColumn(ColumnName = "safety_class", IsNullable = false)]
    public AiProjectWorkSafetyClass SafetyClass { get; set; } = AiProjectWorkSafetyClass.Metadata;

    [SugarColumn(ColumnName = "disclosure_json", Length = 8000, IsNullable = true)]
    public string? DisclosureJson { get; set; }

    [SugarColumn(ColumnName = "disclosure_payload", IsNullable = true)]
    public byte[]? DisclosurePayload { get; set; }

    [SugarColumn(ColumnName = "repair_policy_json", Length = 4000, IsNullable = true)]
    public string? RepairPolicyJson { get; set; }

    [SugarColumn(ColumnName = "attempt_count", IsNullable = false)]
    public int AttemptCount { get; set; }

    [SugarColumn(ColumnName = "approved_at", IsNullable = true)]
    public DateTime? ApprovedAt { get; set; }

    [SugarColumn(ColumnName = "completed_at", IsNullable = true)]
    public DateTime? CompletedAt { get; set; }

    [SugarColumn(ColumnName = "error_message", Length = 2000, IsNullable = true)]
    public string? ErrorMessage { get; set; }
}

[SugarTable("sys_ai_project_work_batch_task")]
public class AiProjectWorkBatchTask : EntityBaseWithTenant
{
    [SugarColumn(ColumnName = "plan_id", IsNullable = false)]
    public long PlanId { get; set; }

    [SugarColumn(ColumnName = "batch_id", IsNullable = false)]
    public long BatchId { get; set; }

    [SugarColumn(ColumnName = "task_id", IsNullable = false)]
    public long TaskId { get; set; }

    [SugarColumn(ColumnName = "sort_order", IsNullable = false)]
    public int SortOrder { get; set; }
}

[SugarTable("sys_ai_project_work_attempt")]
public class AiProjectWorkAttempt : EntityBaseWithTenant
{
    [SugarColumn(ColumnName = "plan_id", IsNullable = false)]
    public long PlanId { get; set; }

    [SugarColumn(ColumnName = "batch_id", IsNullable = false)]
    public long BatchId { get; set; }

    [SugarColumn(ColumnName = "task_id", IsNullable = false)]
    public long TaskId { get; set; }

    [SugarColumn(ColumnName = "attempt_number", IsNullable = false)]
    public int AttemptNumber { get; set; }

    [SugarColumn(ColumnName = "status", IsNullable = false)]
    public AiProjectWorkAttemptStatus Status { get; set; } = AiProjectWorkAttemptStatus.Pending;

    [SugarColumn(ColumnName = "executor_mode", Length = 30, IsNullable = false)]
    public string ExecutorMode { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "failure_category", IsNullable = false)]
    public AiProjectFailureCategory FailureCategory { get; set; }

    [SugarColumn(ColumnName = "failure_signature", Length = 64, IsNullable = true)]
    public string? FailureSignature { get; set; }

    [SugarColumn(ColumnName = "evidence_json", Length = 8000, IsNullable = true)]
    public string? EvidenceJson { get; set; }

    [SugarColumn(ColumnName = "evidence_payload", IsNullable = true)]
    public byte[]? EvidencePayload { get; set; }

    [SugarColumn(ColumnName = "repair_json", Length = 8000, IsNullable = true)]
    public string? RepairJson { get; set; }

    [SugarColumn(ColumnName = "repair_payload", IsNullable = true)]
    public byte[]? RepairPayload { get; set; }

    [SugarColumn(ColumnName = "result_json", Length = 8000, IsNullable = true)]
    public string? ResultJson { get; set; }

    [SugarColumn(ColumnName = "result_payload", IsNullable = true)]
    public byte[]? ResultPayload { get; set; }

    [SugarColumn(ColumnName = "error_message", Length = 2000, IsNullable = true)]
    public string? ErrorMessage { get; set; }

    [SugarColumn(ColumnName = "started_at", IsNullable = true)]
    public DateTime? StartedAt { get; set; }

    [SugarColumn(ColumnName = "completed_at", IsNullable = true)]
    public DateTime? CompletedAt { get; set; }
}

[SugarTable("sys_ai_project_work_checkpoint")]
public class AiProjectWorkCheckpoint : EntityBaseWithTenant
{
    [SugarColumn(ColumnName = "plan_id", IsNullable = false)]
    public long PlanId { get; set; }

    [SugarColumn(ColumnName = "batch_id", IsNullable = false)]
    public long BatchId { get; set; }

    [SugarColumn(ColumnName = "task_id", IsNullable = false)]
    public long TaskId { get; set; }

    [SugarColumn(ColumnName = "attempt_id", IsNullable = false)]
    public long AttemptId { get; set; }

    [SugarColumn(ColumnName = "kind", IsNullable = false)]
    public AiProjectWorkCheckpointKind Kind { get; set; }

    [SugarColumn(ColumnName = "checkpoint_key", Length = 100, IsNullable = false)]
    public string CheckpointKey { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "action_name", Length = 100, IsNullable = true)]
    public string? ActionName { get; set; }

    [SugarColumn(ColumnName = "status", IsNullable = false)]
    public AiProjectWorkCheckpointStatus Status { get; set; } = AiProjectWorkCheckpointStatus.Pending;

    [SugarColumn(ColumnName = "sort_order", IsNullable = false)]
    public int SortOrder { get; set; }

    [SugarColumn(ColumnName = "input_hash", Length = 64, IsNullable = true)]
    public string? InputHash { get; set; }

    [SugarColumn(ColumnName = "idempotency_key", Length = 128, IsNullable = false)]
    public string IdempotencyKey { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "result_json", Length = 8000, IsNullable = true)]
    public string? ResultJson { get; set; }

    [SugarColumn(ColumnName = "result_payload", IsNullable = true)]
    public byte[]? ResultPayload { get; set; }

    [SugarColumn(ColumnName = "diagnostic_output", Length = 8000, IsNullable = true)]
    public string? DiagnosticOutput { get; set; }

    [SugarColumn(ColumnName = "started_at", IsNullable = true)]
    public DateTime? StartedAt { get; set; }

    [SugarColumn(ColumnName = "completed_at", IsNullable = true)]
    public DateTime? CompletedAt { get; set; }
}
