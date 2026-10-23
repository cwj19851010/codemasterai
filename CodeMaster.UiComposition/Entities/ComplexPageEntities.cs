using CodeMaster.Core.Entities;
using CodeMaster.UiComposition.Entities;
using SqlSugar;

namespace CodeMaster.Domain.Entities.CodeGen;

public enum ComplexPageType
{
    Custom = 1,
    Workspace = 2,
    Dashboard = 3,
    Form = 4,
    List = 5,
    Detail = 6
}

public enum PageSyncStatus
{
    Draft = 1,
    Generated = 2,
    NeedsSync = 3,
    Conflict = 4,
    GenerationFailed = 5
}

public enum PageDataSourceType
{
    EntityService = 1,
    View = 2,
    SqlQuery = 3,
    Api = 4
}

public enum PageDataShape
{
    Single = 1,
    Collection = 2,
    Paged = 3,
    Options = 4,
    Aggregate = 5
}

public enum PageSyncMode
{
    Auto = 1,
    Manual = 2
}

public enum PageBindingRole
{
    Container = 1,
    Field = 2,
    Table = 3,
    Column = 4,
    Form = 5,
    Detail = 6,
    Options = 7,
    Event = 8
}

public enum PageBindingStatus
{
    Active = 1,
    Invalid = 2,
    NeedsReview = 3
}

public enum PageDependencyType
{
    Direct = 1,
    Relation = 2,
    Aggregate = 3
}

public enum PageNodeOrigin
{
    Generated = 1,
    User = 2,
    External = 3
}

[SugarTable("sys_project_page")]
public class ProjectPage : EntityBaseWithTenant
{
    [SugarColumn(ColumnName = "project_id", IsNullable = false)]
    public long ProjectId { get; set; }

    [SugarColumn(ColumnName = "module_id", IsNullable = false)]
    public long ModuleId { get; set; }

    [SugarColumn(ColumnName = "page_key", Length = 100, IsNullable = false)]
    public string PageKey { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "page_name", Length = 200, IsNullable = false)]
    public string PageName { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "description", Length = 500, IsNullable = true)]
    public string? Description { get; set; }

    [SugarColumn(ColumnName = "route_path", Length = 300, IsNullable = false)]
    public string RoutePath { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "icon", Length = 100, IsNullable = true)]
    public string? Icon { get; set; }

    [SugarColumn(ColumnName = "order_num", IsNullable = false)]
    public int OrderNum { get; set; }

    [SugarColumn(ColumnName = "page_type", IsNullable = false)]
    public ComplexPageType PageType { get; set; } = ComplexPageType.Custom;

    [SugarColumn(ColumnName = "layout_preset_id", IsNullable = true)]
    public long? LayoutPresetId { get; set; }

    [SugarColumn(ColumnName = "layout_json", ColumnDataType = "text", IsNullable = false)]
    public string LayoutJson { get; set; } = "{\"version\":1,\"children\":[]}";

    [SugarColumn(ColumnName = "definition_version", IsNullable = false)]
    public int DefinitionVersion { get; set; } = 1;

    [SugarColumn(ColumnName = "schema_version", Length = 32, IsNullable = false)]
    public string SchemaVersion { get; set; } = "1";

    [SugarColumn(ColumnName = "sync_status", IsNullable = false)]
    public PageSyncStatus SyncStatus { get; set; } = PageSyncStatus.Draft;

    [SugarColumn(ColumnName = "sync_mode", IsNullable = false)]
    public PageSyncMode SyncMode { get; set; } = PageSyncMode.Auto;

    [SugarColumn(ColumnName = "is_menu_synced", IsNullable = false)]
    public bool IsMenuSynced { get; set; }

    [SugarColumn(ColumnName = "target_directory", Length = 500, IsNullable = true)]
    public string? TargetDirectory { get; set; }

    [SugarColumn(ColumnName = "last_generated_time", IsNullable = true)]
    public DateTime? LastGeneratedTime { get; set; }

    [SugarColumn(ColumnName = "last_generated_vue_hash", Length = 64, IsNullable = true)]
    public string? LastGeneratedVueHash { get; set; }

    [SugarColumn(ColumnName = "last_definition_hash", Length = 64, IsNullable = true)]
    public string? LastDefinitionHash { get; set; }

    [SugarColumn(ColumnName = "last_error", Length = 2000, IsNullable = true)]
    public string? LastError { get; set; }
}

[SugarTable("sys_page_data_source")]
public class PageDataSource : EntityBaseWithTenant
{
    [SugarColumn(ColumnName = "page_id", IsNullable = false)]
    public long PageId { get; set; }

    [SugarColumn(ColumnName = "source_key", Length = 100, IsNullable = false)]
    public string SourceKey { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "source_type", IsNullable = false)]
    public PageDataSourceType SourceType { get; set; } = PageDataSourceType.EntityService;

    [SugarColumn(ColumnName = "project_data_source_id", IsNullable = true)]
    public long? ProjectDataSourceId { get; set; }

    [SugarColumn(ColumnName = "entity_id", IsNullable = true)]
    public long? EntityId { get; set; }

    [SugarColumn(ColumnName = "service_name", Length = 200, IsNullable = false)]
    public string ServiceName { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "method_name", Length = 200, IsNullable = false)]
    public string MethodName { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "dto_name", Length = 300, IsNullable = false)]
    public string DtoName { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "data_shape", IsNullable = false)]
    public PageDataShape DataShape { get; set; } = PageDataShape.Single;

    [SugarColumn(ColumnName = "sync_mode", IsNullable = false)]
    public PageSyncMode SyncMode { get; set; } = PageSyncMode.Auto;

    [SugarColumn(ColumnName = "params_json", ColumnDataType = "text", IsNullable = false)]
    public string ParamsJson { get; set; } = "{}";

    [SugarColumn(ColumnName = "schema_hash", Length = 64, IsNullable = true)]
    public string? SchemaHash { get; set; }

    [SugarColumn(ColumnName = "last_synced_time", IsNullable = true)]
    public DateTime? LastSyncedTime { get; set; }

    [SugarColumn(ColumnName = "order_num", IsNullable = false)]
    public int OrderNum { get; set; }

    [SugarColumn(ColumnName = "is_enabled", IsNullable = false)]
    public bool IsEnabled { get; set; } = true;
}

[SugarTable("sys_page_component_binding")]
public class PageComponentBinding : EntityBaseWithTenant
{
    [SugarColumn(ColumnName = "page_id", IsNullable = false)]
    public long PageId { get; set; }

    [SugarColumn(ColumnName = "component_id", Length = 100, IsNullable = false)]
    public string ComponentId { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "binding_id", Length = 100, IsNullable = false)]
    public string BindingId { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "data_source_id", IsNullable = false)]
    public long DataSourceId { get; set; }

    [SugarColumn(ColumnName = "field_id", IsNullable = true)]
    public long? FieldId { get; set; }

    [SugarColumn(ColumnName = "binding_role", IsNullable = false)]
    public PageBindingRole BindingRole { get; set; } = PageBindingRole.Container;

    [SugarColumn(ColumnName = "binding_status", IsNullable = false)]
    public PageBindingStatus BindingStatus { get; set; } = PageBindingStatus.Active;

    [SugarColumn(ColumnName = "sync_mode", IsNullable = false)]
    public PageSyncMode SyncMode { get; set; } = PageSyncMode.Auto;

    [SugarColumn(ColumnName = "auto_include_new_fields", IsNullable = false)]
    public bool AutoIncludeNewFields { get; set; }

    [SugarColumn(ColumnName = "control_type", Length = 100, IsNullable = true)]
    public string? ControlType { get; set; }

    [SugarColumn(ColumnName = "props_json", ColumnDataType = "text", IsNullable = false)]
    public string PropsJson { get; set; } = "{}";

    [SugarColumn(ColumnName = "label_override", Length = 200, IsNullable = true)]
    public string? LabelOverride { get; set; }

    [SugarColumn(ColumnName = "origin", IsNullable = false)]
    public PageNodeOrigin Origin { get; set; } = PageNodeOrigin.Generated;

    [SugarColumn(ColumnName = "generated_gen_id", Length = 160, IsNullable = false)]
    public string GeneratedGenId { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "last_known_path", Length = 500, IsNullable = true)]
    public string? LastKnownPath { get; set; }
}

[SugarTable("sys_page_entity_dependency")]
public class PageEntityDependency : EntityBaseWithTenant
{
    [SugarColumn(ColumnName = "page_id", IsNullable = false)]
    public long PageId { get; set; }

    [SugarColumn(ColumnName = "entity_id", IsNullable = false)]
    public long EntityId { get; set; }

    [SugarColumn(ColumnName = "data_source_id", IsNullable = true)]
    public long? DataSourceId { get; set; }

    [SugarColumn(ColumnName = "dependency_type", IsNullable = false)]
    public PageDependencyType DependencyType { get; set; } = PageDependencyType.Direct;

    [SugarColumn(ColumnName = "field_ids_json", ColumnDataType = "text", IsNullable = false)]
    public string FieldIdsJson { get; set; } = "[]";

    [SugarColumn(ColumnName = "summary_json", ColumnDataType = "text", IsNullable = false)]
    public string SummaryJson { get; set; } = "{}";

    [SugarColumn(ColumnName = "schema_hash", Length = 64, IsNullable = true)]
    public string? SchemaHash { get; set; }
}

[SugarTable("sys_page_layout_preset")]
public class PageLayoutPreset : EntityBaseWithTenant
{
    [SugarColumn(ColumnName = "preset_key", Length = 100, IsNullable = false)]
    public string PresetKey { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "preset_name", Length = 200, IsNullable = false)]
    public string PresetName { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "description", Length = 500, IsNullable = true)]
    public string? Description { get; set; }

    [SugarColumn(ColumnName = "layout_json", ColumnDataType = "text", IsNullable = false)]
    public string LayoutJson { get; set; } = "{\"version\":1,\"children\":[]}";

    [SugarColumn(ColumnName = "sort", IsNullable = false)]
    public int Sort { get; set; }

    [SugarColumn(ColumnName = "is_system", IsNullable = false)]
    public bool IsSystem { get; set; } = true;
}

/// <summary>
/// 项目级数据源资产。一个数据源定义一次，可被多个复杂页面和传统页面引用。
/// 数据源变化（SchemaHash 变化）时通过 <see cref="PageDataSourceBinding"/> 反向找到所有受影响页面。
/// </summary>
[SugarTable("sys_project_data_source")]
public class ProjectDataSource : EntityBaseWithTenant
{
    [SugarColumn(ColumnName = "project_id", IsNullable = false)]
    public long ProjectId { get; set; }

    [SugarColumn(ColumnName = "source_key", Length = 100, IsNullable = false)]
    public string SourceKey { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "display_name", Length = 200, IsNullable = false)]
    public string DisplayName { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "source_type", IsNullable = false)]
    public PageDataSourceType SourceType { get; set; } = PageDataSourceType.EntityService;

    [SugarColumn(ColumnName = "entity_id", IsNullable = true)]
    public long? EntityId { get; set; }

    [SugarColumn(ColumnName = "service_name", Length = 200, IsNullable = false)]
    public string ServiceName { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "method_name", Length = 200, IsNullable = false)]
    public string MethodName { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "dto_name", Length = 300, IsNullable = false)]
    public string DtoName { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "data_shape", IsNullable = false)]
    public PageDataShape DataShape { get; set; } = PageDataShape.Single;

    [SugarColumn(ColumnName = "sync_mode", IsNullable = false)]
    public PageSyncMode SyncMode { get; set; } = PageSyncMode.Auto;

    [SugarColumn(ColumnName = "params_json", ColumnDataType = "text", IsNullable = false)]
    public string ParamsJson { get; set; } = "{}";

    [SugarColumn(ColumnName = "view_name", Length = 200, IsNullable = true)]
    public string? ViewName { get; set; }

    [SugarColumn(ColumnName = "openapi_document_id", IsNullable = true)]
    public long? OpenApiDocumentId { get; set; }

    [SugarColumn(ColumnName = "operation_key", Length = 500, IsNullable = true)]
    public string? OperationKey { get; set; }

    [SugarColumn(ColumnName = "http_method", Length = 20, IsNullable = true)]
    public string? HttpMethod { get; set; }

    [SugarColumn(ColumnName = "route_path", Length = 1000, IsNullable = true)]
    public string? RoutePath { get; set; }

    [SugarColumn(ColumnName = "summary", Length = 500, IsNullable = true)]
    public string? Summary { get; set; }

    [SugarColumn(ColumnName = "description", Length = 2000, IsNullable = true)]
    public string? Description { get; set; }

    [SugarColumn(ColumnName = "request_entity_id", IsNullable = true)]
    public long? RequestEntityId { get; set; }

    [SugarColumn(ColumnName = "response_entity_id", IsNullable = true)]
    public long? ResponseEntityId { get; set; }

    [SugarColumn(ColumnName = "operation_contract_json", ColumnDataType = "text", IsNullable = true)]
    public string? OperationContractJson { get; set; }

    [SugarColumn(ColumnName = "openapi_sync_state", IsNullable = true)]
    public OpenApiSyncState? OpenApiSyncState { get; set; }

    [SugarColumn(ColumnName = "schema_hash", Length = 64, IsNullable = true)]
    public string? SchemaHash { get; set; }

    [SugarColumn(ColumnName = "last_synced_time", IsNullable = true)]
    public DateTime? LastSyncedTime { get; set; }

    [SugarColumn(ColumnName = "order_num", IsNullable = false)]
    public int OrderNum { get; set; }

    [SugarColumn(ColumnName = "is_enabled", IsNullable = false)]
    public bool IsEnabled { get; set; } = true;
}

/// <summary>
/// 页面与项目数据源的绑定（多对多）。复杂页面用 PageId 定位，传统 CRUD 页面用
/// ModuleEntityId + PageType 定位（无配置记录时传统页面仍走隐式实体数据源，行为不变）。
/// </summary>
[SugarTable("sys_page_data_source_binding")]
public class PageDataSourceBinding : EntityBaseWithTenant
{
    [SugarColumn(ColumnName = "page_id", IsNullable = true)]
    public long? PageId { get; set; }

    [SugarColumn(ColumnName = "module_entity_id", IsNullable = true)]
    public long? ModuleEntityId { get; set; }

    [SugarColumn(ColumnName = "page_type", Length = 50, IsNullable = true)]
    public string? PageType { get; set; }

    [SugarColumn(ColumnName = "data_source_id", IsNullable = false)]
    public long DataSourceId { get; set; }

    [SugarColumn(ColumnName = "binding_role", IsNullable = false)]
    public PageBindingRole BindingRole { get; set; } = PageBindingRole.Container;

    [SugarColumn(ColumnName = "binding_status", IsNullable = false)]
    public PageBindingStatus BindingStatus { get; set; } = PageBindingStatus.Active;

    [SugarColumn(ColumnName = "sync_mode", IsNullable = false)]
    public PageSyncMode SyncMode { get; set; } = PageSyncMode.Auto;

    [SugarColumn(ColumnName = "last_known_schema_hash", Length = 64, IsNullable = true)]
    public string? LastKnownSchemaHash { get; set; }
}
