using CodeMaster.Application.Dtos.CodeGen;
using CodeMaster.Core.Authorization;
using CodeMaster.Core.Dtos;
using CodeMaster.Core.DynamicApi;
using CodeMaster.Domain.Entities.CodeGen;
using Mapster;
using SqlSugar;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CodeMaster.Application.Services.CodeGen;

/// <summary>
/// 项目级数据源资产管理：定义一次、多页面复用；变化时通过页面绑定反向传播。
/// 复杂页面用 PageId 引用，传统 CRUD 页面用 ModuleEntityId + PageType 引用（无配置时行为不变）。
/// </summary>
[DynamicApiPermission(requirePermission: true)]
public sealed class ProjectDataSourceService : IProjectDataSourceService
{
    private static readonly Regex SourceKeyPattern = new("^[A-Za-z][A-Za-z0-9_-]*$", RegexOptions.Compiled);
    private readonly ISqlSugarClient _db;
    private readonly IApplicationServiceCapabilityCatalog? _capabilityCatalog;

    public ProjectDataSourceService(
        ISqlSugarClient db,
        IApplicationServiceCapabilityCatalog? capabilityCatalog = null)
    {
        _db = db;
        _capabilityCatalog = capabilityCatalog;
    }

    [Permission("codegen:projectpage:list")]
    public async Task<List<ProjectDataSourceDto>> GetListAsync(DataSourceQueryDto query)
    {
        query ??= new DataSourceQueryDto();
        return (await CreateFilteredQuery(query).ToListAsync()).Adapt<List<ProjectDataSourceDto>>();
    }

    [Permission("codegen:projectpage:list")]
    public async Task<PagedResultDto<ProjectDataSourceDto>> GetPagedListAsync(DataSourceQueryDto query)
    {
        query ??= new DataSourceQueryDto();
        query.PageNum = Math.Max(1, query.PageNum);
        query.PageSize = Math.Clamp(query.PageSize, 1, 200);
        RefAsync<int> total = 0;
        var items = await CreateFilteredQuery(query)
            .ToPageListAsync(query.PageNum, query.PageSize, total);
        return new PagedResultDto<ProjectDataSourceDto>
        {
            Items = items.Adapt<List<ProjectDataSourceDto>>(),
            Total = total,
            PageNum = query.PageNum,
            PageSize = query.PageSize
        };
    }

    [Permission("codegen:projectpage:view")]
    public async Task<ProjectDataSourceDto?> GetByIdAsync(long id)
    {
        var source = await _db.Queryable<ProjectDataSource>()
            .Where(item => item.Id == id && !item.IsDeleted)
            .FirstAsync();
        return source?.Adapt<ProjectDataSourceDto>();
    }

    [Permission("codegen:projectpage:list")]
    public async Task<ProjectDataSourceCapabilityCatalogDto> GetCapabilityCatalogAsync(long projectId)
    {
        var project = await _db.Queryable<Project>()
            .Where(item => item.Id == projectId && !item.IsDeleted)
            .FirstAsync() ?? throw new InvalidOperationException($"项目 {projectId} 不存在。");

        var entities = await _db.Queryable<ModuleEntity>()
            .Where(item => item.ProjectId == project.Id && item.TenantId == project.TenantId &&
                           !item.IsDeleted && item.GenerationMode == EntityGenerationMode.FullStack)
            .OrderBy(item => item.OrderNum)
            .OrderBy(item => item.Id)
            .ToListAsync();
        var catalog = new ProjectDataSourceCapabilityCatalogDto
        {
            Entities = entities.Select(CreateEntityCapability).ToList(),
            ApiServices = (_capabilityCatalog?.Services ?? Array.Empty<ApplicationServiceCapability>())
                .Select(CreateServiceCapability)
                .ToList(),
            Views = (await _db.Queryable<ProjectDataSource>()
                    .Where(item => item.ProjectId == project.Id
                                   && item.TenantId == project.TenantId
                                   && item.SourceType == PageDataSourceType.View
                                   && !item.IsDeleted)
                    .OrderBy(item => item.OrderNum)
                    .OrderBy(item => item.Id)
                    .ToListAsync())
                .Select(item => new ViewDataSourceCapabilityDto
                {
                    DataSourceId = item.Id,
                    ProjectId = item.ProjectId,
                    SourceKey = item.SourceKey,
                    DisplayName = item.DisplayName,
                    ViewName = item.ViewName,
                    ServiceName = item.ServiceName,
                    MethodName = item.MethodName,
                    DtoName = item.DtoName,
                    DataShape = item.DataShape,
                    SchemaHash = item.SchemaHash
                })
                .ToList()
        };
        return catalog;
    }

    [Permission("codegen:projectpage:create")]
    public async Task<long> CreateAsync(CreateProjectDataSourceDto input)
    {
        var project = await ValidateInputAsync(input, null);
        var schemaHash = await ComputeSchemaHashCoreAsync(input);
        var entity = input.Adapt<ProjectDataSource>();
        entity.TenantId = project.TenantId;
        entity.SchemaHash = schemaHash;
        entity.CreateBy = "CodeMaster";
        entity.CreateUserId = 0;
        entity.UpdateUserId = 0;
        entity.CreateTime = DateTime.UtcNow;
        return await _db.Insertable(entity).ExecuteReturnSnowflakeIdAsync();
    }

    [Permission("codegen:projectpage:create")]
    public async Task<long> RegisterViewAsync(CreateProjectDataSourceDto input)
    {
        if (input.SourceType != PageDataSourceType.View)
            throw new ArgumentException("视图注册接口只接受 View 数据源。");
        return await CreateAsync(input);
    }

    [Permission("codegen:projectpage:update")]
    public async Task<int> UpdateAsync(long id, UpdateProjectDataSourceDto input)
        => (await UpdateWithImpactAsync(id, input)).AffectedRows;

    [Permission("codegen:projectpage:update")]
    public async Task<DataSourceChangeResultDto> UpdateWithImpactAsync(long id, UpdateProjectDataSourceDto input)
    {
        var existing = await _db.Queryable<ProjectDataSource>()
            .Where(item => item.Id == id && !item.IsDeleted)
            .FirstAsync() ?? throw new InvalidOperationException($"数据源 {id} 不存在。");
        if (existing.OpenApiDocumentId.HasValue)
            throw new InvalidOperationException("OpenAPI 导入的数据源必须通过重新导入 OpenAPI 文档更新。");
        if (input.ProjectId != existing.ProjectId)
            throw new InvalidOperationException("数据源创建后不能迁移到其他项目。");
        await ValidateInputAsync(input, id);

        var entity = input.Adapt<ProjectDataSource>();
        entity.Id = id;
        entity.TenantId = existing.TenantId;
        entity.SchemaHash = await ComputeSchemaHashCoreAsync(input);
        entity.LastSyncedTime = DateTime.UtcNow;
        entity.UpdateBy = "CodeMaster";
        entity.UpdateUserId = 0;
        entity.UpdateTime = DateTime.UtcNow;
        var changed = await _db.Updateable(entity)
            .IgnoreColumns(item => new { item.IsDeleted, item.DeleteTime, item.CreateTime, item.CreateBy })
            .ExecuteCommandAsync();

        var schemaChanged = !string.Equals(existing.SchemaHash, entity.SchemaHash, StringComparison.OrdinalIgnoreCase);
        if (schemaChanged)
            await PropagateSchemaChangeAsync(id, PageBindingStatus.NeedsReview);
        var affectedPages = await GetAffectedPagesCoreAsync(id, includeDeletedSource: true);
        return CreateChangeResult(id, changed, "Updated", schemaChanged, false, affectedPages);
    }

    [Permission("codegen:projectpage:delete")]
    public async Task<int> DeleteAsync(long id)
        => (await DeleteWithImpactAsync(id)).AffectedRows;

    [Permission("codegen:projectpage:delete")]
    public async Task<DataSourceChangeResultDto> DeleteWithImpactAsync(long id)
    {
        var source = await _db.Queryable<ProjectDataSource>()
            .Where(item => item.Id == id && !item.IsDeleted)
            .FirstAsync() ?? throw new InvalidOperationException($"数据源 {id} 不存在。");
        if (source.OpenApiDocumentId.HasValue)
            throw new InvalidOperationException("OpenAPI 导入的数据源必须通过 OpenAPI 文档的变更确认流程删除。");
        var affectedBeforeDelete = await GetAffectedPagesCoreAsync(id, includeDeletedSource: true);
        var affected = await _db.Updateable<ProjectDataSource>()
            .SetColumns(item => new ProjectDataSource { IsDeleted = true, DeleteTime = DateTime.UtcNow })
            .Where(item => item.Id == id && !item.IsDeleted)
            .ExecuteCommandAsync();
        await PropagateSchemaChangeAsync(id, PageBindingStatus.Invalid);
        var affectedPages = await GetAffectedPagesCoreAsync(id, includeDeletedSource: true);
        if (affectedPages.Count == 0)
            affectedPages = affectedBeforeDelete;
        return CreateChangeResult(id, affected, "Deleted", true, true, affectedPages);
    }

    [Permission("codegen:projectpage:view")]
    public async Task<string> ComputeSchemaHashAsync(long id)
    {
        var source = await _db.Queryable<ProjectDataSource>()
            .Where(item => item.Id == id && !item.IsDeleted)
            .FirstAsync() ?? throw new InvalidOperationException($"数据源 {id} 不存在。");
        return await ComputeSchemaHashCoreAsync(source.Adapt<CreateProjectDataSourceDto>());
    }

    /// <summary>
    /// 数据源变化后查找所有引用页面：复杂页面标记 NeedsSync，传统页面记录 SchemaHash 变化。
    /// </summary>
    [Permission("codegen:projectpage:view")]
    public async Task<List<DataSourceImpactDto>> GetAffectedPagesAsync(long dataSourceId)
    {
        return await GetAffectedPagesCoreAsync(dataSourceId, includeDeletedSource: true);
    }

    [Permission("codegen:projectpage:view")]
    public async Task<List<DataSourceImpactDto>> QueryAffectedPagesAsync(AffectedPageQueryDto query)
    {
        if (query == null || query.EntityId.HasValue == query.DataSourceId.HasValue)
            throw new ArgumentException("必须且只能指定 EntityId 或 DataSourceId。");

        var dataSourceIds = query.DataSourceId.HasValue
            ? new List<long> { query.DataSourceId.Value }
            : await _db.Queryable<ProjectDataSource>()
                .Where(item => item.EntityId == query.EntityId!.Value)
                .Select(item => item.Id)
                .ToListAsync();
        var result = new List<DataSourceImpactDto>();
        foreach (var dataSourceId in dataSourceIds.Distinct())
            result.AddRange(await GetAffectedPagesCoreAsync(dataSourceId, includeDeletedSource: true));
        return result
            .GroupBy(item => new { item.DataSourceId, item.PageId, item.ModuleEntityId, item.PageType })
            .Select(group => group.First())
            .ToList();
    }

    private async Task<List<DataSourceImpactDto>> GetAffectedPagesCoreAsync(long dataSourceId, bool includeDeletedSource)
    {
        var sourceQuery = _db.Queryable<ProjectDataSource>();
        if (includeDeletedSource)
            sourceQuery = sourceQuery.ClearFilter();
        sourceQuery = sourceQuery.Where(item => item.Id == dataSourceId);
        if (!includeDeletedSource)
            sourceQuery = sourceQuery.Where(item => !item.IsDeleted);
        var source = await sourceQuery.FirstAsync();
        var bindings = await _db.Queryable<PageDataSourceBinding>()
            .Where(item => item.DataSourceId == dataSourceId && !item.IsDeleted)
            .ToListAsync();
        if (bindings.Count == 0) return new List<DataSourceImpactDto>();

        var pageIds = bindings.Where(item => item.PageId.HasValue).Select(item => item.PageId!.Value).Distinct().ToList();
        var pages = pageIds.Count == 0
            ? new List<ProjectPage>()
            : await _db.Queryable<ProjectPage>().Where(item => pageIds.Contains(item.Id) && !item.IsDeleted).ToListAsync();
        var pagesById = pages.ToDictionary(item => item.Id);

        var result = new List<DataSourceImpactDto>();
        foreach (var binding in bindings)
        {
            var schemaChanged = source != null
                ? !string.Equals(source.SchemaHash, binding.LastKnownSchemaHash, StringComparison.OrdinalIgnoreCase)
                : false;
            if (binding.PageId.HasValue && pagesById.TryGetValue(binding.PageId.Value, out var page))
            {
                result.Add(new DataSourceImpactDto
                {
                    DataSourceId = dataSourceId,
                    EntityId = source?.EntityId,
                    PageId = page.Id,
                    PageKey = page.PageKey,
                    PageName = page.PageName,
                    SyncStatus = page.SyncStatus,
                    SchemaChanged = schemaChanged,
                    DataSourceDeleted = source?.IsDeleted == true,
                    BindingStatus = binding.BindingStatus
                });
            }
            else if (binding.ModuleEntityId.HasValue)
            {
                result.Add(new DataSourceImpactDto
                {
                    DataSourceId = dataSourceId,
                    EntityId = source?.EntityId,
                    ModuleEntityId = binding.ModuleEntityId,
                    PageType = binding.PageType,
                    PageName = binding.PageType ?? string.Empty,
                    SchemaChanged = schemaChanged,
                    DataSourceDeleted = source?.IsDeleted == true,
                    BindingStatus = binding.BindingStatus
                });
            }
        }
        return result;
    }

    [Permission("codegen:projectpage:update")]
    public async Task<long> BindPageAsync(CreatePageDataSourceBindingDto input)
    {
        if (input.PageId.HasValue == input.ModuleEntityId.HasValue)
            throw new ArgumentException("页面绑定必须且只能指定 PageId 或 ModuleEntityId。");
        if (input.ModuleEntityId.HasValue && string.IsNullOrWhiteSpace(input.PageType))
            throw new ArgumentException("传统页面绑定必须指定 PageType。");

        var source = await _db.Queryable<ProjectDataSource>()
            .Where(item => item.Id == input.DataSourceId && !item.IsDeleted)
            .FirstAsync() ?? throw new InvalidOperationException($"数据源 {input.DataSourceId} 不存在。");

        if (input.PageId.HasValue)
        {
            var page = await _db.Queryable<ProjectPage>()
                .Where(item => item.Id == input.PageId.Value && !item.IsDeleted)
                .FirstAsync() ?? throw new InvalidOperationException($"复杂页面 {input.PageId.Value} 不存在。");
            if (page.ProjectId != source.ProjectId || page.TenantId != source.TenantId)
                throw new InvalidOperationException("数据源与复杂页面必须属于同一项目和租户。");
        }
        else
        {
            var moduleEntityId = input.ModuleEntityId!.Value;
            var entity = await _db.Queryable<ModuleEntity>()
                .Where(item => item.Id == moduleEntityId && !item.IsDeleted)
                .FirstAsync() ?? throw new InvalidOperationException($"实体 {moduleEntityId} 不存在。");
            if (entity.ProjectId != source.ProjectId || entity.TenantId != source.TenantId)
                throw new InvalidOperationException("数据源与传统页面实体必须属于同一项目和租户。");
        }

        var duplicateQuery = _db.Queryable<PageDataSourceBinding>()
            .Where(item => !item.IsDeleted)
            .WhereIF(input.PageId.HasValue,
                item => item.PageId == input.PageId!.Value && item.DataSourceId == input.DataSourceId)
            .WhereIF(!input.PageId.HasValue,
                item => item.ModuleEntityId == input.ModuleEntityId!.Value && item.PageType == input.PageType);
        var duplicate = await duplicateQuery.FirstAsync();
        if (duplicate != null)
        {
            if (!input.PageId.HasValue && duplicate.DataSourceId != input.DataSourceId)
                throw new InvalidOperationException(
                    $"Traditional page {input.ModuleEntityId}/{input.PageType} already has a data-source override. Unbind it before selecting another source.");
            return duplicate.Id;
        }

        var binding = new PageDataSourceBinding
        {
            TenantId = source.TenantId,
            PageId = input.PageId,
            ModuleEntityId = input.ModuleEntityId,
            PageType = input.PageType,
            DataSourceId = input.DataSourceId,
            BindingRole = input.BindingRole,
            BindingStatus = input.BindingStatus,
            SyncMode = input.SyncMode,
            LastKnownSchemaHash = source.SchemaHash,
            CreateBy = "CodeMaster",
            CreateUserId = 0,
            UpdateUserId = 0,
            CreateTime = DateTime.UtcNow
        };
        return await _db.Insertable(binding).ExecuteReturnSnowflakeIdAsync();
    }

    [Permission("codegen:projectpage:update")]
    public async Task<int> UnbindPageAsync(long bindingId)
    {
        return await _db.Updateable<PageDataSourceBinding>()
            .SetColumns(item => new PageDataSourceBinding { IsDeleted = true, DeleteTime = DateTime.UtcNow })
            .Where(item => item.Id == bindingId && !item.IsDeleted)
            .ExecuteCommandAsync();
    }

    [Permission("codegen:projectpage:view")]
    public async Task<List<PageDataSourceBindingDto>> GetPageBindingsAsync(long? pageId, long? moduleEntityId, string? pageType)
    {
        if (!pageId.HasValue && !moduleEntityId.HasValue)
            throw new ArgumentException("必须指定 PageId 或 ModuleEntityId。");
        var query = _db.Queryable<PageDataSourceBinding>().Where(item => !item.IsDeleted);
        if (pageId.HasValue)
            query = query.Where(item => item.PageId == pageId.Value);
        else
            query = query.Where(item => item.ModuleEntityId == moduleEntityId!.Value && item.PageType == pageType);
        return (await query.OrderBy(item => item.Id).ToListAsync()).Adapt<List<PageDataSourceBindingDto>>();
    }

    private ISugarQueryable<ProjectDataSource> CreateFilteredQuery(DataSourceQueryDto query)
        => _db.Queryable<ProjectDataSource>()
            .Where(item => !item.IsDeleted && item.ProjectId == query.ProjectId)
            .WhereIF(query.SourceType.HasValue, item => item.SourceType == query.SourceType!.Value)
            .WhereIF(!string.IsNullOrWhiteSpace(query.Keyword),
                item => item.SourceKey.Contains(query.Keyword!) || item.DisplayName.Contains(query.Keyword!) ||
                        item.ServiceName.Contains(query.Keyword!) || item.DtoName.Contains(query.Keyword!))
            .OrderBy(item => item.OrderNum)
            .OrderBy(item => item.Id);

    private async Task<Project> ValidateInputAsync(CreateProjectDataSourceDto input, long? selfId)
    {
        if (input.ProjectId <= 0) throw new ArgumentException("项目不能为空。");
        if (!SourceKeyPattern.IsMatch(input.SourceKey)) throw new ArgumentException("SourceKey 只能包含字母、数字、下划线和连字符，且以字母开头。");
        if (string.IsNullOrWhiteSpace(input.DisplayName)) throw new ArgumentException("显示名称不能为空。");
        ValidateParamsJson(input.ParamsJson);

        var project = await _db.Queryable<Project>().Where(item => item.Id == input.ProjectId && !item.IsDeleted).FirstAsync();
        if (project == null) throw new InvalidOperationException($"项目 {input.ProjectId} 不存在。");

        var duplicateQuery = _db.Queryable<ProjectDataSource>()
            .Where(item => !item.IsDeleted && item.ProjectId == input.ProjectId && item.SourceKey == input.SourceKey);
        if (selfId is { } currentId)
            duplicateQuery = duplicateQuery.Where(item => item.Id != currentId);
        var duplicate = await duplicateQuery.FirstAsync();
        if (duplicate != null) throw new InvalidOperationException($"项目内已存在 SourceKey：{input.SourceKey}。");

        switch (input.SourceType)
        {
            case PageDataSourceType.EntityService:
                await ValidateEntitySourceAsync(input);
                break;
            case PageDataSourceType.View:
                if (string.IsNullOrWhiteSpace(input.ViewName))
                    throw new ArgumentException("视图数据源必须指定 ViewName。");
                ValidateServiceContract(input, requireKnownEntityMethod: true);
                break;
            case PageDataSourceType.Api:
                ValidateApiSource(input);
                break;
            default:
                throw new ArgumentException($"暂不支持的数据源类型：{input.SourceType}。");
        }

        return project;
    }

    private async Task ValidateEntitySourceAsync(CreateProjectDataSourceDto input)
    {
        if (!input.EntityId.HasValue) throw new ArgumentException("实体数据源必须指定实体。");
        var entity = await _db.Queryable<ModuleEntity>()
            .Where(item => item.Id == input.EntityId.Value && !item.IsDeleted && item.ProjectId == input.ProjectId)
            .FirstAsync();
        if (entity == null) throw new InvalidOperationException("实体不存在或不属于当前项目。");
        if (entity.GenerationMode == EntityGenerationMode.MetadataOnly)
            throw new InvalidOperationException("OpenAPI 元数据模型不能注册为实体服务数据源。");

        var expectedService = $"{entity.Name}Service";
        var expectedDto = $"{entity.Name}Dto";
        if (!string.Equals(input.ServiceName, expectedService, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"实体 {entity.Name} 的服务名应为 {expectedService}。");
        var entityMethod = CreateEntityMethods(entity).FirstOrDefault(item =>
            string.Equals(item.MethodName, input.MethodName, StringComparison.OrdinalIgnoreCase));
        if (entityMethod == null)
            throw new InvalidOperationException($"方法 {input.MethodName} 不在实体能力目录中。");
        if (!string.Equals(input.DtoName, expectedDto, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"DTO 名应为 {expectedDto}。");
        if (entityMethod.DataShape != input.DataShape)
            throw new InvalidOperationException($"Method {input.MethodName} does not support data shape {input.DataShape}.");
    }

    private void ValidateApiSource(CreateProjectDataSourceDto input)
    {
        ValidateServiceContract(
            input,
            requireKnownEntityMethod: false,
            requireServiceSuffix: _capabilityCatalog == null);
        if (_capabilityCatalog == null)
            return;

        var method = _capabilityCatalog.FindMethod(input.ServiceName, input.MethodName)
            ?? throw new InvalidOperationException($"Service {input.ServiceName} does not expose method {input.MethodName}.");
        if (!string.Equals(method.HttpMethod, "GET", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Service method {input.MethodName} uses {method.HttpMethod}; API data sources currently support GET methods only.");
        if (method.Parameters.Count(item => item.IsRouteParameter) > 1)
            throw new InvalidOperationException($"Service method {input.MethodName} has multiple route parameters; API data sources currently support at most one.");
        var routeParameter = method.Parameters.FirstOrDefault(item => item.IsRouteParameter);
        if (routeParameter != null &&
            !method.RoutePath.EndsWith($"/{{{routeParameter.Name}}}", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"Service method {input.MethodName} must place its route parameter at the end of the route.");
        var expectedShape = ToPageDataShape(method.ResultShape);
        if (expectedShape != input.DataShape)
            throw new InvalidOperationException($"Service method {input.MethodName} returns {expectedShape}, not {input.DataShape}.");
        if (!string.IsNullOrWhiteSpace(input.DtoName) && !TypeNameMatches(input.DtoName, method.ItemType))
            throw new InvalidOperationException($"Service method {input.MethodName} returns DTO {GetSimpleTypeName(method.ItemType)}.");
    }

    private static void ValidateServiceContract(
        CreateProjectDataSourceDto input,
        bool requireKnownEntityMethod,
        bool requireServiceSuffix = true)
    {
        if (string.IsNullOrWhiteSpace(input.ServiceName) ||
            (requireServiceSuffix && !input.ServiceName.EndsWith("Service", StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("数据源必须指定以 Service 结尾的服务名。");
        if (string.IsNullOrWhiteSpace(input.MethodName) || !Regex.IsMatch(input.MethodName, "^[A-Za-z][A-Za-z0-9_]*$"))
            throw new ArgumentException("数据源必须指定合法的方法名。");
        if (string.IsNullOrWhiteSpace(input.DtoName) || !Regex.IsMatch(input.DtoName, "^[A-Za-z][A-Za-z0-9_.<>]*$"))
            throw new ArgumentException("数据源必须指定合法的 DTO 名。");
        if (requireKnownEntityMethod)
            ValidateMethodShape(input.MethodName, input.DataShape);
    }

    private static void ValidateMethodShape(string methodName, PageDataShape dataShape)
    {
        var supportedShapes = methodName switch
        {
            "GetByIdAsync" => new[] { PageDataShape.Single },
            "GetListAsync" => new[] { PageDataShape.Collection, PageDataShape.Options },
            "GetPagedListAsync" or "GetPageListAsync" => new[] { PageDataShape.Paged },
            "GetOptionListAsync" => new[] { PageDataShape.Options, PageDataShape.Collection },
            _ => Array.Empty<PageDataShape>()
        };
        if (supportedShapes.Length == 0)
            throw new InvalidOperationException($"方法 {methodName} 不在已知服务能力目录中。");
        if (!supportedShapes.Contains(dataShape))
            throw new InvalidOperationException($"方法 {methodName} 不支持数据形态 {dataShape}。");
    }

    private static List<DataSourceMethodCapabilityDto> CreateEntityMethods(ModuleEntity entity)
    {
        var methods = new List<DataSourceMethodCapabilityDto>();
        if (entity.HasPrimaryKey)
        {
            methods.Add(new DataSourceMethodCapabilityDto
            {
                MethodName = "GetByIdAsync",
                DtoName = $"{entity.Name}Dto",
                DataShape = PageDataShape.Single,
                ResultType = $"{entity.Name}Dto"
            });
        }

        methods.Add(new DataSourceMethodCapabilityDto
        {
            MethodName = "GetListAsync",
            DtoName = $"{entity.Name}Dto",
            DataShape = PageDataShape.Collection,
            ResultType = $"List<{entity.Name}Dto>"
        });
        methods.Add(new DataSourceMethodCapabilityDto
        {
            MethodName = "GetPagedListAsync",
            DtoName = $"{entity.Name}Dto",
            DataShape = PageDataShape.Paged,
            ResultType = $"PagedResultDto<{entity.Name}Dto>"
        });
        if (entity.IsReadOnly)
        {
            if (entity.HasPrimaryKey)
            {
                methods.Add(new DataSourceMethodCapabilityDto
                {
                    MethodName = "GetDetailAsync",
                    DtoName = $"{entity.Name}Dto",
                    DataShape = PageDataShape.Single,
                    ResultType = $"{entity.Name}Dto"
                });
            }
            methods.Add(new DataSourceMethodCapabilityDto
            {
                MethodName = "GetOptionListAsync",
                DtoName = $"{entity.Name}Dto",
                DataShape = PageDataShape.Options,
                ResultType = $"List<{entity.Name}Dto>"
            });
        }
        return methods;
    }

    private static EntityDataSourceCapabilityDto CreateEntityCapability(ModuleEntity entity)
        => new()
        {
            EntityId = entity.Id,
            EntityName = entity.Name,
            DisplayName = string.IsNullOrWhiteSpace(entity.Description) ? entity.Name : entity.Description,
            ServiceName = $"{entity.Name}Service",
            DtoName = $"{entity.Name}Dto",
            HasPrimaryKey = entity.HasPrimaryKey,
            Methods = CreateEntityMethods(entity)
        };

    private static ServiceDataSourceCapabilityDto CreateServiceCapability(ApplicationServiceCapability service)
        => new()
        {
            ServiceName = service.ServiceName,
            DisplayName = service.ServiceName.EndsWith("Service", StringComparison.OrdinalIgnoreCase)
                ? service.ServiceName[..^"Service".Length]
                : service.ServiceName,
            Methods = service.Methods.Select(method => new DataSourceMethodCapabilityDto
            {
                MethodName = method.MethodName,
                DtoName = GetSimpleTypeName(method.ItemType),
                DataShape = ToPageDataShape(method.ResultShape),
                ResultType = method.ResultType,
                HttpMethod = method.HttpMethod,
                RoutePath = method.RoutePath,
                Parameters = method.Parameters.Select(parameter => new DataSourceParameterCapabilityDto
                {
                    Name = parameter.Name,
                    TypeName = parameter.TypeName,
                    IsOptional = parameter.IsOptional,
                    IsRouteParameter = parameter.IsRouteParameter
                }).ToList()
            }).ToList()
        };

    private static PageDataShape ToPageDataShape(string resultShape)
        => resultShape switch
        {
            "Paged" => PageDataShape.Paged,
            "Collection" => PageDataShape.Collection,
            "Options" => PageDataShape.Options,
            _ => PageDataShape.Single
        };

    private static bool TypeNameMatches(string expected, string actual)
        => string.Equals(GetSimpleTypeName(expected), GetSimpleTypeName(actual), StringComparison.OrdinalIgnoreCase);

    private static string GetSimpleTypeName(string typeName)
    {
        var genericIndex = typeName.IndexOf('<');
        var simple = genericIndex >= 0 ? typeName[..genericIndex] : typeName;
        var lastDot = simple.LastIndexOf('.');
        return lastDot >= 0 ? simple[(lastDot + 1)..] : simple;
    }

    private static void ValidateParamsJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new ArgumentException("ParamsJson 必须是有效的 JSON 对象或数组。");
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array))
                throw new ArgumentException("ParamsJson 必须是 JSON 对象或数组。");
        }
        catch (JsonException exception)
        {
            throw new ArgumentException("ParamsJson 不是有效的 JSON。", exception);
        }
    }

    /// <summary>
    /// 数据源 SchemaHash：Entity 基于实体字段元数据，Api/View 基于服务契约规范化。
    /// </summary>
    private async Task<string> ComputeSchemaHashCoreAsync(CreateProjectDataSourceDto input)
    {
        if (input.SourceType == PageDataSourceType.EntityService && input.EntityId.HasValue)
        {
            var fields = await _db.Queryable<EntityField>()
                .Where(item => item.ModuleEntityId == input.EntityId.Value)
                .OrderBy(item => item.Id)
                .Select(item => new { item.Id, item.Name, item.DataType, item.IsNullable, item.IsPrimaryKey, item.IsDeleted })
                .ToListAsync();
            var canonical = JsonSerializer.Serialize(new
            {
                input.SourceType, input.EntityId, input.ServiceName, input.MethodName, input.DtoName, input.DataShape, Fields = fields
            });
            return Hash(canonical);
        }

        var contract = JsonSerializer.Serialize(new
        {
            input.SourceType,
            input.ServiceName,
            input.MethodName,
            input.DtoName,
            input.DataShape,
            input.ViewName,
            Params = TryParseJson(input.ParamsJson)
        });
        return Hash(contract);
    }

    private static object? TryParseJson(string json)
        => JsonSerializer.Deserialize<JsonElement>(json);

    private static string Hash(string canonical)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();

    /// <summary>
    /// 数据源 SchemaHash 变化后，将引用它的复杂页面标记为 NeedsSync。
    /// 绑定记录的 LastKnownSchemaHash 保持旧值，直到页面重新同步，从而支持 SchemaChanged 检测。
    /// </summary>
    private async Task PropagateSchemaChangeAsync(long dataSourceId, PageBindingStatus bindingStatus)
    {
        await _db.Updateable<PageDataSourceBinding>()
            .SetColumns(binding => new PageDataSourceBinding
            {
                BindingStatus = bindingStatus,
                UpdateTime = DateTime.UtcNow,
                UpdateBy = "CodeMaster"
            })
            .Where(binding => binding.DataSourceId == dataSourceId && !binding.IsDeleted)
            .ExecuteCommandAsync();

        var pageIds = await _db.Queryable<PageDataSourceBinding>()
            .Where(item => item.DataSourceId == dataSourceId && !item.IsDeleted && item.PageId.HasValue)
            .Select(item => item.PageId!.Value)
            .Distinct()
            .ToListAsync();
        if (pageIds.Count > 0)
        {
            await _db.Updateable<ProjectPage>()
                .SetColumns(page => new ProjectPage { SyncStatus = PageSyncStatus.NeedsSync, LastError = null })
                .Where(page => pageIds.Contains(page.Id))
                .ExecuteCommandAsync();
        }
    }

    private static DataSourceChangeResultDto CreateChangeResult(
        long dataSourceId,
        int affectedRows,
        string changeType,
        bool schemaChanged,
        bool dataSourceDeleted,
        List<DataSourceImpactDto> affectedPages)
        => new()
        {
            DataSourceId = dataSourceId,
            AffectedRows = affectedRows,
            ChangeType = changeType,
            SchemaChanged = schemaChanged,
            DataSourceDeleted = dataSourceDeleted,
            AffectedPageCount = affectedPages.Count,
            Summary = affectedPages.Count == 0
                ? $"Data source {dataSourceId} {changeType}; no page references were found."
                : $"Data source {dataSourceId} {changeType}; {affectedPages.Count} page references are affected.",
            AffectedPages = affectedPages
        };
}
