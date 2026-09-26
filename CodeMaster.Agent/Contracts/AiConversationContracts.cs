using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using CodeMaster.Agent.Serialization;
using CodeMaster.Application.Dtos.CodeGen;

namespace CodeMaster.Agent.Contracts;

public static class AiWorkflowModes
{
    public const string Standard = "Standard";
    public const string Minimal = "Minimal";

    public static string Normalize(string? value)
        => string.Equals(value, Minimal, StringComparison.OrdinalIgnoreCase)
            ? Minimal
            : Standard;

    public static bool ShouldAutoApprove(string? workflowMode, bool autoApproveApprovals)
        => Normalize(workflowMode) == Minimal || autoApproveApprovals;
}

public sealed class AiConversationDto
{
    public long Id { get; set; }
    public long ProjectId { get; set; }
    public string ProjectName { get; set; } = string.Empty;
    public long ProviderId { get; set; }
    public string ProviderName { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string WorkflowMode { get; set; } = AiWorkflowModes.Standard;
    public bool AutoApproveApprovals { get; set; }
    public DateTime? LastMessageAt { get; set; }
    public DateTime CreateTime { get; set; }
}

public sealed class CreateAiConversationRequest
{
    public long ProjectId { get; set; }
    public long ProviderId { get; set; }
    public string? Title { get; set; }
    public string? WorkflowMode { get; set; }
    public bool AutoApproveApprovals { get; set; }
}

public sealed class UpdateAiConversationPreferencesRequest
{
    public string? WorkflowMode { get; set; }
    public bool AutoApproveApprovals { get; set; }
}

public sealed class UpdateAiConversationProviderRequest
{
    public long ProviderId { get; set; }
}

public sealed class AiMessageDto
{
    public long Id { get; set; }
    public string? RequestId { get; set; }
    public string Role { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public string? MetadataJson { get; set; }
    public int Sequence { get; set; }
    public DateTime CreateTime { get; set; }
}

public sealed class SendAiMessageRequest
{
    public long ConversationId { get; set; }
    public string? RequestId { get; set; }
    public string Content { get; set; } = string.Empty;
}

public sealed class CancelAiChatRequest
{
    public long ConversationId { get; set; }
    public string RequestId { get; set; } = string.Empty;
}

public sealed class AiChatResult
{
    public AiMessageDto Message { get; set; } = new();
    public List<AiToolExecutionDto> PendingApprovals { get; set; } = new();
}

public sealed class AiChatStreamEvent
{
    public string Event { get; set; } = string.Empty;
    public string RequestId { get; set; } = string.Empty;
    public int Sequence { get; set; }
    public string? Phase { get; set; }
    public string? Text { get; set; }
    public string? ToolName { get; set; }
    public string? Message { get; set; }
    public string? ToolStage { get; set; }
    public string? NextAction { get; set; }
    public int Attempt { get; set; }
    public string? ExceptionType { get; set; }
    public string? ExceptionSource { get; set; }
    public string? FailureKind { get; set; }
    public string? CorrelationId { get; set; }
    public string? StackSnippet { get; set; }
    public AiChatResult? Result { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}

public sealed class BeginLocalAiChatResult
{
    public bool AlreadyCompleted { get; set; }
    public AiChatResult? CompletedResult { get; set; }
    public long ConversationId { get; set; }
    public long ProviderId { get; set; }
    public string RequestId { get; set; } = string.Empty;
    public string Instructions { get; set; } = string.Empty;
    public List<AiMessageDto> Messages { get; set; } = new();
    public List<string> AvailableTools { get; set; } = new();
    public bool RequiresWorkPlan { get; set; }
}

public sealed class InvokeLocalAiToolRequest
{
    public long ConversationId { get; set; }
    public string RequestId { get; set; } = string.Empty;
    public string ToolName { get; set; } = string.Empty;
    public string? KnownProjectStructureVersion { get; set; }
    public JsonElement Arguments { get; set; }
}

public sealed class InvokeLocalAiToolResult
{
    public string Output { get; set; } = string.Empty;
    public string? ProjectStructureVersion { get; set; }
    public bool ProjectStructureNotModified { get; set; }
}

public sealed class CompleteLocalAiChatRequest
{
    public long ConversationId { get; set; }
    public string RequestId { get; set; } = string.Empty;
    public string ResponseText { get; set; } = string.Empty;
    public int PlanContinuationAttempts { get; set; }
}

public sealed class AiToolExecutionDto
{
    public long Id { get; set; }
    public long ConversationId { get; set; }
    public long ProjectId { get; set; }
    public long? WorkPlanId { get; set; }
    public long? WorkBatchId { get; set; }
    public long? WorkTaskId { get; set; }
    public string? RequestId { get; set; }
    public string ToolName { get; set; } = string.Empty;
    public string? InputJson { get; set; }
    public string? OutputJson { get; set; }
    public string Status { get; set; } = string.Empty;
    public string? ErrorMessage { get; set; }
    public DateTime CreateTime { get; set; }
}

public sealed class CreateModuleProposal
{
    [Description("Technical module name used as a C# namespace and generated directory. Required ASCII PascalCase, for example OrderManagement. Never put Chinese text here.")]
    public string ModuleName { get; set; } = string.Empty;
    [Description("Human-readable module title. Chinese or another display language belongs here, for example 订单管理.")]
    public string ModuleDescription { get; set; } = string.Empty;
    public string? Icon { get; set; }
    [Description("Top-level menu order. Use 3..9998 for ordinary business modules. Values 1 and 2 are reserved for System/Monitor. Use 9999 only for the Reporting/Reports module; use 0 to request automatic allocation.")]
    public int OrderNum { get; set; }
    public string? RoutePath { get; set; }
    public string? Remark { get; set; }
}

public sealed class CreateEntityProposal
{
    public long ModuleId { get; set; }
    [Description("Technical module name. Use the existing/proposed ASCII PascalCase ModuleName, for example OrderManagement; never use the Chinese display title.")]
    public string? ModuleName { get; set; }
    public string Name { get; set; } = string.Empty;
    [Description("Short display title used by menus and page headings. Keep it within 50 characters. Prefer Chinese when the project's display language is Chinese.")]
    public string Description { get; set; } = string.Empty;
    [Description("Long-form business semantics for Agent analysis and documentation. Explain purpose, lifecycle, tenant or deletion rules, and important constraints. It is not rendered as a menu title.")]
    public string? BusinessDescription { get; set; }
    [Description("Optional custom physical table name. Leave null for CodeMaster's default plural snake_case name without a prefix. Set only when the user explicitly requests a custom table name; never invent a prefix.")]
    public string? TableName { get; set; }
    [Description("Whether to generate the standard long Id field and IEntity<long>. Writable and tree entities require this. Keyless entities must be read-only.")]
    public bool HasPrimaryKey { get; set; } = true;
    [Description("Adds ITree, ParentId, and Ancestors. Tree entities must have a primary key.")]
    public bool IsTree { get; set; }
    [Description("Disables create/update/delete. Keyed read-only entities keep GetById; keyless read-only entities expose list/query/export only.")]
    public bool IsReadOnly { get; set; }
    [Description("Adds ITenant and the protected TenantId system field.")]
    public bool HasTenant { get; set; }
    [Description("Adds IDept and protected DeptId, DeptAncestors, and CreateUserId system fields.")]
    public bool HasDataPermission { get; set; }
    [Description("Adds IAuditEntity and CreateUserId/CreateBy/CreateTime/UpdateUserId/UpdateBy/UpdateTime system fields.")]
    public bool HasAudit { get; set; } = true;
    [Description("Adds ISoftDelete and IsDeleted/DeleteTime/DeleteBy/DeleteUserId system fields.")]
    public bool HasSoftDelete { get; set; } = true;
    public bool GenerateFrontend { get; set; } = true;
    public bool IsChildTable { get; set; }
    public string? FrontendRoute { get; set; }
    public string? MenuIcon { get; set; }
    public int OrderNum { get; set; }
    public string? Remark { get; set; }
    [Description("Optional existing entity id whose non-system field and control metadata should be copied into this new entity. Prefer CopyFieldsFromEntityName when the name is known. This is useful for repeated or all-controls entities and is expanded by the local client before server validation. Explicit Fields are appended and replace copied fields with the same name.")]
    public long? CopyFieldsFromEntityId { get; set; }
    [Description("Optional existing entity name whose non-system field and control metadata should be copied into this new entity. Use this instead of enumerating every field for repeated or all-controls entities. Do not set both copy source properties. Explicit Fields are appended and replace copied fields with the same name.")]
    public string? CopyFieldsFromEntityName { get; set; }
    public List<CreateEntityFieldProposal> Fields { get; set; } = new();
    public List<CreateEntityRelationProposal> Relations { get; set; } = new();
}

public sealed class CreateEntityFieldProposal
{
    public string Name { get; set; } = string.Empty;
    [Description("Short field label used by generated forms and tables. Keep it within 50 characters. Prefer Chinese when the project's display language is Chinese.")]
    public string Description { get; set; } = string.Empty;
    [Description("Long-form field semantics for Agent analysis and documentation. Explain business meaning, units, value rules, relations, and constraints. It is not rendered as a field label.")]
    public string? BusinessDescription { get; set; }
    [Description("C# data type. Prefer canonical names such as string, int, long, decimal, bool, DateTime, DateOnly, DateTimeOffset, Guid. Use FormControlType=datetime for the UI control; do not use it as the C# type name.")]
    public string DataType { get; set; } = "string";
    public bool IsNullable { get; set; }
    public int? MaxLength { get; set; }
    public int? Precision { get; set; }
    public int? Scale { get; set; }
    public string? DefaultValue { get; set; }
    public bool IsIgnore { get; set; }
    public bool IsPrimaryKey { get; set; }
    public bool IsRequired { get; set; }
    public string? MinValue { get; set; }
    public string? MaxValue { get; set; }
    public string? RegexPattern { get; set; }
    public bool IsEmail { get; set; }
    public bool IsPhone { get; set; }
    public bool ShowInList { get; set; } = true;
    public bool ShowInDetail { get; set; } = true;
    public bool ShowInAddForm { get; set; } = true;
    public bool ShowInEditForm { get; set; } = true;
    public bool ShowInSearch { get; set; }
    [Description("UI control: input, textarea, number, select, select-table, date, datetime, switch, checkbox, radio-group, checkbox-group, file, image, editor, cascader, or table-column. Use ordinary select for scalar code values. select-table and cascader require RelatedEntityName, RelatedEntityIdField, and RelatedEntityDisplayFields; cascader must target a tree entity.")]
    public string FormControlType { get; set; } = "input";
    public int? ListWidth { get; set; }
    public int OrderNum { get; set; }
    [Description("For ordinary select: enum, dict, or api. api means a project/built-in related entity and enables generated batch label resolution plus form option loading.")]
    public string? SelectDataSource { get; set; }
    [Description("For api select, pass a JSON string or object such as {\"loadMode\":\"batch\",\"parentField\":\"ProvinceCode\",\"parentValueField\":\"ParentCode\"}; parentField is the current form field and parentValueField is the source-entity filter field. For dict/static select, keep the existing dictionary code or option JSON.")]
    [JsonConverter(typeof(JsonTextValueConverter))]
    public string? SelectOptions { get; set; }
    public bool IsMultiple { get; set; }
    [Description("Source entity for api select, select-table, or cascader. Do not use select-table for a simple scalar code lookup. For a self-contained control demo, select-table may use SysUser and cascader may use the tree entity SysDept.")]
    public string? RelatedEntityName { get; set; }
    [Description("Value/code field on the api source entity. Defaults to Id.")]
    public string? RelatedEntityIdField { get; set; }
    [Description("Label fields on the related entity. A native string array such as [\"Name\"] and a JSON-encoded string are both accepted. Required for api select, select-table, and cascader.")]
    [JsonConverter(typeof(JsonTextValueConverter))]
    public string? RelatedEntityDisplayFields { get; set; }
    [Description("Optional result mappings. A native JSON array of sourceField/targetField objects and a JSON-encoded string are both accepted.")]
    [JsonConverter(typeof(JsonTextValueConverter))]
    public string? ResultMappings { get; set; }
    [Description("Normal, Computed, or Aggregate. Computed and Aggregate fields are generated as read-only calculated form values.")]
    public string FieldCategory { get; set; } = "Normal";
    [Description("For Computed fields, an arithmetic expression using [FieldName] references, for example [Price]*[Quantity].")]
    public string? Formula { get; set; }
    [Description("For Aggregate fields: Sum, Avg, or Concat.")]
    public string? AggregateType { get; set; }
    [Description("Existing child entity id from an owned one-to-many relation. Create the relation first when the id is not yet known.")]
    public long? AggregateChildEntityId { get; set; }
    [Description("Child field used by the aggregate. Sum/Avg require numeric fields; Concat requires text.")]
    public string? AggregateChildFieldName { get; set; }
    [Description("Optional separator for Concat aggregates.")]
    public string? AggregateSeparator { get; set; }
    public string? Remark { get; set; }
}

public sealed class CreateEntityRelationProposal
{
    [Description("Existing target entity id. Use TargetEntityName instead when the target is created in the same change set.")]
    public long TargetEntityId { get; set; }

    [Description("Target entity name. Required when the target is created in the same change set.")]
    public string? TargetEntityName { get; set; }

    [Description("Navigation/aggregate property name, for example Items, Detail, or Customer.")]
    public string RelationName { get; set; } = string.Empty;

    [Description("Field on the source entity. OneToMany/OwnedOneToOne: principal primary key, normally Id. ManyToOne Reference: dependent foreign key, for example CustomerId.")]
    public string SourceField { get; set; } = string.Empty;

    [Description("Field on the target entity. OneToMany/OwnedOneToOne: dependent foreign key, for example OrderId. ManyToOne Reference: target primary key, normally Id.")]
    public string TargetField { get; set; } = string.Empty;

    [Description("OneToOne, OneToMany, or ManyToOne. ManyToMany is not currently supported.")]
    public string Cardinality { get; set; } = string.Empty;

    [Description("Owned for aggregate child relations; Reference for foreign-key references to an independent entity.")]
    public string Ownership { get; set; } = string.Empty;
    public bool IsRequired { get; set; }

    [Description("Delete for owned aggregate children; Restrict or Keep for independent references.")]
    public string DeleteBehavior { get; set; } = string.Empty;
    public int OrderNum { get; set; }
}

public sealed class ProjectChangeSetProposal
{
    public string Summary { get; set; } = string.Empty;
    public List<CreateModuleProposal> Modules { get; set; } = new();
    public List<CreateEntityProposal> Entities { get; set; } = new();
    [Description("Dictionary changes included in this same CodeMaster metadata change. Use this list instead of creating a separate dictionary request when a request also changes modules or entities.")]
    public List<ProjectDictionaryChangeProposal> Dictionaries { get; set; } = new();
    public List<UpdateModuleProposal> ModuleUpdates { get; set; } = new();
    public List<UpdateEntityProposal> EntityUpdates { get; set; } = new();
    public List<long> DeleteEntityIds { get; set; } = new();
    public List<long> DeleteModuleIds { get; set; } = new();
    public string GenerationMode { get; set; } = "None";
    public List<long> GenerationEntityIds { get; set; } = new();
    public bool BuildAfterGeneration { get; set; }

    [Description("Whether to run the generated project test project after generation. Defaults to true to close the auto-test loop.")]
    public bool TestAfterGeneration { get; set; } = true;
}

public sealed class ProjectDictionaryQuery
{
    [Description("Target tenant id inside the generated project. Use 0 for the host/default tenant.")]
    public long TenantId { get; set; }

    [Description("Optional exact dictionary code, for example order_status.")]
    public string? DictType { get; set; }

    [Description("Optional text matched against dictionary code, name, entry label, or entry value.")]
    public string? Keyword { get; set; }

    public bool IncludeItems { get; set; } = true;

    public bool IncludeDisabled { get; set; } = true;
}

public sealed class ProjectDictionaryChangeProposal
{
    public string Summary { get; set; } = string.Empty;

    [Description("Target tenant id inside the generated project. Use 0 for the host/default tenant.")]
    public long TenantId { get; set; }

    [Description("Stable dictionary code referenced by field SelectOptions, for example order_status.")]
    public string DictType { get; set; } = string.Empty;

    public string DictName { get; set; } = string.Empty;

    public string? LangKey { get; set; }

    [Description("0 enabled, 1 disabled. Omit to preserve the existing value.")]
    public int? Status { get; set; }

    public int? Sort { get; set; }

    public string? Remark { get; set; }

    [Description("Entries to create or update. Existing entries omitted from this list are preserved.")]
    public List<ProjectDictionaryItemProposal> Items { get; set; } = new();
}

public sealed class ProjectDictionaryItemProposal
{
    public string Label { get; set; } = string.Empty;

    [Description("Stable stored value. Existing entries are matched by this value.")]
    public string Value { get; set; } = string.Empty;

    public string? LangKey { get; set; }

    [Description("0 no, 1 yes. Omit to preserve the existing value.")]
    public int? IsDefault { get; set; }

    [Description("0 enabled, 1 disabled. Omit to preserve the existing value.")]
    public int? Status { get; set; }

    public int? Sort { get; set; }

    public string? Remark { get; set; }
}

public sealed class UpdateModuleProposal
{
    public long ModuleId { get; set; }
    [Description("New technical module name used as a C# namespace. When provided, it must be ASCII PascalCase, for example OrderManagement. Never put Chinese text here.")]
    public string? ModuleName { get; set; }
    [Description("New human-readable module title. Chinese or another display language belongs here.")]
    public string? ModuleDescription { get; set; }
    public string? Icon { get; set; }
    [Description("Top-level menu order. Ordinary business modules must use 3..9998; 1 and 2 are reserved, and 9999 is only for Reporting/Reports. Omit to preserve the current order.")]
    public int? OrderNum { get; set; }
    public string? RoutePath { get; set; }
    public string? Remark { get; set; }
}

public sealed class UpdateEntityProposal
{
    public long EntityId { get; set; }
    public string? Name { get; set; }
    [Description("New short display title. Keep it within 50 characters; omit to preserve the current title.")]
    public string? Description { get; set; }
    [Description("New long-form business semantics. Omit to preserve the current value; use an empty string to clear it.")]
    public string? BusinessDescription { get; set; }
    [Description("Optional custom physical table name. Omit to keep the current value. Use an empty string only when the user explicitly asks to restore CodeMaster's default unprefixed table name.")]
    public string? TableName { get; set; }
    [Description("Whether the entity has the standard long Id primary key. Setting false also requires IsReadOnly=true and IsTree=false.")]
    public bool? HasPrimaryKey { get; set; }
    [Description("Tree entities require HasPrimaryKey=true.")]
    public bool? IsTree { get; set; }
    [Description("Keyed read-only keeps GetById; keyless read-only exposes list/query/export only.")]
    public bool? IsReadOnly { get; set; }
    public bool? HasTenant { get; set; }
    public bool? HasDataPermission { get; set; }
    public bool? HasAudit { get; set; }
    public bool? HasSoftDelete { get; set; }
    public bool? GenerateFrontend { get; set; }
    public bool? IsChildTable { get; set; }
    public string? FrontendRoute { get; set; }
    public string? MenuIcon { get; set; }
    public int? OrderNum { get; set; }
    public string? Remark { get; set; }
    public List<CreateEntityFieldProposal> NewFields { get; set; } = new();
    public List<UpdateEntityFieldProposal> UpdatedFields { get; set; } = new();
    public List<long> DeletedFieldIds { get; set; } = new();
    public List<CreateEntityRelationProposal> NewRelations { get; set; } = new();
    public List<UpdateEntityRelationProposal> UpdatedRelations { get; set; } = new();
    public List<long> DeletedRelationIds { get; set; } = new();
}

public sealed class UpdateEntityFieldProposal
{
    public long FieldId { get; set; }
    public string? Name { get; set; }
    [Description("New short field label. Keep it within 50 characters; omit to preserve the current label.")]
    public string? Description { get; set; }
    [Description("New long-form field semantics. Omit to preserve the current value; use an empty string to clear it.")]
    public string? BusinessDescription { get; set; }
    [Description("C# data type. Prefer canonical names such as string, int, long, decimal, bool, DateTime, DateOnly, DateTimeOffset, Guid.")]
    public string? DataType { get; set; }
    public bool? IsNullable { get; set; }
    public int? MaxLength { get; set; }
    public bool ClearMaxLength { get; set; }
    public int? Precision { get; set; }
    public bool ClearPrecision { get; set; }
    public int? Scale { get; set; }
    public bool ClearScale { get; set; }
    public string? DefaultValue { get; set; }
    public bool? IsIgnore { get; set; }
    public bool? IsPrimaryKey { get; set; }
    public bool? IsRequired { get; set; }
    public string? MinValue { get; set; }
    public string? MaxValue { get; set; }
    public string? RegexPattern { get; set; }
    public bool? IsEmail { get; set; }
    public bool? IsPhone { get; set; }
    public bool? ShowInList { get; set; }
    public bool? ShowInDetail { get; set; }
    public bool? ShowInAddForm { get; set; }
    public bool? ShowInEditForm { get; set; }
    public bool? ShowInSearch { get; set; }
    [Description("UI control type. Supported values match CreateEntityFieldProposal.FormControlType. Keep ordinary scalar lookups as select. select-table and cascader require a related entity, value field, and display fields; cascader must target a tree entity.")]
    public string? FormControlType { get; set; }
    public int? ListWidth { get; set; }
    public bool ClearListWidth { get; set; }
    public int? OrderNum { get; set; }
    [Description("For select, use enum, dict, or api. api enables generated batch label resolution and option loading.")]
    public string? SelectDataSource { get; set; }
    [Description("For api select, pass a JSON string or object such as {\"loadMode\":\"batch\",\"parentField\":\"ProvinceCode\",\"parentValueField\":\"ParentCode\"}; parentField is the current form field and parentValueField is the source-entity filter field; otherwise preserve dictionary/static option config.")]
    [JsonConverter(typeof(JsonTextValueConverter))]
    public string? SelectOptions { get; set; }
    public bool? IsMultiple { get; set; }
    [Description("Related source entity for api select, select-table, or cascader. For a self-contained control demo, use SysUser for select-table and the tree entity SysDept for cascader.")]
    public string? RelatedEntityName { get; set; }
    [Description("Value/code field on the api source entity; defaults to Id.")]
    public string? RelatedEntityIdField { get; set; }
    [Description("Related display fields. A native string array and a JSON-encoded string are both accepted. Required for api select, select-table, and cascader.")]
    [JsonConverter(typeof(JsonTextValueConverter))]
    public string? RelatedEntityDisplayFields { get; set; }
    [Description("Optional result mappings. A native JSON array of sourceField/targetField objects and a JSON-encoded string are both accepted.")]
    [JsonConverter(typeof(JsonTextValueConverter))]
    public string? ResultMappings { get; set; }
    [Description("Normal, Computed, or Aggregate.")]
    public string? FieldCategory { get; set; }
    [Description("Computed arithmetic formula using [FieldName] references.")]
    public string? Formula { get; set; }
    [Description("Aggregate type: Sum, Avg, or Concat.")]
    public string? AggregateType { get; set; }
    [Description("Existing child entity id from an owned one-to-many relation.")]
    public long? AggregateChildEntityId { get; set; }
    public bool ClearAggregateChildEntityId { get; set; }
    [Description("Child source field used by the aggregate.")]
    public string? AggregateChildFieldName { get; set; }
    public string? AggregateSeparator { get; set; }
    public string? Remark { get; set; }
}

public sealed class UpdateEntityRelationProposal
{
    public long RelationId { get; set; }
    public long? TargetEntityId { get; set; }
    public string? TargetEntityName { get; set; }
    public string? RelationName { get; set; }
    public string? SourceField { get; set; }
    public string? TargetField { get; set; }
    public string? Cardinality { get; set; }
    public string? Ownership { get; set; }
    public bool? IsRequired { get; set; }
    public string? DeleteBehavior { get; set; }
    public int? OrderNum { get; set; }
}

public sealed class ProjectChangeSetValidationResult
{
    public bool IsValid => Errors.Count == 0;
    public List<string> Errors { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
    public int ModuleCount { get; set; }
    public int EntityCount { get; set; }
    public int FieldCount { get; set; }
    public int RelationCount { get; set; }
    public int UpdatedModuleCount { get; set; }
    public int UpdatedEntityCount { get; set; }
    public int DeletedModuleCount { get; set; }
    public int DeletedEntityCount { get; set; }
    public int DictionaryCount { get; set; }
}

public sealed class ProjectChangeSetExecutionResult
{
    public long ProjectId { get; set; }
    public long? WorkPlanId { get; set; }
    public long? WorkBatchId { get; set; }
    public long? WorkTaskId { get; set; }
    public string Summary { get; set; } = string.Empty;
    public List<string> AutomaticRepairs { get; set; } = new();
    public List<ProjectChangeCreatedItem> CreatedModules { get; set; } = new();
    public List<ProjectChangeCreatedItem> CreatedEntities { get; set; } = new();
    public List<ProjectChangeCreatedItem> UpdatedModules { get; set; } = new();
    public List<ProjectChangeCreatedItem> UpdatedEntities { get; set; } = new();
    public List<long> DeletedModuleIds { get; set; } = new();
    public List<long> DeletedEntityIds { get; set; } = new();
    public int CreatedRelationCount { get; set; }
    public int CreatedDictionaryCount { get; set; }
    public List<AiClientActionDto> ClientActions { get; set; } = new();
    public string? ClientExecutionOutput { get; set; }
    public string? ClientExecutionFailedAction { get; set; }
    public string? ClientExecutionError { get; set; }
}

public sealed class UiPageEnhancementProposal
{
    [Description("Short user-facing summary of the visual change.")]
    public string Summary { get; set; } = string.Empty;

    [Description("Scaffold for Login/Dashboard, or EntityPage for index/add/edit/detail.")]
    public string TargetKind { get; set; } = "EntityPage";

    [Description("Login or Dashboard when TargetKind=Scaffold.")]
    public string? Page { get; set; }

    [Description("Existing entity id when TargetKind=EntityPage.")]
    public long? EntityId { get; set; }

    [Description("Existing entity name alternative when EntityId is not supplied.")]
    public string? EntityName { get; set; }

    [Description("index, add, edit, or detail when TargetKind=EntityPage.")]
    public string? PageType { get; set; }

    [Description("Enterprise, Technology, Industrial, Commerce, or Minimal.")]
    public string Style { get; set; } = "Enterprise";

    public string? Headline { get; set; }

    public string? Subtitle { get; set; }

    public List<string> Highlights { get; set; } = new();

    [Description("Optional six-digit hex color such as #2563eb.")]
    public string? PrimaryColor { get; set; }

    [Description("Optional six-digit hex color such as #0f766e.")]
    public string? SecondaryColor { get; set; }

    [Description("Replace prior Agent design operations instead of merging them.")]
    public bool ReplaceExistingDesign { get; set; }

    [Description("Structured operations anchored by stable genId values. Supported types: SetTag, SetProp, RemoveProp, SetGrid, Move, Group.")]
    public List<ProjectUiNodeOperationDto> Operations { get; set; } = new();

    public bool BuildAfterApply { get; set; } = true;
}

public sealed class UiPageEnhancementExecutionResult
{
    public long ProjectId { get; set; }
    public string Summary { get; set; } = string.Empty;
    public string TargetKind { get; set; } = string.Empty;
    public string Page { get; set; } = string.Empty;
    public List<AiClientActionDto> ClientActions { get; set; } = new();
    public string? ClientExecutionOutput { get; set; }
    public string? ClientExecutionFailedAction { get; set; }
    public string? ClientExecutionError { get; set; }
}

public sealed class ProjectChangeCreatedItem
{
    public long Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class EntityBlueprintQuery
{
    public long EntityId { get; set; }
    public string? EntityName { get; set; }
}

public sealed class UiPageBlueprintQuery
{
    public long? EntityId { get; set; }
    public string? EntityName { get; set; }
    public string PageType { get; set; } = "index";
}

public sealed class AiClientActionDto
{
    public string Action { get; set; } = string.Empty;
    public long ProjectId { get; set; }
    public long? WorkPlanId { get; set; }
    public long? WorkBatchId { get; set; }
    public long? WorkTaskId { get; set; }
    public long? WorkAttemptId { get; set; }
    public long? WorkCheckpointId { get; set; }
    public string? WorkCheckpointKey { get; set; }
    public string? InputHash { get; set; }
    public string? IdempotencyKey { get; set; }
    public long? PageId { get; set; }
    public long? EntityId { get; set; }
    public List<long> EntityIds { get; set; } = new();
    public string? TargetKind { get; set; }
    public string? Page { get; set; }
    public string? PageType { get; set; }
    public string? Style { get; set; }
    public string? Headline { get; set; }
    public string? Subtitle { get; set; }
    public List<string> Highlights { get; set; } = new();
    public string? PrimaryColor { get; set; }
    public string? SecondaryColor { get; set; }
    public bool ReplaceExistingDesign { get; set; }
    public bool Incremental { get; set; } = true;
    public List<ProjectUiNodeOperationDto> Operations { get; set; } = new();
}

public sealed class FailLocalAiChatRequest
{
    public long ConversationId { get; set; }
    public string RequestId { get; set; } = string.Empty;
    public string ErrorMessage { get; set; } = string.Empty;
}

public sealed class CompleteAiClientActionsRequest
{
    public bool Success { get; set; }
    public string? Output { get; set; }
    public string? ErrorMessage { get; set; }
    public string? FailedAction { get; set; }
    public string? DiagnosticOutput { get; set; }
}

public sealed class CompleteAiClientActionsResult
{
    public AiToolExecutionDto Execution { get; set; } = new();
    public AiToolExecutionDto? NextExecution { get; set; }
    public AiProjectWorkPlanDto? WorkPlan { get; set; }
    public AiMessageDto? RepairMessage { get; set; }
    public SendAiMessageRequest? LocalRepairRequest { get; set; }
    public List<AiToolExecutionDto> PendingApprovals { get; set; } = new();
    public int RepairAttempt { get; set; }
    public bool AutomaticRepairStopped { get; set; }
    public string? AnalysisError { get; set; }
}
