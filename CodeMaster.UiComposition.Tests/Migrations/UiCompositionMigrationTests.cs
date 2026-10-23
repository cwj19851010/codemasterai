using CodeMaster.Domain.Entities.CodeGen;
using CodeMaster.Domain.Entities.System;
using CodeMaster.Migrator.Persistence.EfCore;
using CodeMaster.Migrator.SeedData.CodeGen;
using CodeMaster.UiComposition.Entities;
using CodeMaster.UiComposition.Migrator;
using CodeMaster.UiComposition.Migrator.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Yitter.IdGenerator;

namespace CodeMaster.UiComposition.Tests.Migrations;

public sealed class UiCompositionMigrationTests
{
    private const string HistoryTable = "__EFMigrationsHistory_UiComposition";
    private static readonly string MigrationsAssembly = typeof(UiCompositionDbContext).Assembly.GetName().Name!;

    [Theory]
    [InlineData("SqlServer")]
    [InlineData("MySql")]
    [InlineData("PostgreSQL")]
    [InlineData("Sqlite")]
    public void MigrationScript_CanBeGeneratedForSupportedProvider(string provider)
    {
        using var dbContext = CreateScriptContext(provider);
        if (!string.Equals(provider, "SqlServer", StringComparison.OrdinalIgnoreCase))
        {
            Assert.False(dbContext.Database.HasPendingModelChanges());
        }
        var script = dbContext.GetService<IMigrator>().GenerateScript();

        Assert.Contains("sys_project_page", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("sys_project_data_source", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("sys_page_data_source_binding", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("sys_openapi_document", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("sys_openapi_schema_binding", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("sys_openapi_member_binding", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("sys_project_view_definition", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("sys_project_view_dependency", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("sys_project_view_field_lineage", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("openapi_document_id", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("project_data_source_id", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(HistoryTable, script, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MigrationModule_AppliesFreshSqliteDatabaseAndIsIdempotent()
    {
        var databasePath = CreateTemporaryDatabasePath();
        try
        {
            var module = new UiCompositionDatabaseMigrationModule();
            var context = CreateSqliteExecutionContext(databasePath);

            await module.MigrateAsync(context);
            await module.MigrateAsync(context);

            await using (var dbContext = CreateSqliteContext(databasePath))
            {
                var applied = (await dbContext.Database.GetAppliedMigrationsAsync()).ToArray();
                Assert.Equal(7, applied.Length);
                Assert.Empty(await dbContext.Database.GetPendingMigrationsAsync());
                Assert.Equal(14, await CountUiCompositionTablesAsync(dbContext));
            }
        }
        finally
        {
            File.Delete(databasePath);
        }
    }

    [Fact]
    public async Task MigrationModule_AdoptsExistingSchemaWithoutRecreatingTables()
    {
        var databasePath = CreateTemporaryDatabasePath();
        try
        {
            await using (var existingContext = CreateSqliteContext(databasePath))
            {
                await existingContext.Database.EnsureCreatedAsync();
                Assert.Empty(await existingContext.Database.GetAppliedMigrationsAsync());
            }

            var module = new UiCompositionDatabaseMigrationModule();
            await module.MigrateAsync(CreateSqliteExecutionContext(databasePath));

            await using (var migratedContext = CreateSqliteContext(databasePath))
            {
                Assert.Equal(7, (await migratedContext.Database.GetAppliedMigrationsAsync()).Count());
                Assert.Equal(14, await CountUiCompositionTablesAsync(migratedContext));
            }
        }
        finally
        {
            File.Delete(databasePath);
        }
    }

    [Fact]
    public async Task MigrationModule_PromotesLegacyPageSourcesWithoutLosingBindings()
    {
        var databasePath = CreateTemporaryDatabasePath();
        try
        {
            await using (var existingContext = CreateSqliteContext(databasePath))
            {
                await existingContext.Database.EnsureCreatedAsync();
                existingContext.AddRange(
                    new ProjectPage
                    {
                        Id = 1001,
                        TenantId = 1,
                        ProjectId = 10,
                        ModuleId = 20,
                        PageKey = "orders",
                        PageName = "Orders",
                        RoutePath = "/orders"
                    },
                    new ProjectPage
                    {
                        Id = 1002,
                        TenantId = 1,
                        ProjectId = 10,
                        ModuleId = 21,
                        PageKey = "customers",
                        PageName = "Customers",
                        RoutePath = "/customers"
                    },
                    new PageDataSource
                    {
                        Id = 2001,
                        TenantId = 1,
                        PageId = 1001,
                        SourceKey = "orders",
                        ServiceName = "OrderService",
                        MethodName = "GetPagedListAsync",
                        DtoName = "OrderDto",
                        DataShape = PageDataShape.Paged
                    },
                    new PageDataSource
                    {
                        Id = 2002,
                        TenantId = 1,
                        PageId = 1002,
                        SourceKey = "orders",
                        ServiceName = "OrderService",
                        MethodName = "GetPagedListAsync",
                        DtoName = "OrderDto",
                        DataShape = PageDataShape.Paged
                    },
                    new PageComponentBinding
                    {
                        Id = 3001,
                        TenantId = 1,
                        PageId = 1002,
                        ComponentId = "table",
                        BindingId = "table-orders",
                        DataSourceId = 2002,
                        GeneratedGenId = "gen-table-orders"
                    },
                    new PageEntityDependency
                    {
                        Id = 4001,
                        TenantId = 1,
                        PageId = 1002,
                        EntityId = 5001,
                        DataSourceId = 2002
                    });
                await existingContext.SaveChangesAsync();
            }

            var module = new UiCompositionDatabaseMigrationModule();
            var executionContext = CreateSqliteExecutionContext(databasePath);
            await module.MigrateAsync(executionContext);
            await module.MigrateAsync(executionContext);

            await using var migratedContext = CreateSqliteContext(databasePath);
            var projectSources = await migratedContext.Set<ProjectDataSource>().ToListAsync();
            var pageBindings = await migratedContext.Set<PageDataSourceBinding>().ToListAsync();
            var component = await migratedContext.Set<PageComponentBinding>().SingleAsync(item => item.Id == 3001);
            var dependency = await migratedContext.Set<PageEntityDependency>().SingleAsync(item => item.Id == 4001);

            var projectSource = Assert.Single(projectSources);
            Assert.Equal(10, projectSource.ProjectId);
            Assert.Equal("orders", projectSource.SourceKey);
            Assert.Equal(2, pageBindings.Count);
            Assert.All(pageBindings, binding => Assert.Equal(projectSource.Id, binding.DataSourceId));
            Assert.Equal(projectSource.Id, component.DataSourceId);
            Assert.Equal(projectSource.Id, dependency.DataSourceId);
        }
        finally
        {
            File.Delete(databasePath);
        }
    }

    [Fact]
    public async Task FreshDatabase_SeedsLayoutPresetsAndDisplayTemplatesIdempotently()
    {
        var databasePath = CreateTemporaryDatabasePath();
        try
        {
            await using (var migrationContext = CreateSqliteContext(databasePath))
                await migrationContext.Database.MigrateAsync();

            var options = new DbContextOptionsBuilder<CodeMasterDbContext>()
                .UseSqlite($"Data Source={databasePath};Pooling=False")
                .Options;
            await using var seedContext = new SeedTestDbContext(options);
            var module = new ComplexPageLayoutModule();

            await module.AddInitialDataAsync(seedContext);
            await seedContext.SaveChangesAsync();
            await module.AddInitialDataAsync(seedContext);
            await seedContext.SaveChangesAsync();

            var presets = await seedContext.Set<PageLayoutPreset>()
                .IgnoreQueryFilters()
                .OrderBy(item => item.Sort)
                .ToListAsync();
            var templates = await seedContext.Set<PageDisplayTemplate>()
                .IgnoreQueryFilters()
                .OrderBy(item => item.Sort)
                .ToListAsync();

            Assert.Equal(5, presets.Count);
            Assert.Contains(presets, item => item.PresetKey == "blank");
            Assert.Contains(presets, item => item.PresetKey == "master-detail");
            Assert.Equal(3, templates.Count);
            Assert.Contains(templates, item => item.TemplateKey == "query-form-space");
            Assert.Contains(templates, item => item.TemplateKey == "table-field-link");
            Assert.All(templates, item => Assert.True(item.IsEnabled));
        }
        finally
        {
            File.Delete(databasePath);
        }
    }

    [Fact]
    public async Task MenuSeed_CreatesDataSourceRootAndMovesEntityMenuIdempotently()
    {
        var databasePath = CreateTemporaryDatabasePath();
        try
        {
            var options = new DbContextOptionsBuilder<CodeMasterDbContext>()
                .UseSqlite($"Data Source={databasePath};Pooling=False")
                .Options;
            await using var dbContext = new SeedTestDbContext(options);
            await dbContext.Database.EnsureCreatedAsync();
            YitIdHelper.SetIdGenerator(new IdGeneratorOptions { WorkerId = 31 });

            var codegenRoot = new SysMenu
            {
                Id = 100,
                MenuName = "代码管理",
                Path = "codegen",
                Component = "Layout",
                MenuType = "M",
                OrderNum = 3
            };
            var entityMenu = new SysMenu
            {
                Id = 101,
                ParentId = codegenRoot.Id,
                MenuName = "实体",
                TitleKey = "entity",
                Path = "moduleEntity",
                Component = "codegen/moduleEntity/index",
                MenuType = "C",
                Perms = "codegen:moduleEntity:list"
            };
            dbContext.AddRange(
                new SysRole { Id = 1, TenantId = 0, RoleName = "管理员", RoleKey = "admin", RoleSort = 1 },
                codegenRoot,
                entityMenu);
            await dbContext.SaveChangesAsync();

            var module = new ProjectPageModule();
            await module.AddInitialDataAsync(dbContext);
            await dbContext.SaveChangesAsync();
            await module.AddInitialDataAsync(dbContext);
            await dbContext.SaveChangesAsync();

            var root = await dbContext.Set<SysMenu>().SingleAsync(item => item.Path == "dataSource" && item.MenuType == "M");
            var movedEntity = await dbContext.Set<SysMenu>().SingleAsync(item => item.Path == "moduleEntity" && item.Component == "codegen/moduleEntity/index");
            var view = await dbContext.Set<SysMenu>().SingleAsync(item => item.Path == "dataSourceView");
            var api = await dbContext.Set<SysMenu>().SingleAsync(item => item.Path == "dataSourceApi");

            Assert.Null(root.ParentId);
            Assert.Equal("数据源管理", root.MenuName);
            Assert.Equal(root.Id, movedEntity.ParentId);
            Assert.Equal(root.Id, view.ParentId);
            Assert.Equal(root.Id, api.ParentId);
            Assert.Equal(1, movedEntity.OrderNum);
            Assert.Equal(2, view.OrderNum);
            Assert.Equal(3, api.OrderNum);
            Assert.Equal(4, codegenRoot.OrderNum);
            Assert.Equal(3, await dbContext.Set<SysMenu>().CountAsync(item => item.ParentId == root.Id && item.Visible));
            Assert.True(await dbContext.Set<SysMenu>().AnyAsync(item => item.ParentId == null && item.Path == "codegen/moduleentity/add" && !item.Visible));
            Assert.True(await dbContext.Set<SysMenu>().AnyAsync(item => item.ParentId == null && item.Path == "codegen/moduleentity/edit" && !item.Visible));
            Assert.True(await dbContext.Set<SysMenu>().AnyAsync(item => item.ParentId == null && item.Path == "codegen/moduleentity/detail" && !item.Visible));
            Assert.True(await dbContext.Set<SysRoleMenu>().AnyAsync(item => item.RoleId == 1 && item.MenuId == root.Id));
        }
        finally
        {
            File.Delete(databasePath);
        }
    }

    [Fact]
    public void ComponentBindingModel_ComponentIndexIsNotUnique_ButBindingIndexStaysUnique()
    {
        using var dbContext = CreateScriptContext("Sqlite");
        var designTimeModel = dbContext.GetService<IDesignTimeModel>().Model;
        var entityType = designTimeModel.FindEntityType(typeof(PageComponentBinding))!;
        var indexes = entityType.GetIndexes().ToArray();

        var componentIndex = Assert.Single(indexes,
            index => index.Properties.Select(property => property.Name).SequenceEqual(new[] { "PageId", "ComponentId" }));
        Assert.False(componentIndex.IsUnique);

        var bindingIndex = Assert.Single(indexes,
            index => index.Properties.Select(property => property.Name).SequenceEqual(new[] { "PageId", "BindingId" }));
        Assert.True(bindingIndex.IsUnique);
    }

    [Fact]
    public async Task MigrationModule_AllowsMultipleBindingsForSameComponent()
    {
        var databasePath = CreateTemporaryDatabasePath();
        try
        {
            var module = new UiCompositionDatabaseMigrationModule();
            await module.MigrateAsync(CreateSqliteExecutionContext(databasePath));

            await using var dbContext = CreateSqliteContext(databasePath);
            dbContext.AddRange(
                new PageComponentBinding
                {
                    Id = 9101,
                    TenantId = 1,
                    PageId = 1001,
                    ComponentId = "btn-submit",
                    BindingId = "btn-submit:click",
                    DataSourceId = 2001,
                    BindingRole = PageBindingRole.Event
                },
                new PageComponentBinding
                {
                    Id = 9102,
                    TenantId = 1,
                    PageId = 1001,
                    ComponentId = "btn-submit",
                    BindingId = "btn-submit:success",
                    DataSourceId = 2001,
                    BindingRole = PageBindingRole.Event
                });
            await dbContext.SaveChangesAsync();

            Assert.Equal(2,
                await dbContext.Set<PageComponentBinding>().CountAsync(item => item.ComponentId == "btn-submit"));
        }
        finally
        {
            File.Delete(databasePath);
        }
    }

    [Fact]
    public void PublicModelContributor_MapsPrivateTablesButExcludesThemFromBaseMigrations()
    {
        var options = new DbContextOptionsBuilder<ContributorTestDbContext>()
            .UseSqlite("Data Source=:memory:")
            .Options;
        using var dbContext = new ContributorTestDbContext(options);
        var designTimeModel = dbContext.GetService<IDesignTimeModel>().Model;
        var entityType = designTimeModel.FindEntityType(typeof(ProjectPage));

        Assert.NotNull(entityType);
        var table = StoreObjectIdentifier.Table("sys_project_page", null);
        Assert.True(entityType!.IsTableExcludedFromMigrations(table));
    }

    private static UiCompositionDbContext CreateScriptContext(string provider)
    {
        var builder = new DbContextOptionsBuilder<UiCompositionDbContext>();
        switch (provider)
        {
            case "SqlServer":
                builder.UseSqlServer(
                    "Server=localhost;Database=CodeMasterMigrationTest;User Id=sa;Password=NotUsed123!;TrustServerCertificate=True",
                    options => options.MigrationsAssembly(MigrationsAssembly).MigrationsHistoryTable(HistoryTable));
                break;
            case "MySql":
                builder.UseMySql(
                    "Server=localhost;Database=codemaster_migration_test;User=root;Password=not-used",
                    new MySqlServerVersion(new Version(8, 0, 36)),
                    options => options.MigrationsAssembly(MigrationsAssembly).MigrationsHistoryTable(HistoryTable));
                break;
            case "PostgreSQL":
                builder.UseNpgsql(
                    "Host=localhost;Database=codemaster_migration_test;Username=postgres;Password=not-used",
                    options => options.MigrationsAssembly(MigrationsAssembly).MigrationsHistoryTable(HistoryTable));
                break;
            case "Sqlite":
                builder.UseSqlite(
                    "Data Source=:memory:",
                    options => options.MigrationsAssembly(MigrationsAssembly).MigrationsHistoryTable(HistoryTable));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(provider), provider, null);
        }

        return new UiCompositionDbContext(builder.Options);
    }

    private static UiCompositionDbContext CreateSqliteContext(string databasePath)
    {
        var builder = new DbContextOptionsBuilder<UiCompositionDbContext>();
        builder.UseSqlite(
            $"Data Source={databasePath};Pooling=False",
            options => options.MigrationsAssembly(MigrationsAssembly).MigrationsHistoryTable(HistoryTable));
        return new UiCompositionDbContext(builder.Options);
    }

    private static MigrationModuleExecutionContext CreateSqliteExecutionContext(string databasePath)
        => new()
        {
            DatabaseProvider = "Sqlite",
            ConnectionString = $"Data Source={databasePath};Pooling=False",
            ConfigureDbContext = (builder, connectionString, migrationsAssembly, historyTable) =>
                builder.UseSqlite(
                    connectionString,
                    options => options.MigrationsAssembly(migrationsAssembly).MigrationsHistoryTable(historyTable))
        };

    private static async Task<int> CountUiCompositionTablesAsync(UiCompositionDbContext dbContext)
    {
        var connection = dbContext.Database.GetDbConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*)
            FROM sqlite_master
            WHERE type = 'table'
              AND name IN (
                'sys_page_component_binding',
                'sys_page_data_source',
                'sys_page_entity_dependency',
                'sys_page_layout_preset',
                'sys_project_page',
                'sys_page_data_source_binding',
                'sys_project_data_source',
                'sys_page_display_template',
                'sys_openapi_document',
                'sys_openapi_schema_binding',
                'sys_openapi_member_binding',
                'sys_project_view_definition',
                'sys_project_view_dependency',
                'sys_project_view_field_lineage')
            """;
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private static string CreateTemporaryDatabasePath()
        => Path.Combine(CodeMasterTestPaths.Root, $"codemaster-ui-composition-{Guid.NewGuid():N}.db");

    private sealed class ContributorTestDbContext(DbContextOptions<ContributorTestDbContext> options)
        : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            new ProjectPageModelContributor().Configure(modelBuilder);
        }
    }

    private sealed class SeedTestDbContext(DbContextOptions<CodeMasterDbContext> options)
        : CodeMasterDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            new ProjectPageModelContributor().Configure(modelBuilder);
        }
    }
}
