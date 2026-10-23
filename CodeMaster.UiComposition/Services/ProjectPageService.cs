using CodeMaster.Application.Dtos.CodeGen;
using CodeMaster.Application.Services.CodeGen.Relations;
using CodeMaster.Core.Authorization;
using CodeMaster.Core.Dtos;
using CodeMaster.Core.DynamicApi;
using CodeMaster.Domain.Entities.CodeGen;
using CodeMaster.UiComposition.Entities;
using Mapster;
using SqlSugar;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Yitter.IdGenerator;

namespace CodeMaster.Application.Services.CodeGen;

[DynamicApiPermission(requirePermission: true)]
public sealed class ProjectPageService : IProjectPageService, ICodeGenerationMetadataObserver
{
    private static readonly Regex PageKeyPattern = new("^[A-Za-z][A-Za-z0-9_-]*$", RegexOptions.Compiled);
    private static readonly Regex ComponentIdPattern = new("^[A-Za-z0-9_-]+$", RegexOptions.Compiled);
    private static readonly Regex ParameterReferencePattern = new(
        @"^\$(?:route|page|event)(?:\.[A-Za-z_][A-Za-z0-9_-]*)+$|^\$source\.[A-Za-z][A-Za-z0-9_-]*(?:\.[A-Za-z_][A-Za-z0-9_-]*)+$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private readonly ISqlSugarClient _db;
    private readonly IApplicationServiceCapabilityCatalog? _capabilityCatalog;

    public ProjectPageService(
        ISqlSugarClient db,
        IApplicationServiceCapabilityCatalog? capabilityCatalog = null)
    {
        _db = db;
        _capabilityCatalog = capabilityCatalog;
    }

    [Permission("codegen:projectpage:list")]
    public async Task<List<ProjectPageDto>> GetListAsync(ProjectPageQueryDto query)
    {
        query ??= new ProjectPageQueryDto();
        var pages = await CreateFilteredPageQuery(query).ToListAsync();
        return await BuildListDtosAsync(pages);
    }

    [Permission("codegen:projectpage:list")]
    public async Task<PagedResultDto<ProjectPageDto>> GetPagedListAsync(ProjectPageQueryDto query)
    {
        query ??= new ProjectPageQueryDto();
        query.PageNum = Math.Max(1, query.PageNum);
        query.PageSize = Math.Clamp(query.PageSize, 1, 200);
        RefAsync<int> total = 0;
        var pages = await CreateFilteredPageQuery(query)
            .ToPageListAsync(query.PageNum, query.PageSize, total);
        return new PagedResultDto<ProjectPageDto>
        {
            Items = await BuildListDtosAsync(pages),
            Total = total,
            PageNum = query.PageNum,
            PageSize = query.PageSize
        };
    }

    [Permission("codegen:projectpage:view")]
    public async Task<ProjectPageDto?> GetByIdAsync(long id)
    {
        var page = await _db.Queryable<ProjectPage>().Where(item => item.Id == id && !item.IsDeleted).FirstAsync();
        return page == null ? null : await BuildDtoAsync(page);
    }

    [Permission("codegen:projectpage:view")]
    public async Task<PageDefinitionSnapshotDto> GetDefinitionSnapshotAsync(long id)
    {
        var page = await GetByIdAsync(id) ?? throw new InvalidOperationException($"Project page {id} was not found.");
        var sourceKeyById = page.DataSources.ToDictionary(item => item.Id, item => item.SourceKey);
        return new PageDefinitionSnapshotDto
        {
            DefinitionVersion = page.DefinitionVersion,
            SchemaVersion = page.SchemaVersion,
            Page = new CreateProjectPageDto
            {
                ProjectId = page.ProjectId,
                ModuleId = page.ModuleId,
                PageKey = page.PageKey,
                PageName = page.PageName,
                Description = page.Description,
                RoutePath = page.RoutePath,
                Icon = page.Icon,
                OrderNum = page.OrderNum,
                PageType = page.PageType,
                LayoutPresetId = page.LayoutPresetId,
                LayoutJson = page.LayoutJson,
                SyncMode = page.SyncMode,
                IsMenuSynced = page.IsMenuSynced,
                DataSources = page.DataSources.Select(ToCreateDataSource).ToList(),
                Bindings = page.Bindings.Select(binding =>
                    ToCreateBinding(binding, sourceKeyById[binding.DataSourceId])).ToList()
            },
            Dependencies = page.Dependencies.Select(dependency => new PageEntityDependencySnapshotDto
            {
                EntityId = dependency.EntityId,
                DataSourceKey = dependency.DataSourceId.HasValue && sourceKeyById.TryGetValue(dependency.DataSourceId.Value, out var sourceKey)
                    ? sourceKey
                    : null,
                DependencyType = dependency.DependencyType,
                FieldIdsJson = dependency.FieldIdsJson,
                SummaryJson = dependency.SummaryJson,
                SchemaHash = dependency.SchemaHash
            }).ToList()
        };
    }

    [Permission("codegen:projectpage:create")]
    public async Task<long> CreateAsync(CreateProjectPageDto input)
    {
        ValidatePageInput(input);
        await ValidateProjectAndModuleAsync(input.ProjectId, input.ModuleId);
        await ValidateUniquePageAsync(input.ProjectId, input.PageKey, input.RoutePath, null);

        var page = new ProjectPage
        {
            Id = YitIdHelper.NextId(),
            CreateBy = "CodeMaster",
            UpdateUserId = 0,
            ProjectId = input.ProjectId,
            ModuleId = input.ModuleId,
            PageKey = NormalizePageKey(input.PageKey),
            PageName = input.PageName.Trim(),
            Description = input.Description?.Trim(),
            RoutePath = NormalizeRoute(input.RoutePath),
            Icon = input.Icon?.Trim(),
            OrderNum = input.OrderNum,
            PageType = input.PageType,
            LayoutPresetId = input.LayoutPresetId,
            LayoutJson = await ResolveLayoutJsonAsync(input.LayoutPresetId, input.LayoutJson),
            SyncMode = input.SyncMode,
            IsMenuSynced = input.IsMenuSynced,
            SyncStatus = PageSyncStatus.Draft,
            DefinitionVersion = 1,
            SchemaVersion = "1"
        };

        await SavePageGraphAsync(page, input.DataSources, input.Bindings, isNew: true);
        return page.Id;
    }

    [Permission("codegen:projectpage:update")]
    public async Task<int> UpdateAsync(long id, UpdateProjectPageDto input)
    {
        await SaveDefinitionAsync(id, input);
        return 1;
    }

    [Permission("codegen:projectpage:update")]
    public async Task<ProjectPageDto> SaveDefinitionAsync(long id, UpdateProjectPageDto input)
    {
        ValidatePageInput(input);
        var page = await _db.Queryable<ProjectPage>().Where(item => item.Id == id && !item.IsDeleted).FirstAsync();
        if (page == null) throw new InvalidOperationException($"Project page {id} was not found.");
        await ValidateProjectAndModuleAsync(input.ProjectId, input.ModuleId);
        await ValidateUniquePageAsync(input.ProjectId, input.PageKey, input.RoutePath, id);
        if (input.DefinitionVersion.HasValue && input.DefinitionVersion.Value != page.DefinitionVersion)
            throw new InvalidOperationException("页面定义版本已变化，请重新加载后再保存。");

        page.ProjectId = input.ProjectId;
        page.ModuleId = input.ModuleId;
        page.PageKey = NormalizePageKey(input.PageKey);
        page.PageName = input.PageName.Trim();
        page.Description = input.Description?.Trim();
        page.RoutePath = NormalizeRoute(input.RoutePath);
        page.Icon = input.Icon?.Trim();
        page.OrderNum = input.OrderNum;
        page.PageType = input.PageType;
        page.LayoutPresetId = input.LayoutPresetId;
        page.LayoutJson = await ResolveLayoutJsonAsync(input.LayoutPresetId, input.LayoutJson);
        page.SyncMode = input.SyncMode;
        page.IsMenuSynced = input.IsMenuSynced;
        page.DefinitionVersion++;
        page.SyncStatus = PageSyncStatus.NeedsSync;
        page.LastError = null;

        await SavePageGraphAsync(page, input.DataSources, input.Bindings, isNew: false);
        return (await BuildDtoAsync(page))!;
    }

    [Permission("codegen:projectpage:update")]
    public async Task<ProjectPageDto> RebindComponentAsync(long id, RebindPageComponentDto input)
    {
        if (input == null) throw new ArgumentNullException(nameof(input));
        if (string.IsNullOrWhiteSpace(input.ComponentId) || !ComponentIdPattern.IsMatch(input.ComponentId.Trim()))
            throw new ArgumentException("A valid ComponentId is required.", nameof(input.ComponentId));

        var page = await GetByIdAsync(id) ?? throw new InvalidOperationException($"Project page {id} was not found.");
        var source = page.DataSources.FirstOrDefault(item => item.Id == input.DataSourceId)
            ?? throw new InvalidOperationException($"Data source {input.DataSourceId} does not belong to page {id}.");
        var tree = PageLayoutTree.Parse(page.LayoutJson);
        var bindingId = string.IsNullOrWhiteSpace(input.BindingId)
            ? $"binding_{input.ComponentId.Trim()}"
            : input.BindingId.Trim();
        tree.BindDataSource(input.ComponentId.Trim(), source.Id, bindingId);
        if (input.FieldId.HasValue)
            tree.BindField(input.ComponentId.Trim(), input.FieldId.Value, bindingId);

        var sourceKeyById = page.DataSources.ToDictionary(item => item.Id, item => item.SourceKey);
        var bindings = page.Bindings
            .Where(item => !string.Equals(item.ComponentId, input.ComponentId, StringComparison.OrdinalIgnoreCase))
            .Select(item => ToCreateBinding(item, sourceKeyById[item.DataSourceId]))
            .ToList();
        bindings.Add(new CreatePageComponentBindingDto
        {
            ComponentId = input.ComponentId.Trim(),
            BindingId = bindingId,
            GeneratedGenId = $"gen_binding_{bindingId}",
            DataSourceKey = source.SourceKey,
            FieldId = input.FieldId,
            BindingRole = input.BindingRole,
            BindingStatus = PageBindingStatus.Active,
            SyncMode = input.SyncMode,
            AutoIncludeNewFields = input.AutoIncludeNewFields,
            ControlType = input.ControlType,
            PropsJson = input.PropsJson,
            LabelOverride = input.LabelOverride,
            Origin = PageNodeOrigin.Generated
        });

        return await SaveDefinitionAsync(id, new UpdateProjectPageDto
        {
            ProjectId = page.ProjectId,
            ModuleId = page.ModuleId,
            PageKey = page.PageKey,
            PageName = page.PageName,
            Description = page.Description,
            RoutePath = page.RoutePath,
            Icon = page.Icon,
            OrderNum = page.OrderNum,
            PageType = page.PageType,
            LayoutJson = tree.ToJson(),
            SyncMode = page.SyncMode,
            IsMenuSynced = page.IsMenuSynced,
            DefinitionVersion = page.DefinitionVersion,
            DataSources = page.DataSources.Select(ToCreateDataSource).ToList(),
            Bindings = bindings
        });
    }

    [Permission("codegen:projectpage:view")]
    public async Task<PageDefinitionValidationDto> ValidateDefinitionAsync(ValidatePageDefinitionDto input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var result = new PageDefinitionValidationDto();
        try
        {
            var definition = input.Definition ?? throw new ArgumentException("Definition is required.");
            ValidatePageInput(definition);
            await ValidateProjectAndModuleAsync(definition.ProjectId, definition.ModuleId);
            await ValidateUniquePageAsync(definition.ProjectId, definition.PageKey, definition.RoutePath, input.CurrentPageId);
            ValidateSourceAndBindingShape(definition.DataSources, definition.Bindings);

            var page = new ProjectPage
            {
                Id = input.CurrentPageId ?? YitIdHelper.NextId(),
                ProjectId = definition.ProjectId,
                ModuleId = definition.ModuleId,
                PageKey = definition.PageKey,
                PageName = definition.PageName,
                RoutePath = definition.RoutePath,
                LayoutJson = await ResolveLayoutJsonAsync(definition.LayoutPresetId, definition.LayoutJson)
            };
            var layout = PageLayoutTree.Parse(page.LayoutJson);
            var sources = await MaterializeSourcesAsync(page, definition.DataSources);
            _ = await MaterializeBindingsAsync(page, sources, definition.Bindings, layout);
            result.NormalizedLayoutJson = layout.ToJson();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NotSupportedException or JsonException)
        {
            result.Errors.Add(ex.Message);
            AddValidationSuggestions(result, ex.Message);
        }
        return result;
    }

    [Permission("codegen:projectpage:delete")]
    public async Task<int> DeleteAsync(long id)
    {
        var page = await _db.Queryable<ProjectPage>().Where(item => item.Id == id && !item.IsDeleted).FirstAsync();
        if (page == null) return 0;

        _db.Ado.BeginTran();
        try
        {
            await _db.Updateable<ProjectPage>()
                .SetColumns(item => new ProjectPage { IsDeleted = true, DeleteTime = DateTime.UtcNow })
                .Where(item => item.Id == id)
                .ExecuteCommandAsync();
            await _db.Updateable<PageDataSource>().SetColumns(item => new PageDataSource { IsDeleted = true, DeleteTime = DateTime.UtcNow }).Where(item => item.PageId == id).ExecuteCommandAsync();
            await _db.Updateable<PageComponentBinding>().SetColumns(item => new PageComponentBinding { IsDeleted = true, DeleteTime = DateTime.UtcNow }).Where(item => item.PageId == id).ExecuteCommandAsync();
            await _db.Updateable<PageEntityDependency>().SetColumns(item => new PageEntityDependency { IsDeleted = true, DeleteTime = DateTime.UtcNow }).Where(item => item.PageId == id).ExecuteCommandAsync();
            await _db.Updateable<PageDataSourceBinding>().SetColumns(item => new PageDataSourceBinding { IsDeleted = true, DeleteTime = DateTime.UtcNow }).Where(item => item.PageId == id).ExecuteCommandAsync();
            _db.Ado.CommitTran();
            return 1;
        }
        catch
        {
            _db.Ado.RollbackTran();
            throw;
        }
    }

    [Permission("codegen:projectpage:create")]
    public async Task<long> CopyAsync(long id, string pageKey, string pageName)
    {
        var source = await GetByIdAsync(id) ?? throw new InvalidOperationException("源页面不存在。");
        var normalizedPageKey = NormalizePageKey(pageKey);
        var copiedTree = PageLayoutTree.Parse(source.LayoutJson);
        var identities = copiedTree.RegenerateStableIds(normalizedPageKey);
        var input = new CreateProjectPageDto
        {
            ProjectId = source.ProjectId,
            ModuleId = source.ModuleId,
            PageKey = normalizedPageKey,
            PageName = pageName,
            Description = source.Description,
            RoutePath = source.RoutePath.TrimEnd('/') + "-" + normalizedPageKey.ToLowerInvariant(),
            Icon = source.Icon,
            OrderNum = source.OrderNum,
            PageType = source.PageType,
            LayoutPresetId = source.LayoutPresetId,
            LayoutJson = copiedTree.ToJson(),
            SyncMode = source.SyncMode,
            IsMenuSynced = false,
            DataSources = source.DataSources.Select(sourceItem => new CreatePageDataSourceDto
            {
                SourceKey = sourceItem.SourceKey,
                SourceType = sourceItem.SourceType,
                ProjectDataSourceId = sourceItem.ProjectDataSourceId,
                EntityId = sourceItem.EntityId,
                ServiceName = sourceItem.ServiceName,
                MethodName = sourceItem.MethodName,
                DtoName = sourceItem.DtoName,
                DataShape = sourceItem.DataShape,
                SyncMode = sourceItem.SyncMode,
                ParamsJson = sourceItem.ParamsJson,
                OrderNum = sourceItem.OrderNum,
                IsEnabled = sourceItem.IsEnabled
            }).ToList(),
            Bindings = source.Bindings.Select(binding => new CreatePageComponentBindingDto
            {
                ComponentId = identities.ComponentIds[binding.ComponentId],
                BindingId = identities.BindingIds[binding.BindingId],
                GeneratedGenId = $"gen_binding_{identities.BindingIds[binding.BindingId]}",
                DataSourceKey = source.DataSources.FirstOrDefault(item => item.Id == binding.DataSourceId)?.SourceKey,
                FieldId = binding.FieldId,
                BindingRole = binding.BindingRole,
                BindingStatus = binding.BindingStatus,
                SyncMode = binding.SyncMode,
                AutoIncludeNewFields = binding.AutoIncludeNewFields,
                ControlType = binding.ControlType,
                PropsJson = binding.PropsJson,
                LabelOverride = binding.LabelOverride,
                Origin = binding.Origin,
                LastKnownPath = binding.LastKnownPath
            }).ToList()
        };
        return await CreateAsync(input);
    }

    [Permission("codegen:projectpage:list")]
    public async Task<List<PageLayoutPresetDto>> GetLayoutPresetsAsync()
    {
        var presets = await _db.Queryable<PageLayoutPreset>().OrderBy(item => item.Sort).ToListAsync();
        return presets.Adapt<List<PageLayoutPresetDto>>();
    }

    [Permission("codegen:projectpage:list")]
    public async Task<string> GetLayoutTreeAsync(string presetKey)
    {
        if (string.IsNullOrWhiteSpace(presetKey)) throw new ArgumentException("布局预设 key 不能为空。", nameof(presetKey));
        var preset = await _db.Queryable<PageLayoutPreset>().Where(item => item.PresetKey == presetKey).FirstAsync();
        if (preset == null) throw new InvalidOperationException($"布局预设不存在：{presetKey}");
        ValidateLayoutJson(preset.LayoutJson);
        return preset.LayoutJson;
    }

    [Permission("codegen:projectpage:view")]
    public async Task<List<EntityServiceCapabilityDto>> GetEntityServiceCapabilitiesAsync(long projectId)
    {
        var entities = await _db.Queryable<ModuleEntity>()
            .Where(item => item.ProjectId == projectId && !item.IsDeleted)
            .OrderBy(item => item.OrderNum)
            .ToListAsync();
        var entityIds = entities.Select(item => item.Id).ToList();
        var fields = entityIds.Count == 0
            ? new List<EntityField>()
            : await _db.Queryable<EntityField>()
                .Where(item => entityIds.Contains(item.ModuleEntityId) && !item.IsDeleted)
                .OrderBy(item => item.OrderNum)
                .ToListAsync();

        return entities.Select(entity => new EntityServiceCapabilityDto
        {
            EntityId = entity.Id,
            EntityName = entity.Name,
            GenerationMode = entity.GenerationMode,
            ServiceName = $"{entity.Name}Service",
            Methods = entity.GenerationMode == EntityGenerationMode.FullStack
                ? BuildEntityServiceMethods(entity)
                : new List<EntityServiceMethodDto>(),
            Fields = fields.Where(field => field.ModuleEntityId == entity.Id).Select(field => new EntityFieldCapabilityDto
            {
                FieldId = field.Id,
                Name = field.Name,
                DataType = field.DataType,
                IsNullable = field.IsNullable,
                IsPrimaryKey = field.IsPrimaryKey
            }).ToList()
        }).ToList();
    }

    private static List<EntityServiceMethodDto> BuildEntityServiceMethods(ModuleEntity entity)
    {
        var methods = new List<EntityServiceMethodDto>
        {
            new() { MethodName = "GetListAsync", DtoName = $"{entity.Name}Dto", SupportedShapes = new() { PageDataShape.Collection, PageDataShape.Options } },
            new() { MethodName = "GetPagedListAsync", DtoName = $"{entity.Name}Dto", SupportedShapes = new() { PageDataShape.Paged } }
        };
        if (entity.HasPrimaryKey)
            methods.Insert(0, new() { MethodName = "GetByIdAsync", DtoName = $"{entity.Name}Dto", SupportedShapes = new() { PageDataShape.Single } });
        if (entity.IsReadOnly)
        {
            if (entity.HasPrimaryKey)
                methods.Add(new() { MethodName = "GetDetailAsync", DtoName = $"{entity.Name}Dto", SupportedShapes = new() { PageDataShape.Single } });
            methods.Add(new() { MethodName = "GetOptionListAsync", DtoName = $"{entity.Name}Dto", SupportedShapes = new() { PageDataShape.Options, PageDataShape.Collection } });
        }
        return methods;
    }

    [Permission("codegen:projectpage:view")]
    public async Task<List<PageImpactDto>> GetAffectedPagesByEntityIdAsync(long entityId)
    {
        var dependencies = await _db.Queryable<PageEntityDependency>()
            .Where(item => item.EntityId == entityId && !item.IsDeleted)
            .ToListAsync();
        var pageIds = dependencies.Select(item => item.PageId).Distinct().ToList();
        if (pageIds.Count == 0) return new List<PageImpactDto>();
        var pages = await _db.Queryable<ProjectPage>().Where(item => pageIds.Contains(item.Id) && !item.IsDeleted).ToListAsync();
        var sources = await _db.Queryable<PageDataSource>().Where(item => pageIds.Contains(item.PageId) && !item.IsDeleted).ToListAsync();
        var bindings = await _db.Queryable<PageComponentBinding>().Where(item => pageIds.Contains(item.PageId) && !item.IsDeleted).ToListAsync();

        return pages.Select(page => new PageImpactDto
        {
            PageId = page.Id,
            PageKey = page.PageKey,
            PageName = page.PageName,
            SyncStatus = page.SyncStatus,
            DataSources = sources.Where(item => item.PageId == page.Id).Adapt<List<PageDataSourceDto>>(),
            Bindings = bindings.Where(item => item.PageId == page.Id).Adapt<List<PageComponentBindingDto>>()
        }).ToList();
    }

    [Permission("codegen:projectpage:update")]
    public async Task<int> MarkNeedsSyncAsync(long entityId)
    {
        var pageIds = await _db.Queryable<PageEntityDependency>()
            .Where(item => item.EntityId == entityId && !item.IsDeleted)
            .Select(item => item.PageId)
            .Distinct()
            .ToListAsync();
        if (pageIds.Count == 0) return 0;
        return await _db.Updateable<ProjectPage>()
            .SetColumns(page => new ProjectPage { SyncStatus = PageSyncStatus.NeedsSync, LastError = null })
            .Where(page => pageIds.Contains(page.Id))
            .ExecuteCommandAsync();
    }

    async Task ICodeGenerationMetadataObserver.EntityChangedAsync(long entityId)
    {
        if (_db.DbMaintenance.IsAnyTable("sys_page_entity_dependency", false))
            await MarkNeedsSyncAsync(entityId);
    }

    [Permission("codegen:projectpage:view")]
    public async Task<string> ComputeEntitySchemaHashAsync(long entityId)
    {
        var entity = await _db.Queryable<ModuleEntity>().Where(item => item.Id == entityId).FirstAsync();
        if (entity == null) throw new InvalidOperationException($"实体 {entityId} 不存在。");
        var fields = await _db.Queryable<EntityField>()
            .Where(item => item.ModuleEntityId == entityId)
            .OrderBy(item => item.Id)
            .Select(item => new
            {
                item.Id, item.Name, item.DataType, item.IsNullable, item.IsPrimaryKey,
                item.FormControlType, item.ShowInSearch, item.ShowInList, item.ShowInAddForm,
                item.ShowInEditForm, item.ShowInDetail, item.IsDeleted
            })
            .ToListAsync();
        var canonical = JsonSerializer.Serialize(new { entity.Id, entity.Name, entity.TableName, Fields = fields });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    private ISugarQueryable<ProjectPage> CreateFilteredPageQuery(ProjectPageQueryDto query)
        => _db.Queryable<ProjectPage>()
            .Where(page => !page.IsDeleted)
            .WhereIF(query.ProjectId.HasValue, page => page.ProjectId == query.ProjectId!.Value)
            .WhereIF(query.ModuleId.HasValue, page => page.ModuleId == query.ModuleId!.Value)
            .WhereIF(!string.IsNullOrWhiteSpace(query.PageKey), page => page.PageKey.Contains(query.PageKey!))
            .WhereIF(!string.IsNullOrWhiteSpace(query.PageName), page => page.PageName.Contains(query.PageName!))
            .WhereIF(query.SyncStatus.HasValue, page => page.SyncStatus == query.SyncStatus!.Value)
            .OrderBy(page => page.OrderNum)
            .OrderBy(page => page.Id);

    private async Task<List<ProjectPageDto>> BuildListDtosAsync(IReadOnlyList<ProjectPage> pages)
    {
        var result = pages.Adapt<List<ProjectPageDto>>();
        var pageIds = pages.Select(page => page.Id).ToList();
        if (pageIds.Count == 0) return result;
        var sources = await _db.Queryable<PageDataSource>()
            .Where(item => pageIds.Contains(item.PageId) && !item.IsDeleted)
            .ToListAsync();
        var bindings = await _db.Queryable<PageComponentBinding>()
            .Where(item => pageIds.Contains(item.PageId) && !item.IsDeleted)
            .ToListAsync();
        foreach (var page in result)
        {
            page.DataSources = sources.Where(item => item.PageId == page.Id).Adapt<List<PageDataSourceDto>>();
            page.Bindings = bindings.Where(item => item.PageId == page.Id).Adapt<List<PageComponentBindingDto>>();
        }
        return result;
    }

    private async Task SavePageGraphAsync(
        ProjectPage page,
        IReadOnlyList<CreatePageDataSourceDto> sourceInputs,
        IReadOnlyList<CreatePageComponentBindingDto> bindingInputs,
        bool isNew)
    {
        ValidateSourceAndBindingShape(sourceInputs, bindingInputs);
        var layout = PageLayoutTree.Parse(page.LayoutJson);
        var sources = await MaterializeSourcesAsync(page, sourceInputs);
        ComplexPageEventContract.Validate(layout, sources.Select(item => item.SourceKey).ToArray());
        var bindings = await MaterializeBindingsAsync(page, sources, bindingInputs, layout);
        var projectSourceBindings = MaterializeProjectSourceBindings(page, sources);
        page.LayoutJson = layout.ToJson();
        var dependencies = await BuildDependenciesAsync(page.Id, page.ProjectId, sources, bindings);
        page.LastDefinitionHash = ComputeDefinitionHash(page, sources, bindings);
        page.SyncStatus = page.SyncStatus == PageSyncStatus.Conflict ? PageSyncStatus.Conflict : PageSyncStatus.NeedsSync;

        _db.Ado.BeginTran();
        try
        {
            if (isNew)
                await _db.Insertable(page).ExecuteCommandAsync();
            else
            {
                page.UpdateTime = DateTime.UtcNow;
                await _db.Updateable(page).ExecuteCommandAsync();
                // These graph tables use unconditional unique indexes on page/source,
                // page/binding, and page/entity/type. Soft-deleting the old rows would
                // leave those keys occupied and make every definition update fail when
                // the same source or binding is recreated. The graph is a replaceable
                // projection of the page definition, so remove the old projection inside
                // the same transaction before inserting the validated replacement.
                await _db.Deleteable<PageDataSource>().Where(item => item.PageId == page.Id).ExecuteCommandAsync();
                await _db.Deleteable<PageComponentBinding>().Where(item => item.PageId == page.Id).ExecuteCommandAsync();
                await _db.Deleteable<PageEntityDependency>().Where(item => item.PageId == page.Id).ExecuteCommandAsync();
            }

            if (sources.Count > 0) await _db.Insertable(sources).ExecuteCommandAsync();
            if (bindings.Count > 0) await _db.Insertable(bindings).ExecuteCommandAsync();
            if (dependencies.Count > 0) await _db.Insertable(dependencies).ExecuteCommandAsync();
            await SynchronizeProjectSourceBindingsAsync(page.Id, projectSourceBindings);
            _db.Ado.CommitTran();
        }
        catch
        {
            _db.Ado.RollbackTran();
            throw;
        }
    }

    private async Task<List<PageDataSource>> MaterializeSourcesAsync(ProjectPage page, IReadOnlyList<CreatePageDataSourceDto> inputs)
    {
        var result = new List<PageDataSource>();
        foreach (var input in inputs)
        {
            ValidateDataSource(input);
            ProjectDataSource? projectSource = null;
            if (input.ProjectDataSourceId.HasValue)
            {
                projectSource = await _db.Queryable<ProjectDataSource>()
                    .Where(item => item.Id == input.ProjectDataSourceId.Value
                                   && item.ProjectId == page.ProjectId
                                   && !item.IsDeleted)
                    .FirstAsync();
                ValidateImportedApiSource(input, projectSource);
            }
            ModuleEntity? entity = null;
            var entityId = projectSource?.ResponseEntityId ?? projectSource?.EntityId ?? input.EntityId;
            if (entityId.HasValue)
            {
                entity = await _db.Queryable<ModuleEntity>()
                    .Where(item => item.Id == entityId.Value && item.ProjectId == page.ProjectId && !item.IsDeleted)
                    .FirstAsync();
                if (entity == null) throw new InvalidOperationException($"数据源实体不属于当前项目：{entityId}");
            }

            string schemaHash;
            if (input.SourceType == PageDataSourceType.EntityService)
            {
                ValidateEntityServiceCapability(input, entity);
                schemaHash = await ComputeEntitySchemaHashAsync(entity!.Id);
            }
            else if (projectSource != null)
            {
                schemaHash = projectSource.SchemaHash
                    ?? throw new InvalidOperationException($"OpenAPI data source {projectSource.SourceKey} has no schema hash.");
            }
            else
            {
                ValidateApiServiceCapability(input);
                var entitySchemaHash = entity == null ? null : await ComputeEntitySchemaHashAsync(entity.Id);
                schemaHash = ComputeApiSchemaHash(input, entitySchemaHash);
            }
            result.Add(new PageDataSource
            {
                Id = YitIdHelper.NextId(),
                CreateBy = "CodeMaster",
                UpdateUserId = 0,
                TenantId = projectSource?.TenantId ?? page.TenantId,
                PageId = page.Id,
                SourceKey = input.SourceKey.Trim(),
                SourceType = input.SourceType,
                ProjectDataSourceId = projectSource?.Id,
                EntityId = entity?.Id,
                ServiceName = projectSource?.ServiceName ?? input.ServiceName.Trim(),
                MethodName = projectSource?.MethodName ?? input.MethodName.Trim(),
                DtoName = projectSource?.DtoName ?? input.DtoName.Trim(),
                DataShape = projectSource?.DataShape ?? input.DataShape,
                SyncMode = input.SyncMode,
                ParamsJson = input.ParamsJson,
                SchemaHash = schemaHash,
                LastSyncedTime = DateTime.UtcNow,
                OrderNum = input.OrderNum,
                IsEnabled = input.IsEnabled
            });
        }
        await ValidateDataSourceParameterReferencesAsync(inputs, result);
        return result;
    }

    private static List<PageDataSourceBinding> MaterializeProjectSourceBindings(
        ProjectPage page,
        IReadOnlyList<PageDataSource> sources)
    {
        return sources
            .Where(item => item.ProjectDataSourceId.HasValue)
            .GroupBy(item => item.ProjectDataSourceId!.Value)
            .Select(group => new PageDataSourceBinding
            {
                Id = YitIdHelper.NextId(),
                TenantId = group.First().TenantId,
                CreateBy = "CodeMaster",
                CreateUserId = 0,
                UpdateUserId = 0,
                PageId = page.Id,
                DataSourceId = group.Key,
                BindingRole = PageBindingRole.Container,
                BindingStatus = PageBindingStatus.Active,
                SyncMode = group.First().SyncMode,
                LastKnownSchemaHash = group.First().SchemaHash
            })
            .ToList();
    }

    private async Task SynchronizeProjectSourceBindingsAsync(
        long pageId,
        IReadOnlyList<PageDataSourceBinding> desired)
    {
        var existing = await _db.Queryable<PageDataSourceBinding>()
            .ClearFilter()
            .Where(item => item.PageId == pageId)
            .ToListAsync();
        var desiredSourceIds = desired.Select(item => item.DataSourceId).ToHashSet();

        foreach (var binding in desired)
        {
            var current = existing.FirstOrDefault(item => item.DataSourceId == binding.DataSourceId);
            if (current == null)
            {
                await _db.Insertable(binding).ExecuteCommandAsync();
                continue;
            }

            current.TenantId = binding.TenantId;
            current.BindingRole = binding.BindingRole;
            current.BindingStatus = binding.BindingStatus;
            current.SyncMode = binding.SyncMode;
            current.LastKnownSchemaHash = binding.LastKnownSchemaHash;
            current.IsDeleted = false;
            current.DeleteTime = null;
            current.UpdateTime = DateTime.UtcNow;
            await _db.Updateable(current).ExecuteCommandAsync();
        }

        var removedIds = existing
            .Where(item => !item.IsDeleted && !desiredSourceIds.Contains(item.DataSourceId))
            .Select(item => item.Id)
            .ToList();
        if (removedIds.Count > 0)
        {
            await _db.Updateable<PageDataSourceBinding>()
                .SetColumns(item => new PageDataSourceBinding { IsDeleted = true, DeleteTime = DateTime.UtcNow })
                .Where(item => removedIds.Contains(item.Id))
                .ExecuteCommandAsync();
        }
    }

    private static void ValidateImportedApiSource(
        CreatePageDataSourceDto input,
        ProjectDataSource? source)
    {
        if (source == null)
            throw new InvalidOperationException($"Project data source {input.ProjectDataSourceId} does not exist in the current project.");
        if (input.SourceType != PageDataSourceType.Api || source.SourceType != PageDataSourceType.Api || !source.OpenApiDocumentId.HasValue)
            throw new InvalidOperationException("Only an imported OpenAPI operation can be referenced as a project API source.");
        if (!source.IsEnabled || source.OpenApiSyncState is not (OpenApiSyncState.Active or OpenApiSyncState.Detached))
            throw new InvalidOperationException($"OpenAPI data source {source.SourceKey} is not active and cannot be used by a page.");
        if (!string.Equals(source.HttpMethod, "GET", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException($"OpenAPI data source {source.SourceKey} uses {source.HttpMethod}; complex-page query sources currently support GET only.");
        if (string.IsNullOrWhiteSpace(source.RoutePath))
            throw new InvalidOperationException($"OpenAPI data source {source.SourceKey} has no route path.");
        var routeParameters = Regex.Matches(source.RoutePath, "\\{(?<name>[^}/]+)\\}")
            .Select(match => match.Groups["name"].Value)
            .ToArray();
        if (routeParameters.Length > 1)
            throw new NotSupportedException($"OpenAPI data source {source.SourceKey} has more than one route parameter.");
        if (routeParameters.Length == 1 &&
            !source.RoutePath.EndsWith($"/{{{routeParameters[0]}}}", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException($"OpenAPI data source {source.SourceKey} must place its route parameter at the final path segment.");
    }

    private async Task<List<PageComponentBinding>> MaterializeBindingsAsync(
        ProjectPage page,
        IReadOnlyList<PageDataSource> sources,
        IReadOnlyList<CreatePageComponentBindingDto> inputs,
        PageLayoutTree layout)
    {
        var result = new List<PageComponentBinding>();
        foreach (var input in inputs)
        {
            var source = input.DataSourceId.HasValue
                ? sources.FirstOrDefault(item => item.Id == input.DataSourceId.Value)
                : sources.FirstOrDefault(item => string.Equals(item.SourceKey, input.DataSourceKey, StringComparison.OrdinalIgnoreCase));
            if (source == null) throw new InvalidOperationException($"绑定引用的数据源不存在：{input.DataSourceKey ?? input.DataSourceId?.ToString()}");
            var layoutNode = layout.Find(input.ComponentId.Trim())
                ?? throw new InvalidOperationException($"Binding component '{input.ComponentId}' does not exist in the layout tree.");
            if (input.FieldId.HasValue)
            {
                if (!source.EntityId.HasValue)
                    throw new InvalidOperationException($"API 数据源 {source.SourceKey} 未关联字段模型，不能创建字段级绑定。");
                var field = await _db.Queryable<EntityField>()
                    .ClearFilter()
                    .Where(item => item.Id == input.FieldId.Value)
                    .FirstAsync();
                var fieldIsUsable = field != null && !field.IsDeleted && field.ModuleEntityId == source.EntityId;
                var invalidBindingCanKeepHistoricalField = input.BindingStatus == PageBindingStatus.Invalid &&
                    (field == null || field.ModuleEntityId == source.EntityId);
                if (!fieldIsUsable && !invalidBindingCanKeepHistoricalField)
                    throw new InvalidOperationException($"字段 {input.FieldId} 不属于数据源实体。");
            }
            ValidateBindingCompatibility(input, source, layoutNode, IsInsideSearchArea(layout, input.ComponentId));
            var bindingId = string.IsNullOrWhiteSpace(input.BindingId) ? $"binding_{input.ComponentId.Trim()}" : input.BindingId.Trim();
            // A component has one primary data binding. Event bindings are
            // additional triggers and must not replace the component's
            // primary binding in the layout tree.
            if (input.BindingRole != PageBindingRole.Event)
            {
                layoutNode.DataSourceId = source.Id;
                layoutNode.BindingId = bindingId;
                layoutNode.FieldId = input.FieldId;
            }
            result.Add(new PageComponentBinding
            {
                Id = YitIdHelper.NextId(),
                CreateBy = "CodeMaster",
                UpdateUserId = 0,
                PageId = page.Id,
                ComponentId = input.ComponentId.Trim(),
                BindingId = bindingId,
                DataSourceId = source.Id,
                FieldId = input.FieldId,
                BindingRole = input.BindingRole,
                BindingStatus = input.BindingStatus,
                SyncMode = input.SyncMode,
                AutoIncludeNewFields = input.AutoIncludeNewFields,
                ControlType = input.ControlType?.Trim(),
                PropsJson = input.PropsJson,
                LabelOverride = input.LabelOverride?.Trim(),
                Origin = input.Origin,
                GeneratedGenId = string.IsNullOrWhiteSpace(input.GeneratedGenId) ? $"gen_binding_{bindingId}" : input.GeneratedGenId.Trim(),
                LastKnownPath = input.LastKnownPath?.Trim()
            });
        }
        return result;
    }

    private async Task<List<PageEntityDependency>> BuildDependenciesAsync(
        long pageId,
        long projectId,
        IReadOnlyList<PageDataSource> sources,
        IReadOnlyList<PageComponentBinding> bindings)
    {
        var result = new List<PageEntityDependency>();
        var graph = await new EntityRelationGraphBuilder(_db).BuildForProjectAsync(projectId);
        var schemaHashCache = new Dictionary<long, string>();

        foreach (var group in sources.Where(item => item.EntityId.HasValue).GroupBy(item => item.EntityId!.Value))
        {
            var sourceIds = group.Select(item => item.Id).ToHashSet();
            var fieldIds = bindings.Where(item => sourceIds.Contains(item.DataSourceId) && item.FieldId.HasValue)
                .Select(item => item.FieldId!.Value).Distinct().OrderBy(item => item).ToArray();
            var schemaHash = await GetEntitySchemaHashCachedAsync(group.Key, schemaHashCache);
            var schemaFields = await _db.Queryable<EntityField>()
                .Where(item => item.ModuleEntityId == group.Key && !item.IsDeleted)
                .OrderBy(item => item.Id)
                .Select(item => new
                {
                    item.Id,
                    item.Name,
                    item.Description,
                    item.DataType,
                    item.FormControlType,
                    item.IsDeleted
                })
                .ToListAsync();
            result.Add(new PageEntityDependency
            {
                Id = YitIdHelper.NextId(),
                CreateBy = "CodeMaster",
                UpdateUserId = 0,
                PageId = pageId,
                EntityId = group.Key,
                DataSourceId = group.First().Id,
                DependencyType = PageDependencyType.Direct,
                FieldIdsJson = JsonSerializer.Serialize(fieldIds),
                SummaryJson = JsonSerializer.Serialize(new
                {
                    sourceKeys = group.Select(item => item.SourceKey).ToArray(),
                    fields = schemaFields
                }),
                SchemaHash = schemaHash
            });

            var relatedEntityIds = graph.Edges
                .Where(edge => edge.SourceEntityId == group.Key || edge.TargetEntityId == group.Key)
                .SelectMany(edge => new[] { edge.SourceEntityId, edge.TargetEntityId })
                .Where(entityId => entityId != group.Key)
                .Distinct()
                .OrderBy(entityId => entityId)
                .ToArray();
            foreach (var relatedEntityId in relatedEntityIds)
            {
                var relationEdges = graph.Edges
                    .Where(edge =>
                        (edge.SourceEntityId == group.Key && edge.TargetEntityId == relatedEntityId) ||
                        (edge.SourceEntityId == relatedEntityId && edge.TargetEntityId == group.Key))
                    .Select(edge => new
                    {
                        edge.Id,
                        edge.RelationName,
                        edge.Cardinality,
                        edge.Ownership,
                        edge.SourceEntityId,
                        edge.TargetEntityId
                    })
                    .OrderBy(item => item.Id)
                    .ToArray();
                result.Add(new PageEntityDependency
                {
                    Id = YitIdHelper.NextId(),
                    CreateBy = "CodeMaster",
                    UpdateUserId = 0,
                    PageId = pageId,
                    EntityId = relatedEntityId,
                    DataSourceId = group.First().Id,
                    DependencyType = PageDependencyType.Relation,
                    FieldIdsJson = "[]",
                    SummaryJson = JsonSerializer.Serialize(new { relations = relationEdges }),
                    SchemaHash = await GetEntitySchemaHashCachedAsync(relatedEntityId, schemaHashCache)
                });
            }
        }
        // Multiple page data sources can reach the same related entity. The
        // database key is page/entity/type, so keep one dependency projection.
        return result
            .GroupBy(item => new { item.PageId, item.EntityId, item.DependencyType })
            .Select(group => group.First())
            .ToList();
    }

    private async Task<string> GetEntitySchemaHashCachedAsync(long entityId, Dictionary<long, string> cache)
    {
        if (cache.TryGetValue(entityId, out var cached)) return cached;
        var hash = await ComputeEntitySchemaHashAsync(entityId);
        cache[entityId] = hash;
        return hash;
    }

    private async Task<ProjectPageDto?> BuildDtoAsync(ProjectPage page)
    {
        var dto = page.Adapt<ProjectPageDto>();
        dto.DataSources = (await _db.Queryable<PageDataSource>().Where(item => item.PageId == page.Id && !item.IsDeleted).ToListAsync()).Adapt<List<PageDataSourceDto>>();
        dto.Bindings = (await _db.Queryable<PageComponentBinding>().Where(item => item.PageId == page.Id && !item.IsDeleted).ToListAsync()).Adapt<List<PageComponentBindingDto>>();
        dto.Dependencies = (await _db.Queryable<PageEntityDependency>().Where(item => item.PageId == page.Id && !item.IsDeleted).ToListAsync()).Adapt<List<PageEntityDependencyDto>>();
        return dto;
    }

    private async Task ValidateProjectAndModuleAsync(long projectId, long moduleId)
    {
        var module = await _db.Queryable<ProjectModule>().Where(item => item.Id == moduleId && !item.IsDeleted).FirstAsync();
        if (module == null || module.ProjectId != projectId)
            throw new InvalidOperationException("模块不存在或不属于当前项目。");
    }

    private async Task ValidateUniquePageAsync(long projectId, string pageKey, string routePath, long? currentId)
    {
        var keyQuery = _db.Queryable<ProjectPage>()
            .Where(item => item.ProjectId == projectId && item.PageKey == pageKey && !item.IsDeleted);
        var routeQuery = _db.Queryable<ProjectPage>()
            .Where(item => item.ProjectId == projectId && item.RoutePath == routePath && !item.IsDeleted);
        if (currentId.HasValue)
        {
            var id = currentId.Value;
            keyQuery = keyQuery.Where(item => item.Id != id);
            routeQuery = routeQuery.Where(item => item.Id != id);
        }

        var duplicateKey = await keyQuery.AnyAsync();
        if (duplicateKey) throw new InvalidOperationException($"页面 PageKey 已存在：{pageKey}");
        var duplicateRoute = await routeQuery.AnyAsync();
        if (duplicateRoute) throw new InvalidOperationException($"页面路由已存在：{routePath}");
    }

    private static void ValidatePageInput(CreateProjectPageDto input)
    {
        if (input == null) throw new ArgumentNullException(nameof(input));
        input.PageKey = NormalizePageKey(input.PageKey);
        input.RoutePath = NormalizeRoute(input.RoutePath);
        if (string.IsNullOrWhiteSpace(input.PageName)) throw new ArgumentException("页面名称不能为空。", nameof(input.PageName));
        ValidateLayoutJson(input.LayoutJson);
    }

    private static void ValidateSourceAndBindingShape(IReadOnlyList<CreatePageDataSourceDto> sources, IReadOnlyList<CreatePageComponentBindingDto> bindings)
    {
        if (sources.GroupBy(item => item.SourceKey.Trim(), StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1))
            throw new ArgumentException("同一页面内 SourceKey 必须唯一。");
        foreach (var group in bindings.GroupBy(item => item.ComponentId.Trim(), StringComparer.OrdinalIgnoreCase))
        {
            if (group.Count(item => item.BindingRole != PageBindingRole.Event) > 1)
                throw new ArgumentException("同一页面内 ComponentId 必须唯一。");
        }
        if (bindings.GroupBy(item => item.BindingId ?? $"binding_{item.ComponentId}", StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1))
            throw new ArgumentException("同一页面内 BindingId 必须唯一。");
        if (bindings.GroupBy(
                item => string.IsNullOrWhiteSpace(item.GeneratedGenId)
                    ? $"gen_binding_{item.BindingId ?? $"binding_{item.ComponentId}"}"
                    : item.GeneratedGenId,
                StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1))
            throw new ArgumentException("Generated binding anchors must be unique within a page.");
        foreach (var binding in bindings)
        {
            if (string.IsNullOrWhiteSpace(binding.ComponentId) || !ComponentIdPattern.IsMatch(binding.ComponentId.Trim()))
                throw new ArgumentException($"无效的 ComponentId：{binding.ComponentId}");
            var bindingId = string.IsNullOrWhiteSpace(binding.BindingId) ? $"binding_{binding.ComponentId}" : binding.BindingId.Trim();
            if (!ComponentIdPattern.IsMatch(bindingId))
                throw new ArgumentException($"Invalid BindingId '{bindingId}'.");
            ValidateJsonObject(binding.PropsJson, "PropsJson");
        }
    }

    private static void ValidateDataSource(CreatePageDataSourceDto input)
    {
        if (string.IsNullOrWhiteSpace(input.SourceKey) || !ComponentIdPattern.IsMatch(input.SourceKey.Trim()))
            throw new ArgumentException($"无效的 SourceKey：{input.SourceKey}");
        if (input.SourceType == PageDataSourceType.SqlQuery)
            throw new NotSupportedException("SqlQuery 是预留数据源类型，当前版本未启用。");
        if (input.SourceType == PageDataSourceType.View)
            throw new NotSupportedException("视图必须先通过视图构建器注册为只读实体，再作为实体数据源加入页面。");
        if (input.SourceType is not (PageDataSourceType.EntityService or PageDataSourceType.Api))
            throw new NotSupportedException($"当前不支持数据源类型：{input.SourceType}。");
        if (input.SourceType == PageDataSourceType.EntityService && !input.EntityId.HasValue)
            throw new ArgumentException($"实体数据源 {input.SourceKey} 必须绑定实体。");
        if (input.ProjectDataSourceId.HasValue && input.SourceType != PageDataSourceType.Api)
            throw new ArgumentException($"项目数据源引用 {input.SourceKey} 只能用于 API 数据源。");
        if (string.IsNullOrWhiteSpace(input.ServiceName) || string.IsNullOrWhiteSpace(input.MethodName) || string.IsNullOrWhiteSpace(input.DtoName))
            throw new ArgumentException($"数据源 {input.SourceKey} 的服务、方法和 DTO 必须完整。");
        ValidateJsonObject(input.ParamsJson, "ParamsJson");
        _ = ReadParameterReferences(input.ParamsJson);
    }

    private void ValidateApiServiceCapability(CreatePageDataSourceDto input)
    {
        if (_capabilityCatalog == null)
        {
            if (!input.MethodName.StartsWith("Get", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException($"API 数据源 {input.SourceKey} 当前只支持 GET 查询方法。");
            return;
        }

        var method = _capabilityCatalog.FindMethod(input.ServiceName, input.MethodName)
            ?? throw new ArgumentException($"API 数据源 {input.SourceKey} 的服务方法不在已注册能力目录中。");
        if (!string.Equals(method.HttpMethod, "GET", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"API 数据源 {input.SourceKey} 当前只支持 GET 方法，{input.MethodName} 使用 {method.HttpMethod}。");
        if (method.Parameters.Count(item => item.IsRouteParameter) > 1)
            throw new ArgumentException($"API 数据源 {input.SourceKey} 暂不支持包含多个路由参数的方法，请先封装为单一路由参数或查询参数接口。");
        var routeParameter = method.Parameters.FirstOrDefault(item => item.IsRouteParameter);
        if (routeParameter != null &&
            !method.RoutePath.EndsWith($"/{{{routeParameter.Name}}}", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException(
                $"API data source {input.SourceKey} must place its route parameter at the end of {method.RoutePath}.");
        var expectedShape = ToPageDataShape(method.ResultShape);
        if (expectedShape != input.DataShape)
            throw new ArgumentException($"API 方法 {input.MethodName} 返回 {expectedShape}，与配置的数据形态 {input.DataShape} 不一致。");
        if (!TypeNameMatches(input.DtoName, method.ItemType))
            throw new ArgumentException($"API 方法 {input.MethodName} 返回 DTO {GetSimpleTypeName(method.ItemType)}，与配置的 {input.DtoName} 不一致。");
    }

    private static string ComputeApiSchemaHash(CreatePageDataSourceDto input, string? entitySchemaHash)
    {
        var canonical = JsonSerializer.Serialize(new
        {
            input.SourceType,
            input.EntityId,
            input.ServiceName,
            input.MethodName,
            input.DtoName,
            input.DataShape,
            input.ParamsJson,
            EntitySchemaHash = entitySchemaHash
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    private static PageDataShape ToPageDataShape(string resultShape)
        => resultShape switch
        {
            "Paged" => PageDataShape.Paged,
            "Collection" => PageDataShape.Collection,
            "Options" => PageDataShape.Options,
            "Aggregate" => PageDataShape.Aggregate,
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

    private async Task ValidateDataSourceParameterReferencesAsync(
        IReadOnlyList<CreatePageDataSourceDto> inputs,
        IReadOnlyList<PageDataSource> sources)
    {
        var sourceByKey = sources.ToDictionary(item => item.SourceKey, StringComparer.OrdinalIgnoreCase);
        var entityIds = sources.Where(item => item.EntityId.HasValue).Select(item => item.EntityId!.Value).Distinct().ToList();
        var fields = entityIds.Count == 0
            ? new List<EntityField>()
            : await _db.Queryable<EntityField>()
                .Where(item => entityIds.Contains(item.ModuleEntityId) && !item.IsDeleted)
                .ToListAsync();

        foreach (var input in inputs)
        {
            foreach (var reference in ReadParameterReferences(input.ParamsJson))
            {
                if (!reference.StartsWith("$source.", StringComparison.OrdinalIgnoreCase)) continue;
                var segments = reference.Split('.', StringSplitOptions.RemoveEmptyEntries);
                if (segments.Length < 3 || !sourceByKey.TryGetValue(segments[1], out var referencedSource))
                    throw new ArgumentException($"Parameter reference '{reference}' uses an undeclared data source.");
                var fieldName = segments[2];
                if (!referencedSource.EntityId.HasValue || !fields.Any(field =>
                        field.ModuleEntityId == referencedSource.EntityId.Value &&
                        string.Equals(field.Name, fieldName, StringComparison.OrdinalIgnoreCase)))
                    throw new ArgumentException($"Parameter reference '{reference}' uses a field that is not exposed by data source '{segments[1]}'.");
            }
        }
    }

    private static IReadOnlyList<string> ReadParameterReferences(string json)
    {
        using var document = JsonDocument.Parse(json);
        var result = new List<string>();
        ReadParameterReferences(document.RootElement, result);
        return result;
    }

    private static void ReadParameterReferences(JsonElement element, List<string> result)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var properties = element.EnumerateObject().ToList();
            var referenceIndex = properties.FindIndex(item => item.NameEquals("$ref"));
            if (referenceIndex >= 0)
            {
                var referenceProperty = properties[referenceIndex];
                if (properties.Count != 1 || referenceProperty.Value.ValueKind != JsonValueKind.String)
                    throw new ArgumentException("A parameter reference must be an object containing only a string $ref property.");
                var reference = referenceProperty.Value.GetString() ?? string.Empty;
                if (!ParameterReferencePattern.IsMatch(reference))
                    throw new ArgumentException($"Unsupported parameter reference '{reference}'.");
                result.Add(reference);
                return;
            }
            foreach (var property in properties)
                ReadParameterReferences(property.Value, result);
            return;
        }

        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray()) ReadParameterReferences(item, result);
            return;
        }

        if (element.ValueKind == JsonValueKind.String &&
            (element.GetString() ?? string.Empty).StartsWith('$'))
            throw new ArgumentException("Dynamic parameter values must use the structured { \"$ref\": \"...\" } form.");
    }

    private static bool IsInsideSearchArea(PageLayoutTree layout, string componentId)
    {
        foreach (var node in EnumerateLayoutNodes(layout.Root))
        {
            if (node.Type.Equals("search", StringComparison.OrdinalIgnoreCase) ||
                node.Type.Equals("query-form", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var descendant in EnumerateLayoutNodes(node))
                    if (string.Equals(descendant.ComponentId, componentId, StringComparison.OrdinalIgnoreCase))
                        return true;
            }
        }
        return false;
    }

    private static IEnumerable<PageLayoutNode> EnumerateLayoutNodes(PageLayoutNode root)
    {
        yield return root;
        foreach (var child in root.Children)
            foreach (var descendant in EnumerateLayoutNodes(child))
                yield return descendant;
    }

    private static void ValidateBindingCompatibility(
        CreatePageComponentBindingDto input,
        PageDataSource source,
        PageLayoutNode node,
        bool inSearchQuery = false)
    {
        var supportedShapes = input.BindingRole switch
        {
            PageBindingRole.Table or PageBindingRole.Column => new[] { PageDataShape.Collection, PageDataShape.Paged },
            PageBindingRole.Form or PageBindingRole.Detail => new[] { PageDataShape.Single },
            PageBindingRole.Field when inSearchQuery => new[] { PageDataShape.Single, PageDataShape.Collection, PageDataShape.Paged },
            PageBindingRole.Field => new[] { PageDataShape.Single },
            PageBindingRole.Options => new[] { PageDataShape.Options, PageDataShape.Collection },
            _ => Enum.GetValues<PageDataShape>()
        };
        if (!supportedShapes.Contains(source.DataShape))
            throw new ArgumentException($"Binding role {input.BindingRole} is incompatible with data shape {source.DataShape}.");
        if (input.BindingRole is PageBindingRole.Column or PageBindingRole.Field or PageBindingRole.Detail or PageBindingRole.Options && !input.FieldId.HasValue)
            throw new ArgumentException($"Binding role {input.BindingRole} requires a FieldId.");

        var allowedNodeTypes = input.BindingRole switch
        {
            PageBindingRole.Table => new[] { "table" },
            PageBindingRole.Column => new[] { "table-column", "component" },
            PageBindingRole.Form => new[] { "form", "edit-form", "query-form" },
            PageBindingRole.Detail => new[] { "detail", "detail-field", "description", "description-field" },
            PageBindingRole.Field => new[] { "form-field", "component", "edit-form", "query-form" },
            _ => Array.Empty<string>()
        };
        if (allowedNodeTypes.Length > 0 && !allowedNodeTypes.Contains(node.Type, StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException($"Binding role {input.BindingRole} cannot be applied to layout node type '{node.Type}'.");
    }

    private static void ValidateEntityServiceCapability(CreatePageDataSourceDto input, ModuleEntity? entity)
    {
        if (entity == null)
            throw new InvalidOperationException($"Data source entity {input.EntityId} does not belong to the current project.");
        if (entity.GenerationMode != EntityGenerationMode.FullStack)
            throw new InvalidOperationException($"Metadata-only entity {entity.Name} cannot be used as an entity-service data source.");
        var serviceName = input.ServiceName.Trim();
        var expectedService = $"{entity.Name}Service";
        if (!string.Equals(serviceName, expectedService, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"数据源 {input.SourceKey} 必须使用实体服务 {expectedService}。");

        var method = input.MethodName.Trim();
        var normalizedMethod = method.EndsWith("Async", StringComparison.OrdinalIgnoreCase) ? method[..^5] : method;
        var expectedDto = normalizedMethod switch
        {
            "GetById" or "GetList" or "GetPagedList" or "GetPageList" => $"{entity.Name}Dto",
            _ => null
        };
        if (expectedDto == null)
            throw new ArgumentException($"数据源 {input.SourceKey} 的方法 {method} 不在实体能力目录中。");
        if (!string.Equals(input.DtoName.Trim(), expectedDto, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"数据源 {input.SourceKey} 的 DTO 必须为 {expectedDto}。");

        var supportedShapes = normalizedMethod switch
        {
            "GetById" => new[] { PageDataShape.Single },
            "GetList" => new[] { PageDataShape.Collection, PageDataShape.Options },
            "GetPagedList" or "GetPageList" => new[] { PageDataShape.Paged },
            _ => Array.Empty<PageDataShape>()
        };
        if (!supportedShapes.Contains(input.DataShape))
            throw new ArgumentException($"数据源 {input.SourceKey} 的方法 {method} 不支持数据形态 {input.DataShape}。");
    }

    private static void AddValidationSuggestions(PageDefinitionValidationDto result, string message)
    {
        if (message.Contains("incompatible with data shape", StringComparison.OrdinalIgnoreCase))
            result.Suggestions.Add("Collection/Paged 数据源请绑定 table；Single 数据源请绑定 edit-form 或 description；Options/Collection 请用于选项控件。");
        if (message.Contains("能力目录", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("实体服务", StringComparison.OrdinalIgnoreCase))
            result.Suggestions.Add("先调用实体能力目录，并从返回的 serviceName、methodName、dtoName 和 supportedShapes 中选择。");
        if (message.Contains("不属于数据源实体", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("requires a FieldId", StringComparison.OrdinalIgnoreCase))
            result.Suggestions.Add("字段绑定必须使用当前数据源实体能力目录中公开的 FieldId。");
        if (message.Contains("layout", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("Component", StringComparison.OrdinalIgnoreCase))
            result.Suggestions.Add("只提交结构化布局树，并确保每个绑定的 componentId 存在于树中；不要提交 Vue 源码。");
    }

    private static string NormalizePageKey(string value)
    {
        var normalized = value?.Trim() ?? string.Empty;
        if (!PageKeyPattern.IsMatch(normalized) || normalized.Contains("..", StringComparison.Ordinal))
            throw new ArgumentException("PageKey 只能包含字母开头的字母、数字、下划线或短横线。");
        return normalized;
    }

    private static string NormalizeRoute(string value)
    {
        var normalized = value?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(normalized) || !normalized.StartsWith('/') || normalized.Contains("..", StringComparison.Ordinal) || normalized.Contains('\\'))
            throw new ArgumentException("页面路由必须是项目内的绝对 URL 路径，且不能包含 .. 或反斜杠。");
        return "/" + normalized.Trim('/');
    }

    private async Task<string> ResolveLayoutJsonAsync(long? presetId, string? layoutJson)
    {
        if (!string.IsNullOrWhiteSpace(layoutJson))
        {
            ValidateLayoutJson(layoutJson);
            return layoutJson;
        }
        if (presetId.HasValue)
        {
            var preset = await _db.Queryable<PageLayoutPreset>().Where(item => item.Id == presetId.Value).FirstAsync();
            if (preset == null) throw new InvalidOperationException("布局预设不存在。");
            ValidateLayoutJson(preset.LayoutJson);
            return preset.LayoutJson;
        }
        return "{\"version\":1,\"root\":{\"type\":\"page\",\"componentId\":\"page_root\",\"children\":[]}}";
    }

    private static void ValidateLayoutJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return;
        try
        {
            _ = PageLayoutTree.Parse(json);
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("布局 JSON 必须是对象。");
        }
        catch (JsonException ex)
        {
            throw new ArgumentException("布局 JSON 无效。", ex);
        }
    }

    private static void ValidateJsonObject(string? json, string name)
    {
        if (string.IsNullOrWhiteSpace(json)) throw new ArgumentException($"{name} 不能为空。");
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new ArgumentException($"{name} 必须是 JSON 对象。");
        }
        catch (JsonException ex)
        {
            throw new ArgumentException($"{name} 必须是有效 JSON 对象。", ex);
        }
    }

    private static CreatePageDataSourceDto ToCreateDataSource(PageDataSourceDto source) => new()
    {
        SourceKey = source.SourceKey,
        SourceType = source.SourceType,
        ProjectDataSourceId = source.ProjectDataSourceId,
        EntityId = source.EntityId,
        ServiceName = source.ServiceName,
        MethodName = source.MethodName,
        DtoName = source.DtoName,
        DataShape = source.DataShape,
        SyncMode = source.SyncMode,
        ParamsJson = source.ParamsJson,
        OrderNum = source.OrderNum,
        IsEnabled = source.IsEnabled
    };

    private static CreatePageComponentBindingDto ToCreateBinding(PageComponentBindingDto binding, string sourceKey) => new()
    {
        ComponentId = binding.ComponentId,
        BindingId = binding.BindingId,
        GeneratedGenId = binding.GeneratedGenId,
        DataSourceKey = sourceKey,
        FieldId = binding.FieldId,
        BindingRole = binding.BindingRole,
        BindingStatus = binding.BindingStatus,
        SyncMode = binding.SyncMode,
        AutoIncludeNewFields = binding.AutoIncludeNewFields,
        ControlType = binding.ControlType,
        PropsJson = binding.PropsJson,
        LabelOverride = binding.LabelOverride,
        Origin = binding.Origin,
        LastKnownPath = binding.LastKnownPath
    };

    private static string ComputeDefinitionHash(ProjectPage page, IReadOnlyList<PageDataSource> sources, IReadOnlyList<PageComponentBinding> bindings)
    {
        var canonical = JsonSerializer.Serialize(new
        {
            page.ProjectId, page.ModuleId, page.PageKey, page.RoutePath, page.PageType, page.LayoutJson,
            Sources = sources.OrderBy(item => item.SourceKey).Select(item => new { item.SourceKey, item.ProjectDataSourceId, item.EntityId, item.ServiceName, item.MethodName, item.DtoName, item.DataShape, item.ParamsJson }),
            Bindings = bindings.OrderBy(item => item.BindingId).Select(item => new { item.ComponentId, item.BindingId, item.DataSourceId, item.FieldId, item.BindingRole, item.SyncMode, item.AutoIncludeNewFields, item.ControlType, item.PropsJson })
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }
}
