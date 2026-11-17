using CodeMaster.Application.Dtos.CodeGen;
using CodeMaster.Application.Services.CodeGen;
using CodeMaster.Core.DynamicApi;
using CodeMaster.Core.Entities;
using CodeMaster.Core.Services;
using CodeMaster.Domain.Entities.CodeGen;
using CodeMaster.Infrastructure.DynamicApi;
using CodeMaster.Infrastructure.Persistence.SqlSugar;
using CodeMaster.Core.Enums;
using SqlSugar;
using Xunit;

namespace CodeMaster.UiComposition.Tests;

public sealed class ProjectDataSourceServiceTests : IDisposable
{
    private readonly string _dbPath;
    private readonly ISqlSugarClient _db;
    private readonly ProjectDataSourceService _service;

    public ProjectDataSourceServiceTests()
    {
        _dbPath = Path.Combine(CodeMasterTestPaths.Root, $"codemaster_datasource_{Guid.NewGuid():N}.db");
        _db = CreateDatabase(_dbPath);
        _db.CodeFirst.InitTables(
            typeof(Project),
            typeof(ProjectModule),
            typeof(ModuleEntity),
            typeof(EntityField),
            typeof(ProjectPage),
            typeof(PageDataSource),
            typeof(PageComponentBinding),
            typeof(PageEntityDependency),
            typeof(PageLayoutPreset),
            typeof(ProjectDataSource),
            typeof(PageDataSourceBinding));
        _service = new ProjectDataSourceService(_db);
        SeedBaseline();
    }

    public void Dispose()
    {
        _db.Ado.Close();
        _db.Dispose();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }

    [Fact]
    public async Task Create_entity_source_validates_service_name_and_sets_schema_hash()
    {
        var invalid = new CreateProjectDataSourceDto
        {
            ProjectId = 1,
            SourceKey = "orderList",
            DisplayName = "订单列表",
            SourceType = PageDataSourceType.EntityService,
            EntityId = 10,
            ServiceName = "WrongService",
            MethodName = "GetPagedListAsync",
            DtoName = "OrderDto",
            DataShape = PageDataShape.Paged
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.CreateAsync(invalid));

        var valid = new CreateProjectDataSourceDto
        {
            ProjectId = 1,
            SourceKey = "orderList",
            DisplayName = "订单列表",
            SourceType = PageDataSourceType.EntityService,
            EntityId = 10,
            ServiceName = "OrderService",
            MethodName = "GetPagedListAsync",
            DtoName = "OrderDto",
            DataShape = PageDataShape.Paged
        };
        var id = await _service.CreateAsync(valid);
        var created = await _service.GetByIdAsync(id);
        Assert.NotNull(created);
        Assert.False(string.IsNullOrWhiteSpace(created.SchemaHash));
        Assert.Equal(PageDataShape.Paged, created.DataShape);
    }

    [Fact]
    public async Task Create_view_and_api_sources_accepts_contracts()
    {
        var view = new CreateProjectDataSourceDto
        {
            ProjectId = 1,
            SourceKey = "orderStatsView",
            DisplayName = "订单统计视图",
            SourceType = PageDataSourceType.View,
            ViewName = "v_order_stats",
            ServiceName = "OrderStatsViewService",
            MethodName = "GetPagedListAsync",
            DtoName = "OrderStatsViewDto",
            DataShape = PageDataShape.Paged
        };
        var viewId = await _service.CreateAsync(view);
        Assert.True(viewId > 0);

        var api = new CreateProjectDataSourceDto
        {
            ProjectId = 1,
            SourceKey = "crossEntityReport",
            DisplayName = "跨实体报表",
            SourceType = PageDataSourceType.Api,
            ServiceName = "ReportService",
            MethodName = "GetMonthlyReportAsync",
            DtoName = "MonthlyReportDto",
            DataShape = PageDataShape.Collection
        };
        var apiId = await _service.CreateAsync(api);
        var created = await _service.GetByIdAsync(apiId);
        Assert.NotNull(created);
        Assert.NotEqual(viewId, apiId);
    }

    [Fact]
    public async Task Api_source_must_match_a_registered_service_method_when_catalog_is_available()
    {
        var catalog = new ApplicationServiceCapabilityCatalog(new[] { typeof(RegisteredReportService) });
        var strictService = new ProjectDataSourceService(_db, catalog);
        var capabilities = await strictService.GetCapabilityCatalogAsync(1);
        Assert.Contains(capabilities.Entities, item => item.EntityId == 10 && item.ServiceName == "OrderService");
        Assert.DoesNotContain(capabilities.Entities, item => item.EntityId == 12);
        var reportCapability = Assert.Single(capabilities.ApiServices, item => item.ServiceName == nameof(RegisteredReportService));
        var reportMethod = Assert.Single(reportCapability.Methods, item => item.MethodName == nameof(RegisteredReportService.GetReportAsync));
        Assert.Equal("GET", reportMethod.HttpMethod);
        // 动态 API 路由是 /api/{module}/{controller}/{action}，module 取命名空间的第 4 段，
        // 不足 4 段时回落为 "app"。RegisteredReportService 在 CodeMaster.UiComposition.Tests
        // （3 段）下，预期应是 /api/app/...，与 DynamicApiControllerConvention 保持一致。
        Assert.Equal("/api/app/registeredreport/getreport", reportMethod.RoutePath);

        var unregistered = new CreateProjectDataSourceDto
        {
            ProjectId = 1,
            SourceKey = "unregisteredReport",
            DisplayName = "Unregistered report",
            SourceType = PageDataSourceType.Api,
            ServiceName = "MissingService",
            MethodName = "GetReportAsync",
            DtoName = "ReportDto",
            DataShape = PageDataShape.Collection
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() => strictService.CreateAsync(unregistered));

        var nonTerminalRoute = new CreateProjectDataSourceDto
        {
            ProjectId = 1,
            SourceKey = "nonTerminalRoute",
            DisplayName = "Non-terminal route parameter",
            SourceType = PageDataSourceType.Api,
            ServiceName = nameof(RegisteredReportService),
            MethodName = nameof(RegisteredReportService.GetReportDetailAsync),
            DtoName = nameof(RegisteredReportDto),
            DataShape = PageDataShape.Single
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() => strictService.CreateAsync(nonTerminalRoute));

        var registered = new CreateProjectDataSourceDto
        {
            ProjectId = 1,
            SourceKey = "registeredReport",
            DisplayName = "Registered report",
            SourceType = PageDataSourceType.Api,
            ServiceName = nameof(RegisteredReportService),
            MethodName = nameof(RegisteredReportService.GetReportAsync),
            DtoName = nameof(RegisteredReportDto),
            DataShape = PageDataShape.Collection
        };
        var id = await strictService.CreateAsync(registered);
        Assert.True(id > 0);
    }

    [Fact]
    public async Task Metadata_only_model_cannot_be_registered_as_entity_service_source()
    {
        var input = new CreateProjectDataSourceDto
        {
            ProjectId = 1,
            SourceKey = "externalOrder",
            DisplayName = "External order",
            SourceType = PageDataSourceType.EntityService,
            EntityId = 12,
            ServiceName = "ExternalOrderDtoService",
            MethodName = "GetPagedListAsync",
            DtoName = "ExternalOrderDtoDto",
            DataShape = PageDataShape.Paged
        };

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => _service.CreateAsync(input));
        Assert.Contains("元数据", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Create_duplicate_source_key_within_project_is_rejected()
    {
        var input = new CreateProjectDataSourceDto
        {
            ProjectId = 1,
            SourceKey = "orderList",
            DisplayName = "订单列表",
            SourceType = PageDataSourceType.EntityService,
            EntityId = 10,
            ServiceName = "OrderService",
            MethodName = "GetPagedListAsync",
            DtoName = "OrderDto",
            DataShape = PageDataShape.Paged
        };
        await _service.CreateAsync(input);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.CreateAsync(input));
    }

    [Fact]
    public async Task Update_propagates_needs_sync_to_bound_complex_page()
    {
        var input = new CreateProjectDataSourceDto
        {
            ProjectId = 1,
            SourceKey = "orderList",
            DisplayName = "订单列表",
            SourceType = PageDataSourceType.EntityService,
            EntityId = 10,
            ServiceName = "OrderService",
            MethodName = "GetPagedListAsync",
            DtoName = "OrderDto",
            DataShape = PageDataShape.Paged
        };
        var id = await _service.CreateAsync(input);

        await _service.BindPageAsync(new CreatePageDataSourceBindingDto
        {
            PageId = 100,
            DataSourceId = id,
            BindingRole = PageBindingRole.Container
        });
        var page = await _db.Queryable<ProjectPage>().Where(item => item.Id == 100).FirstAsync();
        Assert.Equal(PageSyncStatus.Generated, page.SyncStatus);

        input.MethodName = "GetListAsync";
        input.DataShape = PageDataShape.Collection;
        var updateResult = await _service.UpdateWithImpactAsync(id, new UpdateProjectDataSourceDto
        {
            ProjectId = input.ProjectId,
            SourceKey = input.SourceKey,
            DisplayName = input.DisplayName,
            SourceType = input.SourceType,
            EntityId = input.EntityId,
            ServiceName = input.ServiceName,
            MethodName = input.MethodName,
            DtoName = input.DtoName,
            DataShape = input.DataShape,
            SyncMode = input.SyncMode,
            ParamsJson = input.ParamsJson,
            ViewName = input.ViewName,
            OrderNum = input.OrderNum,
            IsEnabled = input.IsEnabled
        });
        Assert.True(updateResult.SchemaChanged);
        Assert.Equal(1, updateResult.AffectedPageCount);

        page = await _db.Queryable<ProjectPage>().Where(item => item.Id == 100).FirstAsync();
        Assert.Equal(PageSyncStatus.NeedsSync, page.SyncStatus);

        var impacts = await _service.GetAffectedPagesAsync(id);
        Assert.Single(impacts);
        Assert.True(impacts[0].SchemaChanged);
        Assert.Equal(100, impacts[0].PageId);

        var unifiedImpacts = await _service.QueryAffectedPagesAsync(new AffectedPageQueryDto { EntityId = 10 });
        Assert.Contains(unifiedImpacts, item => item.DataSourceId == id && item.PageId == 100);
    }

    [Fact]
    public async Task Bind_is_idempotent_and_delete_marks_impact_while_bound()
    {
        var input = new CreateProjectDataSourceDto
        {
            ProjectId = 1,
            SourceKey = "orderList",
            DisplayName = "订单列表",
            SourceType = PageDataSourceType.EntityService,
            EntityId = 10,
            ServiceName = "OrderService",
            MethodName = "GetPagedListAsync",
            DtoName = "OrderDto",
            DataShape = PageDataShape.Paged
        };
        var id = await _service.CreateAsync(input);

        var first = await _service.BindPageAsync(new CreatePageDataSourceBindingDto
        {
            PageId = 100,
            DataSourceId = id,
            BindingRole = PageBindingRole.Table
        });
        var second = await _service.BindPageAsync(new CreatePageDataSourceBindingDto
        {
            PageId = 100,
            DataSourceId = id,
            BindingRole = PageBindingRole.Table
        });
        Assert.Equal(first, second);

        _db.QueryFilter.AddTableFilter<ISoftDelete>(item => !item.IsDeleted);
        var deletion = await _service.DeleteWithImpactAsync(id);
        Assert.Equal(1, deletion.AffectedRows);
        Assert.True(deletion.DataSourceDeleted);
        Assert.Single(deletion.AffectedPages);
        Assert.True(deletion.AffectedPages[0].DataSourceDeleted);
        Assert.Equal(PageBindingStatus.Invalid, deletion.AffectedPages[0].BindingStatus);

        var postDeleteImpacts = await _service.QueryAffectedPagesAsync(
            new AffectedPageQueryDto { DataSourceId = id });
        Assert.Single(postDeleteImpacts);
        Assert.True(postDeleteImpacts[0].DataSourceDeleted);

        var page = await _db.Queryable<ProjectPage>().Where(item => item.Id == 100).FirstAsync();
        Assert.Equal(PageSyncStatus.NeedsSync, page.SyncStatus);

        var bindings = await _service.GetPageBindingsAsync(pageId: 100, moduleEntityId: null, pageType: null);
        Assert.Single(bindings);
        Assert.Equal(PageBindingStatus.Invalid, bindings[0].BindingStatus);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.BindPageAsync(new CreatePageDataSourceBindingDto
        {
            PageId = 100,
            DataSourceId = id,
            BindingRole = PageBindingRole.Table
        }));
    }

    [Fact]
    public async Task Legacy_crud_page_binding_uses_entity_and_page_type()
    {
        var input = new CreateProjectDataSourceDto
        {
            ProjectId = 1,
            SourceKey = "orderList",
            DisplayName = "订单列表",
            SourceType = PageDataSourceType.EntityService,
            EntityId = 10,
            ServiceName = "OrderService",
            MethodName = "GetPagedListAsync",
            DtoName = "OrderDto",
            DataShape = PageDataShape.Paged
        };
        var id = await _service.CreateAsync(input);
        var bindingId = await _service.BindPageAsync(new CreatePageDataSourceBindingDto
        {
            ModuleEntityId = 10,
            PageType = "index",
            DataSourceId = id,
            BindingRole = PageBindingRole.Table
        });
        Assert.True(bindingId > 0);

        var impacts = await _service.GetAffectedPagesAsync(id);
        Assert.Single(impacts);
        Assert.Equal(10, impacts[0].ModuleEntityId);
        Assert.Equal("index", impacts[0].PageType);
    }

    [Fact]
    public async Task Legacy_crud_page_allows_only_one_active_override_per_page_type()
    {
        var firstSourceId = await _service.CreateAsync(CreateOrderSource());
        var secondSource = CreateOrderSource();
        secondSource.SourceKey = "orderOptions";
        secondSource.DisplayName = "Order options";
        var secondSourceId = await _service.CreateAsync(secondSource);

        var bindingId = await _service.BindPageAsync(new CreatePageDataSourceBindingDto
        {
            ModuleEntityId = 10,
            PageType = "index",
            DataSourceId = firstSourceId,
            BindingRole = PageBindingRole.Table
        });

        var sameBindingId = await _service.BindPageAsync(new CreatePageDataSourceBindingDto
        {
            ModuleEntityId = 10,
            PageType = "index",
            DataSourceId = firstSourceId,
            BindingRole = PageBindingRole.Table
        });
        Assert.Equal(bindingId, sameBindingId);

        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.BindPageAsync(new CreatePageDataSourceBindingDto
        {
            ModuleEntityId = 10,
            PageType = "index",
            DataSourceId = secondSourceId,
            BindingRole = PageBindingRole.Table
        }));
    }

    [Fact]
    public async Task Binding_requires_exactly_one_of_page_or_entity()
    {
        var input = new CreateProjectDataSourceDto
        {
            ProjectId = 1,
            SourceKey = "orderList",
            DisplayName = "订单列表",
            SourceType = PageDataSourceType.EntityService,
            EntityId = 10,
            ServiceName = "OrderService",
            MethodName = "GetPagedListAsync",
            DtoName = "OrderDto",
            DataShape = PageDataShape.Paged
        };
        var id = await _service.CreateAsync(input);

        await Assert.ThrowsAsync<ArgumentException>(() => _service.BindPageAsync(new CreatePageDataSourceBindingDto
        {
            DataSourceId = id
        }));
        await Assert.ThrowsAsync<ArgumentException>(() => _service.BindPageAsync(new CreatePageDataSourceBindingDto
        {
            PageId = 100,
            ModuleEntityId = 10,
            DataSourceId = id
        }));
        await Assert.ThrowsAsync<ArgumentException>(() => _service.BindPageAsync(new CreatePageDataSourceBindingDto
        {
            ModuleEntityId = 10,
            DataSourceId = id
        }));
    }

    [Fact]
    public async Task Create_and_binding_inherit_tenant_from_their_owner()
    {
        var sourceId = await _service.CreateAsync(CreateOrderSource());

        var source = await _db.Queryable<ProjectDataSource>()
            .Where(item => item.Id == sourceId)
            .FirstAsync();
        Assert.Equal(7, source.TenantId);

        var bindingId = await _service.BindPageAsync(new CreatePageDataSourceBindingDto
        {
            PageId = 100,
            DataSourceId = sourceId,
            BindingRole = PageBindingRole.Table
        });
        var binding = await _db.Queryable<PageDataSourceBinding>()
            .Where(item => item.Id == bindingId)
            .FirstAsync();
        Assert.Equal(7, binding.TenantId);
    }

    [Fact]
    public async Task Binding_rejects_missing_and_cross_project_targets()
    {
        var sourceId = await _service.CreateAsync(CreateOrderSource());

        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.BindPageAsync(new CreatePageDataSourceBindingDto
        {
            PageId = 999,
            DataSourceId = sourceId
        }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.BindPageAsync(new CreatePageDataSourceBindingDto
        {
            PageId = 200,
            DataSourceId = sourceId
        }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.BindPageAsync(new CreatePageDataSourceBindingDto
        {
            ModuleEntityId = 20,
            PageType = "index",
            DataSourceId = sourceId
        }));
    }

    [Fact]
    public async Task Update_rejects_project_move_and_does_not_sync_for_metadata_only_changes()
    {
        var input = CreateOrderSource();
        var sourceId = await _service.CreateAsync(input);
        await _service.BindPageAsync(new CreatePageDataSourceBindingDto
        {
            PageId = 100,
            DataSourceId = sourceId,
            BindingRole = PageBindingRole.Table
        });

        var metadataOnly = ToUpdate(input);
        metadataOnly.DisplayName = "Order list renamed";
        metadataOnly.OrderNum = 20;
        await _service.UpdateAsync(sourceId, metadataOnly);
        var page = await _db.Queryable<ProjectPage>().Where(item => item.Id == 100).FirstAsync();
        Assert.Equal(PageSyncStatus.Generated, page.SyncStatus);

        var moved = ToUpdate(input);
        moved.ProjectId = 2;
        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.UpdateAsync(sourceId, moved));
    }

    [Fact]
    public async Task Entity_method_shape_and_params_json_are_validated()
    {
        var invalidShape = CreateOrderSource();
        invalidShape.DataShape = PageDataShape.Single;
        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.CreateAsync(invalidShape));

        var invalidJson = CreateOrderSource();
        invalidJson.SourceKey = "invalidParams";
        invalidJson.ParamsJson = "not-json";
        await Assert.ThrowsAsync<ArgumentException>(() => _service.CreateAsync(invalidJson));

        var scalarJson = CreateOrderSource();
        scalarJson.SourceKey = "scalarParams";
        scalarJson.ParamsJson = "123";
        await Assert.ThrowsAsync<ArgumentException>(() => _service.CreateAsync(scalarJson));
    }

    private static CreateProjectDataSourceDto CreateOrderSource()
        => new()
        {
            ProjectId = 1,
            SourceKey = "orderList",
            DisplayName = "Order list",
            SourceType = PageDataSourceType.EntityService,
            EntityId = 10,
            ServiceName = "OrderService",
            MethodName = "GetPagedListAsync",
            DtoName = "OrderDto",
            DataShape = PageDataShape.Paged,
            ParamsJson = "{}"
        };

    private static UpdateProjectDataSourceDto ToUpdate(CreateProjectDataSourceDto input)
        => new()
        {
            ProjectId = input.ProjectId,
            SourceKey = input.SourceKey,
            DisplayName = input.DisplayName,
            SourceType = input.SourceType,
            EntityId = input.EntityId,
            ServiceName = input.ServiceName,
            MethodName = input.MethodName,
            DtoName = input.DtoName,
            DataShape = input.DataShape,
            SyncMode = input.SyncMode,
            ParamsJson = input.ParamsJson,
            ViewName = input.ViewName,
            OrderNum = input.OrderNum,
            IsEnabled = input.IsEnabled
        };

    private void SeedBaseline()
    {
        var now = DateTime.UtcNow;
        _db.Insertable(new Project
        {
            Id = 1,
            TenantId = 7,
            ProjectName = "DataSourceTarget",
            DisplayName = "DataSource Target",
            DatabaseType = DatabaseType.SQLite,
            ConnectionString = $"Data Source={_dbPath};Mode=ReadWriteCreate;",
            ProjectPath = CodeMasterTestPaths.Root,
            CreateBy = "test",
            CreateUserId = 0,
            UpdateUserId = 0,
            CreateTime = now
        }).ExecuteCommand();
        _db.Insertable(new ModuleEntity
        {
            Id = 10,
            TenantId = 7,
            ProjectId = 1,
            ModuleId = 2,
            Name = "Order",
            Description = "Order",
            TableName = "sales_order",
            CreateBy = "test",
            CreateUserId = 0,
            UpdateUserId = 0,
            CreateTime = now
        }).ExecuteCommand();
        _db.Insertable(new EntityField
        {
            Id = 11,
            TenantId = 7,
            ModuleEntityId = 10,
            Name = "OrderNo",
            Description = "Order number",
            DataType = "string",
            CreateBy = "test",
            CreateUserId = 0,
            UpdateUserId = 0,
            CreateTime = now
        }).ExecuteCommand();
        _db.Insertable(new ModuleEntity
        {
            Id = 12,
            TenantId = 7,
            ProjectId = 1,
            ModuleId = 2,
            Name = "ExternalOrderDto",
            Description = "Imported API model",
            GenerationMode = EntityGenerationMode.MetadataOnly,
            HasPrimaryKey = false,
            IsReadOnly = true,
            GenerateFrontend = false,
            HasAudit = false,
            HasSoftDelete = false,
            CreateBy = "test",
            CreateUserId = 0,
            UpdateUserId = 0,
            CreateTime = now
        }).ExecuteCommand();
        _db.Insertable(new ProjectPage
        {
            Id = 100,
            TenantId = 7,
            ProjectId = 1,
            ModuleId = 2,
            PageKey = "order-workspace",
            PageName = "订单工作台",
            RoutePath = "/codegen/project/order-workspace",
            PageType = ComplexPageType.Workspace,
            LayoutJson = "{\"version\":1,\"children\":[]}",
            SyncStatus = PageSyncStatus.Generated,
            CreateBy = "test",
            CreateUserId = 0,
            UpdateUserId = 0,
            CreateTime = now
        }).ExecuteCommand();
        _db.Insertable(new Project
        {
            Id = 2,
            TenantId = 8,
            ProjectName = "OtherTarget",
            DisplayName = "Other Target",
            DatabaseType = DatabaseType.SQLite,
            ConnectionString = $"Data Source={_dbPath};Mode=ReadWriteCreate;",
            ProjectPath = CodeMasterTestPaths.Root,
            CreateBy = "test",
            CreateUserId = 0,
            UpdateUserId = 0,
            CreateTime = now
        }).ExecuteCommand();
        _db.Insertable(new ModuleEntity
        {
            Id = 20,
            TenantId = 8,
            ProjectId = 2,
            ModuleId = 3,
            Name = "Customer",
            Description = "Customer",
            TableName = "customer",
            CreateBy = "test",
            CreateUserId = 0,
            UpdateUserId = 0,
            CreateTime = now
        }).ExecuteCommand();
        _db.Insertable(new ProjectPage
        {
            Id = 200,
            TenantId = 8,
            ProjectId = 2,
            ModuleId = 3,
            PageKey = "customer-workspace",
            PageName = "Customer workspace",
            RoutePath = "/codegen/project/customer-workspace",
            PageType = ComplexPageType.Workspace,
            LayoutJson = "{\"version\":1,\"children\":[]}",
            SyncStatus = PageSyncStatus.Generated,
            CreateBy = "test",
            CreateUserId = 0,
            UpdateUserId = 0,
            CreateTime = now
        }).ExecuteCommand();
    }

    private static ISqlSugarClient CreateDatabase(string path)
    {
        return new SqlSugarClient(new ConnectionConfig
        {
            ConnectionString = $"Data Source={path};Mode=ReadWriteCreate;Pooling=False;",
            DbType = DbType.Sqlite,
            IsAutoCloseConnection = true,
            InitKeyType = InitKeyType.Attribute,
            ConfigureExternalServices = SqlSugarSetup.GetConfigureExternalServices(DbType.Sqlite)
        });
    }
}

public sealed class RegisteredReportService : IApplicationService
{
    public Task<List<RegisteredReportDto>> GetReportAsync()
        => Task.FromResult(new List<RegisteredReportDto>());

    [DynamicApi(HttpMethod = "GET", Route = "report/{projectId}/detail")]
    public Task<RegisteredReportDto> GetReportDetailAsync(
        [Microsoft.AspNetCore.Mvc.FromRoute] long projectId)
        => Task.FromResult(new RegisteredReportDto { Name = projectId.ToString() });
}

public sealed class RegisteredReportDto
{
    public string Name { get; set; } = string.Empty;
}
