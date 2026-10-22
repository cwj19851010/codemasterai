using CodeMaster.Application.Dtos.CodeGen;
using CodeMaster.Core.Entities;
using CodeMaster.Core.Enums;
using CodeMaster.Domain.Entities.CodeGen;
using CodeMaster.LocalAgent.Extensibility;
using CodeMaster.LocalAgent.Models;
using CodeMaster.LocalAgent.Services;
using Microsoft.Extensions.Options;
using SqlSugar;

namespace CodeMaster.LocalAgent.Tests.Services;

public class LocalMetadataStoreTests
{
    [Fact]
    public async Task CreateAsync_restores_public_metadata_and_cleans_up_database()
    {
        var metadataRoot = CreateTempDirectory();
        var projectPath = Path.Combine(metadataRoot, "Generated", "OrderManager");
        try
        {
            var store = new LocalMetadataStore(Options.Create(new LocalAgentOptions
            {
                MetadataRoot = metadataRoot
            }));
            var bundle = CreateBundle();
            string databasePath;

            using (var context = await store.CreateAsync(bundle, projectPath))
            {
                databasePath = Directory.EnumerateFiles(metadataRoot, "*.metadata.db").Single();
                var project = await context.Db.Queryable<Project>().FirstAsync();
                var entity = await context.Db.Queryable<ModuleEntity>().FirstAsync();

                Assert.Equal(projectPath, project.ProjectPath);
                Assert.Equal("LocalAgent", project.CreateBy);
                Assert.Equal(string.Empty, project.DisplayNameEn);
                Assert.Equal("Order", entity.Name);
            }

            Assert.False(File.Exists(databasePath));
        }
        finally
        {
            DeleteDirectory(metadataRoot);
        }
    }

    [Fact]
    public async Task CreateAsync_allows_optional_metadata_contributors()
    {
        var metadataRoot = CreateTempDirectory();
        try
        {
            var store = new LocalMetadataStore(
                Options.Create(new LocalAgentOptions { MetadataRoot = metadataRoot }),
                contributors: [new TestMetadataContributor()]);

            using var context = await store.CreateAsync(CreateBundle());
            var row = await context.Db.Queryable<TestMetadataRow>().FirstAsync();

            Assert.Equal("extension-loaded", row.Value);
        }
        finally
        {
            DeleteDirectory(metadataRoot);
        }
    }

    private static GenerationBundleDto CreateBundle() => new()
    {
        Project = new Project
        {
            Id = 1001,
            ProjectName = "OrderManager",
            DisplayName = "Order Manager",
            DisplayNameEn = null,
            DatabaseType = DatabaseType.SQLite,
            ConnectionString = "Data Source=OrderManager.db",
            ProjectPath = CodeMasterTestPaths.Root,
            CreateBy = null,
            CreateTime = default
        },
        Modules =
        {
            new ProjectModule
            {
                Id = 2001,
                ProjectId = 1001,
                ModuleName = "Sales",
                ModuleDescription = "Sales",
                CreateBy = null,
                CreateTime = default
            }
        },
        Entities =
        {
            new ModuleEntity
            {
                Id = 3001,
                ProjectId = 1001,
                ModuleId = 2001,
                Name = "Order",
                Description = "Order",
                CreateBy = null,
                CreateTime = default
            }
        }
    };

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(CodeMasterTestPaths.Root, "CodeMaster.LocalAgent.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteDirectory(string path)
    {
        if (!Directory.Exists(path)) return;
        try
        {
            Directory.Delete(path, true);
        }
        catch
        {
            // SQLite can briefly retain a handle after a failed assertion.
        }
    }

    private sealed class TestMetadataContributor : ILocalMetadataBundleContributor
    {
        public IReadOnlyCollection<Type> EntityTypes => [typeof(TestMetadataRow)];

        public async Task RestoreAsync(GenerationBundleDto bundle, ISqlSugarClient db)
        {
            await db.Insertable(new TestMetadataRow
            {
                Id = 9001,
                Value = "extension-loaded",
                CreateBy = "test",
                CreateUserId = 0,
                UpdateUserId = 0,
                CreateTime = DateTime.UtcNow
            }).ExecuteCommandAsync();
        }
    }

    [SugarTable("test_local_metadata_extension")]
    private sealed class TestMetadataRow : EntityBase
    {
        [SugarColumn(ColumnName = "value", Length = 100)]
        public string Value { get; set; } = string.Empty;
    }
}
