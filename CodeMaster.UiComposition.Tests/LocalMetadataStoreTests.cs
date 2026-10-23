using System.Data;
using System.Text.Json;
using CodeMaster.Application.Dtos.CodeGen;
using CodeMaster.Application.Services.CodeGen;
using CodeMaster.Core.Enums;
using CodeMaster.Domain.Entities.CodeGen;
using CodeMaster.LocalAgent.Extensibility;
using CodeMaster.LocalAgent.Models;
using CodeMaster.LocalAgent.Services;
using CodeMaster.UiComposition.LocalAgent;
using CodeMaster.UiComposition.Entities;
using Microsoft.Extensions.Options;

namespace CodeMaster.LocalAgent.Tests.Services;

public class LocalMetadataStoreTests
{
    [Fact]
    public async Task CreateAsync_creates_sqlite_metadata_database_and_normalizes_null_strings()
    {
        var metadataRoot = Path.Combine(CodeMasterTestPaths.Root, "CodeMaster.LocalAgent.Tests", Guid.NewGuid().ToString("N"));
        var projectPathOverride = Path.Combine(metadataRoot, "GeneratedProjects", "OrderManager");
        Directory.CreateDirectory(metadataRoot);

        try
        {
            var store = CreateStore(Options.Create(new LocalAgentOptions
            {
                MetadataRoot = metadataRoot
            }));

            var bundle = CreateBundle();
            string dbPath;

            using (var context = await store.CreateAsync(bundle, projectPathOverride))
            {
                dbPath = Directory.EnumerateFiles(metadataRoot, $"{bundle.Project.Id}*.metadata.db").Single();
                Assert.True(File.Exists(dbPath));

                var project = await context.Db.Queryable<Project>().FirstAsync(p => p.Id == bundle.Project.Id);
                Assert.Equal(projectPathOverride, project.ProjectPath);
                Assert.Equal(string.Empty, project.DisplayNameEn);
                Assert.Equal("LocalAgent", project.CreateBy);
                Assert.NotEqual(default, project.CreateTime);

                var module = await context.Db.Queryable<ProjectModule>().FirstAsync(m => m.Id == 2001);
                Assert.Equal(string.Empty, module.Icon);
                Assert.Equal(string.Empty, module.RoutePath);
                Assert.Equal("LocalAgent", module.CreateBy);

                var field = await context.Db.Queryable<EntityField>().FirstAsync(f => f.Id == 4001);
                Assert.True(field.IsMultiple);
                Assert.Equal(string.Empty, field.RelatedEntityName);
                Assert.Equal("[{\"sourceField\":\"Name\",\"targetField\":\"CustomerName\"}]", field.ResultMappings);
                Assert.Equal("LocalAgent", field.CreateBy);

                var entityRelation = await context.Db.Queryable<EntityRelation>().FirstAsync(r => r.Id == 5002);
                Assert.Equal(3001, entityRelation.SourceEntityId);
                Assert.Equal(3002, entityRelation.TargetEntityId);
                Assert.Equal("Detail", entityRelation.RelationName);
                Assert.Equal(EntityRelationOwnership.Owned, entityRelation.Ownership);

                var columnTypes = ReadSqliteColumnTypes(context, "sys_entity_field");
                Assert.Equal("integer", columnTypes["is_multiple"]);
                Assert.Equal("integer", columnTypes["show_in_list"]);
                Assert.StartsWith("text", columnTypes["name"]);
                Assert.StartsWith("text", columnTypes["result_mappings"]);

                var projectPage = await context.Db.Queryable<ProjectPage>().FirstAsync(page => page.Id == 9001);
                Assert.Equal("order-workbench", projectPage.PageKey);
                Assert.Single(await context.Db.Queryable<PageDataSource>().Where(source => source.PageId == 9001).ToListAsync());
                Assert.Equal(2, (await context.Db.Queryable<PageComponentBinding>().Where(binding => binding.PageId == 9001).ToListAsync()).Count);
                Assert.Single(await context.Db.Queryable<PageEntityDependency>().Where(dependency => dependency.PageId == 9001).ToListAsync());
                Assert.Single(await context.Db.Queryable<PageLayoutPreset>().ToListAsync());
                Assert.Single(await context.Db.Queryable<PageDisplayTemplate>().ToListAsync());

                var generated = await CreateGenerationService(context).GenerateAsync(9001);
                Assert.True(generated.Success, generated.Error);
                Assert.True(File.Exists(generated.VuePath));
                Assert.False(string.IsNullOrWhiteSpace(generated.VueHash));
                var generatedVue = await File.ReadAllTextAsync(generated.VuePath);
                Assert.Contains("el-table-column", generatedVue);
            }

            Assert.False(File.Exists(dbPath));
            Assert.False(File.Exists(dbPath + "-wal"));
            Assert.False(File.Exists(dbPath + "-shm"));
        }
        finally
        {
            DeleteDirectoryWithRetries(metadataRoot);
        }
    }

    [Fact]
    public void Complex_page_bundle_version_validation_is_explicit_and_legacy_safe()
    {
        var bundle = CreateBundle();
        UiCompositionLocalCodegenActionHandler.ValidateBundle(bundle);

        bundle.ExtensionVersions[ProjectPageModuleContract.Key] = 0;
        var legacy = Assert.Throws<InvalidOperationException>(() =>
            UiCompositionLocalCodegenActionHandler.ValidateBundle(bundle));
        Assert.Contains("too old", legacy.Message);

        bundle.ExtensionVersions[ProjectPageModuleContract.Key] = ProjectPageModuleContract.Version + 1;
        var future = Assert.Throws<InvalidOperationException>(() =>
            UiCompositionLocalCodegenActionHandler.ValidateBundle(bundle));
        Assert.Contains("Unsupported UI composition metadata version", future.Message);
    }

    [Fact]
    public async Task Complex_page_generation_produces_equivalent_output_across_restored_stores()
    {
        var metadataRoot = Path.Combine(CodeMasterTestPaths.Root, "CodeMaster.LocalAgent.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(metadataRoot);

        try
        {
            var bundle = CreateBundle();
            var options = Options.Create(new LocalAgentOptions { MetadataRoot = metadataRoot });

            string? expectedVue = null;
            string? expectedAuto = null;
            string? expectedTree = null;

            using (var context = await CreateStore(options).CreateAsync(bundle, Path.Combine(metadataRoot, "LocalAgent", "OrderManager")))
            {
                var result = await CreateGenerationService(context).GenerateAsync(9001);
                Assert.True(result.Success, result.Error);
                expectedVue = await File.ReadAllTextAsync(Path.Combine(result.OutputDirectory, "index.vue"));
                expectedAuto = await File.ReadAllTextAsync(Path.Combine(result.OutputDirectory, "order-workbench.auto.js"));
                expectedTree = await File.ReadAllTextAsync(Path.Combine(result.OutputDirectory, "order-workbench.tree.json"));
            }

            using (var context = await CreateStore(options).CreateAsync(bundle, Path.Combine(metadataRoot, "WebApi", "OrderManager")))
            {
                var generated = await CreateGenerationService(context).GenerateAsync(9001);
                Assert.True(generated.Success, generated.Error);
                Assert.Equal(expectedVue, await File.ReadAllTextAsync(Path.Combine(generated.OutputDirectory, "index.vue")));
                Assert.Equal(expectedAuto, await File.ReadAllTextAsync(Path.Combine(generated.OutputDirectory, "order-workbench.auto.js")));
                Assert.Equal(expectedTree, await File.ReadAllTextAsync(Path.Combine(generated.OutputDirectory, "order-workbench.tree.json")));
            }
        }
        finally
        {
            DeleteDirectoryWithRetries(metadataRoot);
        }
    }

    private static GenerationBundleDto CreateBundle()
    {
        var bundle = new GenerationBundleDto
        {
            Project = new Project
            {
                Id = 1001,
                ProjectName = "OrderManager",
                DisplayName = "Order Manager",
                DisplayNameEn = null,
                Description = null,
                DatabaseType = DatabaseType.SQLite,
                ConnectionString = "Data Source=OrderManager.db",
                ProjectPath = "D:\\ServerPath",
                LogoPath = null,
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
                    Icon = null,
                    RoutePath = null,
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
                    TableName = null,
                    GenerateFrontend = true,
                    CreateBy = null,
                    CreateTime = default
                },
                new ModuleEntity
                {
                    Id = 3002,
                    ProjectId = 1001,
                    ModuleId = 2001,
                    Name = "OrderDetail",
                    Description = "Order detail",
                    TableName = null,
                    GenerateFrontend = true,
                    IsChildTable = true,
                    CreateBy = null,
                    CreateTime = default
                }
            },
            Fields =
            {
                new EntityField
                {
                    Id = 4001,
                    ModuleEntityId = 3001,
                    Name = "CustomerIds",
                    Description = "Customers",
                    DataType = "string",
                    FormControlType = "select-table",
                    SelectDataSource = null,
                    SelectOptions = null,
                    IsMultiple = true,
                    RelatedEntityName = null,
                    RelatedEntityIdField = null,
                    RelatedEntityDisplayFields = null,
                    ResultMappings = "[{\"sourceField\":\"Name\",\"targetField\":\"CustomerName\"}]",
                    ShowInList = true,
                    ShowInDetail = true,
                    ShowInAddForm = true,
                    ShowInEditForm = true,
                    FieldCategory = "Normal",
                    CreateBy = null,
                    CreateTime = default
                }
            },
            Relations =
            {
                new OneToManyRelation
                {
                    Id = 5001,
                    ModuleEntityId = 3001,
                    MasterField = "Id",
                    ChildEntityId = 3002,
                    ChildEntityName = "OrderItem",
                    ChildForeignKey = "OrderId",
                    CreateBy = null,
                    CreateTime = default
                }
            },
            EntityRelations =
            {
                new EntityRelation
                {
                    Id = 5002,
                    SourceEntityId = 3001,
                    TargetEntityId = 3002,
                    RelationName = "Detail",
                    SourceField = "Id",
                    TargetField = "OrderId",
                    Cardinality = EntityRelationCardinality.OneToOne,
                    Ownership = EntityRelationOwnership.Owned,
                    DeleteBehavior = EntityRelationDeleteBehavior.Delete,
                    CreateBy = null,
                    CreateTime = default
                }
            },
            PageTemplates =
            {
                new SysPageTemplate
                {
                    Id = 6001,
                    PageType = "index",
                    Name = "Index",
                    HtmlContent = "<template />",
                    ScriptSections = "{}",
                    CreateBy = null,
                    CreateTime = default
                }
            },
            FieldControlTemplates =
            {
                new SysFieldControlTemplate
                {
                    Id = 7001,
                    ControlType = "input",
                    PageSection = "list",
                    HtmlContent = "<span />",
                    ScriptSections = "{}",
                    CreateBy = null,
                    CreateTime = default
                }
            },
            ChildTemplates =
            {
                new SysChildTemplate
                {
                    Id = 8001,
                    PageType = "add",
                    ChildType = "card",
                    HtmlContent = "<el-table />",
                    ScriptSections = "{}",
                    CreateBy = null,
                    CreateTime = default
                }
            }
        };

        var extension = new ProjectPageBundleExtension
        {
            Pages =
            {
                new ProjectPage
                {
                    Id = 9001,
                    ProjectId = 1001,
                    ModuleId = 2001,
                    PageKey = "order-workbench",
                    PageName = "Order workbench",
                    RoutePath = "/sales/order-workbench",
                    LayoutJson = "{\"version\":1,\"root\":{\"type\":\"page\",\"componentId\":\"page_root\",\"children\":[{\"type\":\"table\",\"componentId\":\"order_table\",\"children\":[{\"type\":\"table-column\",\"componentId\":\"customer_ids\"}]}]}}",
                    CreateBy = null,
                    CreateTime = default
                }
            },
            DataSources =
            {
                new PageDataSource
                {
                    Id = 9002,
                    PageId = 9001,
                    SourceKey = "orderList",
                    EntityId = 3001,
                    ServiceName = "OrderService",
                    MethodName = "GetPagedListAsync",
                    DtoName = "OrderDto",
                    DataShape = PageDataShape.Paged,
                    ParamsJson = "{}",
                    IsEnabled = true,
                    CreateBy = null,
                    CreateTime = default
                }
            },
            ComponentBindings =
            {
                new PageComponentBinding
                {
                    Id = 9003,
                    PageId = 9001,
                    ComponentId = "order_table",
                    BindingId = "binding_order_table",
                    DataSourceId = 9002,
                    BindingRole = PageBindingRole.Table,
                    GeneratedGenId = "gen_binding_binding_order_table",
                    PropsJson = "{}",
                    CreateBy = null,
                    CreateTime = default
                },
                new PageComponentBinding
                {
                    Id = 9004,
                    PageId = 9001,
                    ComponentId = "customer_ids",
                    BindingId = "binding_customer_ids",
                    DataSourceId = 9002,
                    FieldId = 4001,
                    BindingRole = PageBindingRole.Column,
                    GeneratedGenId = "gen_binding_binding_customer_ids",
                    PropsJson = "{}",
                    CreateBy = null,
                    CreateTime = default
                }
            },
            EntityDependencies =
            {
                new PageEntityDependency
                {
                    Id = 9005,
                    PageId = 9001,
                    EntityId = 3001,
                    DataSourceId = 9002,
                    DependencyType = PageDependencyType.Direct,
                    FieldIdsJson = "[4001]",
                    SummaryJson = "{}",
                    CreateBy = null,
                    CreateTime = default
                }
            },
            LayoutPresets =
            {
                new PageLayoutPreset
                {
                    Id = 9006,
                    PresetKey = "blank",
                    PresetName = "Blank",
                    LayoutJson = "{\"version\":1,\"root\":{\"type\":\"page\",\"componentId\":\"page_root\",\"children\":[]}}",
                    Sort = 1,
                    IsSystem = true,
                    CreateBy = null,
                    CreateTime = default
                }
            },
            DisplayTemplates =
            {
                new PageDisplayTemplate
                {
                    Id = 9007,
                    TemplateKey = "card-table",
                    TemplateName = "Card table",
                    Scope = PageDisplayTemplateScope.Container,
                    TargetType = "table",
                    TemplateContent = "<el-card>{{items}}</el-card>",
                    Sort = 1,
                    IsEnabled = true,
                    CreateBy = null,
                    CreateTime = default
                }
            }
        };
        bundle.ExtensionVersions[ProjectPageModuleContract.Key] = ProjectPageModuleContract.Version;
        bundle.Extensions[ProjectPageModuleContract.Key] = JsonSerializer.SerializeToElement(extension);
        return bundle;
    }

    private static LocalMetadataStore CreateStore(IOptions<LocalAgentOptions> options) =>
        new(options, contributors: [new UiCompositionLocalMetadataContributor()]);

    private static ComplexPageGenerationService CreateGenerationService(LocalMetadataContext context)
    {
        var pageService = new ProjectPageService(context.Db);
        return new ComplexPageGenerationService(context.Db, pageService);
    }

    private static Dictionary<string, string> ReadSqliteColumnTypes(LocalMetadataContext context, string tableName)
    {
        var table = context.Db.Ado.GetDataTable($"PRAGMA table_info('{tableName}')");

        return table.Rows.Cast<DataRow>()
            .ToDictionary(
                row => Convert.ToString(row["name"]) ?? string.Empty,
                row => (Convert.ToString(row["type"]) ?? string.Empty).ToLowerInvariant());
    }

    private static void DeleteDirectoryWithRetries(string path)
    {
        if (!Directory.Exists(path))
            return;

        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                ClearSqlitePools();
                GC.Collect();
                GC.WaitForPendingFinalizers();
                Directory.Delete(path, recursive: true);
                return;
            }
            catch (IOException) when (attempt < 4)
            {
                Thread.Sleep(100);
            }
            catch (UnauthorizedAccessException) when (attempt < 4)
            {
                Thread.Sleep(100);
            }
        }
    }

    private static void ClearSqlitePools()
    {
        var sqliteConnectionType = Type.GetType("Microsoft.Data.Sqlite.SqliteConnection, Microsoft.Data.Sqlite");
        sqliteConnectionType
            ?.GetMethod("ClearAllPools", Type.EmptyTypes)
            ?.Invoke(null, null);
    }
}
