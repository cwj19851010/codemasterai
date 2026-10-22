using CodeMaster.Core.Entities;
using SqlSugar;

namespace CodeMaster.Domain.Entities.Ai;

[SugarTable("sys_ai_project_work_plan")]
public class AiProjectWorkPlan : EntityBaseWithTenant
{
    [SugarColumn(ColumnName = "conversation_id", IsNullable = false)]
    public long ConversationId { get; set; }

    [SugarColumn(ColumnName = "user_id", IsNullable = false)]
    public long UserId { get; set; }

    [SugarColumn(ColumnName = "project_id", IsNullable = false)]
    public long ProjectId { get; set; }

    [SugarColumn(ColumnName = "plan_key", Length = 64, IsNullable = false)]
    public string PlanKey { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "title", Length = 200, IsNullable = false)]
    public string Title { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "summary", Length = 2000, IsNullable = true)]
    public string? Summary { get; set; }

    [SugarColumn(ColumnName = "status", IsNullable = false)]
    public AiProjectWorkPlanStatus Status { get; set; } = AiProjectWorkPlanStatus.Draft;

    [SugarColumn(ColumnName = "activity_slot", Length = 100, IsNullable = false)]
    public string ActivitySlot { get; set; } = "active";

    [SugarColumn(ColumnName = "structure_version", IsNullable = false)]
    public long StructureVersion { get; set; } = 1;

    [SugarColumn(ColumnName = "repair_mode", IsNullable = false)]
    public AiProjectRepairMode RepairMode { get; set; } = AiProjectRepairMode.MetadataOnly;

    [SugarColumn(ColumnName = "active_batch_id", IsNullable = true)]
    public long? ActiveBatchId { get; set; }

    [SugarColumn(ColumnName = "started_at", IsNullable = true)]
    public DateTime? StartedAt { get; set; }

    [SugarColumn(ColumnName = "completed_at", IsNullable = true)]
    public DateTime? CompletedAt { get; set; }

    [SugarColumn(ColumnName = "last_activity_at", IsNullable = false)]
    public DateTime LastActivityAt { get; set; } = DateTime.UtcNow;
}

[SugarTable("sys_ai_project_work_stage")]
public class AiProjectWorkStage : EntityBaseWithTenant
{
    [SugarColumn(ColumnName = "plan_id", IsNullable = false)]
    public long PlanId { get; set; }

    [SugarColumn(ColumnName = "stage_key", Length = 64, IsNullable = false)]
    public string StageKey { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "kind", IsNullable = false)]
    public AiProjectWorkStageKind Kind { get; set; }

    [SugarColumn(ColumnName = "title", Length = 200, IsNullable = false)]
    public string Title { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "summary", Length = 2000, IsNullable = true)]
    public string? Summary { get; set; }

    [SugarColumn(ColumnName = "status", IsNullable = false)]
    public AiProjectWorkStageStatus Status { get; set; } = AiProjectWorkStageStatus.Pending;

    [SugarColumn(ColumnName = "sort_order", IsNullable = false)]
    public int SortOrder { get; set; }

    [SugarColumn(ColumnName = "generation_mode", Length = 30, IsNullable = true)]
    public string? GenerationMode { get; set; }

    [SugarColumn(ColumnName = "build_after_stage", IsNullable = false)]
    public bool BuildAfterStage { get; set; } = true;
}

[SugarTable("sys_ai_project_work_task")]
public class AiProjectWorkTask : EntityBaseWithTenant
{
    [SugarColumn(ColumnName = "plan_id", IsNullable = false)]
    public long PlanId { get; set; }

    [SugarColumn(ColumnName = "stage_id", IsNullable = false)]
    public long StageId { get; set; }

    [SugarColumn(ColumnName = "task_key", Length = 100, IsNullable = false)]
    public string TaskKey { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "title", Length = 200, IsNullable = false)]
    public string Title { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "summary", Length = 2000, IsNullable = true)]
    public string? Summary { get; set; }

    [SugarColumn(ColumnName = "operation_kind", Length = 100, IsNullable = false)]
    public string OperationKind { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "status", IsNullable = false)]
    public AiProjectWorkTaskStatus Status { get; set; } = AiProjectWorkTaskStatus.Pending;

    [SugarColumn(ColumnName = "execution_location", IsNullable = false)]
    public AiProjectWorkExecutionLocation ExecutionLocation { get; set; } = AiProjectWorkExecutionLocation.Client;

    [SugarColumn(ColumnName = "safety_class", IsNullable = false)]
    public AiProjectWorkSafetyClass SafetyClass { get; set; } = AiProjectWorkSafetyClass.Metadata;

    [SugarColumn(ColumnName = "definition_json", Length = 8000, IsNullable = true)]
    public string? DefinitionJson { get; set; }

    [SugarColumn(ColumnName = "definition_payload", IsNullable = true)]
    public byte[]? DefinitionPayload { get; set; }

    [SugarColumn(ColumnName = "payload_hash", Length = 64, IsNullable = true)]
    public string? PayloadHash { get; set; }

    [SugarColumn(ColumnName = "semantic_references_json", Length = 8000, IsNullable = true)]
    public string? SemanticReferencesJson { get; set; }

    [SugarColumn(ColumnName = "affected_object_ids_json", Length = 8000, IsNullable = true)]
    public string? AffectedObjectIdsJson { get; set; }

    [SugarColumn(ColumnName = "sort_order", IsNullable = false)]
    public int SortOrder { get; set; }

    [SugarColumn(ColumnName = "retry_limit", IsNullable = false)]
    public int RetryLimit { get; set; } = 3;

    [SugarColumn(ColumnName = "attempt_count", IsNullable = false)]
    public int AttemptCount { get; set; }

    [SugarColumn(ColumnName = "last_failure_category", IsNullable = false)]
    public AiProjectFailureCategory LastFailureCategory { get; set; }

    [SugarColumn(ColumnName = "last_failure_signature", Length = 64, IsNullable = true)]
    public string? LastFailureSignature { get; set; }

    [SugarColumn(ColumnName = "last_diagnosis", Length = 2000, IsNullable = true)]
    public string? LastDiagnosis { get; set; }

    [SugarColumn(ColumnName = "requires_generation", IsNullable = false)]
    public bool RequiresGeneration { get; set; }

    [SugarColumn(ColumnName = "requires_build", IsNullable = false)]
    public bool RequiresBuild { get; set; } = true;
}

[SugarTable("sys_ai_project_work_task_dependency")]
public class AiProjectWorkTaskDependency : EntityBaseWithTenant
{
    [SugarColumn(ColumnName = "plan_id", IsNullable = false)]
    public long PlanId { get; set; }

    [SugarColumn(ColumnName = "task_id", IsNullable = false)]
    public long TaskId { get; set; }

    [SugarColumn(ColumnName = "depends_on_task_id", IsNullable = false)]
    public long DependsOnTaskId { get; set; }
}
