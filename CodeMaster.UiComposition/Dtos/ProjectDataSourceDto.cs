using CodeMaster.Core.Dtos;
using CodeMaster.Domain.Entities.CodeGen;
using CodeMaster.UiComposition.Entities;

namespace CodeMaster.Application.Dtos.CodeGen;

public class ProjectDataSourceDto : EntityDto
{
    public long ProjectId { get; set; }
    public string SourceKey { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public PageDataSourceType SourceType { get; set; }
    public long? EntityId { get; set; }
    public string ServiceName { get; set; } = string.Empty;
    public string MethodName { get; set; } = string.Empty;
    public string DtoName { get; set; } = string.Empty;
    public PageDataShape DataShape { get; set; }
    public PageSyncMode SyncMode { get; set; }
    public string ParamsJson { get; set; } = "{}";
    public string? ViewName { get; set; }
    public long? OpenApiDocumentId { get; set; }
    public string? OperationKey { get; set; }
    public string? HttpMethod { get; set; }
    public string? RoutePath { get; set; }
    public string? Summary { get; set; }
    public string? Description { get; set; }
    public long? RequestEntityId { get; set; }
    public long? ResponseEntityId { get; set; }
    public string? OperationContractJson { get; set; }
    public OpenApiSyncState? OpenApiSyncState { get; set; }
    public string? SchemaHash { get; set; }
    public DateTime? LastSyncedTime { get; set; }
    public int OrderNum { get; set; }
    public bool IsEnabled { get; set; }
}

public class CreateProjectDataSourceDto
{
    public long ProjectId { get; set; }
    public string SourceKey { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public PageDataSourceType SourceType { get; set; } = PageDataSourceType.EntityService;
    public long? EntityId { get; set; }
    public string ServiceName { get; set; } = string.Empty;
    public string MethodName { get; set; } = string.Empty;
    public string DtoName { get; set; } = string.Empty;
    public PageDataShape DataShape { get; set; } = PageDataShape.Single;
    public PageSyncMode SyncMode { get; set; } = PageSyncMode.Auto;
    public string ParamsJson { get; set; } = "{}";
    public string? ViewName { get; set; }
    public int OrderNum { get; set; }
    public bool IsEnabled { get; set; } = true;
}

public class UpdateProjectDataSourceDto : CreateProjectDataSourceDto
{
}

public class PageDataSourceBindingDto : EntityDto
{
    public long? PageId { get; set; }
    public long? ModuleEntityId { get; set; }
    public string? PageType { get; set; }
    public long DataSourceId { get; set; }
    public PageBindingRole BindingRole { get; set; }
    public PageBindingStatus BindingStatus { get; set; }
    public PageSyncMode SyncMode { get; set; }
    public string? LastKnownSchemaHash { get; set; }
}

public class CreatePageDataSourceBindingDto
{
    public long? PageId { get; set; }
    public long? ModuleEntityId { get; set; }
    public string? PageType { get; set; }
    public long DataSourceId { get; set; }
    public PageBindingRole BindingRole { get; set; } = PageBindingRole.Container;
    public PageBindingStatus BindingStatus { get; set; } = PageBindingStatus.Active;
    public PageSyncMode SyncMode { get; set; } = PageSyncMode.Auto;
}

/// <summary>
/// 数据源变化后的受影响页面摘要（复杂页面与传统页面统一表达）。
/// </summary>
public class DataSourceImpactDto
{
    public long DataSourceId { get; set; }
    public long? EntityId { get; set; }
    public long? PageId { get; set; }
    public string PageKey { get; set; } = string.Empty;
    public string PageName { get; set; } = string.Empty;
    public long? ModuleEntityId { get; set; }
    public string? PageType { get; set; }
    public PageSyncStatus SyncStatus { get; set; }
    public bool SchemaChanged { get; set; }
    public bool DataSourceDeleted { get; set; }
    public PageBindingStatus BindingStatus { get; set; }
}

public class DataSourceQueryDto : PagedQueryDto
{
    public long ProjectId { get; set; }
    public string? Keyword { get; set; }
    public PageDataSourceType? SourceType { get; set; }
}

public class ProjectDataSourceCapabilityCatalogDto
{
    public List<EntityDataSourceCapabilityDto> Entities { get; set; } = new();
    public List<ServiceDataSourceCapabilityDto> ApiServices { get; set; } = new();
    public List<ViewDataSourceCapabilityDto> Views { get; set; } = new();
}

public class EntityDataSourceCapabilityDto
{
    public long EntityId { get; set; }
    public string EntityName { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string ServiceName { get; set; } = string.Empty;
    public string DtoName { get; set; } = string.Empty;
    public bool HasPrimaryKey { get; set; }
    public List<DataSourceMethodCapabilityDto> Methods { get; set; } = new();
}

public class ServiceDataSourceCapabilityDto
{
    public string ServiceName { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public List<DataSourceMethodCapabilityDto> Methods { get; set; } = new();
}

public class ViewDataSourceCapabilityDto
{
    public long DataSourceId { get; set; }
    public long ProjectId { get; set; }
    public string SourceKey { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string? ViewName { get; set; }
    public string ServiceName { get; set; } = string.Empty;
    public string MethodName { get; set; } = string.Empty;
    public string DtoName { get; set; } = string.Empty;
    public PageDataShape DataShape { get; set; }
    public string? SchemaHash { get; set; }
}

public class DataSourceMethodCapabilityDto
{
    public string MethodName { get; set; } = string.Empty;
    public string DtoName { get; set; } = string.Empty;
    public PageDataShape DataShape { get; set; }
    public string ResultType { get; set; } = string.Empty;
    public string HttpMethod { get; set; } = "GET";
    public string RoutePath { get; set; } = string.Empty;
    public List<DataSourceParameterCapabilityDto> Parameters { get; set; } = new();
}

public class DataSourceParameterCapabilityDto
{
    public string Name { get; set; } = string.Empty;
    public string TypeName { get; set; } = string.Empty;
    public bool IsOptional { get; set; }
    public bool IsRouteParameter { get; set; }
}

public class AffectedPageQueryDto
{
    public long? EntityId { get; set; }
    public long? DataSourceId { get; set; }
}

public class DataSourceChangeResultDto
{
    public long DataSourceId { get; set; }
    public int AffectedRows { get; set; }
    public string ChangeType { get; set; } = string.Empty;
    public bool SchemaChanged { get; set; }
    public bool DataSourceDeleted { get; set; }
    public int AffectedPageCount { get; set; }
    public string Summary { get; set; } = string.Empty;
    public List<DataSourceImpactDto> AffectedPages { get; set; } = new();
}
