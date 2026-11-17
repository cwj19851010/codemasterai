using CodeMaster.Application.Dtos.CodeGen;
using CodeMaster.Application.Services.CodeGen;
using CodeMaster.Core.Enums;
using CodeMaster.Domain.Entities.CodeGen;
using CodeMaster.Infrastructure.Persistence.SqlSugar;
using CodeMaster.UiComposition.Entities;
using SqlSugar;
using Xunit;

namespace CodeMaster.UiComposition.Tests;

public sealed class ProjectViewBuilderServiceTests : IDisposable
{
    private readonly string _dbPath;
    private readonly ISqlSugarClient _db;
    private readonly ProjectViewBuilderService _service;

    public ProjectViewBuilderServiceTests()
    {
        _dbPath = Path.Combine(CodeMasterTestPaths.Root, $"codemaster_view_builder_{Guid.NewGuid():N}.db");
        _db = new SqlSugarClient(new ConnectionConfig
        {
            ConnectionString = $"Data Source={_dbPath};Mode=ReadWriteCreate;Pooling=False;",
            DbType = DbType.Sqlite,
            IsAutoCloseConnection = true,
            InitKeyType = InitKeyType.Attribute,
            ConfigureExternalServices = SqlSugarSetup.GetConfigureExternalServices(DbType.Sqlite)
        });
        _db.CodeFirst.InitTables(typeof(Project), typeof(ProjectModule), typeof(ModuleEntity), typeof(EntityField), typeof(EntityRelation), typeof(OneToManyRelation), typeof(ProjectDataSource), typeof(ProjectViewDefinition), typeof(ProjectViewDependency), typeof(ProjectViewFieldLineage));
        Seed();
        _service = new ProjectViewBuilderService(_db);
    }

    public void Dispose()
    {
        _db.Ado.Close();
        _db.Dispose();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }

    [Fact]
    public async Task Catalog_returns_entities_fields_and_inferred_join_candidates()
    {
        var catalog = await _service.GetEntityCatalogAsync(1);

        Assert.Equal(DatabaseType.SQLite, catalog.DatabaseType);
        Assert.Equal(new[] { 10L, 20L }, catalog.Entities.Select(item => item.EntityId));
        Assert.Contains(catalog.Entities.Single(item => item.EntityId == 10).Fields,
            field => field.Name == "CustomerId" && field.ColumnName == "customer_id");
        var join = Assert.Single(catalog.JoinCandidates);
        Assert.Equal(10, join.SourceEntityId);
        Assert.Equal(20, join.TargetEntityId);
        Assert.Equal("CustomerId", join.SourceField);
        Assert.Equal("Id", join.TargetField);
        Assert.True(join.IsInferred);
    }

    [Fact]
    public async Task Catalog_and_preview_exclude_metadata_only_api_models()
    {
        var catalog = await _service.GetEntityCatalogAsync(1);

        Assert.DoesNotContain(catalog.Entities, item => item.EntityId == 40);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _service.PreviewSqlAsync(new PreviewViewSqlDto
            {
                ProjectId = 1,
                Entities = new List<ViewBuilderEntitySelectionDto>
                {
                    new() { EntityId = 40, FieldIds = new List<long> { 41 } }
                }
            }));
        Assert.Contains("do not belong", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Preview_builds_dialect_aware_sql_from_selected_fields_and_inferred_join()
    {
        var preview = await _service.PreviewSqlAsync(new PreviewViewSqlDto
        {
            ProjectId = 1,
            Entities = new List<ViewBuilderEntitySelectionDto>
            {
                new() { EntityId = 10, FieldIds = new List<long> { 11, 13 } },
                new() { EntityId = 20, FieldIds = new List<long> { 21, 22 } }
            }
        });

        Assert.Equal(4, preview.SelectedFields.Count);
        Assert.Contains("sales_order", preview.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("customer", preview.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Left JOIN", preview.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("customer_id", preview.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Order_OrderNo", preview.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.True(preview.Joins.Single().IsInferred);

        var rows = _db.Ado.GetDataTable(preview.Sql);
        Assert.Equal(1, rows.Rows.Count);
    }

    [Fact]
    public async Task Preview_accepts_manual_inner_join_and_uses_cross_join_for_unconnected_entities()
    {
        var manual = await _service.PreviewSqlAsync(new PreviewViewSqlDto
        {
            ProjectId = 1,
            IncludeInferredJoins = false,
            Entities = new List<ViewBuilderEntitySelectionDto>
            {
                new() { EntityId = 10, FieldIds = new List<long> { 12 } },
                new() { EntityId = 20, FieldIds = new List<long> { 22 } }
            },
            Joins = new List<ViewBuilderJoinDto>
            {
                new()
                {
                    SourceEntityId = 10,
                    TargetEntityId = 20,
                    SourceField = "CustomerId",
                    TargetField = "Id",
                    JoinType = "Inner"
                }
            }
        });
        Assert.Contains("Inner JOIN", manual.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.False(manual.Joins.Single().IsInferred);

        var disconnected = await _service.PreviewSqlAsync(new PreviewViewSqlDto
        {
            ProjectId = 1,
            IncludeInferredJoins = false,
            Entities = new List<ViewBuilderEntitySelectionDto>
            {
                new() { EntityId = 10, FieldIds = new List<long> { 12 } },
                new() { EntityId = 20, FieldIds = new List<long> { 22 } }
            }
        });
        Assert.Contains("CROSS JOIN", disconnected.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(disconnected.Joins);
        Assert.Equal(1, _db.Ado.GetDataTable(disconnected.Sql).Rows.Count);
    }

    [Fact]
    public async Task Preview_accepts_fully_manual_sql_without_entity_selection_and_preserves_text()
    {
        const string sql = "WITH recent AS (SELECT id, name FROM customer WHERE name LIKE '%select%')\nSELECT id, name FROM recent;";

        var preview = await _service.PreviewSqlAsync(new PreviewViewSqlDto
        {
            ProjectId = 1,
            Sql = sql
        });

        // 预览返回的是「实际会执行」的 SQL：FROM <实体名> 会被补成
        // FROM <物理表> AS <实体别名>（列映射需要这个别名）。这是无害改写，
        // 除此之外 CTE、投影、条件必须逐字保留。
        var withoutInjectedAlias = preview.Sql.Replace(
            "FROM customer AS customer", "FROM customer", StringComparison.OrdinalIgnoreCase);
        Assert.Equal(sql, withoutInjectedAlias);
        Assert.Empty(preview.SelectedFields);
        Assert.Empty(preview.Joins);
    }

    [Fact]
    public async Task Preview_rejects_manual_write_or_multiple_statements()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _service.PreviewSqlAsync(new PreviewViewSqlDto
        {
            ProjectId = 1,
            Sql = "UPDATE customer SET name = 'changed'"
        }));

        await Assert.ThrowsAsync<ArgumentException>(() => _service.PreviewSqlAsync(new PreviewViewSqlDto
        {
            ProjectId = 1,
            Sql = "SELECT id FROM customer; DROP TABLE customer;"
        }));
    }

    [Fact]
    public async Task CreateView_replaces_sqlite_view_and_reports_actual_column_difference()
    {
        var first = await _service.CreateViewAsync(new CreateViewDefinitionDto
        {
            ProjectId = 1,
            ViewName = "v_order_customer",
            Sql = "SELECT id AS order_id, name AS customer_name FROM customer",
            ExpectedColumns = new List<string> { "order_id", "missing_column" }
        });

        Assert.False(first.ReplacedExistingView);
        Assert.Equal(new[] { "customer_name", "order_id" }, first.Columns.Select(item => item.Name));
        Assert.True(first.RequiresColumnConfirmation);
        Assert.Contains("missing_column", first.MissingColumns);
        Assert.Contains("customer_name", first.ExtraColumns);

        var replacement = await _service.CreateViewAsync(new CreateViewDefinitionDto
        {
            ProjectId = 1,
            ViewName = "v_order_customer",
            Sql = "SELECT id AS order_id FROM customer",
            ExpectedColumns = new List<string> { "order_id" }
        });

        Assert.True(replacement.ReplacedExistingView);
        Assert.False(replacement.RequiresColumnConfirmation);
        Assert.Single(replacement.Columns);
    }

    [Fact]
    public async Task CreateView_rejects_unsafe_name_and_existing_table_conflict()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _service.CreateViewAsync(new CreateViewDefinitionDto
        {
            ProjectId = 1,
            ViewName = "v_order_customer;drop",
            Sql = "SELECT id FROM customer"
        }));

        _db.Ado.ExecuteCommand("CREATE TABLE conflicting_object (id INTEGER)");
        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.CreateViewAsync(new CreateViewDefinitionDto
        {
            ProjectId = 1,
            ViewName = "conflicting_object",
            Sql = "SELECT id FROM customer"
        }));
    }

    [Fact]
    public async Task RegisterReadOnlyViewEntity_maps_real_columns_and_selected_key_to_Id()
    {
        var view = await _service.CreateViewAsync(new CreateViewDefinitionDto
        {
            ProjectId = 1,
            ViewName = "v_order_customer",
            Sql = "SELECT id AS order_id, name AS customer_name FROM customer"
        });
        var registered = await _service.RegisterReadOnlyViewEntityAsync(new RegisterReadOnlyViewEntityDto
        {
            ProjectId = 1,
            ModuleId = 2,
            ViewName = view.ViewName,
            EntityName = "OrderCustomerView",
            DisplayName = "Order customer view",
            KeyColumn = "order_id"
        });

        Assert.True(registered.EntityId > 0);
        Assert.True(registered.DataSourceId > 0);
        Assert.Equal("order_id", registered.KeyColumn);
        Assert.True(registered.GenerateFrontend);

        var entity = await _db.Queryable<ModuleEntity>().Where(item => item.Id == registered.EntityId).FirstAsync();
        Assert.True(entity.IsReadOnly);
        Assert.True(entity.HasPrimaryKey);
        Assert.Equal("v_order_customer", entity.TableName);
        var fields = await _db.Queryable<EntityField>().Where(item => item.ModuleEntityId == entity.Id).OrderBy(item => item.OrderNum).ToListAsync();
        Assert.Equal("Id", fields[0].Name);
        Assert.Equal("order_id", fields[0].ColumnName);
        Assert.True(fields[0].IsPrimaryKey);
        Assert.Contains(fields, item => item.Name == "CustomerName" && item.ColumnName == "customer_name");
        var source = await _db.Queryable<ProjectDataSource>().Where(item => item.Id == registered.DataSourceId).FirstAsync();
        Assert.Equal(PageDataSourceType.EntityService, source.SourceType);
        Assert.Equal(registered.EntityId, source.EntityId);
        Assert.Equal("GetPagedListAsync", source.MethodName);
    }

    [Fact]
    public async Task RegisterReadOnlyViewEntity_trusts_declared_field_mapping_and_reports_column_differences_as_warnings()
    {
        var view = await _service.CreateViewAsync(new CreateViewDefinitionDto
        {
            ProjectId = 1,
            ViewName = "v_declared_order_customer",
            Sql = "SELECT id AS order_id, name AS customer_name FROM customer",
            ExpectedColumns = new List<string> { "order_id", "declared_customer_name" }
        });

        Assert.True(view.RequiresColumnConfirmation);
        Assert.NotEmpty(view.Warnings);

        var registered = await _service.RegisterReadOnlyViewEntityAsync(new RegisterReadOnlyViewEntityDto
        {
            ProjectId = 1,
            ModuleId = 2,
            ViewName = view.ViewName,
            EntityName = "DeclaredOrderCustomerView",
            DisplayName = "Declared order customer view",
            KeyColumn = "order_id",
            Fields = new List<ViewEntityFieldDefinitionDto>
            {
                new() { Name = "OrderKey", ColumnName = "order_id", DataType = "int", DisplayName = "Order key" },
                new() { Name = "CustomerDisplay", ColumnName = "declared_customer_name", DataType = "string?", IsNullable = true }
            }
        });

        var fields = await _db.Queryable<EntityField>()
            .Where(item => item.ModuleEntityId == registered.EntityId)
            .OrderBy(item => item.OrderNum)
            .ToListAsync();
        Assert.Collection(fields,
            key =>
            {
                Assert.Equal("Id", key.Name);
                Assert.Equal("order_id", key.ColumnName);
                Assert.Equal("long", key.DataType);
                Assert.True(key.IsPrimaryKey);
            },
            value =>
            {
                Assert.Equal("CustomerDisplay", value.Name);
                Assert.Equal("declared_customer_name", value.ColumnName);
                Assert.Equal("string?", value.DataType);
            });
    }

    [Fact]
    public async Task Definition_only_view_can_register_declared_consumer_before_physical_deployment()
    {
        var definitions = new ProjectViewDefinitionService(_db, _service);
        var saved = await definitions.SaveAsync(new SaveProjectViewDefinitionDto
        {
            ProjectId = 1,
            DefinitionKey = "prepared_customer_view",
            ViewName = "prepared_customer_view",
            Sql = "SELECT id AS customer_id, name AS customer_name FROM customer",
            KeyColumn = "customer_id",
            AllowReplace = true,
            Fields =
            [
                new() { OutputName = "CustomerId", OutputColumn = "customer_id", DataType = "long", LineageKind = ProjectViewLineageKind.Computed },
                new() { OutputName = "CustomerName", OutputColumn = "customer_name", DataType = "string", LineageKind = ProjectViewLineageKind.Computed }
            ]
        });

        Assert.Equal(ProjectViewDeploymentState.DefinitionOnly, saved.DeploymentState);
        Assert.Empty(await _service.GetExistingViewsAsync(1));

        var registered = await _service.RegisterReadOnlyViewEntityAsync(new RegisterReadOnlyViewEntityDto
        {
            ViewDefinitionId = saved.Id,
            AllowUnappliedDefinition = true,
            ProjectId = 1,
            ModuleId = 2,
            ViewName = saved.ViewName,
            EntityName = "PreparedCustomerView",
            DisplayName = "Prepared customer view",
            KeyColumn = "customer_id",
            DataSourceType = PageDataSourceType.View,
            Fields =
            [
                new() { Name = "CustomerId", ColumnName = "customer_id", DataType = "long" },
                new() { Name = "CustomerName", ColumnName = "customer_name", DataType = "string" }
            ]
        });

        Assert.True(registered.EntityId > 0);
        var linked = await _db.Queryable<ProjectViewDefinition>().Where(item => item.Id == saved.Id).FirstAsync();
        Assert.Equal(registered.EntityId, linked.ModuleEntityId);
        Assert.Equal(registered.DataSourceId, linked.ProjectDataSourceId);
        Assert.DoesNotContain(await _service.GetExistingViewsAsync(1), item => item.ViewName == saved.ViewName);
    }

    [Fact]
    public async Task RegisterReadOnlyViewEntity_rejects_duplicate_declared_output_aliases()
    {
        var view = await _service.CreateViewAsync(new CreateViewDefinitionDto
        {
            ProjectId = 1,
            ViewName = "v_duplicate_alias",
            Sql = "SELECT id AS order_id, name AS customer_name FROM customer"
        });

        var error = await Assert.ThrowsAsync<ArgumentException>(() =>
            _service.RegisterReadOnlyViewEntityAsync(new RegisterReadOnlyViewEntityDto
            {
                ProjectId = 1,
                ModuleId = 2,
                ViewName = view.ViewName,
                EntityName = "DuplicateAliasView",
                KeyColumn = "order_id",
                Fields = new List<ViewEntityFieldDefinitionDto>
                {
                    new() { Name = "OrderKey", ColumnName = "order_id", DataType = "long" },
                    new() { Name = "DuplicateKey", ColumnName = "order_id", DataType = "string" }
                }
            }));

        Assert.Contains("distinct SQL AS aliases", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RegisterReadOnlyViewEntity_restores_soft_deleted_consumer_on_approval_retry()
    {
        var view = await _service.CreateViewAsync(new CreateViewDefinitionDto
        {
            ProjectId = 1,
            ViewName = "v_restored_customer",
            Sql = "SELECT id AS customer_id, name AS customer_name FROM customer"
        });
        var deletedEntity = new ModuleEntity
        {
            Id = 50,
            TenantId = 7,
            ProjectId = 1,
            ModuleId = 2,
            Name = "RestoredCustomerView",
            Description = "Old view",
            IsReadOnly = true,
            IsDeleted = true,
            DeleteBy = "test",
            DeleteTime = DateTime.UtcNow,
            CreateBy = "test",
            CreateUserId = 0,
            UpdateUserId = 0,
            CreateTime = DateTime.UtcNow
        };
        _db.Insertable(deletedEntity).ExecuteCommand();

        var definition = new CodeMaster.UiComposition.Entities.ProjectViewDefinition
        {
            Id = 60,
            TenantId = 7,
            ProjectId = 1,
            DefinitionKey = "v_restored_customer",
            ViewName = view.ViewName,
            DatabaseType = DatabaseType.SQLite,
            Sql = "SELECT id AS customer_id, name AS customer_name FROM customer",
            DefinitionHash = "definition-hash",
            DeclaredSchemaJson = "[]",
            DeclaredSchemaHash = "schema-hash",
            AppliedDefinitionHash = "definition-hash",
            DeploymentState = CodeMaster.UiComposition.Entities.ProjectViewDeploymentState.Applied,
            CreateBy = "test",
            CreateUserId = 0,
            UpdateUserId = 0,
            CreateTime = DateTime.UtcNow
        };
        _db.CodeFirst.InitTables(typeof(CodeMaster.UiComposition.Entities.ProjectViewDefinition));
        _db.Insertable(definition).ExecuteCommand();

        var registered = await _service.RegisterReadOnlyViewEntityAsync(new RegisterReadOnlyViewEntityDto
        {
            ViewDefinitionId = definition.Id,
            ProjectId = 1,
            ModuleId = 2,
            ViewName = view.ViewName,
            EntityName = deletedEntity.Name,
            DisplayName = "Restored customer view",
            KeyColumn = "customer_id",
            DataSourceType = PageDataSourceType.View
        });

        Assert.Equal(deletedEntity.Id, registered.EntityId);
        var restored = await _db.Queryable<ModuleEntity>().Where(item => item.Id == deletedEntity.Id).FirstAsync();
        Assert.False(restored.IsDeleted);
        Assert.Equal(view.ViewName, restored.TableName);
        Assert.Equal(new[] { "Id", "CustomerName" }, (await _db.Queryable<EntityField>()
            .Where(item => item.ModuleEntityId == deletedEntity.Id && !item.IsDeleted)
            .OrderBy(item => item.OrderNum)
            .ToListAsync()).Select(item => item.Name));
        var source = await _db.Queryable<ProjectDataSource>().Where(item => item.Id == registered.DataSourceId).FirstAsync();
        Assert.Equal(PageDataSourceType.View, source.SourceType);
        Assert.Equal(deletedEntity.Id, source.EntityId);

        var retry = await _service.RegisterReadOnlyViewEntityAsync(new RegisterReadOnlyViewEntityDto
        {
            ViewDefinitionId = definition.Id,
            ProjectId = 1,
            ModuleId = 2,
            ViewName = view.ViewName,
            EntityName = deletedEntity.Name,
            DisplayName = "Restored customer view",
            KeyColumn = "customer_id",
            DataSourceType = PageDataSourceType.View
        });
        Assert.Equal(registered.EntityId, retry.EntityId);
        Assert.Equal(registered.DataSourceId, retry.DataSourceId);
    }

    [Fact]
    public async Task RegisterReadOnlyViewEntity_adopts_exact_existing_read_only_entity_and_creates_view_source()
    {
        var view = await _service.CreateViewAsync(new CreateViewDefinitionDto
        {
            ProjectId = 1,
            ViewName = "v_adopted_customer",
            Sql = "SELECT id AS customer_id, name AS customer_name FROM customer"
        });
        var existing = new ModuleEntity
        {
            Id = 70,
            TenantId = 7,
            ProjectId = 1,
            ModuleId = 2,
            Name = "AdoptedCustomerView",
            Description = "Old report",
            TableName = view.ViewName,
            HasPrimaryKey = true,
            IsReadOnly = true,
            GenerateFrontend = true,
            CreateBy = "test",
            CreateUserId = 0,
            UpdateUserId = 0,
            CreateTime = DateTime.UtcNow
        };
        _db.Insertable(existing).ExecuteCommand();
        _db.Insertable(new EntityField
        {
            Id = 71,
            TenantId = 7,
            ModuleEntityId = existing.Id,
            Name = "LegacyField",
            Description = "Legacy",
            DataType = "string",
            CreateBy = "test",
            CreateUserId = 0,
            UpdateUserId = 0,
            CreateTime = DateTime.UtcNow
        }).ExecuteCommand();
        _db.CodeFirst.InitTables(typeof(CodeMaster.UiComposition.Entities.ProjectViewDefinition));
        var definition = new CodeMaster.UiComposition.Entities.ProjectViewDefinition
        {
            Id = 72,
            TenantId = 7,
            ProjectId = 1,
            DefinitionKey = view.ViewName,
            ViewName = view.ViewName,
            DatabaseType = DatabaseType.SQLite,
            Sql = "SELECT id AS customer_id, name AS customer_name FROM customer",
            DefinitionHash = "adopt-definition",
            DeclaredSchemaJson = "[]",
            DeclaredSchemaHash = "adopt-schema",
            AppliedDefinitionHash = "adopt-definition",
            DeploymentState = CodeMaster.UiComposition.Entities.ProjectViewDeploymentState.Applied,
            CreateBy = "test",
            CreateUserId = 0,
            UpdateUserId = 0,
            CreateTime = DateTime.UtcNow
        };
        _db.Insertable(definition).ExecuteCommand();

        var registered = await _service.RegisterReadOnlyViewEntityAsync(new RegisterReadOnlyViewEntityDto
        {
            ViewDefinitionId = definition.Id,
            ExistingEntityId = existing.Id,
            ProjectId = 1,
            ModuleId = 2,
            ViewName = view.ViewName,
            EntityName = existing.Name,
            DisplayName = "Adopted customer view",
            KeyColumn = "customer_id",
            DataSourceType = PageDataSourceType.View
        });

        Assert.Equal(existing.Id, registered.EntityId);
        Assert.True(registered.DataSourceId > 0);
        var source = await _db.Queryable<ProjectDataSource>().Where(item => item.Id == registered.DataSourceId).FirstAsync();
        Assert.Equal(PageDataSourceType.View, source.SourceType);
        Assert.Equal(existing.Id, source.EntityId);
        var fields = await _db.Queryable<EntityField>()
            .Where(item => item.ModuleEntityId == existing.Id && !item.IsDeleted)
            .OrderBy(item => item.OrderNum)
            .ToListAsync();
        Assert.Equal(new[] { "Id", "CustomerName" }, fields.Select(item => item.Name));
        var linked = await _db.Queryable<CodeMaster.UiComposition.Entities.ProjectViewDefinition>()
            .Where(item => item.Id == definition.Id)
            .FirstAsync();
        Assert.Equal(existing.Id, linked.ModuleEntityId);
        Assert.Equal(registered.DataSourceId, linked.ProjectDataSourceId);
    }

    [Fact]
    public async Task ImportExistingView_reads_columns_without_replacing_definition_and_registers_view_source()
    {
        _db.Ado.ExecuteCommand("CREATE VIEW existing_customer_view AS SELECT id AS customer_id, name AS customer_name FROM customer");

        var views = await _service.GetExistingViewsAsync(1);
        Assert.Contains(views, item => item.ViewName == "existing_customer_view");

        var imported = await _service.ImportExistingViewAsync(new RegisterReadOnlyViewEntityDto
        {
            ProjectId = 1,
            ModuleId = 2,
            ViewName = "existing_customer_view",
            EntityName = "ExistingCustomerView",
            DisplayName = "Existing customer view",
            KeyColumn = "customer_id"
        });

        Assert.True(imported.EntityId > 0);
        Assert.True(imported.DataSourceId > 0);
        var source = await _db.Queryable<ProjectDataSource>().Where(item => item.Id == imported.DataSourceId).FirstAsync();
        Assert.Equal(PageDataSourceType.View, source.SourceType);
        Assert.Equal("existing_customer_view", source.ViewName);
        Assert.Contains((await _service.GetExistingViewsAsync(1)), item => item.ViewName == "existing_customer_view");
    }

    [Fact]
    public void CreateView_dispatches_database_specific_replace_and_identifier_rules()
    {
        var method = typeof(ProjectViewBuilderService).GetMethod(
            "BuildCreateViewStatements",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);

        static IReadOnlyList<string> Build(System.Reflection.MethodInfo method, DatabaseType databaseType, bool existing)
            => (IReadOnlyList<string>)method.Invoke(null, new object[] { databaseType, "demo_view", "SELECT 1", existing })!;

        Assert.Equal(
            new[] { "DROP VIEW IF EXISTS [demo_view]", "CREATE VIEW [demo_view] AS SELECT 1" },
            Build(method!, DatabaseType.SqlServer, true));
        Assert.Equal(
            new[] { "CREATE VIEW \"demo_view\" AS SELECT 1" },
            Build(method!, DatabaseType.SQLite, false));
        Assert.Equal(
            new[] { "CREATE OR REPLACE VIEW `demo_view` AS SELECT 1" },
            Build(method!, DatabaseType.MySQL, true));
        Assert.Equal(
            new[] { "DROP VIEW IF EXISTS \"demo_view\"", "CREATE VIEW \"demo_view\" AS SELECT 1" },
            Build(method!, DatabaseType.PostgreSQL, true));
        Assert.Equal(
            new[] { "CREATE OR REPLACE VIEW \"demo_view\" AS SELECT 1" },
            Build(method!, DatabaseType.Oracle, true));
    }

    private void Seed()
    {
        var now = DateTime.UtcNow;
        _db.Ado.ExecuteCommand("CREATE TABLE customer (id INTEGER PRIMARY KEY, name TEXT)");
        _db.Ado.ExecuteCommand("INSERT INTO customer (id, name) VALUES (1, 'Alice')");
        _db.Ado.ExecuteCommand("CREATE TABLE sales_order (id INTEGER PRIMARY KEY, customer_id INTEGER, order_no TEXT)");
        _db.Ado.ExecuteCommand("INSERT INTO sales_order (id, customer_id, order_no) VALUES (100, 1, 'SO-100')");
        _db.Insertable(new Project
        {
            Id = 1,
            TenantId = 7,
            ProjectName = "ViewBuilderTarget",
            DisplayName = "View builder target",
            DatabaseType = DatabaseType.SQLite,
            ConnectionString = $"Data Source={_dbPath};Mode=ReadWriteCreate;",
            ProjectPath = CodeMasterTestPaths.Root,
            CreateBy = "test",
            CreateUserId = 0,
            UpdateUserId = 0,
            CreateTime = now
        }).ExecuteCommand();
        _db.Insertable(new ProjectModule
        {
            Id = 2,
            TenantId = 7,
            ProjectId = 1,
            ModuleName = "Sales",
            ModuleDescription = "Sales",
            Icon = string.Empty,
            RoutePath = string.Empty,
            Remark = string.Empty,
            OrderNum = 1,
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
            HasPrimaryKey = true,
            CreateBy = "test",
            CreateUserId = 0,
            UpdateUserId = 0,
            CreateTime = now
        }).ExecuteCommand();
        _db.Insertable(new ModuleEntity
        {
            Id = 20,
            TenantId = 7,
            ProjectId = 1,
            ModuleId = 2,
            Name = "Customer",
            Description = "Customer",
            TableName = "customer",
            HasPrimaryKey = true,
            CreateBy = "test",
            CreateUserId = 0,
            UpdateUserId = 0,
            CreateTime = now
        }).ExecuteCommand();
        _db.Insertable(new ModuleEntity
        {
            Id = 40,
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
        InsertField(11, 10, "Id", "Order id", "long", true, true, now);
        InsertField(12, 10, "CustomerId", "Customer id", "long", false, false, now);
        InsertField(13, 10, "OrderNo", "Order number", "string", false, false, now);
        InsertField(21, 20, "Id", "Customer id", "long", true, true, now);
        InsertField(22, 20, "Name", "Customer name", "string", false, false, now);
        InsertField(41, 40, "Status", "Status", "string", false, false, now);
        _db.Insertable(new EntityRelation
        {
            Id = 30,
            TenantId = 7,
            SourceEntityId = 10,
            TargetEntityId = 20,
            RelationName = "Customer",
            SourceField = "CustomerId",
            TargetField = "Id",
            Cardinality = EntityRelationCardinality.ManyToOne,
            Ownership = EntityRelationOwnership.Reference,
            IsRequired = false,
            CreateBy = "test",
            CreateUserId = 0,
            UpdateUserId = 0,
            CreateTime = now
        }).ExecuteCommand();
    }

    private void InsertField(long id, long entityId, string name, string description, string dataType,
        bool primaryKey, bool systemField, DateTime now)
    {
        _db.Insertable(new EntityField
        {
            Id = id,
            TenantId = 7,
            ModuleEntityId = entityId,
            Name = name,
            Description = description,
            DataType = dataType,
            IsPrimaryKey = primaryKey,
            IsSystemField = systemField,
            ShowInList = true,
            CreateBy = "test",
            CreateUserId = 0,
            UpdateUserId = 0,
            CreateTime = now
        }).ExecuteCommand();
    }
}
