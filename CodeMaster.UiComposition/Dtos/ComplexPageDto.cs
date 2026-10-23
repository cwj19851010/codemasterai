using CodeMaster.Core.Dtos;
using CodeMaster.Domain.Entities.CodeGen;

namespace CodeMaster.Application.Dtos.CodeGen;

public class ProjectPageDto : EntityDto
{
    public long ProjectId { get; set; }
    public long ModuleId { get; set; }
    public string PageKey { get; set; } = string.Empty;
    public string PageName { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string RoutePath { get; set; } = string.Empty;
    public string? Icon { get; set; }
    public int OrderNum { get; set; }
    public ComplexPageType PageType { get; set; }
    public long? LayoutPresetId { get; set; }
    public string LayoutJson { get; set; } = string.Empty;
    public int DefinitionVersion { get; set; }
    public string SchemaVersion { get; set; } = string.Empty;
    public PageSyncStatus SyncStatus { get; set; }
    public PageSyncMode SyncMode { get; set; }
    public bool IsMenuSynced { get; set; }
    public string? TargetDirectory { get; set; }
    public DateTime? LastGeneratedTime { get; set; }
    public string? LastGeneratedVueHash { get; set; }
    public string? LastDefinitionHash { get; set; }
    public string? LastError { get; set; }
    public List<PageDataSourceDto> DataSources { get; set; } = new();
    public List<PageComponentBindingDto> Bindings { get; set; } = new();
    public List<PageEntityDependencyDto> Dependencies { get; set; } = new();
}

public class ProjectPageQueryDto : PagedQueryDto
{
    public long? ProjectId { get; set; }
    public long? ModuleId { get; set; }
    public string? PageKey { get; set; }
    public string? PageName { get; set; }
    public PageSyncStatus? SyncStatus { get; set; }
}

/// <summary>
/// Portable complex-page definition. Database row identities are intentionally
/// replaced by source keys and stable component/binding identities so the same
/// definition can be consumed by the WebApi, LocalAgent, or a future planner Agent.
/// </summary>
public class PageDefinitionSnapshotDto
{
    public string Format { get; set; } = "codemaster.complex-page";
    public int FormatVersion { get; set; } = 1;
    public int DefinitionVersion { get; set; }
    public string SchemaVersion { get; set; } = "1.0";
    public CreateProjectPageDto Page { get; set; } = new();
    public List<PageEntityDependencySnapshotDto> Dependencies { get; set; } = new();
}

public class PageEntityDependencySnapshotDto
{
    public long EntityId { get; set; }
    public string? DataSourceKey { get; set; }
    public PageDependencyType DependencyType { get; set; }
    public string FieldIdsJson { get; set; } = "[]";
    public string SummaryJson { get; set; } = "{}";
    public string? SchemaHash { get; set; }
}

public class CreateProjectPageDto
{
    public long ProjectId { get; set; }
    public long ModuleId { get; set; }
    public string? ModuleName { get; set; }
    public string PageKey { get; set; } = string.Empty;
    public string PageName { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string RoutePath { get; set; } = string.Empty;
    public string? Icon { get; set; }
    public int OrderNum { get; set; }
    public ComplexPageType PageType { get; set; } = ComplexPageType.Custom;
    public long? LayoutPresetId { get; set; }
    public string? LayoutJson { get; set; }
    public PageSyncMode SyncMode { get; set; } = PageSyncMode.Auto;
    public bool IsMenuSynced { get; set; }
    public List<CreatePageDataSourceDto> DataSources { get; set; } = new();
    public List<CreatePageComponentBindingDto> Bindings { get; set; } = new();
}

public class UpdateProjectPageDto : CreateProjectPageDto
{
    public int? DefinitionVersion { get; set; }
}

public class PageDataSourceDto : EntityDto
{
    public long PageId { get; set; }
    public string SourceKey { get; set; } = string.Empty;
    public PageDataSourceType SourceType { get; set; }
    public long? ProjectDataSourceId { get; set; }
    public long? EntityId { get; set; }
    public string ServiceName { get; set; } = string.Empty;
    public string MethodName { get; set; } = string.Empty;
    public string DtoName { get; set; } = string.Empty;
    public PageDataShape DataShape { get; set; }
    public PageSyncMode SyncMode { get; set; }
    public string ParamsJson { get; set; } = "{}";
    public string? SchemaHash { get; set; }
    public DateTime? LastSyncedTime { get; set; }
    public int OrderNum { get; set; }
    public bool IsEnabled { get; set; }
}

public class CreatePageDataSourceDto
{
    public string SourceKey { get; set; } = string.Empty;
    public PageDataSourceType SourceType { get; set; } = PageDataSourceType.EntityService;
    public long? ProjectDataSourceId { get; set; }
    public long? EntityId { get; set; }
    public string? EntityName { get; set; }
    public string ServiceName { get; set; } = string.Empty;
    public string MethodName { get; set; } = string.Empty;
    public string DtoName { get; set; } = string.Empty;
    public PageDataShape DataShape { get; set; } = PageDataShape.Single;
    public PageSyncMode SyncMode { get; set; } = PageSyncMode.Auto;
    public string ParamsJson { get; set; } = "{}";
    public int OrderNum { get; set; }
    public bool IsEnabled { get; set; } = true;
}

public class PageComponentBindingDto : EntityDto
{
    public long PageId { get; set; }
    public string ComponentId { get; set; } = string.Empty;
    public string BindingId { get; set; } = string.Empty;
    public long DataSourceId { get; set; }
    public long? FieldId { get; set; }
    public PageBindingRole BindingRole { get; set; }
    public PageBindingStatus BindingStatus { get; set; }
    public PageSyncMode SyncMode { get; set; }
    public bool AutoIncludeNewFields { get; set; }
    public string? ControlType { get; set; }
    public string PropsJson { get; set; } = "{}";
    public string? LabelOverride { get; set; }
    public PageNodeOrigin Origin { get; set; }
    public string GeneratedGenId { get; set; } = string.Empty;
    public string? LastKnownPath { get; set; }
}

public class CreatePageComponentBindingDto
{
    public string ComponentId { get; set; } = string.Empty;
    public string? BindingId { get; set; }
    public string? GeneratedGenId { get; set; }
    public string? DataSourceKey { get; set; }
    public long? DataSourceId { get; set; }
    public long? FieldId { get; set; }
    public PageBindingRole BindingRole { get; set; } = PageBindingRole.Container;
    public PageBindingStatus BindingStatus { get; set; } = PageBindingStatus.Active;
    public PageSyncMode SyncMode { get; set; } = PageSyncMode.Auto;
    public bool AutoIncludeNewFields { get; set; }
    public string? ControlType { get; set; }
    public string PropsJson { get; set; } = "{}";
    public string? LabelOverride { get; set; }
    public PageNodeOrigin Origin { get; set; } = PageNodeOrigin.Generated;
    public string? LastKnownPath { get; set; }
}

public sealed class ComplexPageEventDefinitionDto
{
    public string EventName { get; set; } = "click";
    public List<ComplexPageActionDefinitionDto> Actions { get; set; } = new();
}

public sealed class ComplexPageActionDefinitionDto
{
    public string Type { get; set; } = string.Empty;
    public string? SourceKey { get; set; }
    public string? TargetComponentId { get; set; }
    public string? Path { get; set; }
    public string? Key { get; set; }
    public string? Value { get; set; }
    public string? Message { get; set; }
    public string? MessageType { get; set; }
}

public class PageEntityDependencyDto : EntityDto
{
    public long PageId { get; set; }
    public long EntityId { get; set; }
    public long? DataSourceId { get; set; }
    public PageDependencyType DependencyType { get; set; }
    public string FieldIdsJson { get; set; } = "[]";
    public string SummaryJson { get; set; } = "{}";
    public string? SchemaHash { get; set; }
}

public class PageLayoutPresetDto : EntityDto
{
    public string PresetKey { get; set; } = string.Empty;
    public string PresetName { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string LayoutJson { get; set; } = string.Empty;
    public int Sort { get; set; }
    public bool IsSystem { get; set; }
}

public class EntityServiceCapabilityDto
{
    public long EntityId { get; set; }
    public string EntityName { get; set; } = string.Empty;
    public EntityGenerationMode GenerationMode { get; set; }
    public string ServiceName { get; set; } = string.Empty;
    public List<EntityServiceMethodDto> Methods { get; set; } = new();
    public List<EntityFieldCapabilityDto> Fields { get; set; } = new();
}

public class EntityServiceMethodDto
{
    public string MethodName { get; set; } = string.Empty;
    public string DtoName { get; set; } = string.Empty;
    public List<PageDataShape> SupportedShapes { get; set; } = new();
}

public class EntityFieldCapabilityDto
{
    public long FieldId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string DataType { get; set; } = string.Empty;
    public bool IsNullable { get; set; }
    public bool IsPrimaryKey { get; set; }
}

public class PageImpactDto
{
    public long PageId { get; set; }
    public string PageKey { get; set; } = string.Empty;
    public string PageName { get; set; } = string.Empty;
    public PageSyncStatus SyncStatus { get; set; }
    public List<PageDataSourceDto> DataSources { get; set; } = new();
    public List<PageComponentBindingDto> Bindings { get; set; } = new();
}

public class ComplexPageGenerationResultDto
{
    public bool Success { get; set; }
    public bool HasConflicts { get; set; }
    public long PageId { get; set; }
    public string PageKey { get; set; } = string.Empty;
    public string OutputDirectory { get; set; } = string.Empty;
    public string VuePath { get; set; } = string.Empty;
    public string TreePath { get; set; } = string.Empty;
    public string VueHash { get; set; } = string.Empty;
    public string? BackupDirectory { get; set; }
    public string? Error { get; set; }
    public List<string> Files { get; set; } = new();
    public List<GeneratedRegionConflictDto> Conflicts { get; set; } = new();
}

public class GeneratedRegionConflictDto
{
    public string GenId { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
}

public class ComplexPageExternalChangeDto
{
    public long PageId { get; set; }
    public string VuePath { get; set; } = string.Empty;
    public string CurrentVueHash { get; set; } = string.Empty;
    public string? LastGeneratedVueHash { get; set; }
    public bool Changed { get; set; }
    public bool CanImport { get; set; }
    public int ExternalNodeCount { get; set; }
    public int MatchedNodeCount { get; set; }
    public string? BackupDirectory { get; set; }
    public string? VueCompilerVersion { get; set; }
    public bool ScriptSetupPreserved { get; set; }
    public string? ScriptSetupBackupPath { get; set; }
    public string? Error { get; set; }
    public List<ExternalTreeDiffItemDto> Diffs { get; set; } = new();
}

public class ExternalTreeDiffItemDto
{
    public string ChangeType { get; set; } = string.Empty;
    public string ComponentId { get; set; } = string.Empty;
    public string Tag { get; set; } = string.Empty;
    public string? GenId { get; set; }
    public string Detail { get; set; } = string.Empty;
}

public class RebindPageComponentDto
{
    public string ComponentId { get; set; } = string.Empty;
    public string? BindingId { get; set; }
    public long DataSourceId { get; set; }
    public long? FieldId { get; set; }
    public PageBindingRole BindingRole { get; set; } = PageBindingRole.Container;
    public PageSyncMode SyncMode { get; set; } = PageSyncMode.Auto;
    public bool AutoIncludeNewFields { get; set; }
    public string? ControlType { get; set; }
    public string PropsJson { get; set; } = "{}";
    public string? LabelOverride { get; set; }
}

public class ComplexPageSyncPreviewDto
{
    public long PageId { get; set; }
    public string PageKey { get; set; } = string.Empty;
    public bool RequiresSync { get; set; }
    public List<ComplexPageFieldChangeDto> Changes { get; set; } = new();
}

public class ComplexPageFieldChangeDto
{
    public long EntityId { get; set; }
    public long FieldId { get; set; }
    public string ChangeType { get; set; } = string.Empty;
    public string? PreviousName { get; set; }
    public string? CurrentName { get; set; }
    public string? PreviousDataType { get; set; }
    public string? CurrentDataType { get; set; }
    public List<string> BindingIds { get; set; } = new();
}

public class ComplexPageSyncResultDto
{
    public bool Success { get; set; }
    public long PageId { get; set; }
    public int AddedBindings { get; set; }
    public int InvalidBindings { get; set; }
    public int ReviewBindings { get; set; }
    public int DefinitionVersion { get; set; }
    public List<ComplexPageFieldChangeDto> Changes { get; set; } = new();
    public string? Error { get; set; }
}
