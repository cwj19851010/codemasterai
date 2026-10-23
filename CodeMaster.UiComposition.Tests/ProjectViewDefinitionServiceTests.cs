using CodeMaster.Application.Dtos.CodeGen;
using CodeMaster.Application.Services.CodeGen;
using CodeMaster.Core.Enums;
using CodeMaster.Domain.Entities.CodeGen;
using CodeMaster.Infrastructure.Persistence.SqlSugar;
using CodeMaster.UiComposition.Entities;
using SqlSugar;

namespace CodeMaster.UiComposition.Tests;

public sealed class ProjectViewDefinitionServiceTests : IDisposable
{
    private readonly string _dbPath;
    private readonly string _projectRoot;
    private readonly ISqlSugarClient _db;
    private readonly ProjectViewDefinitionService _service;

    public ProjectViewDefinitionServiceTests()
    {
        _dbPath = Path.Combine(CodeMasterTestPaths.Root, $"codemaster_view_definition_{Guid.NewGuid():N}.db");
        _projectRoot = Path.Combine(CodeMasterTestPaths.Root, $"codemaster_view_definition_project_{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(_projectRoot, "ViewDefinitionTarget", "ViewDefinitionTarget.Migrator"));
        _db = new SqlSugarClient(new ConnectionConfig
        {
            ConnectionString = $"Data Source={_dbPath};Mode=ReadWriteCreate;Pooling=False;",
            DbType = DbType.Sqlite,
            IsAutoCloseConnection = true,
            InitKeyType = InitKeyType.Attribute,
            ConfigureExternalServices = SqlSugarSetup.GetConfigureExternalServices(DbType.Sqlite)
        });
        _db.CodeFirst.InitTables(
            typeof(Project), typeof(ProjectModule), typeof(ModuleEntity), typeof(EntityField),
            typeof(ProjectViewDefinition), typeof(ProjectViewDependency), typeof(ProjectViewFieldLineage));
        Seed();
        _service = new ProjectViewDefinitionService(_db);
    }

    [Fact]
    public async Task Save_persists_definition_sources_and_mixed_lineage_without_deploying()
    {
        var saved = await _service.SaveAsync(new SaveProjectViewDefinitionDto
        {
            ProjectId = 1,
            DefinitionKey = "orderSummary",
            ViewName = "order_summary",
            Sql = "SELECT o.order_no AS order_no, SUM(o.amount) AS total_amount, 'sales' AS source_kind FROM sales_order o GROUP BY o.order_no",
            Dependencies =
            [
                new() { DependencyKey = "orders", Kind = ProjectViewDependencyKind.ManagedEntity, PhysicalName = "sales_order", ModuleEntityId = 10 },
                new() { DependencyKey = "external", Kind = ProjectViewDependencyKind.ExternalTable, PhysicalName = "reporting_currency", SourceAlias = "currency" }
            ],
            Fields =
            [
                new() { OutputName = "OrderNo", OutputColumn = "order_no", DataType = "string", LineageKind = ProjectViewLineageKind.Direct, SourceDependencyKey = "orders", SourceEntityId = 10, SourceFieldId = 11 },
                new() { OutputName = "TotalAmount", OutputColumn = "total_amount", DataType = "decimal", LineageKind = ProjectViewLineageKind.Aggregate, Expression = "SUM(o.amount)" },
                new() { OutputName = "SourceKind", OutputColumn = "source_kind", DataType = "string", LineageKind = ProjectViewLineageKind.Constant, Expression = "'sales'" }
            ]
        });

        Assert.Equal(ProjectViewDeploymentState.DefinitionOnly, saved.DeploymentState);
        Assert.Equal("order_summary", saved.ViewName);
        Assert.Equal(2, saved.Dependencies.Count);
        Assert.Equal(ProjectViewLineageKind.Aggregate, saved.Fields.Single(item => item.OutputName == "TotalAmount").LineageKind);
        Assert.Null(saved.AppliedDefinitionHash);

        var loaded = await _service.GetByIdAsync(saved.Id);
        Assert.Equal(saved.DefinitionHash, loaded!.DefinitionHash);
        Assert.Equal(3, loaded.Fields.Count);

        var deployed = await _service.DeployAsync(new DeployProjectViewDefinitionDto
        {
            DefinitionId = saved.Id,
            ExpectedDefinitionVersion = saved.DefinitionVersion,
            ExpectedDefinitionHash = saved.DefinitionHash
        });
        Assert.False(deployed.AlreadyApplied);
        Assert.Equal(ProjectViewDeploymentState.Applied, deployed.Definition.DeploymentState);
        Assert.NotNull(deployed.Definition.ActualSchemaHash);
        var diff = await _service.GetSchemaDiffAsync(saved.Id);
        Assert.True(diff.IsInSync);
        Assert.Empty(diff.MissingColumns);
        Assert.Empty(diff.ExtraColumns);

        var repeated = await _service.DeployAsync(new DeployProjectViewDefinitionDto
        {
            DefinitionId = saved.Id,
            ExpectedDefinitionVersion = saved.DefinitionVersion,
            ExpectedDefinitionHash = saved.DefinitionHash
        });
        Assert.True(repeated.AlreadyApplied);
    }

    [Fact]
    public async Task Save_rejects_stale_definition_versions_and_allows_unknown_external_lineage()
    {
        var saved = await _service.SaveAsync(new SaveProjectViewDefinitionDto
        {
            ProjectId = 1,
            DefinitionKey = "externalView",
            ViewName = "external_view",
            Ownership = ProjectViewOwnership.ImportedExternal,
            Dependencies = [new() { DependencyKey = "legacy", Kind = ProjectViewDependencyKind.ExternalView, PhysicalName = "legacy_view" }],
            Fields = [new() { OutputName = "UnknownValue", OutputColumn = "unknown_value", DataType = "string", LineageKind = ProjectViewLineageKind.Unknown }]
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.SaveAsync(new SaveProjectViewDefinitionDto
        {
            Id = saved.Id,
            ProjectId = 1,
            ExpectedDefinitionVersion = 99,
            DefinitionKey = saved.DefinitionKey,
            ViewName = saved.ViewName,
            Ownership = ProjectViewOwnership.ImportedExternal,
            Fields = saved.Fields.Select(item => new ProjectViewFieldLineageInputDto
            {
                OutputName = item.OutputName,
                OutputColumn = item.OutputColumn,
                DataType = item.DataType,
                LineageKind = item.LineageKind
            }).ToList()
        }));
    }

    [Fact]
    public async Task Delete_rewrites_generated_module_so_migrator_drops_removed_managed_view()
    {
        var saved = await _service.SaveAsync(new SaveProjectViewDefinitionDto
        {
            ProjectId = 1,
            DefinitionKey = "deletableView",
            ViewName = "deletable_view",
            Sql = "SELECT order_no AS order_no FROM sales_order",
            Fields =
            [
                new() { OutputName = "OrderNo", OutputColumn = "order_no", DataType = "string", LineageKind = ProjectViewLineageKind.Computed, Expression = "order_no" }
            ]
        });
        await _service.DeployAsync(new DeployProjectViewDefinitionDto
        {
            DefinitionId = saved.Id,
            ExpectedDefinitionVersion = saved.DefinitionVersion,
            ExpectedDefinitionHash = saved.DefinitionHash
        });

        var generatedPath = Path.Combine(
            _projectRoot,
            "ViewDefinitionTarget",
            "ViewDefinitionTarget.Migrator",
            "Persistence",
            "EfCore",
            "GeneratedViewMigrationModule.cs");
        var before = await File.ReadAllTextAsync(generatedPath);
        Assert.Contains("ManagedViewMigrationModule", before);
        Assert.Contains("new(\"deletable_view\"", before);

        Assert.Equal(1, await _service.DeleteAsync(saved.Id));

        var after = await File.ReadAllTextAsync(generatedPath);
        Assert.Contains("ManagedViewMigrationModule", after);
        Assert.Contains("No deployed CodeMaster-managed views", after);
        Assert.DoesNotContain("new(\"deletable_view\"", after);
        Assert.Null(await _service.GetByIdAsync(saved.Id));
    }

    /// <summary>
    /// 视图 SQL 写的是实体名（Order），物理表是 sales_order。SQLite 的 CREATE VIEW 延迟解析，
    /// 不转换会建出一个"建成功、一查就 no such table"的坏视图。这里同时验证：视图可查询、
    /// 列名不被误替换、重复创建（幂等）仍然可用。
    /// </summary>
    [Fact]
    public async Task CreateView_resolves_entity_names_to_physical_tables_and_stays_queryable()
    {
        var builder = new ProjectViewBuilderService(_db);

        var first = await builder.CreateViewAsync(new CreateViewDefinitionDto
        {
            ProjectId = 1,
            ViewName = "order_physical",
            Sql = "SELECT o.order_no AS order_no, o.amount AS amount FROM Order o",
            ExpectedColumns = ["order_no", "amount"],
            ReplaceExistingView = false
        });

        Assert.False(first.RequiresColumnConfirmation);
        Assert.Equal(["amount", "order_no"], first.Columns.Select(item => item.Name).OrderBy(item => item).ToArray());

        var rows = _db.Ado.SqlQuery<dynamic>("SELECT order_no, amount FROM order_physical");
        Assert.Single(rows);

        // 幂等：转换过的物理表名不会被二次替换，替换重建后依然可查
        var second = await builder.CreateViewAsync(new CreateViewDefinitionDto
        {
            ProjectId = 1,
            ViewName = "order_physical",
            Sql = "SELECT o.order_no AS order_no, o.amount AS amount FROM sales_order o",
            ExpectedColumns = ["order_no", "amount"],
            ReplaceExistingView = true
        });

        Assert.False(second.RequiresColumnConfirmation);
        Assert.Single(_db.Ado.SqlQuery<dynamic>("SELECT order_no, amount FROM order_physical"));
    }

    /// <summary>
    /// 只替换 FROM / JOIN 后面的标识符：列名里含实体名（p.ProjectId）时不能被误伤，
    /// 且转换必须幂等（预览转换一次、执行再转换一次）。
    /// </summary>
    [Fact]
    public void ResolveSqlTables_replaces_only_from_and_join_targets()
    {
        var entities = new List<ModuleEntity>
        {
            new() { Id = 1, Name = "Project", TableName = "engineering_management__project" },
            new() { Id = 2, Name = "MaterialContract", TableName = "material_management__material_contract" }
        };
        var map = ViewSqlTableResolver.BuildTableMap(entities);

        const string sql =
            "SELECT p.Id AS Id, p.ProjectId, mc.ContractNo FROM Project p " +
            "LEFT JOIN MaterialContract mc ON mc.ProjectId = p.Id " +
            "WHERE p.IsDeleted = 0";
        var resolved = ViewSqlTableResolver.Resolve(sql, map);

        Assert.Contains("FROM engineering_management__project p", resolved);
        Assert.Contains("JOIN material_management__material_contract mc", resolved);
        // 列名保持原样
        Assert.Contains("p.ProjectId", resolved);
        Assert.Contains("mc.ProjectId", resolved);
        // 幂等
        Assert.Equal(resolved, ViewSqlTableResolver.Resolve(resolved, map));
    }

    public void Dispose()
    {
        _db.Ado.Close();
        _db.Dispose();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
        if (Directory.Exists(_projectRoot)) Directory.Delete(_projectRoot, true);
    }

    /// <summary>
    /// 视图构建器建视图成功后必须把受管定义推进到 Applied。定义行原本只在
    /// ProjectViewDefinitionService 的部署路径里推进，历史失败留下的 Failed 会让
    /// GeneratedViewMigrationExporter 永久跳过该视图，生成的 Migrator 一直是空的。
    /// </summary>
    [Fact]
    public async Task CreateView_recovers_failed_managed_definition_into_applied()
    {
        var now = DateTime.UtcNow;
        _db.Insertable(new ProjectViewDefinition
        {
            Id = 100, TenantId = 7, ProjectId = 1, DefinitionKey = "orderPhysical",
            ViewName = "order_physical", DatabaseType = DatabaseType.SQLite,
            Sql = "SELECT o.order_no AS order_no, o.amount AS amount FROM Order o",
            DefinitionHash = "HASH-A", DefinitionVersion = 1,
            Ownership = ProjectViewOwnership.Managed,
            DeploymentState = ProjectViewDeploymentState.Failed,
            LastError = "SQLite Error 1: 'no such table: main.Order'.",
            DeclaredSchemaJson = "[]", DeclaredSchemaHash = string.Empty,
            CreateBy = "test", CreateUserId = 0, UpdateUserId = 0, CreateTime = now
        }).ExecuteCommand();

        var builder = new ProjectViewBuilderService(_db);
        await builder.CreateViewAsync(new CreateViewDefinitionDto
        {
            ProjectId = 1,
            ViewName = "order_physical",
            Sql = "SELECT o.order_no AS order_no, o.amount AS amount FROM Order o",
            ExpectedColumns = ["order_no", "amount"],
            ReplaceExistingView = false
        });

        var definition = await _db.Queryable<ProjectViewDefinition>()
            .Where(item => item.Id == 100).FirstAsync();
        Assert.NotNull(definition);
        Assert.Equal(ProjectViewDeploymentState.Applied, definition!.DeploymentState);
        Assert.Equal(definition.DefinitionHash, definition.AppliedDefinitionHash);
        Assert.Null(definition.LastError);
        Assert.NotNull(definition.LastDeployedAt);
    }

    /// <summary>
    /// SQLite 的 CREATE VIEW 不校验列引用，坏定义也能"创建成功"。替换失败必须整体回滚，
    /// 不能让一个原本可用的视图被换成查询即报错的坏视图。
    /// </summary>
    [Fact]
    public async Task CreateView_rolls_back_broken_replacement_and_keeps_previous_view()
    {
        var builder = new ProjectViewBuilderService(_db);
        await builder.CreateViewAsync(new CreateViewDefinitionDto
        {
            ProjectId = 1,
            ViewName = "order_physical",
            Sql = "SELECT o.order_no AS order_no, o.amount AS amount FROM Order o",
            ExpectedColumns = ["order_no", "amount"],
            ReplaceExistingView = false
        });

        await Assert.ThrowsAnyAsync<Exception>(() => builder.CreateViewAsync(new CreateViewDefinitionDto
        {
            ProjectId = 1,
            ViewName = "order_physical",
            Sql = "SELECT o.missing_column AS order_no, o.amount AS amount FROM Order o",
            ExpectedColumns = ["order_no", "amount"],
            ReplaceExistingView = true
        }));

        var rows = _db.Ado.SqlQuery<dynamic>("SELECT order_no, amount FROM order_physical");
        Assert.Single(rows);
    }

    private void Seed()
    {
        var now = DateTime.UtcNow;
        _db.Ado.ExecuteCommand("CREATE TABLE sales_order (order_no TEXT NOT NULL, amount NUMERIC NOT NULL)");
        _db.Ado.ExecuteCommand("INSERT INTO sales_order (order_no, amount) VALUES ('SO-1', 10)");
        _db.Insertable(new Project
        {
            Id = 1, TenantId = 7, ProjectName = "ViewDefinitionTarget", DisplayName = "View definition target",
            DatabaseType = DatabaseType.SQLite, ConnectionString = $"Data Source={_dbPath};Mode=ReadWriteCreate;",
            ProjectPath = _projectRoot, CreateBy = "test", CreateUserId = 0, UpdateUserId = 0, CreateTime = now
        }).ExecuteCommand();
        _db.Insertable(new ProjectModule
        {
            Id = 2, TenantId = 7, ProjectId = 1, ModuleName = "Sales", ModuleDescription = "Sales",
            Icon = string.Empty, RoutePath = string.Empty, Remark = string.Empty, OrderNum = 1,
            CreateBy = "test", CreateUserId = 0, UpdateUserId = 0, CreateTime = now
        }).ExecuteCommand();
        _db.Insertable(new ModuleEntity
        {
            Id = 10, TenantId = 7, ProjectId = 1, ModuleId = 2, Name = "Order", Description = "Order",
            TableName = "sales_order", HasPrimaryKey = true, CreateBy = "test", CreateUserId = 0,
            UpdateUserId = 0, CreateTime = now
        }).ExecuteCommand();
        _db.Insertable(new EntityField
        {
            Id = 11, TenantId = 7, ModuleEntityId = 10, Name = "OrderNo", Description = "Order number",
            ColumnName = "order_no", DataType = "string", IsNullable = false, IsPrimaryKey = false,
            CreateBy = "test", CreateUserId = 0, UpdateUserId = 0, CreateTime = now
        }).ExecuteCommand();
    }
}
