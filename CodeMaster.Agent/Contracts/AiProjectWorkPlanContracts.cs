using System.ComponentModel;
using System.Text.Json;

namespace CodeMaster.Agent.Contracts;

public sealed class UpsertAiProjectWorkPlanRequest
{
    [Description("Existing plan id when updating. Omit to create a plan.")]
    public long? PlanId { get; set; }

    [Description("Conversation that owns this plan.")]
    public long ConversationId { get; set; }

    [Description("CodeMaster project id.")]
    public long ProjectId { get; set; }

    [Description("Stable caller-supplied plan key. Generated when omitted on create.")]
    public string? PlanKey { get; set; }

    [Description("UPDATE only: the exact positive integer structureVersion from get_project_work_plan, e.g. 1. CREATE: omit or null. Never use a project metadata hash, contentHash, timestamp, decimal, or exponent here.")]
    public long? ExpectedStructureVersion { get; set; }

    [Description("Required user-visible plan title, for example 'Foreign Trade Management System'.")]
    public string Title { get; set; } = string.Empty;
    public string? Summary { get; set; }

    [Description("When true, update only the supplied stages and tasks and preserve all unspecified plan content. Use this for adding or repairing one task without reproducing the entire plan snapshot.")]
    public bool PreserveUnspecified { get; set; }

    [Description("Existing non-terminal task keys to cancel while preserving all other unspecified tasks. Use when splitting or replacing a malformed task. Completed tasks cannot be cancelled.")]
    public List<string> CancelTaskKeys { get; set; } = [];

    [Description("Disabled or MetadataOnly. TestSourceRepair is retired: the agent never reads or patches CodeMaster source code, and the value is still accepted so stored plans keep loading.")]
    public string RepairMode { get; set; } = "MetadataOnly";

    public List<AiProjectWorkStageInput> Stages { get; set; } = [];
}

public sealed class ProposeAiProjectWorkBatchRequest
{
    public long PlanId { get; set; }
    public long? ExpectedStructureVersion { get; set; }

    [Description("Optional stable task keys. When omitted, the first ready stage is grouped automatically.")]
    public List<string> TaskKeys { get; set; } = [];

    [Description("Maximum number of tasks in one approval batch. The server also enforces its configured payload and entity limits.")]
    public int MaxTasks { get; set; } = 20;
}

public sealed class AiProjectWorkTaskQuery
{
    [Description("Stable task key from get_project_completion_status, for example relations-reference-integrity.")]
    public string TaskKey { get; set; } = string.Empty;
}

public sealed class SupersedeAiProjectWorkPlanRequest
{
    [Description("Active work plan id to retire after a malformed or obsolete plan is diagnosed. History is preserved and no project metadata is changed.")]
    public long PlanId { get; set; }
}

public sealed class ConfirmAiProjectWorkPlanRequest
{
    [Description("The revision shown to the user. The server rejects stale confirmations.")]
    public long ExpectedStructureVersion { get; set; }

    [Description("SHA-256 hash of the exact standard JSON shown to the user. The server rejects changed content.")]
    public string ContentHash { get; set; } = string.Empty;
}

public sealed class ConfirmAiProjectWorkPlanResult
{
    public AiProjectWorkPlanDto Plan { get; set; } = new();
    public AiToolExecutionDto? Execution { get; set; }
    public string Message { get; set; } = string.Empty;
}

public sealed class ResumeAiProjectWorkPlanResult
{
    public AiProjectWorkPlanDto Plan { get; set; } = new();
    public AiToolExecutionDto? Execution { get; set; }
    public string Message { get; set; } = string.Empty;
}

public sealed class RetryAiProjectWorkTaskRequest
{
    [Description("Failure signature currently shown in the task list. Prevents retrying after the diagnosis changed.")]
    public string FailureSignature { get; set; } = string.Empty;
}

public sealed class AiProjectWorkBatchOperation
{
    public long TaskId { get; set; }
    public string TaskKey { get; set; } = string.Empty;
    public string OperationKind { get; set; } = string.Empty;
    public string? DefinitionJson { get; set; }
    public string PayloadHash { get; set; } = string.Empty;
}

public sealed class AiProjectWorkBatchProposal
{
    public AiProjectWorkBatchDto Batch { get; set; } = new();
    public long PlanId { get; set; }
    public long StructureVersion { get; set; }
    public string InputHash { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public List<AiProjectWorkBatchOperation> Operations { get; set; } = [];
}

public sealed class AiProjectWorkBatchExecutionResult
{
    public long ProjectId { get; set; }
    public long PlanId { get; set; }
    public long BatchId { get; set; }
    public string Summary { get; set; } = string.Empty;
    public List<object> OperationResults { get; set; } = [];
    public List<AiClientActionDto> ClientActions { get; set; } = [];
}

public sealed class AiProjectConsistencyCheckRequest
{
    public string Summary { get; set; } = "最终一致性检查";
    public bool RequireSevenStages { get; set; } = true;
}

public sealed class AiProjectConsistencyCheckResult
{
    public long ProjectId { get; set; }
    public bool IsValid => Errors.Count == 0;
    public int ModuleCount { get; set; }
    public int EntityCount { get; set; }
    public int FieldCount { get; set; }
    public int RelationCount { get; set; }
    public List<string> Errors { get; set; } = [];
    public List<string> Warnings { get; set; } = [];
}

public sealed class GenerateProjectWorkRequest
{
    public string Summary { get; set; } = "一次性生成代码";
    public string GenerationMode { get; set; } = "Full";
    public List<long> EntityIds { get; set; } = [];
    public bool BuildAfterGeneration { get; set; } = true;
    public bool TestAfterGeneration { get; set; } = true;
}

public sealed class GenerateProjectWorkResult
{
    public long ProjectId { get; set; }
    public string Summary { get; set; } = string.Empty;
    public List<AiClientActionDto> ClientActions { get; set; } = [];
}

public sealed class AiProjectWorkStageInput
{
    public string StageKey { get; set; } = string.Empty;

    [Description("EntityFields, Views, Relations, FieldControls, ComplexPages, ConsistencyCheck, or Custom. Views are defined after base entities, relations are finalized after view consumers exist, and controls are finalized after relation field types are known. The plan ends at ConsistencyCheck: the agent writes metadata only and never schedules a GenerateCode stage; that legacy value remains accepted for stored plans. Legacy Foundation and DataControls values remain accepted for stored plans.")]
    public string Kind { get; set; } = string.Empty;

    [Description("Required user-visible stage title, for example '实体和字段'.")]
    public string Title { get; set; } = string.Empty;
    public string? Summary { get; set; }
    public int? SortOrder { get; set; }
    public string? GenerationMode { get; set; }
    public bool BuildAfterStage { get; set; } = true;
    public List<AiProjectWorkTaskInput> Tasks { get; set; } = [];
}

public sealed class AiProjectWorkTaskInput
{
    public string TaskKey { get; set; } = string.Empty;
    [Description("Required user-visible task title, for example 'Create customer and supplier entities'.")]
    public string Title { get; set; } = string.Empty;
    public string? Summary { get; set; }

    [Description("Typed operation adapter key, for example project_change_set, view_definition, or complex_page.")]
    public string OperationKind { get; set; } = string.Empty;

    [Description("Server, Client, or LocalAgent.")]
    public string ExecutionLocation { get; set; } = "Client";

    [Description("ReadOnly, Metadata, GeneratedArtifacts, Database, or PlatformSource.")]
    public string SafetyClass { get; set; } = "Metadata";

    [Description("Typed executable payload. For project_change_set, canonical ProjectChangeSetProposal JSON is accepted. A module that does not exist yet belongs in modules (create) and is referenced by entities through moduleName; moduleUpdates only accepts modules that already have a real moduleId, never moduleId 0. A new moduleName must never equal the project name itself, because generated modules live under the project root namespace and the same name would make the build fail with CS0234. Deferred stages may also use deterministic references resolved by the server at execution: existing entity display updates as entities[{entityId,description,fields:{FieldName:DisplayName}}], controlBindings[{entity,field,...field update properties}], and top-level relations[{sourceEntity,targetEntity,...relation properties}]. Do not invent any other wrapper shape.")]
    public JsonElement? Definition { get; set; }

    [Description("References that must already exist when this task becomes ready, using exact technical names such as module:MasterData, entity:Customer, field:SalesOrder.Status, or relation:SalesOrder.Items. Use project for a foundation task. Do not reference localized display labels or objects created by this same task; later tasks may reference outputs created by prerequisite tasks.")]
    public List<string> SemanticReferences { get; set; } = [];
    public List<string> DependsOnTaskKeys { get; set; } = [];
    public int? SortOrder { get; set; }
    public int RetryLimit { get; set; } = 3;
    public bool RequiresGeneration { get; set; }
    public bool RequiresBuild { get; set; } = true;
}

public sealed class AiProjectWorkPlanDto
{
    public long Id { get; set; }
    public long ConversationId { get; set; }
    public long ProjectId { get; set; }
    public string PlanKey { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string? Summary { get; set; }
    public string Status { get; set; } = string.Empty;
    public long StructureVersion { get; set; }
    public string RepairMode { get; set; } = string.Empty;
    public long? ActiveBatchId { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public DateTime LastActivityAt { get; set; }
    public int CompletedTasks { get; set; }
    public int TotalTasks { get; set; }
    public string WorkflowSchemaVersion { get; set; } = "1.0";
    public string ConfirmationStatus { get; set; } = string.Empty;
    public bool RequiresConfirmation { get; set; }
    public string? ContentHash { get; set; }
    public AiProjectWorkPlanChangeSummary ChangeSummary { get; set; } = new();
    public JsonElement? StandardJson { get; set; }
    public List<AiProjectWorkStageDto> Stages { get; set; } = [];
    public AiProjectWorkBatchDto? ActiveBatch { get; set; }
    public List<string> AvailableActions { get; set; } = [];
    public AiProjectRepairPolicyDto RepairPolicy { get; set; } = new();
}

public sealed class AiProjectWorkPlanChangeSummary
{
    public int Added { get; set; }
    public int Updated { get; set; }
    public int Deleted { get; set; }
    public int Unchanged { get; set; }
}

public sealed class AiProjectRepairPolicyDto
{
    public string Mode { get; set; } = string.Empty;

    [Description("Always false since 2026-10-20: the agent never inspects or patches CodeMaster source code.")]
    public bool SourceRepairEnabled { get; set; }
    public bool SourceRepairAuthorized { get; set; }
    public bool CanInspectCodeMasterSource => SourceRepairEnabled && SourceRepairAuthorized;
    public string Message { get; set; } = string.Empty;
}

public sealed class AiProjectWorkStageDto
{
    public long Id { get; set; }
    public string StageKey { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string? Summary { get; set; }
    public string Status { get; set; } = string.Empty;
    public int SortOrder { get; set; }
    public string? GenerationMode { get; set; }
    public bool BuildAfterStage { get; set; }
    public int CompletedTasks { get; set; }
    public int TotalTasks { get; set; }
    public List<AiProjectWorkTaskDto> Tasks { get; set; } = [];
}

public sealed class AiProjectWorkTaskDto
{
    public long Id { get; set; }
    public long StageId { get; set; }
    public string TaskKey { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string? Summary { get; set; }
    public string OperationKind { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string ExecutionLocation { get; set; } = string.Empty;
    public string SafetyClass { get; set; } = string.Empty;
    public string? PayloadHash { get; set; }
    public List<string> SemanticReferences { get; set; } = [];
    public List<long> DependsOnTaskIds { get; set; } = [];
    public List<string> DependsOnTaskKeys { get; set; } = [];
    public List<string> UnmetDependencyTaskKeys { get; set; } = [];
    public int SortOrder { get; set; }
    public int RetryLimit { get; set; }
    public int AttemptCount { get; set; }
    public string? LastFailureCategory { get; set; }
    public string? LastDiagnosis { get; set; }
    public bool RequiresGeneration { get; set; }
    public bool RequiresBuild { get; set; }
    public AiProjectWorkAttemptDto? LastAttempt { get; set; }
    public AiProjectWorkCheckpointDto? LastCheckpoint { get; set; }
    public List<string> AvailableActions { get; set; } = [];
    public List<AiProjectSemanticReferenceDto> ResolvedReferences { get; set; } = [];
    public JsonElement? StandardJson { get; set; }
    public List<AiProjectWorkChangeDto> Changes { get; set; } = [];
}

/// <summary>
/// A normalized, user-facing change projection. The executable payload remains
/// in <see cref="AiProjectWorkTaskDto.StandardJson"/>; this projection is only
/// used to render add/update/delete rows without asking the model to rewrite it.
/// </summary>
public sealed class AiProjectWorkChangeDto
{
    public string ChangeId { get; set; } = string.Empty;
    public string Operation { get; set; } = string.Empty;
    public string ResourceType { get; set; } = string.Empty;
    public JsonElement? Target { get; set; }
    public JsonElement? Values { get; set; }
    public JsonElement? Differences { get; set; }
    public string? Reason { get; set; }
    public JsonElement? Impacts { get; set; }
}

public sealed class AiProjectSemanticReferenceDto
{
    public string Reference { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public long ObjectId { get; set; }
    public string ObjectName { get; set; } = string.Empty;
}

public sealed class AiProjectWorkBatchDto
{
    public long Id { get; set; }
    public long PlanId { get; set; }
    public long? StageId { get; set; }
    public long? ToolExecutionId { get; set; }
    public string BatchKey { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string? Summary { get; set; }
    public string Status { get; set; } = string.Empty;
    public long StructureVersion { get; set; }
    public string InputHash { get; set; } = string.Empty;
    public string ExecutionLocation { get; set; } = string.Empty;
    public string SafetyClass { get; set; } = string.Empty;
    public int AttemptCount { get; set; }
    public DateTime? ApprovedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public string? ErrorMessage { get; set; }
    public List<long> TaskIds { get; set; } = [];
}

public sealed class AiProjectWorkAttemptDto
{
    public long Id { get; set; }
    public long BatchId { get; set; }
    public long TaskId { get; set; }
    public int AttemptNumber { get; set; }
    public string Status { get; set; } = string.Empty;
    public string ExecutorMode { get; set; } = string.Empty;
    public string? FailureCategory { get; set; }
    public string? FailureSignature { get; set; }
    public AiProjectFailureEvidenceDto? Evidence { get; set; }
    public AiProjectSourceRepairAuditDto? Repair { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
}

public sealed class AiProjectSourceRepairAuditDto
{
    public string RepairId { get; set; } = string.Empty;
    public bool ValidationPassed { get; set; }
    public string ValidationProfile { get; set; } = string.Empty;
    public string Rationale { get; set; } = string.Empty;
    public string? ValidationOutput { get; set; }
    public string? DiffSummary { get; set; }
    public List<string> AffectedCapabilities { get; set; } = [];
    public List<AiProjectSourceRepairFileAuditDto> Files { get; set; } = [];
}

public sealed class AiProjectSourceRepairFileAuditDto
{
    public string RelativePath { get; set; } = string.Empty;
    public string BeforeSha256 { get; set; } = string.Empty;
    public string AfterSha256 { get; set; } = string.Empty;
    public int AddedLines { get; set; }
    public int RemovedLines { get; set; }
}

public sealed class AiProjectFailureEvidenceDto
{
    public string Category { get; set; } = string.Empty;
    public string FailureSignature { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public string ExecutionMode { get; set; } = string.Empty;
    public string ActionName { get; set; } = string.Empty;
    public string? FailedAction { get; set; }
    public string? InputHash { get; set; }
    public string CodeMasterVersion { get; set; } = string.Empty;
    public string? DiagnosticOutput { get; set; }
    public string? ArtifactManifestSummary { get; set; }
    public List<string> PredecessorResults { get; set; } = [];
    public List<string> SupportingEvidence { get; set; } = [];
}

public sealed class AiProjectWorkCheckpointDto
{
    public long Id { get; set; }
    public long BatchId { get; set; }
    public long TaskId { get; set; }
    public long AttemptId { get; set; }
    public string Kind { get; set; } = string.Empty;
    public string CheckpointKey { get; set; } = string.Empty;
    public string? ActionName { get; set; }
    public string Status { get; set; } = string.Empty;
    public int SortOrder { get; set; }
    public string? InputHash { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
}
