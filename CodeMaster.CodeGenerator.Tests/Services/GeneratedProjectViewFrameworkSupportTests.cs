using CodeMaster.Application.Services.CodeGen;
using CodeMaster.Domain.Entities.CodeGen;

namespace CodeMaster.CodeGenerator.Tests.Services;

public sealed class GeneratedProjectViewFrameworkSupportTests
{
    [Fact]
    public void IsManagedViewEntity_requires_the_registered_view_contract()
    {
        Assert.True(GeneratedProjectViewFrameworkSupport.IsManagedViewEntity(new ModuleEntity
        {
            GenerationMode = EntityGenerationMode.FullStack,
            IsReadOnly = true,
            HasAudit = false,
            HasSoftDelete = false,
            TableName = "demo_view"
        }));
        Assert.False(GeneratedProjectViewFrameworkSupport.IsManagedViewEntity(new ModuleEntity
        {
            GenerationMode = EntityGenerationMode.FullStack,
            IsReadOnly = true,
            HasAudit = true,
            TableName = "demo_view"
        }));
    }

    [Fact]
    public async Task EnsureAsync_upgrades_an_older_generated_project_idempotently()
    {
        var root = Path.Combine(CodeMasterTestPaths.Root, $"codemaster-view-support-{Guid.NewGuid():N}");
        const string projectName = "LegacyDemo";
        try
        {
            Directory.CreateDirectory(Path.Combine(root, $"{projectName}.Core", "Entities"));
            Directory.CreateDirectory(Path.Combine(root, $"{projectName}.SourceGenerator"));
            Directory.CreateDirectory(Path.Combine(root, $"{projectName}.Infrastructure", "Persistence", "SqlSugar"));
            Directory.CreateDirectory(Path.Combine(root, $"{projectName}.Migrator"));
            Directory.CreateDirectory(Path.Combine(root, $"{projectName}.Migrator", "Persistence", "EfCore"));
            await File.WriteAllTextAsync(
                Path.Combine(root, $"{projectName}.SourceGenerator", "DbContextGenerator.cs"),
                LegacySourceGenerator());
            await File.WriteAllTextAsync(
                Path.Combine(root, $"{projectName}.Migrator", "Program.cs"),
                LegacyMigratorProgram());
            await File.WriteAllTextAsync(
                Path.Combine(root, $"{projectName}.Migrator", "Persistence", "EfCore", $"{projectName}DbContext.cs"),
                LegacyMigratorDbContext());
            await File.WriteAllTextAsync(
                Path.Combine(root, $"{projectName}.Infrastructure", "Persistence", "SqlSugar", "SqlSugarSetup.cs"),
                LegacySqlSugarSetup());

            await GeneratedProjectViewFrameworkSupport.EnsureAsync(root, projectName);
            await GeneratedProjectViewFrameworkSupport.EnsureAsync(root, projectName);

            Assert.Contains("interface IViewEntity", await File.ReadAllTextAsync(
                Path.Combine(root, $"{projectName}.Core", "Entities", "IViewEntity.cs")));
            var sourceGenerator = await File.ReadAllTextAsync(
                Path.Combine(root, $"{projectName}.SourceGenerator", "DbContextGenerator.cs"));
            Assert.Contains("IViewEntity", sourceGenerator);
            Assert.Contains("builder.ToView", sourceGenerator);
            var program = await File.ReadAllTextAsync(Path.Combine(root, $"{projectName}.Migrator", "Program.cs"));
            Assert.Equal(2, program.Split("ApplyModuleMigrationsAsync", StringSplitOptions.None).Length - 1);
            Assert.Contains("ConfigureDbContextOptions", program);
            var dbContext = await File.ReadAllTextAsync(Path.Combine(
                root,
                $"{projectName}.Migrator",
                "Persistence",
                "EfCore",
                $"{projectName}DbContext.cs"));
            Assert.Contains("IViewEntity", dbContext);
            Assert.Contains("continue;", dbContext);
            var sqlSugarSetup = await File.ReadAllTextAsync(Path.Combine(
                root,
                $"{projectName}.Infrastructure",
                "Persistence",
                "SqlSugar",
                "SqlSugarSetup.cs"));
            Assert.Equal(2, sqlSugarSetup.Split("sugarColumn?.ColumnName", StringSplitOptions.None).Length - 1);
            Assert.True(File.Exists(Path.Combine(
                root,
                $"{projectName}.Migrator",
                "Persistence",
                "EfCore",
                "MigrationModuleCatalog.cs")));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static string LegacySourceGenerator() => """
        private EntityInfo AnalyzeEntity(INamedTypeSymbol typeSymbol)
        {
            var entityInfo = new EntityInfo
            {
                InheritsEntityBase = InheritsFromEntityBase(typeSymbol)
            };
        }
        private string GenerateEntityConfiguration(EntityInfo entity)
        {
            // 配置表名
            sb.AppendLine($"            // 配置表名");
            sb.AppendLine($"            builder.ToTable(\"{entity.TableName}\");");
        }
        private class EntityInfo
        {
            public bool InheritsEntityBase { get; set; }
        }
        """;

    private static string LegacyMigratorProgram() => """
        using Microsoft.EntityFrameworkCore;
        var services = new ServiceCollection();
        services.AddDbContext<LegacyDemoDbContext>(opts =>
        {
        #if DB_PGSQL
            opts.UseNpgsql(connectionString);
        #else
            opts.UseSqlServer(connectionString);
        #endif
        });
        #if DB_MYSQL
            await dbContext.Database.MigrateAsync();
        #else
            await dbContext.Database.MigrateAsync();
        #endif
            Console.WriteLine("✓ 数据库迁移完成\n");
        static string MaskConnectionString(string connectionString) => connectionString;
        """;

    private static string LegacyMigratorDbContext() => """
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            foreach (var entity in modelBuilder.Model.GetEntityTypes())
            {
                var tableName = entity.GetTableName();
                if (!string.IsNullOrEmpty(tableName))
                    entity.SetTableName(ToSnakeCase(tableName));
            }
        }
        """;

    private static string LegacySqlSugarSetup() => """
        private static ConfigureExternalServices Create(DbType dbType) => new()
        {
            EntityService = (property, column) =>
            {
                // 自动转换：UserName -> user_name
                column.DbColumnName = ToSnakeCase(property.Name);

                // 自动映射数据库类型
                column.DataType = GetDatabaseType(property.PropertyType, dbType);
            }
        };

        private static ConfigureExternalServices CreateOther(DbType dbType) => new()
        {
            EntityService = (property, column) =>
            {
                // 自动转换：UserName -> user_name
                column.DbColumnName = ToSnakeCase(property.Name);

                // 自动映射数据库类型
                column.DataType = GetDatabaseType(property.PropertyType, dbType);
            }
        };
        """;
}
