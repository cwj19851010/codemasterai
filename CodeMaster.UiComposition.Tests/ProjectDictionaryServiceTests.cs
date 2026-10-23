using System.Text.Json;
using CodeMaster.Application.Dtos.CodeGen;
using CodeMaster.Application.Services.CodeGen;
using CodeMaster.Core.Enums;
using CodeMaster.Domain.Entities.CodeGen;
using CodeMaster.Domain.Entities.System;
using CodeMaster.Infrastructure.Persistence.Repositories;
using CodeMaster.Infrastructure.Persistence.SqlSugar;
using CodeMaster.UiComposition.Entities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SqlSugar;
using Yitter.IdGenerator;

namespace CodeMaster.UiComposition.Tests;

public sealed class ProjectDictionaryServiceTests : IDisposable
{
    private readonly string _metadataPath;
    private readonly string _targetPath;
    private readonly ISqlSugarClient _metadataDb;
    private readonly ISqlSugarClient _targetDb;
    private readonly ProjectService _service;

    public ProjectDictionaryServiceTests()
    {
        YitIdHelper.SetIdGenerator(new IdGeneratorOptions { WorkerId = 29 });
        _metadataPath = Path.Combine(CodeMasterTestPaths.Root, $"codemaster_dictionary_metadata_{Guid.NewGuid():N}.db");
        _targetPath = Path.Combine(CodeMasterTestPaths.Root, $"codemaster_dictionary_target_{Guid.NewGuid():N}.db");
        _metadataDb = CreateDatabase(_metadataPath);
        _targetDb = CreateDatabase(_targetPath);
        _metadataDb.CodeFirst.InitTables(
            typeof(Project),
            typeof(ProjectModule),
            typeof(ModuleEntity),
            typeof(EntityField),
            typeof(OneToManyRelation),
            typeof(EntityRelation),
            typeof(SysPageTemplate),
            typeof(SysFieldControlTemplate),
            typeof(SysChildTemplate),
            typeof(ProjectPage),
            typeof(PageDataSource),
            typeof(PageComponentBinding),
            typeof(PageEntityDependency),
            typeof(PageLayoutPreset),
            typeof(PageDisplayTemplate));
        CreateDictionaryTables(_targetDb);

        _metadataDb.Insertable(new Project
        {
            Id = 1001,
            ProjectName = "DictionaryTarget",
            DisplayName = "Dictionary Target",
            DatabaseType = DatabaseType.SQLite,
            ConnectionString = $"Data Source={_targetPath};Mode=ReadWriteCreate;",
            ProjectPath = CodeMasterTestPaths.Root,
            CreateBy = "test",
            CreateUserId = 0,
            UpdateUserId = 0,
            CreateTime = DateTime.UtcNow
        }).ExecuteCommand();

        var configuration = new ConfigurationBuilder().Build();
        var serviceProvider = new ServiceCollection()
            .AddSingleton(_metadataDb)
            .AddScoped<IGenerationBundleContributor, ProjectPageGenerationBundleContributor>()
            .BuildServiceProvider();
        _service = new ProjectService(
            new Repository<Project>(_metadataDb),
            null!,
            configuration,
            serviceProvider,
            _metadataDb);
    }

    [Fact]
    public async Task Generation_bundle_exports_complex_page_metadata_with_current_version()
    {
        var now = DateTime.UtcNow;
        _metadataDb.Insertable(new ProjectModule
        {
            Id = 1101,
            ProjectId = 1001,
            ModuleName = "Sales",
            ModuleDescription = "Sales",
            Icon = string.Empty,
            RoutePath = string.Empty,
            Remark = string.Empty,
            CreateBy = "test",
            CreateUserId = 0,
            UpdateUserId = 0,
            CreateTime = now
        }).ExecuteCommand();
        _metadataDb.Insertable(new ModuleEntity
        {
            Id = 1201,
            ProjectId = 1001,
            ModuleId = 1101,
            Name = "Order",
            Description = "Order",
            TableName = "sales_order",
            CreateBy = "test",
            CreateUserId = 0,
            UpdateUserId = 0,
            CreateTime = now
        }).ExecuteCommand();
        _metadataDb.Insertable(new EntityField
        {
            Id = 1301,
            ModuleEntityId = 1201,
            Name = "CustomerName",
            Description = "Customer name",
            DataType = "string",
            CreateBy = "test",
            CreateUserId = 0,
            UpdateUserId = 0,
            CreateTime = now
        }).ExecuteCommand();
        _metadataDb.Insertable(new ProjectPage
        {
            Id = 1401,
            ProjectId = 1001,
            ModuleId = 1101,
            PageKey = "order-workbench",
            PageName = "Order workbench",
            RoutePath = "/sales/order-workbench",
            LayoutJson = "{\"version\":1,\"root\":{\"type\":\"page\",\"componentId\":\"page_root\",\"children\":[]}}",
            CreateBy = "test",
            CreateUserId = 0,
            UpdateUserId = 0,
            CreateTime = now
        }).ExecuteCommand();
        _metadataDb.Insertable(new PageDataSource
        {
            Id = 1501,
            PageId = 1401,
            SourceKey = "orders",
            EntityId = 1201,
            ServiceName = "OrderService",
            MethodName = "GetPagedListAsync",
            DtoName = "OrderDto",
            DataShape = PageDataShape.Paged,
            CreateBy = "test",
            CreateUserId = 0,
            UpdateUserId = 0,
            CreateTime = now
        }).ExecuteCommand();
        _metadataDb.Insertable(new PageComponentBinding
        {
            Id = 1601,
            PageId = 1401,
            ComponentId = "orders_table",
            BindingId = "binding_orders_table",
            DataSourceId = 1501,
            BindingRole = PageBindingRole.Table,
            GeneratedGenId = "gen_binding_binding_orders_table",
            CreateBy = "test",
            CreateUserId = 0,
            UpdateUserId = 0,
            CreateTime = now
        }).ExecuteCommand();
        _metadataDb.Insertable(new PageEntityDependency
        {
            Id = 1701,
            PageId = 1401,
            EntityId = 1201,
            DataSourceId = 1501,
            DependencyType = PageDependencyType.Direct,
            CreateBy = "test",
            CreateUserId = 0,
            UpdateUserId = 0,
            CreateTime = now
        }).ExecuteCommand();
        _metadataDb.Insertable(new PageLayoutPreset
        {
            Id = 1801,
            PresetKey = "blank",
            PresetName = "Blank",
            LayoutJson = "{\"version\":1,\"root\":{\"type\":\"page\",\"componentId\":\"page_root\",\"children\":[]}}",
            Sort = 1,
            CreateBy = "test",
            CreateUserId = 0,
            UpdateUserId = 0,
            CreateTime = now
        }).ExecuteCommand();
        _metadataDb.Insertable(new PageDisplayTemplate
        {
            Id = 1802,
            TemplateKey = "card-table",
            TemplateName = "Card table",
            Scope = PageDisplayTemplateScope.Container,
            TargetType = "table",
            TemplateContent = "<el-card>{{items}}</el-card>",
            Sort = 1,
            IsEnabled = true,
            CreateBy = "test",
            CreateUserId = 0,
            UpdateUserId = 0,
            CreateTime = now
        }).ExecuteCommand();

        var bundle = await _service.GetGenerationBundleAsync(1001);
        var extension = bundle.Extensions[ProjectPageModuleContract.Key]
            .Deserialize<ProjectPageBundleExtension>();

        Assert.Equal(GenerationBundleDto.CurrentVersion, bundle.Version);
        Assert.NotNull(extension);
        Assert.Single(extension.Pages);
        Assert.Single(extension.DataSources);
        Assert.Single(extension.ComponentBindings);
        Assert.Single(extension.EntityDependencies);
        Assert.Single(extension.LayoutPresets);
        Assert.Single(extension.DisplayTemplates);
    }

    [Fact]
    public async Task Save_dictionary_is_idempotent_restores_rows_and_preserves_omitted_items()
    {
        _targetDb.Insertable(new SysDictType
        {
            Id = 2001,
            TenantId = 0,
            DictType = "order_status",
            DictName = "Old name",
            Status = 1,
            Sort = 99,
            IsDeleted = true,
            DeleteTime = DateTime.UtcNow,
            CreateBy = "test",
            CreateUserId = 0,
            UpdateUserId = 0,
            CreateTime = DateTime.UtcNow
        }).ExecuteCommand();
        _targetDb.Insertable(new[]
        {
            new SysDictData
            {
                Id = 3001,
                TenantId = 0,
                DictType = "order_status",
                Label = "Old pending",
                Value = "pending",
                IsDeleted = true,
                DeleteTime = DateTime.UtcNow,
                CreateBy = "test",
                CreateUserId = 0,
                UpdateUserId = 0,
                CreateTime = DateTime.UtcNow
            },
            new SysDictData
            {
                Id = 3002,
                TenantId = 0,
                DictType = "order_status",
                Label = "Legacy",
                Value = "legacy",
                CreateBy = "test",
                CreateUserId = 0,
                UpdateUserId = 0,
                CreateTime = DateTime.UtcNow
            }
        }).ExecuteCommand();

        var input = new SaveProjectDictionaryDto
        {
            DictType = " order_status ",
            DictName = "订单状态",
            Status = 0,
            Sort = 10,
            Items =
            [
                new SaveProjectDictionaryItemDto
                {
                    Label = "待处理",
                    Value = "pending",
                    Sort = 1
                },
                new SaveProjectDictionaryItemDto
                {
                    Label = "已完成",
                    Value = "completed",
                    Sort = 2
                }
            ]
        };

        var first = await _service.SaveProjectDictionaryAsync(1001, input);
        var second = await _service.SaveProjectDictionaryAsync(1001, input);
        var dictionaries = await _service.GetProjectDictionariesAsync(1001, new ProjectDictionaryQueryDto
        {
            DictType = "order_status",
            IncludeItems = true,
            IncludeDisabled = true
        });

        Assert.False(first.Created);
        Assert.True(first.Restored);
        Assert.Equal(1, first.CreatedItemCount);
        Assert.Equal(1, first.UpdatedItemCount);
        Assert.False(second.Created);
        Assert.False(second.Restored);
        Assert.Equal(0, second.CreatedItemCount);
        Assert.Equal(2, second.UpdatedItemCount);

        var dictionary = Assert.Single(dictionaries);
        Assert.Equal(2001, dictionary.Id);
        Assert.Equal("订单状态", dictionary.DictName);
        Assert.Equal(3, dictionary.ItemCount);
        Assert.Contains(dictionary.Items!, item => item.Id == 3001 && item.Label == "待处理" && !string.IsNullOrEmpty(item.Value));
        Assert.Contains(dictionary.Items!, item => item.Value == "completed" && item.Label == "已完成");
        Assert.Contains(dictionary.Items!, item => item.Value == "legacy");
        Assert.Equal(1, _targetDb.Queryable<SysDictType>().Where(item => item.DictType == "order_status").Count());
        Assert.Equal(3, _targetDb.Queryable<SysDictData>().Where(item => item.DictType == "order_status").Count());
    }

    [Fact]
    public async Task Query_dictionary_is_tenant_scoped_and_can_hide_disabled_rows()
    {
        _targetDb.Insertable(new[]
        {
            CreateType(2101, 0, "enabled", "Enabled", 0),
            CreateType(2102, 0, "disabled", "Disabled", 1),
            CreateType(2103, 8, "tenant_only", "Tenant", 0)
        }).ExecuteCommand();

        var result = await _service.GetProjectDictionariesAsync(1001, new ProjectDictionaryQueryDto
        {
            TenantId = 0,
            IncludeItems = false,
            IncludeDisabled = false
        });

        var dictionary = Assert.Single(result);
        Assert.Equal("enabled", dictionary.DictType);
        Assert.Null(dictionary.Items);
    }

    public void Dispose()
    {
        _targetDb.Ado.Close();
        _metadataDb.Ado.Close();
        _targetDb.Dispose();
        _metadataDb.Dispose();
        DeleteFile(_targetPath);
        DeleteFile(_metadataPath);
    }

    private static ISqlSugarClient CreateDatabase(string path)
    {
        return new SqlSugarClient(new ConnectionConfig
        {
            ConnectionString = $"Data Source={path};Mode=ReadWriteCreate;",
            DbType = DbType.Sqlite,
            IsAutoCloseConnection = true,
            InitKeyType = InitKeyType.Attribute,
            ConfigureExternalServices = SqlSugarSetup.GetConfigureExternalServices(DbType.Sqlite)
        });
    }

    private static void CreateDictionaryTables(ISqlSugarClient db)
    {
        db.Ado.ExecuteCommand("""
            CREATE TABLE sys_dict_types (
                id INTEGER NOT NULL PRIMARY KEY,
                dict_name TEXT NOT NULL,
                dict_type TEXT NOT NULL,
                lang_key TEXT NULL,
                status INTEGER NOT NULL,
                sort INTEGER NOT NULL,
                create_by TEXT NULL,
                create_user_id INTEGER NULL,
                create_time TEXT NOT NULL,
                update_user_id INTEGER NULL,
                update_by TEXT NULL,
                update_time TEXT NULL,
                remark TEXT NULL,
                is_deleted INTEGER NOT NULL,
                delete_by TEXT NULL,
                delete_time TEXT NULL,
                delete_user_id INTEGER NULL,
                tenant_id INTEGER NOT NULL
            );
            """);
        db.Ado.ExecuteCommand("""
            CREATE TABLE sys_dict_datas (
                id INTEGER NOT NULL PRIMARY KEY,
                dict_type TEXT NOT NULL,
                label TEXT NOT NULL,
                value TEXT NOT NULL,
                lang_key TEXT NULL,
                is_default INTEGER NOT NULL,
                status INTEGER NOT NULL,
                sort INTEGER NOT NULL,
                create_by TEXT NULL,
                create_user_id INTEGER NULL,
                create_time TEXT NOT NULL,
                update_user_id INTEGER NULL,
                update_by TEXT NULL,
                update_time TEXT NULL,
                remark TEXT NULL,
                is_deleted INTEGER NOT NULL,
                delete_by TEXT NULL,
                delete_time TEXT NULL,
                delete_user_id INTEGER NULL,
                tenant_id INTEGER NOT NULL
            );
            """);
    }

    private static SysDictType CreateType(long id, long tenantId, string dictType, string dictName, int status)
    {
        return new SysDictType
        {
            Id = id,
            TenantId = tenantId,
            DictType = dictType,
            DictName = dictName,
            Status = status,
            CreateBy = "test",
            CreateUserId = 0,
            UpdateUserId = 0,
            CreateTime = DateTime.UtcNow
        };
    }

    private static void DeleteFile(string path)
    {
        if (!File.Exists(path)) return;
        try
        {
            File.Delete(path);
        }
        catch
        {
            // SQLite can briefly retain a handle after a failed assertion.
        }
    }
}
