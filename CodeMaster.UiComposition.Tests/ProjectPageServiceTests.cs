using CodeMaster.Application.Dtos.CodeGen;
using CodeMaster.Application.Services.CodeGen;
using CodeMaster.Core.DynamicApi;
using CodeMaster.Domain.Entities.CodeGen;
using CodeMaster.Infrastructure.DynamicApi;
using CodeMaster.Domain.Entities.System;
using CodeMaster.Infrastructure.Persistence.SqlSugar;
using CodeMaster.Infrastructure.VueParser;
using CodeMaster.UiComposition.Entities;
using CodeMaster.UiComposition.Services;
using SqlSugar;
using Yitter.IdGenerator;

namespace CodeMaster.UiComposition.Tests;

public class ProjectPageServiceTests : IDisposable
{
    private readonly SqlSugarClient _db;
    private readonly ProjectPageService _service;

    public ProjectPageServiceTests()
    {
        YitIdHelper.SetIdGenerator(new IdGeneratorOptions { WorkerId = 2 });
        _db = new SqlSugarClient(new ConnectionConfig
        {
            ConnectionString = $"Data Source={Path.Combine(CodeMasterTestPaths.Root, $"complex_page_{Guid.NewGuid():N}.db")};Mode=ReadWriteCreate;",
            DbType = DbType.Sqlite,
            IsAutoCloseConnection = true,
            InitKeyType = InitKeyType.Attribute,
            ConfigureExternalServices = SqlSugarSetup.GetConfigureExternalServices(DbType.Sqlite)
        });
        _db.CodeFirst.InitTables(
            typeof(Project), typeof(ProjectModule), typeof(ModuleEntity), typeof(EntityField),
            typeof(ProjectPage), typeof(PageDataSource), typeof(PageComponentBinding),
            typeof(PageEntityDependency), typeof(PageLayoutPreset), typeof(EntityRelation),
            typeof(PageDisplayTemplate), typeof(ProjectDataSource), typeof(PageDataSourceBinding));
        _db.Insertable(new Project
        {
            Id = 1,
            ProjectName = "ComplexPageProject",
            DisplayName = "Complex page project",
            ProjectPath = Path.Combine(CodeMasterTestPaths.Root, $"complex_gen_{Guid.NewGuid():N}"),
            ConnectionString = "Data Source=complex.db",
            CreateBy = "test",
            UpdateUserId = 0
        }).ExecuteCommand();
        _db.Insertable(new ProjectModule
        {
            Id = 2,
            ProjectId = 1,
            ModuleName = "Sales",
            ModuleDescription = "Sales",
            Icon = "folder",
            RoutePath = "/sales",
            Remark = string.Empty,
            CreateBy = "test",
            UpdateUserId = 0
        }).ExecuteCommand();
        _db.Insertable(new ModuleEntity
        {
            Id = 3,
            ProjectId = 1,
            ModuleId = 2,
            Name = "Order",
            Description = "Order",
            TableName = "sales_order",
            CreateBy = "test",
            UpdateUserId = 0
        }).ExecuteCommand();
        _db.Insertable(new EntityField
        {
            Id = 4,
            ModuleEntityId = 3,
            Name = "CustomerName",
            Description = "Customer name",
            DataType = "string",
            ShowInList = true,
            CreateBy = "test",
            UpdateUserId = 0
        }).ExecuteCommand();
        _db.Insertable(new PageLayoutPreset
        {
            Id = 5,
            PresetKey = "blank",
            PresetName = "Blank",
            LayoutJson = "{\"version\":1,\"root\":{\"type\":\"page\",\"children\":[]}}",
            Sort = 1,
            CreateBy = "test",
            UpdateUserId = 0
        }).ExecuteCommand();
        _service = new ProjectPageService(_db);
    }

    [Fact]
    public async Task ReadOnlyEntityCapabilities_include_detail_and_option_methods()
    {
        _db.Insertable(new ModuleEntity
        {
            Id = 30,
            ProjectId = 1,
            ModuleId = 2,
            Name = "ReadonlyReport",
            Description = "Readonly report",
            TableName = "readonly_report",
            HasPrimaryKey = true,
            IsReadOnly = true,
            GenerateFrontend = true,
            CreateBy = "test",
            UpdateUserId = 0
        }).ExecuteCommand();

        var capability = (await _service.GetEntityServiceCapabilitiesAsync(1))
            .Single(item => item.EntityId == 30);
        var methods = capability.Methods.Select(item => item.MethodName).ToHashSet(StringComparer.Ordinal);

        Assert.Contains("GetPagedListAsync", methods);
        Assert.Contains("GetDetailAsync", methods);
        Assert.Contains("GetOptionListAsync", methods);
        Assert.DoesNotContain("CreateAsync", methods);
        Assert.DoesNotContain("UpdateAsync", methods);
        Assert.DoesNotContain("DeleteAsync", methods);
    }

    [Fact]
    public async Task ContainerRenderers_generate_table_description_edit_query_and_auto_grid()
    {
        var id = await _service.CreateAsync(new CreateProjectPageDto
        {
            ProjectId = 1,
            ModuleId = 2,
            PageKey = "container-showcase",
            PageName = "Container showcase",
            RoutePath = "/sales/container-showcase",
            LayoutJson = """
            {"version":1,"root":{"type":"page","componentId":"page_root","children":[
              {"type":"row","componentId":"container_row","children":[
                {"type":"column","componentId":"table_column","children":[
                  {"type":"table","componentId":"orders_table","children":[{"type":"table-column","componentId":"orders_name"}]},
                  {"type":"pagination","componentId":"orders_pager"}
                ]},
                {"type":"column","componentId":"description_column","children":[
                  {"type":"description","componentId":"order_description","children":[{"type":"description-field","componentId":"description_name"}]}
                ]},
                {"type":"column","componentId":"edit_column","children":[
                  {"type":"edit-form","componentId":"order_edit","children":[{"type":"form-field","componentId":"edit_name"}]}
                ]},
                {"type":"column","componentId":"query_column","children":[
                  {"type":"query-form","componentId":"order_query","children":[{"type":"form-field","componentId":"query_name"}]}
                ]}
              ]}
            ]}}
            """,
            DataSources =
            {
                new CreatePageDataSourceDto
                {
                    SourceKey = "orderList",
                    EntityId = 3,
                    ServiceName = "OrderService",
                    MethodName = "GetPagedListAsync",
                    DtoName = "OrderDto",
                    DataShape = PageDataShape.Paged
                },
                new CreatePageDataSourceDto
                {
                    SourceKey = "orderDetail",
                    EntityId = 3,
                    ServiceName = "OrderService",
                    MethodName = "GetByIdAsync",
                    DtoName = "OrderDto",
                    DataShape = PageDataShape.Single
                }
            },
            Bindings =
            {
                new CreatePageComponentBindingDto { ComponentId = "orders_table", DataSourceKey = "orderList", BindingRole = PageBindingRole.Table },
                new CreatePageComponentBindingDto { ComponentId = "orders_name", DataSourceKey = "orderList", BindingRole = PageBindingRole.Column, FieldId = 4 },
                new CreatePageComponentBindingDto { ComponentId = "orders_pager", DataSourceKey = "orderList", BindingRole = PageBindingRole.Container },
                new CreatePageComponentBindingDto { ComponentId = "description_name", DataSourceKey = "orderDetail", BindingRole = PageBindingRole.Detail, FieldId = 4 },
                new CreatePageComponentBindingDto { ComponentId = "order_edit", DataSourceKey = "orderDetail", BindingRole = PageBindingRole.Form },
                new CreatePageComponentBindingDto { ComponentId = "edit_name", DataSourceKey = "orderDetail", BindingRole = PageBindingRole.Field, FieldId = 4 },
                new CreatePageComponentBindingDto { ComponentId = "query_name", DataSourceKey = "orderList", BindingRole = PageBindingRole.Field, FieldId = 4 }
            }
        });

        var page = await _service.GetByIdAsync(id);
        Assert.NotNull(page);
        var layout = PageLayoutTree.Parse(page!.LayoutJson);
        Assert.All(layout.Require("container_row").Children, column => Assert.Equal(6, column.Span));

        var generated = await new ComplexPageGenerationService(_db, _service).GenerateAsync(id);
        Assert.True(generated.Success, generated.Error);
        var vue = await File.ReadAllTextAsync(generated.VuePath);
        Assert.Contains("<el-table", vue);
        Assert.Contains("<el-pagination", vue);
        Assert.Contains("<el-descriptions", vue);
        Assert.Contains("<el-form", vue);
        Assert.Contains("inline=\"true\"", vue);
        Assert.Contains("span=\"6\"", vue);
        Assert.Contains("reloadDataSource('orderList'", vue);
        Assert.Contains("state.query['customerName']", vue);
        Assert.NotEmpty(new CodeMaster.Infrastructure.VueParser.VueTemplateParser().Parse(vue));

        await Assert.ThrowsAsync<ArgumentException>(() => _service.CreateAsync(new CreateProjectPageDto
        {
            ProjectId = 1,
            ModuleId = 2,
            PageKey = "invalid-table-shape",
            PageName = "Invalid table shape",
            RoutePath = "/sales/invalid-table-shape",
            LayoutJson = "{\"root\":{\"type\":\"page\",\"componentId\":\"page_root\",\"children\":[{\"type\":\"table\",\"componentId\":\"single_table\"}]}}",
            DataSources =
            {
                new CreatePageDataSourceDto
                {
                    SourceKey = "singleOrder",
                    EntityId = 3,
                    ServiceName = "OrderService",
                    MethodName = "GetByIdAsync",
                    DtoName = "OrderDto",
                    DataShape = PageDataShape.Single
                }
            },
            Bindings =
            {
                new CreatePageComponentBindingDto { ComponentId = "single_table", DataSourceKey = "singleOrder", BindingRole = PageBindingRole.Table }
            }
        }));
    }

    [Fact]
    public async Task CreatePage_PersistsSourcesBindingsAndReverseDependency()
    {
        var id = await _service.CreateAsync(new CreateProjectPageDto
        {
            ProjectId = 1,
            ModuleId = 2,
            PageKey = "order-workbench",
            PageName = "Order workbench",
            RoutePath = "/sales/order-workbench",
            LayoutJson = "{\"version\":1,\"root\":{\"type\":\"page\",\"componentId\":\"page_root\",\"children\":[{\"type\":\"table\",\"componentId\":\"order-table\",\"children\":[{\"type\":\"table-column\",\"componentId\":\"order-customer-name\"}]}]}}",
            DataSources =
            {
                new CreatePageDataSourceDto
                {
                    SourceKey = "orderList",
                    EntityId = 3,
                    ServiceName = "OrderService",
                    MethodName = "GetPageListAsync",
                    DtoName = "OrderDto",
                    DataShape = PageDataShape.Paged
                }
            },
            Bindings =
            {
                new CreatePageComponentBindingDto
                {
                    ComponentId = "order-table",
                    DataSourceKey = "orderList",
                    BindingRole = PageBindingRole.Table,
                    AutoIncludeNewFields = true,
                    PropsJson = "{\"stripe\":true,\"max-height\":600,\"empty-text\":\"No orders\"}"
                },
                new CreatePageComponentBindingDto
                {
                    ComponentId = "order-customer-name",
                    DataSourceKey = "orderList",
                    FieldId = 4,
                    BindingRole = PageBindingRole.Column,
                    ControlType = "text"
                }
            }
        });

        var page = await _service.GetByIdAsync(id);
        Assert.NotNull(page);
        Assert.Equal("order-workbench", page!.PageKey);
        Assert.Single(page.DataSources);
        Assert.Equal(2, page.Bindings.Count);
        Assert.All(page.Bindings, binding => Assert.StartsWith("gen_binding_", binding.GeneratedGenId));
        var dependency = Assert.Single(page.Dependencies);
        Assert.Equal(3, dependency.EntityId);
        Assert.Contains("4", dependency.FieldIdsJson);

        var generated = await new ComplexPageGenerationService(_db, _service).GenerateAsync(id);
        Assert.True(generated.Success, generated.Error);
        var generatedVue = await File.ReadAllTextAsync(generated.VuePath);
        var generatedAuto = await File.ReadAllTextAsync(Path.Combine(generated.OutputDirectory, "order-workbench.auto.js"));
        Assert.Contains("el-table-column", generatedVue);
        Assert.Contains("state.data['orderList']?.items", generatedVue);
        Assert.Contains(" stripe", generatedVue);
        Assert.Contains(":max-height=\"600\"", generatedVue);
        Assert.Contains("empty-text=\"No orders\"", generatedVue);
        Assert.Contains("/sales/order/getpagedlist", generatedAuto);
        Assert.Contains("createComplexPageState", generatedAuto);
        Assert.Contains("resolveReference", generatedAuto);

        var impacts = await _service.GetAffectedPagesByEntityIdAsync(3);
        Assert.Contains(impacts, impact => impact.PageId == id);
        Assert.Equal(1, await _service.MarkNeedsSyncAsync(3));
        Assert.Equal(PageSyncStatus.NeedsSync, (await _service.GetByIdAsync(id))!.SyncStatus);
    }

    [Fact]
    public async Task CreatePage_BuildsRelationDependencyForRelatedEntities()
    {
        var now = DateTime.UtcNow;
        await _db.Insertable(new ModuleEntity
        {
            Id = 20,
            ProjectId = 1,
            ModuleId = 2,
            Name = "OrderItem",
            Description = "Order item",
            TableName = "sales_order_item",
            CreateBy = "test",
            UpdateUserId = 0,
            CreateTime = now
        }).ExecuteCommandAsync();
        await _db.Insertable(new EntityField
        {
            Id = 21,
            ModuleEntityId = 20,
            Name = "ItemName",
            Description = "Item name",
            DataType = "string",
            CreateBy = "test",
            UpdateUserId = 0,
            CreateTime = now
        }).ExecuteCommandAsync();
        await _db.Insertable(new EntityRelation
        {
            Id = 30,
            SourceEntityId = 3,
            TargetEntityId = 20,
            RelationName = "OrderItems",
            SourceField = "Id",
            TargetField = "OrderId",
            Cardinality = EntityRelationCardinality.OneToMany,
            Ownership = EntityRelationOwnership.Owned,
            IsRequired = false,
            DeleteBehavior = EntityRelationDeleteBehavior.Delete,
            OrderNum = 1,
            CreateBy = "test",
            CreateUserId = 0,
            UpdateUserId = 0,
            CreateTime = now
        }).ExecuteCommandAsync();

        var id = await _service.CreateAsync(new CreateProjectPageDto
        {
            ProjectId = 1,
            ModuleId = 2,
            PageKey = "order-detail",
            PageName = "Order detail",
            RoutePath = "/sales/order-detail",
            LayoutJson = "{\"version\":1,\"root\":{\"type\":\"page\",\"componentId\":\"page_root\",\"children\":[{\"type\":\"table\",\"componentId\":\"order-table\"}]}}",
            DataSources =
            {
                new CreatePageDataSourceDto
                {
                    SourceKey = "orderDetail",
                    EntityId = 3,
                    ServiceName = "OrderService",
                    MethodName = "GetPagedListAsync",
                    DtoName = "OrderDto",
                    DataShape = PageDataShape.Paged
                }
            },
            Bindings =
            {
                new CreatePageComponentBindingDto
                {
                    ComponentId = "order-table",
                    DataSourceKey = "orderDetail",
                    BindingRole = PageBindingRole.Table
                }
            }
        });

        var page = await _service.GetByIdAsync(id);
        Assert.NotNull(page);
        var direct = Assert.Single(page!.Dependencies, item => item.DependencyType == PageDependencyType.Direct);
        Assert.Equal(3, direct.EntityId);
        var related = Assert.Single(page.Dependencies, item => item.DependencyType == PageDependencyType.Relation);
        Assert.Equal(20, related.EntityId);

        Assert.Equal(1, await _service.MarkNeedsSyncAsync(20));
        Assert.Equal(PageSyncStatus.NeedsSync, (await _service.GetByIdAsync(id))!.SyncStatus);
        var impacts = await _service.GetAffectedPagesByEntityIdAsync(20);
        Assert.Contains(impacts, impact => impact.PageId == id);
    }

    [Fact]
    public async Task CreatePage_DeduplicatesSharedRelationDependenciesAcrossDataSources()
    {
        var now = DateTime.UtcNow;
        await _db.Insertable(new[]
        {
            new ModuleEntity
            {
                Id = 20, ProjectId = 1, ModuleId = 2, Name = "Issue", Description = "Issue",
                TableName = "issues", CreateBy = "test", UpdateUserId = 0, CreateTime = now
            },
            new ModuleEntity
            {
                Id = 21, ProjectId = 1, ModuleId = 2, Name = "Sprint", Description = "Sprint",
                TableName = "sprints", CreateBy = "test", UpdateUserId = 0, CreateTime = now
            }
        }).ExecuteCommandAsync();
        await _db.Insertable(new[]
        {
            new EntityRelation
            {
                Id = 30, SourceEntityId = 3, TargetEntityId = 20, RelationName = "OrderIssues",
                SourceField = "Id", TargetField = "OrderId", Cardinality = EntityRelationCardinality.OneToMany,
                Ownership = EntityRelationOwnership.Reference, DeleteBehavior = EntityRelationDeleteBehavior.Restrict,
                CreateBy = "test", UpdateUserId = 0, CreateTime = now
            },
            new EntityRelation
            {
                Id = 31, SourceEntityId = 21, TargetEntityId = 20, RelationName = "SprintIssues",
                SourceField = "Id", TargetField = "SprintId", Cardinality = EntityRelationCardinality.OneToMany,
                Ownership = EntityRelationOwnership.Reference, DeleteBehavior = EntityRelationDeleteBehavior.Restrict,
                CreateBy = "test", UpdateUserId = 0, CreateTime = now
            }
        }).ExecuteCommandAsync();

        var pageId = await _service.CreateAsync(new CreateProjectPageDto
        {
            ProjectId = 1,
            ModuleId = 2,
            PageKey = "shared-relation-dashboard",
            PageName = "Shared relation dashboard",
            RoutePath = "/sales/shared-relation-dashboard",
            LayoutJson = "{\"version\":1,\"root\":{\"type\":\"page\",\"componentId\":\"page_root\",\"children\":[{\"type\":\"table\",\"componentId\":\"orders_table\"},{\"type\":\"table\",\"componentId\":\"sprints_table\"}]}}",
            DataSources =
            {
                new CreatePageDataSourceDto
                {
                    SourceKey = "orders", EntityId = 3, ServiceName = "OrderService",
                    MethodName = "GetPagedListAsync", DtoName = "OrderDto", DataShape = PageDataShape.Paged
                },
                new CreatePageDataSourceDto
                {
                    SourceKey = "sprints", EntityId = 21, ServiceName = "SprintService",
                    MethodName = "GetPagedListAsync", DtoName = "SprintDto", DataShape = PageDataShape.Paged
                }
            },
            Bindings =
            {
                new CreatePageComponentBindingDto
                {
                    ComponentId = "orders_table", DataSourceKey = "orders", BindingRole = PageBindingRole.Table
                },
                new CreatePageComponentBindingDto
                {
                    ComponentId = "sprints_table", DataSourceKey = "sprints", BindingRole = PageBindingRole.Table
                }
            }
        });

        var page = await _service.GetByIdAsync(pageId);
        Assert.NotNull(page);
        Assert.Single(page!.Dependencies, item =>
            item.EntityId == 20 && item.DependencyType == PageDependencyType.Relation);
    }

    [Fact]
    public async Task PagingSnapshotAndCopyUsePortableContractsAndIndependentStableIds()
    {
        var sourceId = await _service.CreateAsync(new CreateProjectPageDto
        {
            ProjectId = 1,
            ModuleId = 2,
            PageKey = "snapshot-source",
            PageName = "Snapshot source",
            RoutePath = "/sales/snapshot-source",
            LayoutJson = "{\"version\":1,\"root\":{\"type\":\"page\",\"componentId\":\"page_root\",\"children\":[{\"type\":\"table\",\"componentId\":\"orders\"}]}}",
            DataSources =
            {
                new CreatePageDataSourceDto
                {
                    SourceKey = "orders",
                    EntityId = 3,
                    ServiceName = "OrderService",
                    MethodName = "GetPagedListAsync",
                    DtoName = "OrderDto",
                    DataShape = PageDataShape.Paged
                }
            },
            Bindings =
            {
                new CreatePageComponentBindingDto
                {
                    ComponentId = "orders",
                    BindingId = "binding_orders",
                    DataSourceKey = "orders",
                    BindingRole = PageBindingRole.Table
                }
            }
        });
        for (var index = 1; index <= 2; index++)
        {
            await _service.CreateAsync(new CreateProjectPageDto
            {
                ProjectId = 1,
                ModuleId = 2,
                PageKey = $"paged-{index}",
                PageName = $"Paged {index}",
                RoutePath = $"/sales/paged-{index}"
            });
        }

        var paged = await _service.GetPagedListAsync(new ProjectPageQueryDto { ProjectId = 1, PageNum = 2, PageSize = 2 });
        Assert.Equal(3, paged.Total);
        Assert.Single(paged.Items);

        var snapshot = await _service.GetDefinitionSnapshotAsync(sourceId);
        Assert.Equal("codemaster.complex-page", snapshot.Format);
        Assert.Equal("orders", Assert.Single(snapshot.Page.DataSources).SourceKey);
        Assert.Equal("orders", Assert.Single(snapshot.Page.Bindings).DataSourceKey);
        Assert.Equal("orders", Assert.Single(snapshot.Dependencies).DataSourceKey);

        var copyId = await _service.CopyAsync(sourceId, "snapshot-copy", "Snapshot copy");
        var original = (await _service.GetByIdAsync(sourceId))!;
        var copy = (await _service.GetByIdAsync(copyId))!;
        Assert.NotEqual(original.Bindings.Single().BindingId, copy.Bindings.Single().BindingId);
        Assert.NotEqual(original.Bindings.Single().ComponentId, copy.Bindings.Single().ComponentId);
        Assert.Contains(copy.Bindings.Single().ComponentId, copy.LayoutJson);
        Assert.Contains(copy.Bindings.Single().BindingId, copy.LayoutJson);
    }

    [Fact]
    public async Task ComplexPageEvents_are_validated_and_generated_as_action_chains()
    {
        var pageId = await _service.CreateAsync(new CreateProjectPageDto
        {
            ProjectId = 1,
            ModuleId = 2,
            PageKey = "event-workbench",
            PageName = "Event workbench",
            RoutePath = "/sales/event-workbench",
            LayoutJson = """
                {
                  "version": 1,
                  "root": {
                    "type": "page",
                    "componentId": "page_root",
                    "children": [
                      {
                        "type": "button",
                        "componentId": "refresh_button",
                        "props": {
                          "text": "Refresh",
                          "_events": "[{\"eventName\":\"click\",\"actions\":[{\"type\":\"showMessage\",\"message\":\"Refreshing\",\"messageType\":\"success\"},{\"type\":\"refreshDataSource\",\"sourceKey\":\"orders\"},{\"type\":\"openDialog\",\"targetComponentId\":\"detail_dialog\"}]}]"
                        },
                        "children": []
                      },
                      {
                        "type": "dialog",
                        "componentId": "detail_dialog",
                        "props": { "title": "Order detail" },
                        "children": []
                      }
                    ]
                  }
                }
                """,
            DataSources =
            {
                new CreatePageDataSourceDto
                {
                    SourceKey = "orders",
                    EntityId = 3,
                    ServiceName = "OrderService",
                    MethodName = "GetPagedListAsync",
                    DtoName = "OrderDto",
                    DataShape = PageDataShape.Paged
                }
            }
        });

        var generated = await new ComplexPageGenerationService(_db, _service).GenerateAsync(pageId);
        Assert.True(generated.Success, generated.Error);
        var vue = await File.ReadAllTextAsync(generated.VuePath);
        Assert.Contains("@click=\"event => runPageActions(decodePageActions", vue);
        Assert.Contains("state.ui['detail_dialog']", vue);
        Assert.Contains("ElMessage[action.messageType", vue);
        Assert.Contains("else if (action.type === 'openDialog')", vue);
        Assert.Contains("v-model=\"state.ui['detail_dialog']\"", vue);
        var autoScript = await File.ReadAllTextAsync(Path.Combine(generated.OutputDirectory, "event-workbench.auto.js"));
        Assert.Contains("ui: {}", autoScript);

        var invalid = await Assert.ThrowsAsync<ArgumentException>(() => _service.CreateAsync(new CreateProjectPageDto
        {
            ProjectId = 1,
            ModuleId = 2,
            PageKey = "invalid-event-workbench",
            PageName = "Invalid event workbench",
            RoutePath = "/sales/invalid-event-workbench",
            LayoutJson = """
                {"version":1,"root":{"type":"page","componentId":"page_root","children":[{"type":"button","componentId":"broken_button","props":{"_events":"[{\"eventName\":\"click\",\"actions\":[{\"type\":\"refreshDataSource\",\"sourceKey\":\"missing\"}]}]"},"children":[]}]}}
                """
        }));
        Assert.Contains("unknown data source", invalid.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreatePage_RejectsUnsupportedSourceAndUnsafeRoute()
    {
        await Assert.ThrowsAsync<NotSupportedException>(() => _service.CreateAsync(new CreateProjectPageDto
        {
            ProjectId = 1,
            ModuleId = 2,
            PageKey = "unsupported",
            PageName = "Unsupported",
            RoutePath = "/sales/unsupported",
            DataSources =
            {
                new CreatePageDataSourceDto
                {
                    SourceKey = "query",
                    SourceType = PageDataSourceType.SqlQuery,
                    EntityId = 3,
                    ServiceName = "OrderService",
                    MethodName = "GetListAsync",
                    DtoName = "OrderDto",
                    DataShape = PageDataShape.Collection
                }
            }
        }));

        await Assert.ThrowsAsync<ArgumentException>(() => _service.CreateAsync(new CreateProjectPageDto
        {
            ProjectId = 1,
            ModuleId = 2,
            PageKey = "unsafe",
            PageName = "Unsafe",
            RoutePath = "/sales/../../escape"
        }));

        await Assert.ThrowsAsync<ArgumentException>(() => _service.CreateAsync(new CreateProjectPageDto
        {
            ProjectId = 1,
            ModuleId = 2,
            PageKey = "unknown-capability",
            PageName = "Unknown capability",
            RoutePath = "/sales/unknown-capability",
            DataSources =
            {
                new CreatePageDataSourceDto
                {
                    SourceKey = "orders",
                    EntityId = 3,
                    ServiceName = "OrderService",
                    MethodName = "FindEverythingAsync",
                    DtoName = "OrderDto",
                    DataShape = PageDataShape.Collection
                }
            }
        }));
    }

    [Fact]
    public async Task CreatePage_RejectsDuplicatePageKeyAndRoute()
    {
        var input = new CreateProjectPageDto
        {
            ProjectId = 1,
            ModuleId = 2,
            PageKey = "duplicate",
            PageName = "Duplicate",
            RoutePath = "/sales/duplicate"
        };
        await _service.CreateAsync(input);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.CreateAsync(input));
    }

    [Fact]
    public async Task DataSourceParameters_RequireStructuredDeclaredReferences()
    {
        var id = await _service.CreateAsync(new CreateProjectPageDto
        {
            ProjectId = 1,
            ModuleId = 2,
            PageKey = "structured-parameters",
            PageName = "Structured parameters",
            RoutePath = "/sales/structured-parameters",
            DataSources =
            {
                new CreatePageDataSourceDto
                {
                    SourceKey = "orderDetail",
                    EntityId = 3,
                    ServiceName = "OrderService",
                    MethodName = "GetByIdAsync",
                    DtoName = "OrderDto",
                    DataShape = PageDataShape.Single,
                    ParamsJson = "{\"id\":{\"$ref\":\"$route.id\"}}"
                },
                new CreatePageDataSourceDto
                {
                    SourceKey = "orderList",
                    EntityId = 3,
                    ServiceName = "OrderService",
                    MethodName = "GetPagedListAsync",
                    DtoName = "OrderDto",
                    DataShape = PageDataShape.Paged,
                    ParamsJson = "{\"customerName\":{\"$ref\":\"$source.orderDetail.CustomerName\"}}"
                }
            }
        });
        Assert.Equal(2, (await _service.GetByIdAsync(id))!.DataSources.Count);

        await Assert.ThrowsAsync<ArgumentException>(() => _service.CreateAsync(new CreateProjectPageDto
        {
            ProjectId = 1,
            ModuleId = 2,
            PageKey = "unsafe-parameters",
            PageName = "Unsafe parameters",
            RoutePath = "/sales/unsafe-parameters",
            DataSources =
            {
                new CreatePageDataSourceDto
                {
                    SourceKey = "orders",
                    EntityId = 3,
                    ServiceName = "OrderService",
                    MethodName = "GetPagedListAsync",
                    DtoName = "OrderDto",
                    DataShape = PageDataShape.Paged,
                    ParamsJson = "{\"id\":\"$route.id\"}"
                }
            }
        }));
    }

    [Fact]
    public async Task RebindComponent_UpdatesDatabaseBindingAndLayoutMetadata()
    {
        var pageId = await _service.CreateAsync(new CreateProjectPageDto
        {
            ProjectId = 1,
            ModuleId = 2,
            PageKey = "rebind-workbench",
            PageName = "Rebind workbench",
            RoutePath = "/sales/rebind-workbench",
            LayoutJson = "{\"version\":1,\"root\":{\"type\":\"page\",\"componentId\":\"page_root\",\"children\":[{\"type\":\"table\",\"componentId\":\"orders\",\"children\":[{\"type\":\"table-column\",\"componentId\":\"external-customer\",\"origin\":3}]}]}}",
            DataSources =
            {
                new CreatePageDataSourceDto
                {
                    SourceKey = "orders",
                    EntityId = 3,
                    ServiceName = "OrderService",
                    MethodName = "GetPagedListAsync",
                    DtoName = "OrderDto",
                    DataShape = PageDataShape.Paged
                }
            }
        });
        var sourceId = Assert.Single((await _service.GetByIdAsync(pageId))!.DataSources).Id;

        var updated = await _service.RebindComponentAsync(pageId, new RebindPageComponentDto
        {
            ComponentId = "external-customer",
            DataSourceId = sourceId,
            FieldId = 4,
            BindingRole = PageBindingRole.Column,
            LabelOverride = "Customer"
        });

        var binding = Assert.Single(updated.Bindings);
        Assert.Equal(4, binding.FieldId);
        Assert.Contains(binding.BindingId, updated.LayoutJson);
        Assert.Contains(binding.DataSourceId.ToString(), updated.LayoutJson);
    }

    [Fact]
    public async Task SameField_CanBeBoundMoreThanOnceWithDistinctBindingIdentity()
    {
        var pageId = await _service.CreateAsync(new CreateProjectPageDto
        {
            ProjectId = 1,
            ModuleId = 2,
            PageKey = "repeated-field",
            PageName = "Repeated field",
            RoutePath = "/sales/repeated-field",
            LayoutJson = "{\"version\":1,\"root\":{\"type\":\"page\",\"componentId\":\"page_root\",\"children\":[{\"type\":\"table\",\"componentId\":\"orders\",\"children\":[{\"type\":\"table-column\",\"componentId\":\"customer-primary\"},{\"type\":\"table-column\",\"componentId\":\"customer-secondary\"}]}]}}",
            DataSources =
            {
                new CreatePageDataSourceDto
                {
                    SourceKey = "orders",
                    EntityId = 3,
                    ServiceName = "OrderService",
                    MethodName = "GetPagedListAsync",
                    DtoName = "OrderDto",
                    DataShape = PageDataShape.Paged
                }
            },
            Bindings =
            {
                new CreatePageComponentBindingDto { ComponentId = "orders", DataSourceKey = "orders", BindingRole = PageBindingRole.Table },
                new CreatePageComponentBindingDto { ComponentId = "customer-primary", DataSourceKey = "orders", FieldId = 4, BindingRole = PageBindingRole.Column },
                new CreatePageComponentBindingDto { ComponentId = "customer-secondary", DataSourceKey = "orders", FieldId = 4, BindingRole = PageBindingRole.Column }
            }
        });

        var repeatedBindings = (await _service.GetByIdAsync(pageId))!.Bindings.Where(item => item.FieldId == 4).ToList();
        Assert.Equal(2, repeatedBindings.Count);
        Assert.Equal(2, repeatedBindings.Select(item => item.BindingId).Distinct().Count());
        Assert.Equal(2, repeatedBindings.Select(item => item.GeneratedGenId).Distinct().Count());
    }

    [Fact]
    public async Task ComplexPageGeneration_WritesBundleAndPreservesCustomTreeNodes()
    {
        var pageId = await _service.CreateAsync(new CreateProjectPageDto
        {
            ProjectId = 1,
            ModuleId = 2,
            PageKey = "generated-workbench",
            PageName = "Generated workbench",
            RoutePath = "/sales/generated-workbench",
            LayoutJson = "{\"version\":1,\"root\":{\"type\":\"page\",\"children\":[{\"type\":\"row\",\"children\":[{\"type\":\"column\",\"span\":24,\"children\":[{\"type\":\"table\"}]}]}]}}"
        });

        var generator = new ComplexPageGenerationService(_db, _service);
        var first = await generator.GenerateAsync(pageId);
        Assert.True(first.Success, first.Error);
        Assert.Equal(8, first.Files.Count);
        Assert.All(first.Files, file => Assert.True(File.Exists(file), file));
        Assert.Contains("<template>", await File.ReadAllTextAsync(first.VuePath));
        Assert.Contains("loadComplexPageData", await File.ReadAllTextAsync(Path.Combine(first.OutputDirectory, "generated-workbench.auto.js")));
        var treePath = first.TreePath;
        var tree = await File.ReadAllTextAsync(treePath);
        tree = tree.TrimEnd(']') + ", {\"id\":\"custom-note\",\"tag\":\"el-alert\",\"origin\":2}]";
        await File.WriteAllTextAsync(treePath, tree);

        var second = await generator.GenerateAsync(pageId, incremental: true);
        Assert.True(second.Success, second.Error);
        var mergedTree = await File.ReadAllTextAsync(treePath);
        Assert.Contains("custom-note", mergedTree);
        Assert.Equal(PageSyncStatus.Generated, (await _service.GetByIdAsync(pageId))!.SyncStatus);
    }

    [Fact]
    public async Task Registered_api_source_can_be_saved_and_generated()
    {
        var catalog = new ApplicationServiceCapabilityCatalog(new[] { typeof(RegisteredReportService) });
        var service = new ProjectPageService(_db, catalog);
        var pageId = await service.CreateAsync(new CreateProjectPageDto
        {
            ProjectId = 1,
            ModuleId = 2,
            PageKey = "registered-api",
            PageName = "Registered API",
            RoutePath = "/sales/registered-api",
            LayoutJson = """
                {"version":1,"root":{"type":"page","componentId":"page_root","children":[{"type":"table","componentId":"report_table","children":[]}]}}
                """,
            DataSources =
            {
                new CreatePageDataSourceDto
                {
                    SourceKey = "reportRows",
                    SourceType = PageDataSourceType.Api,
                    ServiceName = nameof(RegisteredReportService),
                    MethodName = nameof(RegisteredReportService.GetReportAsync),
                    DtoName = nameof(RegisteredReportDto),
                    DataShape = PageDataShape.Collection,
                    ParamsJson = "{}"
                }
            },
            Bindings =
            {
                new CreatePageComponentBindingDto
                {
                    ComponentId = "report_table",
                    DataSourceKey = "reportRows",
                    BindingRole = PageBindingRole.Table
                }
            }
        });

        var saved = await service.GetByIdAsync(pageId);
        Assert.Equal(PageDataSourceType.Api, Assert.Single(saved!.DataSources).SourceType);
        Assert.Null(saved.DataSources[0].EntityId);

        var generated = await new ComplexPageGenerationService(
            _db,
            service,
            capabilityCatalog: catalog).GenerateAsync(pageId);
        Assert.True(generated.Success, generated.Error);
        var autoScript = await File.ReadAllTextAsync(
            generated.Files.Single(path => path.EndsWith("registered-api.auto.js", StringComparison.OrdinalIgnoreCase)));
        Assert.Contains("/api/services/registeredreport/getreport", autoScript);
        Assert.Contains("method: \"get\"", autoScript);
    }

    [Fact]
    public async Task Imported_openapi_source_uses_metadata_model_and_generates_stored_route()
    {
        _db.Insertable(new ModuleEntity
        {
            Id = 40,
            ProjectId = 1,
            ModuleId = 2,
            Name = "ExternalOrder",
            Description = "External order API model",
            TableName = string.Empty,
            GenerationMode = EntityGenerationMode.MetadataOnly,
            GenerateFrontend = false,
            CreateBy = "test",
            UpdateUserId = 0
        }).ExecuteCommand();
        _db.Insertable(new EntityField
        {
            Id = 41,
            ModuleEntityId = 40,
            Name = "OrderNo",
            Description = "Order number",
            DataType = "string",
            ShowInList = true,
            CreateBy = "test",
            UpdateUserId = 0
        }).ExecuteCommand();
        _db.Insertable(new ProjectDataSource
        {
            Id = 42,
            ProjectId = 1,
            SourceKey = "openapi-orders",
            DisplayName = "External order detail",
            SourceType = PageDataSourceType.Api,
            EntityId = 40,
            ResponseEntityId = 40,
            ServiceName = "External Orders",
            MethodName = "getExternalOrder",
            DtoName = "ExternalOrderDto",
            DataShape = PageDataShape.Single,
            OpenApiDocumentId = 43,
            OperationKey = "GET /api/external/orders/{id}",
            HttpMethod = "GET",
            RoutePath = "/api/external/orders/{id}",
            OperationContractJson = "{\"parameters\":[{\"name\":\"id\",\"location\":\"path\",\"isRequired\":true}]}",
            OpenApiSyncState = OpenApiSyncState.Active,
            SchemaHash = "openapi-schema-hash",
            IsEnabled = true,
            CreateBy = "test",
            UpdateUserId = 0
        }).ExecuteCommand();

        var capabilities = await _service.GetEntityServiceCapabilitiesAsync(1);
        var metadataCapability = Assert.Single(capabilities, item => item.EntityId == 40);
        Assert.Equal(EntityGenerationMode.MetadataOnly, metadataCapability.GenerationMode);
        Assert.Empty(metadataCapability.Methods);

        var entitySourceError = await Assert.ThrowsAsync<InvalidOperationException>(() => _service.CreateAsync(new CreateProjectPageDto
        {
            ProjectId = 1,
            ModuleId = 2,
            PageKey = "invalid-metadata-entity-source",
            PageName = "Invalid metadata entity source",
            RoutePath = "/sales/invalid-metadata-entity-source",
            LayoutJson = "{\"version\":1,\"root\":{\"type\":\"page\",\"componentId\":\"page_root\",\"children\":[]}}",
            DataSources =
            {
                new CreatePageDataSourceDto
                {
                    SourceKey = "orders",
                    SourceType = PageDataSourceType.EntityService,
                    EntityId = 40,
                    ServiceName = "ExternalOrderService",
                    MethodName = "GetListAsync",
                    DtoName = "ExternalOrderDto",
                    DataShape = PageDataShape.Collection
                }
            }
        }));
        Assert.Contains("Metadata-only", entitySourceError.Message, StringComparison.OrdinalIgnoreCase);

        var input = new CreateProjectPageDto
        {
            ProjectId = 1,
            ModuleId = 2,
            PageKey = "imported-api",
            PageName = "Imported API",
            RoutePath = "/sales/imported-api",
            LayoutJson = "{\"version\":1,\"root\":{\"type\":\"page\",\"componentId\":\"page_root\",\"children\":[]}}",
            DataSources =
            {
                new CreatePageDataSourceDto
                {
                    SourceKey = "externalOrder",
                    SourceType = PageDataSourceType.Api,
                    ProjectDataSourceId = 42,
                    ServiceName = "forged-service",
                    MethodName = "forged-method",
                    DtoName = "forged-dto",
                    DataShape = PageDataShape.Collection,
                    ParamsJson = "{\"id\":{\"$ref\":\"$route.id\"}}"
                }
            }
        };
        var pageId = await _service.CreateAsync(input);
        var saved = await _service.GetByIdAsync(pageId);
        var savedSource = Assert.Single(saved!.DataSources);
        Assert.Equal(42, savedSource.ProjectDataSourceId);
        Assert.Equal(40, savedSource.EntityId);
        Assert.Equal("getExternalOrder", savedSource.MethodName);
        Assert.Equal(PageDataShape.Single, savedSource.DataShape);
        Assert.Single(await _db.Queryable<PageDataSourceBinding>()
            .Where(item => item.PageId == pageId && item.DataSourceId == 42 && !item.IsDeleted)
            .ToListAsync());

        await _service.SaveDefinitionAsync(pageId, new UpdateProjectPageDto
        {
            ProjectId = saved.ProjectId,
            ModuleId = saved.ModuleId,
            PageKey = saved.PageKey,
            PageName = saved.PageName,
            RoutePath = saved.RoutePath,
            PageType = saved.PageType,
            LayoutJson = saved.LayoutJson,
            SyncMode = saved.SyncMode,
            DefinitionVersion = saved.DefinitionVersion,
            DataSources = input.DataSources
        });
        Assert.Single(await _db.Queryable<PageDataSourceBinding>()
            .Where(item => item.PageId == pageId && item.DataSourceId == 42 && !item.IsDeleted)
            .ToListAsync());

        var generated = await new ComplexPageGenerationService(_db, _service).GenerateAsync(pageId);
        Assert.True(generated.Success, generated.Error);
        var autoScript = await File.ReadAllTextAsync(
            generated.Files.Single(path => path.EndsWith("imported-api.auto.js", StringComparison.OrdinalIgnoreCase)));
        Assert.Contains("/api/external/orders", autoScript);
        Assert.Contains("pathParameter: \"id\"", autoScript);
        Assert.DoesNotContain("forged-service", autoScript);

        var detachedSource = await _db.Queryable<ProjectDataSource>().Where(item => item.Id == 42).FirstAsync();
        detachedSource.OpenApiSyncState = OpenApiSyncState.Detached;
        await _db.Updateable(detachedSource).ExecuteCommandAsync();
        var detachedPage = await _service.GetByIdAsync(pageId);
        await _service.SaveDefinitionAsync(pageId, new UpdateProjectPageDto
        {
            ProjectId = detachedPage!.ProjectId,
            ModuleId = detachedPage.ModuleId,
            PageKey = detachedPage.PageKey,
            PageName = detachedPage.PageName,
            RoutePath = detachedPage.RoutePath,
            PageType = detachedPage.PageType,
            LayoutJson = detachedPage.LayoutJson,
            SyncMode = detachedPage.SyncMode,
            DefinitionVersion = detachedPage.DefinitionVersion,
            DataSources = input.DataSources
        });
        var detachedGeneration = await new ComplexPageGenerationService(_db, _service).GenerateAsync(pageId);
        Assert.True(detachedGeneration.Success, detachedGeneration.Error);
    }

    [Fact]
    public async Task ComplexPageGeneration_preserves_modified_region_and_marks_conflict()
    {
        var pageId = await _service.CreateAsync(new CreateProjectPageDto
        {
            ProjectId = 1,
            ModuleId = 2,
            PageKey = "region-conflict-workbench",
            PageName = "Region conflict workbench",
            RoutePath = "/sales/region-conflict-workbench",
            LayoutJson = "{\"version\":1,\"root\":{\"type\":\"page\",\"componentId\":\"page_root\",\"children\":[{\"type\":\"table\",\"componentId\":\"orders\",\"children\":[{\"type\":\"table-column\",\"componentId\":\"customer\"}]}]}}",
            DataSources =
            {
                new CreatePageDataSourceDto
                {
                    SourceKey = "orders",
                    EntityId = 3,
                    ServiceName = "OrderService",
                    MethodName = "GetPagedListAsync",
                    DtoName = "OrderDto",
                    DataShape = PageDataShape.Paged
                }
            },
            Bindings =
            {
                new CreatePageComponentBindingDto
                {
                    ComponentId = "orders",
                    DataSourceKey = "orders",
                    BindingRole = PageBindingRole.Table
                },
                new CreatePageComponentBindingDto
                {
                    ComponentId = "customer",
                    DataSourceKey = "orders",
                    FieldId = 4,
                    BindingRole = PageBindingRole.Column
                }
            }
        });

        var generator = new ComplexPageGenerationService(_db, _service);
        var first = await generator.GenerateAsync(pageId);
        Assert.True(first.Success, first.Error);
        Assert.True(File.Exists(first.VuePath + ".genhash.json"));
        var original = await File.ReadAllTextAsync(first.VuePath);
        var modified = original.Replace("label=\"Customer name\"", "label=\"Manual label\"", StringComparison.Ordinal);
        Assert.NotEqual(original, modified);
        await File.WriteAllTextAsync(first.VuePath, modified);

        var second = await generator.GenerateAsync(pageId, incremental: true);

        Assert.False(second.Success);
        Assert.True(second.HasConflicts);
        Assert.Contains(second.Conflicts, item => item.GenId.StartsWith("gen_", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("Manual label", await File.ReadAllTextAsync(first.VuePath));
        Assert.Equal(PageSyncStatus.Conflict, (await _service.GetByIdAsync(pageId))!.SyncStatus);
    }

    [Fact]
    public async Task ExternalVueSync_RequiresConfirmationAndPreservesVueScriptAndStyle()
    {
        var pageId = await _service.CreateAsync(new CreateProjectPageDto
        {
            ProjectId = 1,
            ModuleId = 2,
            PageKey = "external-sync-workbench",
            PageName = "External sync workbench",
            RoutePath = "/sales/external-sync-workbench",
            LayoutJson = "{\"version\":1,\"root\":{\"type\":\"page\",\"children\":[]}}"
        });

        var generator = new ComplexPageGenerationService(_db, _service);
        var generated = await generator.GenerateAsync(pageId);
        Assert.True(generated.Success, generated.Error);

        var originalVue = await File.ReadAllTextAsync(generated.VuePath);
        var originalScriptSetup = new VueTemplateParser()
            .ParseOfficial(originalVue, Path.GetFileName(generated.VuePath))
            .ScriptSetupContent;
        var externalVue = originalVue.Replace(
            "</template>",
            "  <el-alert title=\"Added outside CodeMaster\" />\n</template>",
            StringComparison.Ordinal);
        externalVue += "\n<style scoped>.external-sync-marker { color: red; }</style>\n";
        await File.WriteAllTextAsync(generated.VuePath, externalVue);

        var sync = new ComplexPageExternalSyncService(_db, _service);
        var inspection = await sync.InspectAsync(pageId);
        Assert.True(inspection.Changed);
        Assert.True(inspection.CanImport);
        Assert.Equal(VueOfficialSfcParser.ExpectedCompilerVersion, inspection.VueCompilerVersion);
        Assert.True(inspection.ScriptSetupPreserved);
        Assert.Contains(inspection.Diffs, diff => diff.ChangeType == "Added" && diff.Detail.Contains("el-alert"));

        var treeBeforePreview = await File.ReadAllTextAsync(generated.TreePath);
        var preview = await sync.ImportTemplateAsync(pageId);
        Assert.True(preview.Changed, preview.Error);
        Assert.False(preview.ExternalNodeCount > 0);
        Assert.Equal(treeBeforePreview, await File.ReadAllTextAsync(generated.TreePath));
        Assert.DoesNotContain("external_", treeBeforePreview);

        var imported = await sync.ImportTemplateAsync(pageId, confirmed: true);
        Assert.False(imported.Changed);
        Assert.True(imported.ExternalNodeCount > 0);
        Assert.True(imported.MatchedNodeCount > 0);
        Assert.NotNull(imported.BackupDirectory);
        Assert.True(Directory.Exists(imported.BackupDirectory));
        Assert.True(imported.ScriptSetupPreserved);
        Assert.NotNull(imported.ScriptSetupBackupPath);
        Assert.True(File.Exists(imported.ScriptSetupBackupPath));
        Assert.Equal(originalScriptSetup, await File.ReadAllTextAsync(imported.ScriptSetupBackupPath));
        Assert.Contains("external_", await File.ReadAllTextAsync(generated.TreePath));
        Assert.Contains("external-sync-marker", await File.ReadAllTextAsync(generated.VuePath));
        Assert.Equal(PageSyncStatus.NeedsSync, (await _service.GetByIdAsync(pageId))!.SyncStatus);

        var regeneratedImport = await generator.GenerateAsync(pageId, incremental: true);
        Assert.True(regeneratedImport.Success, regeneratedImport.Error);
        Assert.Contains("Added outside CodeMaster", await File.ReadAllTextAsync(regeneratedImport.VuePath));

        await File.WriteAllTextAsync(generated.VuePath, originalVue.Replace(
            "</template>",
            "  <el-alert title=\"Rejected edit\" />\n</template>",
            StringComparison.Ordinal));
        var rejected = await sync.RejectImportAsync(pageId);
        Assert.False(rejected.Changed);
        var regenerated = await generator.GenerateAsync(pageId);
        Assert.True(regenerated.Success, regenerated.Error);
        Assert.DoesNotContain("Rejected edit", await File.ReadAllTextAsync(regenerated.VuePath));

        await File.WriteAllTextAsync(generated.VuePath, externalVue);
        var restored = await sync.RestoreBackupAsync(pageId);
        Assert.True(restored.BackupDirectory != null && Directory.Exists(restored.BackupDirectory));
        Assert.Contains("external-sync-marker", await File.ReadAllTextAsync(generated.VuePath));
    }

    [Fact]
    public async Task ExternalImport_DetectsRemovedGenIdsDuplicateIdsAndKeepsCustomScript()
    {
        var pageId = await _service.CreateAsync(new CreateProjectPageDto
        {
            ProjectId = 1,
            ModuleId = 2,
            PageKey = "external-diff-workbench",
            PageName = "External diff workbench",
            RoutePath = "/sales/external-diff-workbench",
            LayoutJson = "{\"version\":1,\"root\":{\"type\":\"page\",\"componentId\":\"page_root\",\"children\":[{\"type\":\"table\",\"componentId\":\"orders\",\"children\":[]}]}}",
            DataSources =
            {
                new CreatePageDataSourceDto
                {
                    SourceKey = "orders",
                    EntityId = 3,
                    ServiceName = "OrderService",
                    MethodName = "GetPagedListAsync",
                    DtoName = "OrderDto",
                    DataShape = PageDataShape.Paged
                }
            },
            Bindings =
            {
                new CreatePageComponentBindingDto
                {
                    ComponentId = "orders",
                    DataSourceKey = "orders",
                    BindingRole = PageBindingRole.Table
                }
            }
        });

        var generator = new ComplexPageGenerationService(_db, _service);
        var generated = await generator.GenerateAsync(pageId);
        Assert.True(generated.Success, generated.Error);
        var originalVue = await File.ReadAllTextAsync(generated.VuePath);
        Assert.Contains("gen_binding_binding_orders", originalVue);

        var externalVue = originalVue
            .Replace("gen_binding_binding_orders", "gen_binding_binding_orders_duplicate", StringComparison.Ordinal)
            .Replace(
                "</template>",
                "  <div class=\"external-toolbar\" data-gen-id=\"gen_binding_binding_orders_duplicate\"><span>Dup</span></div>\n  <el-card class=\"external-card\">New container</el-card>\n</template>",
                StringComparison.Ordinal)
            + "\n<script>\nexport const externalHelper = () => fetch('/custom/api')\n</script>\n";

        await File.WriteAllTextAsync(generated.VuePath, externalVue);

        var sync = new ComplexPageExternalSyncService(_db, _service);
        var inspection = await sync.InspectAsync(pageId);
        Assert.True(inspection.Changed);
        Assert.Contains(inspection.Diffs, diff => diff.ChangeType == "Removed");
        Assert.Contains(inspection.Diffs, diff => diff.ChangeType == "Added" && diff.Tag == "el-card");

        var imported = await sync.ImportTemplateAsync(pageId, confirmed: true);
        Assert.False(imported.Changed);
        var importedTree = await File.ReadAllTextAsync(generated.TreePath);
        Assert.Contains("external-card", importedTree);
        Assert.DoesNotContain("gen_binding_binding_orders\",", importedTree);
        Assert.Contains("externalHelper", await File.ReadAllTextAsync(generated.VuePath));
        Assert.Contains("custom/api", await File.ReadAllTextAsync(generated.VuePath));
    }

    [Fact]
    public async Task GeneratePage_SyncsRouterEntryWhenMenuSyncedEnabled()
    {
        var vueRoot = Path.Combine(CodeMasterTestPaths.Root, $"complex_router_{Guid.NewGuid():N}", "ComplexPageProject.Vue");
        var routerDir = Path.Combine(vueRoot, "src", "router");
        Directory.CreateDirectory(routerDir);
        var routerContent = """
            import Layout from '@/layout/index.vue'

            export const constantRoutes = [
              {
                path: '/',
                component: Layout,
                redirect: '/dashboard',
                children: [
                  {
                    path: 'dashboard',
                    name: 'Dashboard',
                    component: () => import('@/views/dashboard/index.vue'),
                    meta: { title: 'dashboard', icon: 'HomeFilled', affix: true }
                  }
                ]
              }
            ]

            const router = createRouter({ history: createWebHashHistory(), routes: constantRoutes })
            export default router
            """;
        var routerPath = Path.Combine(routerDir, "index.js");
        await File.WriteAllTextAsync(routerPath, routerContent);

        var pageId = await _service.CreateAsync(new CreateProjectPageDto
        {
            ProjectId = 1,
            ModuleId = 2,
            PageKey = "order-menu-sync",
            PageName = "Order menu sync",
            RoutePath = "/sales/order-menu-sync",
            LayoutJson = "{\"version\":1,\"root\":{\"type\":\"page\",\"componentId\":\"page_root\",\"children\":[]}}",
            IsMenuSynced = true
        });

        await _db.Updateable<Project>()
            .SetColumns(item => new Project { ProjectPath = Path.GetDirectoryName(vueRoot)! })
            .Where(item => item.Id == 1)
            .ExecuteCommandAsync();

        var generator = new ComplexPageGenerationService(_db, _service);
        var generated = await generator.GenerateAsync(pageId);
        Assert.True(generated.Success, generated.Error);

        var synced = await File.ReadAllTextAsync(routerPath);
        Assert.Contains("path: 'sales/order-menu-sync'", synced);
        Assert.Contains("name: 'ComplexPageSalesOrderMenuSync'", synced);
        Assert.Contains("import('@/views/sales/order-menu-sync/index.vue')", synced);
        Assert.Contains("title: 'Order menu sync'", synced);

        var second = await generator.GenerateAsync(pageId);
        Assert.True(second.Success, second.Error);
        var afterSecond = await File.ReadAllTextAsync(routerPath);
        Assert.Equal(synced.Count(ch => ch == 's'), afterSecond.Count(ch => ch == 's'));
        Assert.Contains("sales/order-menu-sync", afterSecond);
    }

    [Fact]
    public async Task Synchronization_AddsAutoFieldsPreservesBindingsAndInvalidatesDeletedFields()
    {
        var pageId = await _service.CreateAsync(new CreateProjectPageDto
        {
            ProjectId = 1,
            ModuleId = 2,
            PageKey = "synchronized-workbench",
            PageName = "Synchronized workbench",
            RoutePath = "/sales/synchronized-workbench",
            LayoutJson = "{\"version\":1,\"root\":{\"type\":\"page\",\"componentId\":\"page_root\",\"children\":[{\"type\":\"table\",\"componentId\":\"orders\",\"children\":[{\"type\":\"table-column\",\"componentId\":\"customer-name\"}]}]}}",
            DataSources =
            {
                new CreatePageDataSourceDto
                {
                    SourceKey = "orders",
                    EntityId = 3,
                    ServiceName = "OrderService",
                    MethodName = "GetPagedListAsync",
                    DtoName = "OrderDto",
                    DataShape = PageDataShape.Paged,
                    SyncMode = PageSyncMode.Auto
                }
            },
            Bindings =
            {
                new CreatePageComponentBindingDto
                {
                    ComponentId = "orders",
                    DataSourceKey = "orders",
                    BindingRole = PageBindingRole.Table,
                    SyncMode = PageSyncMode.Auto,
                    AutoIncludeNewFields = true
                },
                new CreatePageComponentBindingDto
                {
                    ComponentId = "customer-name",
                    BindingId = "binding_customer_name",
                    DataSourceKey = "orders",
                    FieldId = 4,
                    BindingRole = PageBindingRole.Column,
                    ControlType = "text",
                    LabelOverride = "Customer"
                }
            }
        });
        var originalBinding = (await _service.GetByIdAsync(pageId))!.Bindings.Single(item => item.FieldId == 4);

        await _db.Updateable<EntityField>()
            .SetColumns(item => new EntityField { Name = "ClientName", UpdateTime = DateTime.UtcNow.AddDays(-2) })
            .Where(item => item.Id == 4)
            .ExecuteCommandAsync();
        await _db.Insertable(new EntityField
        {
            Id = 6,
            ModuleEntityId = 3,
            Name = "TotalAmount",
            Description = "Total amount",
            DataType = "decimal",
            FormControlType = "input-number",
            ShowInList = true,
            OrderNum = 2,
            CreateBy = "test",
            CreateTime = DateTime.UtcNow.AddDays(-3),
            UpdateUserId = 0
        }).ExecuteCommandAsync();

        var synchronization = new ComplexPageSynchronizationService(_db, _service);
        var preview = await synchronization.PreviewAsync(pageId);
        Assert.True(preview.RequiresSync);
        Assert.Contains(preview.Changes, item => item.FieldId == 4 && item.ChangeType == "Renamed");
        Assert.Contains(preview.Changes, item => item.FieldId == 6 && item.ChangeType == "Added");

        var synchronized = await synchronization.SynchronizeAsync(pageId);
        Assert.True(synchronized.Success, synchronized.Error);
        Assert.Equal(1, synchronized.AddedBindings);
        var updatedPage = (await _service.GetByIdAsync(pageId))!;
        var preservedBinding = updatedPage.Bindings.Single(item => item.FieldId == 4);
        Assert.Equal(originalBinding.BindingId, preservedBinding.BindingId);
        Assert.Equal("Customer", preservedBinding.LabelOverride);
        var addedBinding = updatedPage.Bindings.Single(item => item.FieldId == 6);
        Assert.Contains(addedBinding.ComponentId, updatedPage.LayoutJson);
        Assert.Equal(PageBindingStatus.Active, addedBinding.BindingStatus);

        await _db.Updateable<EntityField>()
            .SetColumns(item => new EntityField { IsDeleted = true, DeleteTime = DateTime.UtcNow })
            .Where(item => item.Id == 4)
            .ExecuteCommandAsync();
        var deletedPreview = await synchronization.PreviewAsync(pageId);
        Assert.Contains(deletedPreview.Changes, item => item.FieldId == 4 && item.ChangeType == "Deleted");

        var afterDelete = await synchronization.SynchronizeAsync(pageId);
        Assert.True(afterDelete.Success, afterDelete.Error);
        Assert.Equal(1, afterDelete.InvalidBindings);
        Assert.Equal(PageBindingStatus.Invalid,
            (await _service.GetByIdAsync(pageId))!.Bindings.Single(item => item.FieldId == 4).BindingStatus);

        var generated = await new ComplexPageGenerationService(_db, _service).GenerateAsync(pageId);
        Assert.True(generated.Success, generated.Error);
        Assert.Contains("Invalid binding: binding_customer_name", await File.ReadAllTextAsync(generated.VuePath));
    }

    [Fact]
    public async Task GeneratePage_QueryAreaFieldsBindToStateQuery()
    {
        var now = DateTime.UtcNow;
        await _db.Insertable(new EntityField
        {
            Id = 40,
            ModuleEntityId = 3,
            Name = "CustomerName",
            Description = "Customer name",
            DataType = "string",
            CreateBy = "test",
            UpdateUserId = 0,
            CreateTime = now
        }).ExecuteCommandAsync();

        var layoutJson = """
            {"version":1,"root":{"type":"page","componentId":"page_root","origin":1,"children":[
              {"type":"row","componentId":"query_row","origin":1,"children":[
                {"type":"column","componentId":"query_column","span":24,"origin":1,"children":[
                  {"type":"search","componentId":"query_panel","origin":1,"children":[
                    {"type":"form-field","componentId":"query_customer","origin":1,"children":[]}
                  ]}
                ]}
              ]},
              {"type":"row","componentId":"list_row","origin":1,"children":[
                {"type":"column","componentId":"list_column","span":24,"origin":1,"children":[
                  {"type":"table","componentId":"result_table","origin":1,"children":[]},
                  {"type":"pagination","componentId":"result_pagination","origin":1,"children":[]}
                ]}
              ]}
            ]}}
            """;

        var id = await _service.CreateAsync(new CreateProjectPageDto
        {
            ProjectId = 1,
            ModuleId = 2,
            PageKey = "order-query",
            PageName = "Order query",
            RoutePath = "/sales/order-query",
            LayoutJson = layoutJson,
            DataSources =
            {
                new CreatePageDataSourceDto
                {
                    SourceKey = "orderList",
                    EntityId = 3,
                    ServiceName = "OrderService",
                    MethodName = "GetPageListAsync",
                    DtoName = "OrderDto",
                    DataShape = PageDataShape.Paged
                }
            },
            Bindings =
            {
                new CreatePageComponentBindingDto
                {
                    ComponentId = "result_table",
                    DataSourceKey = "orderList",
                    BindingRole = PageBindingRole.Table
                },
                new CreatePageComponentBindingDto
                {
                    ComponentId = "query_customer",
                    DataSourceKey = "orderList",
                    FieldId = 40,
                    BindingRole = PageBindingRole.Field,
                    ControlType = "input"
                }
            }
        });

        var generated = await new ComplexPageGenerationService(_db, _service).GenerateAsync(id);
        Assert.True(generated.Success, generated.Error);
        var generatedVue = await File.ReadAllTextAsync(generated.VuePath);
        var generatedAuto = await File.ReadAllTextAsync(Path.Combine(generated.OutputDirectory, "order-query.auto.js"));
        Assert.Contains("v-model=\"state.query['customerName']\"", generatedVue);
        Assert.Contains("query: {}", generatedAuto);
        Assert.Contains("...(context.query || {})", generatedAuto);
    }

    [Fact]
    public async Task Regenerate_ReplaysDesignJsonAndPreservesScriptAndFieldsJson()
    {
        var pageId = await _service.CreateAsync(new CreateProjectPageDto
        {
            ProjectId = 1,
            ModuleId = 2,
            PageKey = "design-replay-workbench",
            PageName = "Design replay workbench",
            RoutePath = "/sales/design-replay-workbench",
            LayoutJson = "{\"version\":1,\"root\":{\"type\":\"page\",\"componentId\":\"page_root\",\"children\":[{\"type\":\"table\",\"componentId\":\"orders\",\"children\":[]}]}}",
            DataSources =
            {
                new CreatePageDataSourceDto
                {
                    SourceKey = "orders",
                    EntityId = 3,
                    ServiceName = "OrderService",
                    MethodName = "GetPagedListAsync",
                    DtoName = "OrderDto",
                    DataShape = PageDataShape.Paged
                }
            },
            Bindings =
            {
                new CreatePageComponentBindingDto
                {
                    ComponentId = "orders",
                    DataSourceKey = "orders",
                    BindingRole = PageBindingRole.Table
                }
            }
        });

        var generator = new ComplexPageGenerationService(_db, _service);
        var generated = await generator.GenerateAsync(pageId);
        Assert.True(generated.Success, generated.Error);

        var outputDirectory = generated.OutputDirectory;
        var treeJson = await File.ReadAllTextAsync(generated.TreePath);
        var genIdMatch = System.Text.RegularExpressions.Regex.Match(treeJson, "\"genId\"\\s*:\\s*\"([^\"]+)\"");
        Assert.True(genIdMatch.Success, "No genId found in generated tree.");
        var genId = genIdMatch.Groups[1].Value;

        var designPath = Path.Combine(outputDirectory, "design-replay-workbench.design.json");
        var designDocument = $"{{\"version\":1,\"operations\":[{{\"operationId\":\"op-replay\",\"type\":\"setprop\",\"targetGenId\":\"{genId}\",\"propName\":\"replay-marker\",\"propValue\":\"design-replayed\"}}]}}";
        await File.WriteAllTextAsync(designPath, designDocument);

        const string scriptContent = "{\"version\":1,\"sections\":[{\"type\":\"watch\",\"items\":[{\"name\":\"onReplay\",\"handler\":\"console.log('replay')\"}]}]}";
        const string fieldsContent = "{\"customerName\":{\"version\":1,\"sections\":[{\"type\":\"watch\",\"items\":[{\"name\":\"onCustomerName\",\"handler\":\"console.log('customer')\"}]}]}}";
        await File.WriteAllTextAsync(Path.Combine(outputDirectory, "design-replay-workbench.script.json"), scriptContent);
        await File.WriteAllTextAsync(Path.Combine(outputDirectory, "design-replay-workbench.fields.json"), fieldsContent);

        var regenerated = await generator.GenerateAsync(pageId);
        Assert.True(regenerated.Success, regenerated.Error);
        Assert.Contains("replay-marker=\"design-replayed\"", await File.ReadAllTextAsync(regenerated.VuePath));
        Assert.Equal(scriptContent, await File.ReadAllTextAsync(Path.Combine(outputDirectory, "design-replay-workbench.script.json")));
        Assert.Equal(fieldsContent, await File.ReadAllTextAsync(Path.Combine(outputDirectory, "design-replay-workbench.fields.json")));
        Assert.Equal(designDocument, await File.ReadAllTextAsync(designPath));
    }

    [Fact]
    public async Task OrderWorkbench_MultiSourceRowSelectionDrivesDetailCustomerAndItems()
    {
        var now = DateTime.UtcNow;
        await _db.Insertable(new ModuleEntity
        {
            Id = 60,
            ProjectId = 1,
            ModuleId = 2,
            Name = "Customer",
            Description = "Customer",
            TableName = "sales_customer",
            CreateBy = "test",
            UpdateUserId = 0,
            CreateTime = now
        }).ExecuteCommandAsync();
        await _db.Insertable(new ModuleEntity
        {
            Id = 61,
            ProjectId = 1,
            ModuleId = 2,
            Name = "OrderItem",
            Description = "Order item",
            TableName = "sales_order_item",
            CreateBy = "test",
            UpdateUserId = 0,
            CreateTime = now
        }).ExecuteCommandAsync();
        await _db.Insertable(new EntityField
        {
            Id = 62,
            ModuleEntityId = 60,
            Name = "Name",
            Description = "Customer name",
            DataType = "string",
            CreateBy = "test",
            UpdateUserId = 0,
            CreateTime = now
        }).ExecuteCommandAsync();
        await _db.Insertable(new EntityField
        {
            Id = 63,
            ModuleEntityId = 61,
            Name = "OrderId",
            Description = "Order id",
            DataType = "long",
            CreateBy = "test",
            UpdateUserId = 0,
            CreateTime = now
        }).ExecuteCommandAsync();
        await _db.Insertable(new EntityField
        {
            Id = 64,
            ModuleEntityId = 61,
            Name = "ProductName",
            Description = "Product",
            DataType = "string",
            CreateBy = "test",
            UpdateUserId = 0,
            CreateTime = now
        }).ExecuteCommandAsync();

        var layoutJson = """
            {"version":1,"root":{"type":"page","componentId":"page_root","origin":1,"children":[
              {"type":"row","componentId":"orders_row","origin":1,"children":[
                {"type":"column","componentId":"orders_column","span":24,"origin":1,"children":[
                  {"type":"table","componentId":"orders","origin":1,"children":[]}
                ]}
              ]},
              {"type":"row","componentId":"details_row","origin":1,"children":[
                {"type":"column","componentId":"detail_column","span":12,"origin":1,"children":[
                  {"type":"detail","componentId":"order_detail","origin":1,"children":[]},
                  {"type":"detail","componentId":"customer_detail","origin":1,"children":[]}
                ]},
                {"type":"column","componentId":"items_column","span":12,"origin":1,"children":[
                  {"type":"table","componentId":"order_items","origin":1,"children":[]}
                ]}
              ]}
            ]}}
            """;

        var id = await _service.CreateAsync(new CreateProjectPageDto
        {
            ProjectId = 1,
            ModuleId = 2,
            PageKey = "order-workbench",
            PageName = "Order workbench",
            RoutePath = "/sales/order-workbench",
            LayoutJson = layoutJson,
            DataSources =
            {
                new CreatePageDataSourceDto
                {
                    SourceKey = "orderList",
                    EntityId = 3,
                    ServiceName = "OrderService",
                    MethodName = "GetPagedListAsync",
                    DtoName = "OrderDto",
                    DataShape = PageDataShape.Paged
                },
                new CreatePageDataSourceDto
                {
                    SourceKey = "orderDetail",
                    EntityId = 3,
                    ServiceName = "OrderService",
                    MethodName = "GetByIdAsync",
                    DtoName = "OrderDto",
                    DataShape = PageDataShape.Single,
                    ParamsJson = "{\"id\":{\"$ref\":\"$event.row.id\"}}"
                },
                new CreatePageDataSourceDto
                {
                    SourceKey = "customer",
                    EntityId = 60,
                    ServiceName = "CustomerService",
                    MethodName = "GetByIdAsync",
                    DtoName = "CustomerDto",
                    DataShape = PageDataShape.Single,
                    ParamsJson = "{\"id\":{\"$ref\":\"$event.row.customerId\"}}"
                },
                new CreatePageDataSourceDto
                {
                    SourceKey = "orderItems",
                    EntityId = 61,
                    ServiceName = "OrderItemService",
                    MethodName = "GetListAsync",
                    DtoName = "OrderItemDto",
                    DataShape = PageDataShape.Collection,
                    ParamsJson = "{\"orderId\":{\"$ref\":\"$event.row.id\"}}"
                }
            },
            Bindings =
            {
                new CreatePageComponentBindingDto
                {
                    ComponentId = "orders",
                    DataSourceKey = "orderList",
                    BindingRole = PageBindingRole.Table
                },
                new CreatePageComponentBindingDto
                {
                    ComponentId = "orders",
                    BindingId = "orders-event-orderDetail",
                    DataSourceKey = "orderDetail",
                    BindingRole = PageBindingRole.Event
                },
                new CreatePageComponentBindingDto
                {
                    ComponentId = "orders",
                    BindingId = "orders-event-customer",
                    DataSourceKey = "customer",
                    BindingRole = PageBindingRole.Event
                },
                new CreatePageComponentBindingDto
                {
                    ComponentId = "orders",
                    BindingId = "orders-event-orderItems",
                    DataSourceKey = "orderItems",
                    BindingRole = PageBindingRole.Event
                },
                new CreatePageComponentBindingDto
                {
                    ComponentId = "order_detail",
                    DataSourceKey = "orderDetail",
                    BindingRole = PageBindingRole.Detail,
                    FieldId = 4
                },
                new CreatePageComponentBindingDto
                {
                    ComponentId = "customer_detail",
                    DataSourceKey = "customer",
                    BindingRole = PageBindingRole.Detail,
                    FieldId = 62
                },
                new CreatePageComponentBindingDto
                {
                    ComponentId = "order_items",
                    DataSourceKey = "orderItems",
                    BindingRole = PageBindingRole.Table
                }
            }
        });

        var page = await _service.GetByIdAsync(id);
        Assert.NotNull(page);
        Assert.Equal(4, page.DataSources.Count);
        Assert.Equal(7, page.Bindings.Count);
        Assert.Matches("\\\"bindingId\\\"\\s*:\\s*\\\"binding_orders\\\"", page.LayoutJson);
        Assert.DoesNotContain("orders-event-orderItems", page.LayoutJson);

        var generated = await new ComplexPageGenerationService(_db, _service).GenerateAsync(id);
        Assert.True(generated.Success, generated.Error);
        var auto = await File.ReadAllTextAsync(Path.Combine(generated.OutputDirectory, "order-workbench.auto.js"));
        Assert.Contains("key: \"orderList\"", auto);
        Assert.Contains("key: \"orderDetail\"", auto);
        Assert.Contains("key: \"customer\"", auto);
        Assert.Contains("key: \"orderItems\"", auto);
        Assert.Contains("shape: \"Paged\"", auto);
        Assert.Contains("shape: \"Single\"", auto);
        Assert.Contains("shape: \"Collection\"", auto);
        Assert.Contains("loadOnInit: false", auto);
        Assert.DoesNotContain("loadOnInit: false, loadOnInit: false", auto);
        Assert.Contains("{\"id\":{\"$ref\":\"$event.row.id\"}}", auto);
        Assert.Contains("{\"orderId\":{\"$ref\":\"$event.row.id\"}}", auto);

        var vue = await File.ReadAllTextAsync(generated.VuePath);
        Assert.Contains("@row-click=\"row => {", vue);
        Assert.Contains("reloadDataSource('orderDetail', {}, { row })", vue);
        Assert.Contains("reloadDataSource('customer', {}, { row })", vue);
        Assert.Contains("reloadDataSource('orderItems', {}, { row })", vue);
        Assert.DoesNotContain("row => row =>", vue);
    }

    [Fact]
    public async Task DisplayTemplates_replace_container_and_field_markers_and_preserve_old_bundle_on_failure()
    {
        _db.Insertable(new PageDisplayTemplate
        {
            Id = 8001,
            TenantId = 0,
            TemplateKey = "test-query-template",
            TemplateName = "Test query",
            Scope = PageDisplayTemplateScope.Container,
            TargetType = "query-form",
            TemplateContent = "<div class=\"query-template\">[item]</div>",
            IsSystem = false,
            IsEnabled = true,
            CreateBy = "test",
            CreateTime = DateTime.UtcNow,
            UpdateUserId = 0
        }).ExecuteCommand();
        _db.Insertable(new PageDisplayTemplate
        {
            Id = 8002,
            TenantId = 0,
            TemplateKey = "test-field-template",
            TemplateName = "Test field",
            Scope = PageDisplayTemplateScope.Field,
            TargetType = "table-column",
            TemplateContent = "<a class=\"field-template\">{{ row.field }}</a>",
            IsSystem = false,
            IsEnabled = true,
            CreateBy = "test",
            CreateTime = DateTime.UtcNow,
            UpdateUserId = 0
        }).ExecuteCommand();

        var pageId = await _service.CreateAsync(new CreateProjectPageDto
        {
            ProjectId = 1,
            ModuleId = 2,
            PageKey = "display-template-page",
            PageName = "Display template page",
            RoutePath = "/sales/display-template-page",
            LayoutJson = """
            {"version":1,"root":{"type":"page","componentId":"root","children":[
              {"type":"query-form","componentId":"query","children":[{"type":"form-field","componentId":"query_field"}]},
              {"type":"table","componentId":"table","children":[{"type":"table-column","componentId":"column"}]}
            ]}}
            """,
            DataSources =
            {
                new CreatePageDataSourceDto
                {
                    SourceKey = "orders",
                    EntityId = 3,
                    ServiceName = "OrderService",
                    MethodName = "GetPagedListAsync",
                    DtoName = "OrderDto",
                    DataShape = PageDataShape.Paged
                }
            },
            Bindings =
            {
                new CreatePageComponentBindingDto
                {
                    ComponentId = "query",
                    DataSourceKey = "orders",
                    BindingRole = PageBindingRole.Container,
                    PropsJson = "{\"displayTemplateId\":8001}"
                },
                new CreatePageComponentBindingDto
                {
                    ComponentId = "query_field",
                    DataSourceKey = "orders",
                    BindingRole = PageBindingRole.Field,
                    FieldId = 4
                },
                new CreatePageComponentBindingDto
                {
                    ComponentId = "table",
                    DataSourceKey = "orders",
                    BindingRole = PageBindingRole.Table
                },
                new CreatePageComponentBindingDto
                {
                    ComponentId = "column",
                    DataSourceKey = "orders",
                    BindingRole = PageBindingRole.Column,
                    FieldId = 4,
                    PropsJson = "{\"displayTemplateId\":8002}"
                }
            }
        });

        var generator = new ComplexPageGenerationService(_db, _service);
        var first = await generator.GenerateAsync(pageId);
        Assert.True(first.Success, first.Error);
        var previousVue = await File.ReadAllTextAsync(first.VuePath);
        Assert.Contains("query-template", previousVue);
        Assert.Contains("field-template", previousVue);
        Assert.Contains("scope.row.customerName", previousVue);

        await _db.Updateable<PageDisplayTemplate>()
            .SetColumns(item => new PageDisplayTemplate { TemplateContent = "<div>broken" })
            .Where(item => item.Id == 8001)
            .ExecuteCommandAsync();

        var failed = await generator.GenerateAsync(pageId);
        Assert.False(failed.Success);
        Assert.Contains("未闭合", failed.Error ?? string.Empty);
        var page = await _service.GetByIdAsync(pageId);
        Assert.Equal(PageSyncStatus.GenerationFailed, page!.SyncStatus);
        Assert.Equal(previousVue, await File.ReadAllTextAsync(first.VuePath));
    }

    [Fact]
    public async Task TraditionalDataSourceOverride_replaces_only_script_target_and_tracks_schema_changes()
    {
        _db.Insertable(new ProjectDataSource
        {
            Id = 9101,
            TenantId = 0,
            ProjectId = 1,
            SourceKey = "orderSummary",
            DisplayName = "Order summary",
            SourceType = PageDataSourceType.EntityService,
            EntityId = 3,
            ServiceName = "OrderService",
            MethodName = "GetByIdAsync",
            DtoName = "OrderDto",
            DataShape = PageDataShape.Single,
            SchemaHash = "schema-a",
            ParamsJson = "{}",
            CreateBy = "test",
            CreateTime = DateTime.UtcNow,
            UpdateUserId = 0
        }).ExecuteCommand();
        _db.Insertable(new PageDataSourceBinding
        {
            Id = 9102,
            TenantId = 0,
            ModuleEntityId = 3,
            PageType = "edit",
            DataSourceId = 9101,
            BindingRole = PageBindingRole.Form,
            BindingStatus = PageBindingStatus.Active,
            LastKnownSchemaHash = "schema-a",
            CreateBy = "test",
            CreateTime = DateTime.UtcNow,
            UpdateUserId = 0
        }).ExecuteCommand();

        var result = new PageGenerationResult
        {
            MainScriptJson = """{"imports":[{"path":"@/api/sales/order","mode":"namespace","names":"orderApi"}],"functions":[{"name":"getDetail","body":"const res = await orderApi.getById(id); Object.assign(form, res);"}]}""",
            MainScriptContent = "import * as orderApi from '@/api/sales/order';\nexport function useOrder() { const getDetail = async () => { const res = await orderApi.getById(id); } }"
        };
        var extension = new TraditionalPageDataSourceGenerationExtension(_db);
        await extension.ProcessPageAsync(new CodeGenerationPageContext { EntityId = 3, PageType = "edit", Result = result });

        Assert.Contains("orderSummaryApi.getById", result.MainScriptContent);
        Assert.Contains("import * as orderSummaryApi from '@/api/sales/order';", result.MainScriptContent);
        Assert.Contains("orderSummaryApi.getById", result.MainScriptJson);
        Assert.Contains("orderSummaryApi", result.MainScriptJson);

        await _db.Updateable<ProjectDataSource>()
            .SetColumns(item => new ProjectDataSource { SchemaHash = "schema-b", UpdateTime = DateTime.UtcNow })
            .Where(item => item.Id == 9101)
            .ExecuteCommandAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            extension.ProcessPageAsync(new CodeGenerationPageContext
            {
                EntityId = 3,
                PageType = "edit",
                Result = new PageGenerationResult
                {
                    MainScriptJson = result.MainScriptJson,
                    MainScriptContent = result.MainScriptContent
                }
            }));
        var binding = await _db.Queryable<PageDataSourceBinding>().Where(item => item.Id == 9102).FirstAsync();
        Assert.Equal(PageBindingStatus.NeedsReview, binding!.BindingStatus);

        var changes = new ComponentTreeChangeSet();
        await extension.ContributeIncrementalChangesAsync(3, DateTime.UtcNow.AddMinutes(-1), changes);
        Assert.Contains(9101, changes.ChangedDataSourceIds);
    }

    [Fact]
    public async Task TraditionalDataSourceOverride_is_a_byte_preserving_no_op_without_binding()
    {
        var originalJson = """{"imports":[{"path":"@/api/sales/order","mode":"namespace","names":"orderApi"}],"functions":[{"name":"getList","body":"return orderApi.getPagedList(query);"}]}""";
        var originalContent = "import * as orderApi from '@/api/sales/order';\nexport const getList = () => orderApi.getPagedList(query);";
        var result = new PageGenerationResult
        {
            MainScriptJson = originalJson,
            MainScriptContent = originalContent
        };

        var extension = new TraditionalPageDataSourceGenerationExtension(_db);
        await extension.ProcessPageAsync(new CodeGenerationPageContext
        {
            EntityId = 3,
            PageType = "index",
            Result = result
        });

        Assert.Equal(originalJson, result.MainScriptJson);
        Assert.Equal(originalContent, result.MainScriptContent);
    }

    [Fact]
    public async Task Agent_plan_validation_rejects_incompatible_bindings_and_returns_container_suggestions()
    {
        var planning = CreatePlanningService();
        var plan = new ComplexPageAgentPlanDto
        {
            Summary = "Show paged orders in a detail container",
            Definition = new CreateProjectPageDto
            {
                ProjectId = 1,
                ModuleId = 2,
                PageKey = "invalid-agent-page",
                PageName = "Invalid Agent Page",
                RoutePath = "/sales/invalid-agent-page",
                LayoutJson = """{"version":1,"root":{"type":"page","componentId":"page_root","children":[{"type":"description","componentId":"order_detail","children":[]}]}}""",
                DataSources =
                {
                    new CreatePageDataSourceDto
                    {
                        SourceKey = "orders",
                        EntityId = 3,
                        ServiceName = "OrderService",
                        MethodName = "GetPagedListAsync",
                        DtoName = "OrderDto",
                        DataShape = PageDataShape.Paged
                    }
                },
                Bindings =
                {
                    new CreatePageComponentBindingDto
                    {
                        ComponentId = "order_detail",
                        BindingId = "order_detail",
                        DataSourceKey = "orders",
                        FieldId = 4,
                        BindingRole = PageBindingRole.Detail
                    }
                }
            },
            Decisions =
            {
                new ComplexPageAgentDecisionDto { Target = "dataSource:orders", Reason = "The page needs paging for the order collection." }
            }
        };

        var validation = await planning.ValidateAsync(1, plan);

        Assert.False(validation.IsValid);
        Assert.Contains(validation.Errors, item => item.Contains("incompatible with data shape", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(validation.Suggestions, item => item.Contains("table", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Agent_plan_requires_reasons_swagger_evidence_and_never_accepts_raw_vue()
    {
        var planning = CreatePlanningService();
        var plan = new ComplexPageAgentPlanDto
        {
            Summary = "Unsafe plan",
            Definition = new CreateProjectPageDto
            {
                ProjectId = 1,
                ModuleId = 2,
                PageKey = "unsafe-agent-page",
                PageName = "Unsafe Agent Page",
                RoutePath = "/sales/unsafe-agent-page",
                LayoutJson = "<template><div>raw vue</div></template>",
                DataSources =
                {
                    new CreatePageDataSourceDto
                    {
                        SourceKey = "orders",
                        EntityId = 3,
                        ServiceName = "UnknownService",
                        MethodName = "ArbitraryMethodAsync",
                        DtoName = "OrderSummaryDto",
                        DataShape = PageDataShape.Paged
                    }
                }
            },
            BackendChanges =
            {
                new ComplexPageAgentBackendChangeDto
                {
                    Key = "OrderSummaryDto",
                    ChangeType = "CreateAggregateDto",
                    Description = "Combine order totals and customer fields",
                    RequiresSwaggerValidation = true
                }
            }
        };

        var validation = await planning.ValidateAsync(1, plan);

        Assert.False(validation.IsValid);
        Assert.Contains(validation.Errors, item => item.Contains("data-shape/DTO decision reason", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(validation.Errors, item => item.Contains("Swagger verification", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(validation.Errors, item => item.Contains("Raw Vue source", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(typeof(ComplexPageAgentPlanDto).GetProperties(), property =>
            property.Name.Contains("Vue", StringComparison.OrdinalIgnoreCase) ||
            property.Name.Contains("FilePath", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Agent_multi_source_workbench_plan_applies_then_generates_interactive_bundle()
    {
        var planning = CreatePlanningService();
        var plan = new ComplexPageAgentPlanDto
        {
            Summary = "Create an order list with selected-order detail",
            GenerateAfterApproval = true,
            BuildAfterGeneration = true,
            Definition = new CreateProjectPageDto
            {
                ProjectId = 1,
                ModuleId = 2,
                PageKey = "agent-order-workbench",
                PageName = "Agent Order Workbench",
                RoutePath = "/sales/agent-order-workbench",
                LayoutJson = """{"version":1,"root":{"type":"page","componentId":"page_root","children":[{"type":"table","componentId":"orders_table","children":[{"type":"table-column","componentId":"customer_column","children":[]}]},{"type":"detail","componentId":"order_detail","children":[]}]}}""",
                DataSources =
                {
                    new CreatePageDataSourceDto
                    {
                        SourceKey = "orders",
                        EntityId = 3,
                        ServiceName = "OrderService",
                        MethodName = "GetPagedListAsync",
                        DtoName = "OrderDto",
                        DataShape = PageDataShape.Paged
                    },
                    new CreatePageDataSourceDto
                    {
                        SourceKey = "selectedOrder",
                        EntityId = 3,
                        ServiceName = "OrderService",
                        MethodName = "GetByIdAsync",
                        DtoName = "OrderDto",
                        DataShape = PageDataShape.Single,
                        ParamsJson = "{\"id\":{\"$ref\":\"$route.id\"}}"
                    }
                },
                Bindings =
                {
                    new CreatePageComponentBindingDto { ComponentId = "orders_table", BindingId = "orders_table", DataSourceKey = "orders", BindingRole = PageBindingRole.Table },
                    new CreatePageComponentBindingDto { ComponentId = "customer_column", BindingId = "customer_column", DataSourceKey = "orders", FieldId = 4, BindingRole = PageBindingRole.Column },
                    new CreatePageComponentBindingDto { ComponentId = "order_detail", BindingId = "order_detail", DataSourceKey = "selectedOrder", FieldId = 4, BindingRole = PageBindingRole.Detail }
                }
            },
            Decisions =
            {
                new ComplexPageAgentDecisionDto { Target = "dataSource:orders", Reason = "The table displays a pageable order collection." },
                new ComplexPageAgentDecisionDto { Target = "dataSource:selectedOrder", Reason = "The detail area consumes one order selected by route id." }
            }
        };

        var validation = await planning.ValidateAsync(1, plan);
        Assert.True(validation.IsValid, string.Join(Environment.NewLine, validation.Errors));
        var applied = await planning.ApplyAsync(1, plan);
        var generated = await new ComplexPageGenerationService(_db, _service).GenerateAsync(applied.PageId);

        Assert.True(applied.Created);
        Assert.True(generated.Success, generated.Error);
        var autoScript = await File.ReadAllTextAsync(Path.Combine(generated.OutputDirectory, "agent-order-workbench.auto.js"));
        var vue = await File.ReadAllTextAsync(generated.VuePath);
        Assert.Contains("orders", autoScript);
        Assert.Contains("selectedOrder", autoScript);
        Assert.Contains("reloadComplexPageDataSource", autoScript);
        Assert.Contains("el-table", vue);
        Assert.Contains("el-descriptions", vue);
    }

    private ComplexPageAgentPlanningService CreatePlanningService() =>
        new(_db, _service, new PageDisplayTemplateService(_db));

    public void Dispose()
    {
        _db.Dispose();
    }
}
