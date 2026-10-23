using CodeMaster.Application.Services.CodeGen;
using CodeMaster.Domain.Entities.CodeGen;
using CodeMaster.Infrastructure.Persistence.SqlSugar;
using CodeMaster.UiComposition.Dtos;
using CodeMaster.UiComposition.Entities;
using SqlSugar;
using Yitter.IdGenerator;

namespace CodeMaster.UiComposition.Tests;

public sealed class OpenApiImportServiceTests : IDisposable
{
    private readonly string _databasePath;
    private readonly string _outputRoot;
    private readonly ISqlSugarClient _db;
    private readonly OpenApiImportService _service;

    public OpenApiImportServiceTests()
    {
        _databasePath = Path.Combine(CodeMasterTestPaths.Root, $"codemaster-openapi-{Guid.NewGuid():N}.db");
        _outputRoot = Path.Combine(CodeMasterTestPaths.Root, $"codemaster-openapi-output-{Guid.NewGuid():N}");
        _db = new SqlSugarClient(new ConnectionConfig
        {
            ConnectionString = $"Data Source={_databasePath};Mode=ReadWriteCreate;Pooling=False;",
            DbType = DbType.Sqlite,
            IsAutoCloseConnection = true,
            InitKeyType = InitKeyType.Attribute,
            ConfigureExternalServices = SqlSugarSetup.GetConfigureExternalServices(DbType.Sqlite)
        });
        _db.CodeFirst.InitTables(
            typeof(Project),
            typeof(ProjectModule),
            typeof(ModuleEntity),
            typeof(EntityField),
            typeof(ProjectDataSource),
            typeof(PageDataSourceBinding),
            typeof(ProjectPage),
            typeof(PageComponentBinding),
            typeof(OpenApiDocumentRegistration),
            typeof(OpenApiSchemaBinding),
            typeof(OpenApiMemberBinding));
        YitIdHelper.SetIdGenerator(new IdGeneratorOptions { WorkerId = 43 });
        SeedProject();
        _service = new OpenApiImportService(_db, new TestHttpClientFactory());
    }

    [Fact]
    public void SqlSugarMapping_PreservesExplicitOpenApiColumnNames()
    {
        var documentColumns = _db.DbMaintenance.GetColumnInfosByTableName("sys_openapi_document")
            .Select(item => item.DbColumnName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var dataSourceColumns = _db.DbMaintenance.GetColumnInfosByTableName("sys_project_data_source")
            .Select(item => item.DbColumnName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.Contains("openapi_version", documentColumns);
        Assert.DoesNotContain("open_api_version", documentColumns);
        Assert.Contains("openapi_document_id", dataSourceColumns);
        Assert.Contains("openapi_sync_state", dataSourceColumns);
    }

    public void Dispose()
    {
        _db.Ado.Close();
        _db.Dispose();
        if (File.Exists(_databasePath)) File.Delete(_databasePath);
        if (Directory.Exists(_outputRoot)) Directory.Delete(_outputRoot, recursive: true);
    }

    [Fact]
    public async Task Apply_CreatesMetadataOnlyEntitiesRelationsAndApiDataSourceIdempotently()
    {
        var input = new PreviewOpenApiImportDto
        {
            ProjectId = 1,
            ModuleId = 2,
            Name = "Orders API",
            DocumentContent = Contract(includeLegacyCode: true, idType: "integer")
        };
        var preview = await _service.PreviewAsync(input);

        Assert.Equal(2, preview.Schemas.Count);
        Assert.All(preview.Schemas, item => Assert.Equal(OpenApiSyncState.Added, item.SyncState));
        Assert.Single(preview.Operations);
        Assert.Equal(OpenApiSyncState.Added, preview.Operations[0].SyncState);

        var applied = await _service.ApplyAsync(new ApplyOpenApiImportDto
        {
            ProjectId = input.ProjectId,
            ModuleId = input.ModuleId,
            Name = input.Name,
            DocumentContent = input.DocumentContent,
            ExpectedDocumentHash = preview.DocumentHash,
            ExpectedChangeSetHash = preview.ChangeSetHash
        });

        var entities = await _db.Queryable<ModuleEntity>().Where(item => !item.IsDeleted).ToListAsync();
        Assert.Equal(2, entities.Count);
        Assert.All(entities, entity =>
        {
            Assert.Equal(EntityGenerationMode.MetadataOnly, entity.GenerationMode);
            Assert.False(entity.HasPrimaryKey);
            Assert.True(entity.IsReadOnly);
            Assert.False(entity.HasAudit);
            Assert.False(entity.HasSoftDelete);
            Assert.False(entity.GenerateFrontend);
            Assert.Null(entity.TableName);
        });
        var order = Assert.Single(entities, item => item.Name == "OrderDto");
        var item = Assert.Single(entities, entity => entity.Name == "OrderItemDto");
        var orderFields = await _db.Queryable<EntityField>()
            .Where(field => field.ModuleEntityId == order.Id && !field.IsDeleted)
            .ToListAsync();
        Assert.Contains(orderFields, field => field.Name == "Id" && field.DataType == "int");
        Assert.Contains(orderFields, field => field.Name == "LegacyCode");
        Assert.DoesNotContain(orderFields, field => field.Name == "Items");

        var itemLink = await _db.Queryable<OpenApiMemberBinding>()
            .Where(link => link.MemberName == "Items" && !link.IsDeleted)
            .FirstAsync();
        Assert.Equal(OpenApiMemberKind.Collection, itemLink.MemberKind);
        var targetBinding = await _db.Queryable<OpenApiSchemaBinding>()
            .Where(binding => binding.Id == itemLink.TargetSchemaBindingId)
            .FirstAsync();
        Assert.Equal(item.Id, targetBinding.ModuleEntityId);

        var source = await _db.Queryable<ProjectDataSource>().Where(dataSource => !dataSource.IsDeleted).FirstAsync();
        Assert.Equal(PageDataSourceType.Api, source.SourceType);
        Assert.Equal("getOrder", source.OperationKey);
        Assert.Equal(order.Id, source.ResponseEntityId);
        Assert.Equal("OrderDto", source.DtoName);

        var generation = new OpenApiDtoCodeGenerationExtension(_db);
        var generationContext = new CodeGenerationEntityContext
        {
            EntityId = order.Id,
            ProjectPath = _outputRoot,
            ProjectName = "Cargo",
            ModuleName = "Orders",
            EntityName = "OrderDto"
        };
        var generationError = await Assert.ThrowsAsync<InvalidOperationException>(
            () => generation.GenerateAsync(generationContext));
        Assert.Contains("metadata-only", generationError.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(Path.Combine(_outputRoot, "Cargo.Application")));
        Assert.False(Directory.Exists(Path.Combine(_outputRoot, "Cargo.Domain")));
        Assert.True(await generation.IsOutputCurrentAsync(generationContext));

        var secondPreview = await _service.PreviewAsync(new PreviewOpenApiImportDto
        {
            ProjectId = 1,
            ModuleId = 2,
            DocumentId = applied.DocumentId,
            Name = input.Name,
            DocumentContent = input.DocumentContent
        });
        Assert.Empty(secondPreview.Changes);
        Assert.All(secondPreview.Schemas, schema => Assert.Equal(OpenApiSyncState.Active, schema.SyncState));
        Assert.Equal(OpenApiSyncState.Active, Assert.Single(secondPreview.Operations).SyncState);
    }

    [Fact]
    public async Task Reimport_MarksTypeChangesAndRemovalsThenDeletesOnlyAfterConfirmation()
    {
        var initial = await PreviewAndApplyAsync(Contract(includeLegacyCode: true, idType: "integer"));
        var modifiedContent = Contract(includeLegacyCode: false, idType: "string");
        var preview = await _service.PreviewAsync(new PreviewOpenApiImportDto
        {
            ProjectId = 1,
            ModuleId = 2,
            DocumentId = initial.DocumentId,
            Name = "Orders API",
            DocumentContent = modifiedContent
        });

        Assert.Contains(preview.Changes, item => item.TargetName == "Id" && item.State == OpenApiSyncState.Modified);
        Assert.Contains(preview.Changes, item => item.TargetName == "LegacyCode" && item.State == OpenApiSyncState.PendingDelete);

        await _service.ApplyAsync(new ApplyOpenApiImportDto
        {
            ProjectId = 1,
            ModuleId = 2,
            DocumentId = initial.DocumentId,
            Name = "Orders API",
            DocumentContent = modifiedContent,
            ExpectedDocumentHash = preview.DocumentHash,
            ExpectedChangeSetHash = preview.ChangeSetHash,
            ApplyRemovals = false
        });

        var order = await _db.Queryable<ModuleEntity>().Where(item => item.Name == "OrderDto" && !item.IsDeleted).FirstAsync();
        var idField = await _db.Queryable<EntityField>()
            .Where(item => item.ModuleEntityId == order.Id && item.Name == "Id" && !item.IsDeleted)
            .FirstAsync();
        var legacyField = await _db.Queryable<EntityField>()
            .Where(item => item.ModuleEntityId == order.Id && item.Name == "LegacyCode" && !item.IsDeleted)
            .FirstAsync();
        Assert.Equal("string", idField.DataType);
        Assert.NotNull(legacyField);
        var pending = await _db.Queryable<OpenApiMemberBinding>()
            .Where(item => item.EntityFieldId == legacyField.Id && !item.IsDeleted)
            .FirstAsync();
        Assert.Equal(OpenApiSyncState.PendingDelete, pending.SyncState);

        var removalPreview = await _service.PreviewAsync(new PreviewOpenApiImportDto
        {
            ProjectId = 1,
            ModuleId = 2,
            DocumentId = initial.DocumentId,
            Name = "Orders API",
            DocumentContent = modifiedContent
        });
        await _service.ApplyAsync(new ApplyOpenApiImportDto
        {
            ProjectId = 1,
            ModuleId = 2,
            DocumentId = initial.DocumentId,
            Name = "Orders API",
            DocumentContent = modifiedContent,
            ExpectedDocumentHash = removalPreview.DocumentHash,
            ExpectedChangeSetHash = removalPreview.ChangeSetHash,
            ApplyRemovals = true
        });

        Assert.False(await _db.Queryable<EntityField>()
            .AnyAsync(item => item.Id == legacyField.Id && !item.IsDeleted));
    }

    [Fact]
    public async Task Reimport_CanDetachRemovedFieldAndReattachWhenItReturns()
    {
        var initialContent = Contract(includeLegacyCode: true, idType: "integer");
        var initial = await PreviewAndApplyAsync(initialContent);
        var order = await _db.Queryable<ModuleEntity>()
            .Where(item => item.Name == "OrderDto" && !item.IsDeleted)
            .FirstAsync();
        var legacyField = await _db.Queryable<EntityField>()
            .Where(item => item.ModuleEntityId == order.Id && item.Name == "LegacyCode" && !item.IsDeleted)
            .FirstAsync();
        var originalBinding = await _db.Queryable<OpenApiMemberBinding>()
            .Where(item => item.EntityFieldId == legacyField.Id && !item.IsDeleted)
            .FirstAsync();
        var withoutLegacy = Contract(includeLegacyCode: false, idType: "integer");
        var removalPreview = await _service.PreviewAsync(new PreviewOpenApiImportDto
        {
            ProjectId = 1,
            ModuleId = 2,
            DocumentId = initial.DocumentId,
            Name = "Orders API",
            DocumentContent = withoutLegacy
        });
        var removal = Assert.Single(removalPreview.Changes, item => item.TargetName == "LegacyCode");

        await _service.ApplyAsync(new ApplyOpenApiImportDto
        {
            ProjectId = 1,
            ModuleId = 2,
            DocumentId = initial.DocumentId,
            Name = "Orders API",
            DocumentContent = withoutLegacy,
            ExpectedDocumentHash = removalPreview.DocumentHash,
            ExpectedChangeSetHash = removalPreview.ChangeSetHash,
            DetachChangeKeys = new List<string> { removal.ChangeKey }
        });

        var detachedBinding = await _db.Queryable<OpenApiMemberBinding>()
            .Where(item => item.Id == originalBinding.Id && !item.IsDeleted)
            .FirstAsync();
        Assert.Equal(OpenApiSyncState.Detached, detachedBinding.SyncState);
        Assert.True(await _db.Queryable<EntityField>().AnyAsync(item => item.Id == legacyField.Id && !item.IsDeleted));

        var detachedPreview = await _service.PreviewAsync(new PreviewOpenApiImportDto
        {
            ProjectId = 1,
            ModuleId = 2,
            DocumentId = initial.DocumentId,
            Name = "Orders API",
            DocumentContent = withoutLegacy
        });
        Assert.DoesNotContain(detachedPreview.Changes, item => item.TargetName == "LegacyCode");

        var returnPreview = await _service.PreviewAsync(new PreviewOpenApiImportDto
        {
            ProjectId = 1,
            ModuleId = 2,
            DocumentId = initial.DocumentId,
            Name = "Orders API",
            DocumentContent = initialContent
        });
        Assert.Contains(returnPreview.Changes, item => item.TargetName == "LegacyCode" && item.State == OpenApiSyncState.Added);
        await _service.ApplyAsync(new ApplyOpenApiImportDto
        {
            ProjectId = 1,
            ModuleId = 2,
            DocumentId = initial.DocumentId,
            Name = "Orders API",
            DocumentContent = initialContent,
            ExpectedDocumentHash = returnPreview.DocumentHash,
            ExpectedChangeSetHash = returnPreview.ChangeSetHash
        });

        var reattachedBinding = await _db.Queryable<OpenApiMemberBinding>()
            .Where(item => item.Id == originalBinding.Id && !item.IsDeleted)
            .FirstAsync();
        Assert.Equal(OpenApiSyncState.Active, reattachedBinding.SyncState);
        Assert.Equal(legacyField.Id, reattachedBinding.EntityFieldId);
        Assert.Equal(1, await _db.Queryable<EntityField>()
            .CountAsync(item => item.ModuleEntityId == order.Id && item.Name == "LegacyCode" && !item.IsDeleted));
    }

    [Fact]
    public async Task Reimport_ProtectedRemovalReturnsConflictAndAffectedPageDetails()
    {
        var initial = await PreviewAndApplyAsync(Contract(includeLegacyCode: true, idType: "integer"));
        var source = await _db.Queryable<ProjectDataSource>()
            .Where(item => item.OpenApiDocumentId == initial.DocumentId && !item.IsDeleted)
            .FirstAsync();
        var legacyField = await _db.Queryable<EntityField>()
            .Where(item => item.Name == "LegacyCode" && !item.IsDeleted)
            .FirstAsync();
        var now = DateTime.UtcNow;
        _db.Insertable(new ProjectPage
        {
            Id = 101,
            ProjectId = 1,
            ModuleId = 2,
            TenantId = 8,
            PageKey = "order-workbench",
            PageName = "订单工作台",
            RoutePath = "/orders/workbench",
            SyncMode = PageSyncMode.Auto,
            CreateBy = "test",
            CreateUserId = 0,
            UpdateUserId = 0,
            CreateTime = now
        }).ExecuteCommand();
        _db.Insertable(new PageComponentBinding
        {
            Id = 102,
            PageId = 101,
            ComponentId = "legacyCode",
            BindingId = "binding_legacyCode",
            DataSourceId = source.Id,
            FieldId = legacyField.Id,
            TenantId = 8,
            CreateBy = "test",
            CreateUserId = 0,
            UpdateUserId = 0,
            CreateTime = now
        }).ExecuteCommand();

        var modifiedContent = Contract(includeLegacyCode: false, idType: "integer");
        var preview = await _service.PreviewAsync(new PreviewOpenApiImportDto
        {
            ProjectId = 1,
            ModuleId = 2,
            DocumentId = initial.DocumentId,
            Name = "Orders API",
            DocumentContent = modifiedContent
        });
        var removal = Assert.Single(preview.Changes, item => item.TargetName == "LegacyCode");
        var result = await _service.ApplyAsync(new ApplyOpenApiImportDto
        {
            ProjectId = 1,
            ModuleId = 2,
            DocumentId = initial.DocumentId,
            Name = "Orders API",
            DocumentContent = modifiedContent,
            ExpectedDocumentHash = preview.DocumentHash,
            ExpectedChangeSetHash = preview.ChangeSetHash,
            RemoveChangeKeys = new List<string> { removal.ChangeKey }
        });

        Assert.Equal(1, result.ConflictCount);
        var affected = Assert.Single(result.AffectedPages);
        Assert.Equal(101, affected.PageId);
        Assert.Equal("订单工作台", affected.PageName);
        Assert.Equal(PageSyncStatus.NeedsSync, affected.SyncStatus);
        Assert.True(await _db.Queryable<EntityField>().AnyAsync(item => item.Id == legacyField.Id && !item.IsDeleted));
        var binding = await _db.Queryable<PageComponentBinding>().Where(item => item.Id == 102).FirstAsync();
        Assert.Equal(PageBindingStatus.NeedsReview, binding.BindingStatus);
        var page = await _db.Queryable<ProjectPage>().Where(item => item.Id == 101).FirstAsync();
        Assert.Equal(PageSyncStatus.NeedsSync, page.SyncStatus);
    }

    [Fact]
    public async Task Reimport_DetectsLocalFieldDriftEvenWhenOpenApiDidNotChange()
    {
        var initial = await PreviewAndApplyAsync(Contract(includeLegacyCode: false, idType: "integer"));
        var order = await _db.Queryable<ModuleEntity>().Where(item => item.Name == "OrderDto" && !item.IsDeleted).FirstAsync();
        var field = await _db.Queryable<EntityField>()
            .Where(item => item.ModuleEntityId == order.Id && item.Name == "Id" && !item.IsDeleted)
            .FirstAsync();
        field.DataType = "long";
        await _db.Updateable(field).ExecuteCommandAsync();

        var preview = await _service.PreviewAsync(new PreviewOpenApiImportDto
        {
            ProjectId = 1,
            ModuleId = 2,
            DocumentId = initial.DocumentId,
            Name = "Orders API",
            DocumentContent = Contract(includeLegacyCode: false, idType: "integer")
        });

        Assert.Contains(preview.Changes, item => item.TargetName == "Id" && item.State == OpenApiSyncState.Conflict);
    }

    [Fact]
    public async Task Reimport_CanRemoveAnUnreferencedRecursiveDtoGraph()
    {
        var initial = await PreviewAndApplyAsync(RecursiveContract);
        var preview = await _service.PreviewAsync(new PreviewOpenApiImportDto
        {
            ProjectId = 1,
            ModuleId = 2,
            DocumentId = initial.DocumentId,
            Name = "Orders API",
            DocumentContent = EmptyContract
        });

        await _service.ApplyAsync(new ApplyOpenApiImportDto
        {
            ProjectId = 1,
            ModuleId = 2,
            DocumentId = initial.DocumentId,
            Name = "Orders API",
            DocumentContent = EmptyContract,
            ExpectedDocumentHash = preview.DocumentHash,
            ExpectedChangeSetHash = preview.ChangeSetHash,
            ApplyRemovals = true
        });

        Assert.False(await _db.Queryable<ModuleEntity>().AnyAsync(item => item.Name == "NodeDto" && !item.IsDeleted));
        Assert.False(await _db.Queryable<ProjectDataSource>().AnyAsync(item => item.OpenApiDocumentId == initial.DocumentId && !item.IsDeleted));
    }

    private async Task<OpenApiApplyResultDto> PreviewAndApplyAsync(string content)
    {
        var preview = await _service.PreviewAsync(new PreviewOpenApiImportDto
        {
            ProjectId = 1,
            ModuleId = 2,
            Name = "Orders API",
            DocumentContent = content
        });
        return await _service.ApplyAsync(new ApplyOpenApiImportDto
        {
            ProjectId = 1,
            ModuleId = 2,
            Name = "Orders API",
            DocumentContent = content,
            ExpectedDocumentHash = preview.DocumentHash,
            ExpectedChangeSetHash = preview.ChangeSetHash
        });
    }

    private void SeedProject()
    {
        var now = DateTime.UtcNow;
        _db.Insertable(new Project
        {
            Id = 1,
            TenantId = 8,
            ProjectName = "Cargo",
            DisplayName = "Cargo",
            ConnectionString = "unused",
            ProjectPath = _outputRoot,
            CreateBy = "test",
            CreateUserId = 0,
            UpdateUserId = 0,
            CreateTime = now
        }).ExecuteCommand();
        _db.Insertable(new ProjectModule
        {
            Id = 2,
            TenantId = 8,
            ProjectId = 1,
            ModuleName = "Orders",
            ModuleDescription = "Orders",
            Icon = string.Empty,
            RoutePath = string.Empty,
            Remark = string.Empty,
            CreateBy = "test",
            CreateUserId = 0,
            UpdateUserId = 0,
            CreateTime = now
        }).ExecuteCommand();
    }

    private static string Contract(bool includeLegacyCode, string idType)
    {
        var legacy = includeLegacyCode ? "\"legacyCode\": { \"type\": \"string\" }," : string.Empty;
        return $$"""
        {
          "openapi": "3.0.1",
          "info": { "title": "Orders API", "version": "1" },
          "paths": {
            "/orders/{id}": {
              "get": {
                "operationId": "getOrder",
                "responses": {
                  "200": {
                    "description": "ok",
                    "content": { "application/json": { "schema": { "$ref": "#/components/schemas/Order" } } }
                  }
                }
              }
            }
          },
          "components": {
            "schemas": {
              "Order": {
                "type": "object",
                "required": ["id", "items"],
                "properties": {
                  "id": { "type": "{{idType}}" },
                  {{legacy}}
                  "items": { "type": "array", "items": { "$ref": "#/components/schemas/OrderItem" } }
                }
              },
              "OrderItem": {
                "type": "object",
                "properties": { "name": { "type": "string" } }
              }
            }
          }
        }
        """;
    }

    private const string RecursiveContract = """
    {
      "openapi": "3.0.1",
      "info": { "title": "Recursive", "version": "1" },
      "paths": {
        "/node": {
          "get": {
            "operationId": "getNode",
            "responses": {
              "200": {
                "description": "ok",
                "content": { "application/json": { "schema": { "$ref": "#/components/schemas/Node" } } }
              }
            }
          }
        }
      },
      "components": {
        "schemas": {
          "Node": {
            "type": "object",
            "properties": { "child": { "$ref": "#/components/schemas/Node" } }
          }
        }
      }
    }
    """;

    private const string EmptyContract = """
    {
      "openapi": "3.0.1",
      "info": { "title": "Recursive", "version": "2" },
      "paths": {},
      "components": { "schemas": {} }
    }
    """;

    private sealed class TestHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
