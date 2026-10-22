using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System;
using System.IO;
using CodeMaster.Application.Services.CodeGen;
using CodeMaster.CodeGenerator.Tests.Fixtures;
using CodeMaster.Domain.Entities.CodeGen;
using CodeMaster.Infrastructure.Persistence.SqlSugar;
using SqlSugar;
using Xunit;

namespace CodeMaster.CodeGenerator.Tests.Services;

public class CodeGeneratorServiceTests
{
    private readonly CodeGeneratorService _service;

    public CodeGeneratorServiceTests()
    {
        // Pass null for ISqlSugarClient since we are testing methods that don't hit the DB
        _service = new CodeGeneratorService(null!);
    }

    [Fact]
    public void ResolveComposableImportCollisions_ShouldAliasVueRouterHook()
    {
        var section = new CodeMaster.Application.ScriptBuilder.ScriptSection
        {
            Imports =
            {
                new CodeMaster.Application.ScriptBuilder.ImportInfo
                {
                    From = "vue-router",
                    Mode = "named",
                    Names = "useRouter, useRoute"
                }
            },
            Refs =
            {
                new CodeMaster.Application.ScriptBuilder.RefInfo
                {
                    Name = "route",
                    Value = "useRoute()"
                }
            }
        };

        TemplateCodeGenerator.ResolveComposableImportCollisions(section, "Route");

        Assert.Equal("useRouter, useRoute as useRouteHook", section.Imports[0].Names);
        Assert.Equal("useRouteHook()", section.Refs[0].Value);
    }

    [Fact]
    public async Task GenerateServiceTemplates_ShouldAliasEntityType_WhenModuleNameMatchesEntityName()
    {
        var (db, dbFile) = CreateMetadataDb();
        try
        {
            const long entityId = 99001;
            db.Insertable(new ModuleEntity
            {
                Id = entityId,
                ProjectId = 1,
                ModuleId = 2,
                Name = "BillOfLading",
                Description = "Bill of lading",
                HasPrimaryKey = true,
                CreateBy = "test",
                UpdateUserId = 0
            }).ExecuteCommand();
            db.Insertable(new[]
            {
                SystemField(99002, entityId, "Id", "long", isPrimaryKey: true),
                new EntityField
                {
                    Id = 99003,
                    ModuleEntityId = entityId,
                    Name = "BlNo",
                    Description = "Bill number",
                    DataType = "string",
                    ShowInList = true,
                    CreateBy = "test",
                    UpdateUserId = 0
                }
            }).ExecuteCommand();

            var generator = new CodeGeneratorService(db);
            var serviceCode = await generator.GenerateServiceImplementationAsync(
                entityId,
                "Demo",
                "BillOfLading");
            var interfaceCode = await generator.GenerateServiceInterfaceAsync(
                entityId,
                "Demo",
                "BillOfLading");

            const string alias =
                "using GeneratedEntity = Demo.Domain.Entities.BillOfLading.BillOfLading;";
            Assert.Contains(alias, serviceCode);
            Assert.Contains(alias, interfaceCode);
            Assert.Contains("CrudApplicationService<GeneratedEntity,", serviceCode);
            Assert.Contains("ICrudApplicationService<GeneratedEntity,", interfaceCode);
        }
        finally
        {
            db.Dispose();
            TryDelete(dbFile);
        }
    }

    [Fact]
    public void CalculateInterfaceList_ShouldReturnCorrectInterfaces_ForFullEntity()
    {
        // Arrange
        var entity = MockEntityData.GetFullFeatureEntity();

        // Act
        var result = _service.CalculateInterfaceList(entity);

        // Assert
        Assert.Contains("IEntity", result);
        Assert.Contains("ITenant", result);
        Assert.Contains("IDept", result);
        Assert.Contains("IAuditEntity", result);
        Assert.Contains("ISoftDelete", result);
        Assert.DoesNotContain("ITree", result);
        Assert.Equal(", IEntity, ITenant, IDept, IAuditEntity, ISoftDelete", result);
    }

    [Fact]
    public void BuildTemplateContext_ShouldMapFieldsCorrectly()
    {
        // Arrange
        var entity = MockEntityData.GetFullFeatureEntity();
        var fields = MockEntityData.GetFullFeatureEntityFields();
        var relations = MockEntityData.GetEmptyRelations();

        var (db, dbFile) = CreateMetadataDb();
        try
        {
            SeedRelatedEntityMetadata(db);
            var service = new CodeGeneratorService(db);

            // Act
            var scriptObject = service.BuildTemplateContext(entity, fields, relations, "CodeMaster.Tests", "CodeGen");

            // Assert
            var hasEntity = scriptObject.Contains("entity");
            Assert.True(hasEntity);
        
            var entityDict = (IDictionary<string, object?>)scriptObject["entity"];
            Assert.Equal("employee", entityDict["name_lower"]);
            Assert.Equal("business_employee", entityDict["table_name"]);
            Assert.True((bool)entityDict["has_tenant"]);
            Assert.False((bool)entityDict["is_tree"]);

            // Check fields
            var fieldsList = (List<Dictionary<string, object?>>)entityDict["fields"];
            Assert.Equal(2, fieldsList.Count);
        
            var firstField = fieldsList[0];
            Assert.Equal("Name", firstField["name"]);
            Assert.Equal("input", firstField["form_control_type"]);
        
            var secondField = fieldsList[1];
            Assert.Equal("cascader", secondField["form_control_type"]);
            Assert.Equal("Department", secondField["related_entity_name"]);
            Assert.Equal("name", secondField["related_display_label"]); // Should be parsed to camelCase 'name'
        }
        finally
        {
            db.Dispose();
            TryDelete(dbFile);
        }
    }

    [Theory]
    [InlineData("datetime", "DateTime")]
    [InlineData("DATETIME?", "DateTime?")]
    [InlineData("boolean", "bool")]
    [InlineData("uuid", "Guid")]
    [InlineData("text", "string")]
    [InlineData("CustomStatus", "CustomStatus")]
    public void CSharpDataTypeNormalizer_ShouldCanonicalizeAliases(string input, string expected)
    {
        Assert.Equal(expected, CSharpDataTypeNormalizer.Normalize(input));
    }

    [Theory]
    [InlineData("OrderManagement", true)]
    [InlineData("MES", true)]
    [InlineData("Inventory2", true)]
    [InlineData("订单管理", false)]
    [InlineData("orderManagement", false)]
    [InlineData("Order Management", false)]
    [InlineData("Order-Management", false)]
    [InlineData("", false)]
    public void CSharpModuleNameValidator_ShouldRequireAsciiPascalCase(string input, bool expected)
    {
        Assert.Equal(expected, CSharpModuleNameValidator.IsValid(input));
    }

    [Fact]
    public async Task Generation_ShouldRenderLegacyLowercaseDateTimeAsValidCSharp()
    {
        var (db, dbFile) = CreateMetadataDb();
        try
        {
            const long entityId = 90;
            db.Insertable(new ModuleEntity
            {
                Id = entityId,
                ProjectId = 1,
                ModuleId = 1,
                Name = "LegacyOrder",
                Description = "Legacy order",
                HasPrimaryKey = true,
                CreateBy = "test",
                UpdateUserId = 0
            }).ExecuteCommand();
            db.Insertable(new[]
            {
                SystemField(91, entityId, "Id", "long", isPrimaryKey: true),
                new EntityField
                {
                    Id = 92,
                    ModuleEntityId = entityId,
                    Name = "OrderDate",
                    Description = "Order date",
                    DataType = "datetime",
                    IsRequired = true,
                    ShowInList = true,
                    ShowInAddForm = true,
                    ShowInEditForm = true,
                    FormControlType = "datetime",
                    CreateBy = "test",
                    UpdateUserId = 0
                }
            }).ExecuteCommand();

            var generator = new CodeGeneratorService(db);
            var entityCode = await generator.GenerateEntityAutoAsync(entityId, "Demo", "Orders");
            var dtoCode = await generator.GenerateDtoAsync(entityId, "Demo", "Orders");

            Assert.Contains("public DateTime OrderDate", entityCode);
            Assert.Contains("public DateTime OrderDate", dtoCode);
            Assert.DoesNotContain("public datetime OrderDate", entityCode);
            Assert.DoesNotContain("public datetime OrderDate", dtoCode);
        }
        finally
        {
            db.Dispose();
            TryDelete(dbFile);
        }
    }

    [Fact]
    public async Task Generation_ShouldUseTypedRangesForOneToManyLongIds()
    {
        var (db, dbFile) = CreateMetadataDb();
        try
        {
            const long orderId = 200;
            const long orderItemId = 201;

            db.Insertable(new[]
            {
                new ModuleEntity
                {
                    Id = orderId,
                    ProjectId = 1,
                    ModuleId = 1,
                    Name = "Order",
                    Description = "Order",
                    HasPrimaryKey = true,
                    CreateBy = "test",
                    UpdateUserId = 0
                },
                new ModuleEntity
                {
                    Id = orderItemId,
                    ProjectId = 1,
                    ModuleId = 1,
                    Name = "OrderItem",
                    Description = "Order item",
                    HasPrimaryKey = true,
                    CreateBy = "test",
                    UpdateUserId = 0
                }
            }).ExecuteCommand();

            db.Insertable(new[]
            {
                SystemField(202, orderId, "Id", "long", isPrimaryKey: true),
                SystemField(203, orderItemId, "Id", "long", isPrimaryKey: true),
                new EntityField
                {
                    Id = 204,
                    ModuleEntityId = orderItemId,
                    Name = "OrderId",
                    Description = "Order",
                    DataType = "long",
                    IsRequired = true,
                    MinValue = "1",
                    ShowInAddForm = true,
                    ShowInEditForm = true,
                    CreateBy = "test",
                    UpdateUserId = 0
                },
                new EntityField
                {
                    Id = 205,
                    ModuleEntityId = orderItemId,
                    Name = "ProductId",
                    Description = "Product",
                    DataType = "long?",
                    IsNullable = true,
                    MinValue = "1",
                    ShowInAddForm = true,
                    ShowInEditForm = true,
                    CreateBy = "test",
                    UpdateUserId = 0
                }
            }).ExecuteCommand();

            db.Insertable(new OneToManyRelation
            {
                Id = 206,
                ModuleEntityId = orderId,
                MasterField = "Id",
                ChildEntityId = orderItemId,
                ChildEntityName = "OrderItem",
                ChildForeignKey = "OrderId",
                CreateBy = "test",
                UpdateUserId = 0
            }).ExecuteCommand();

            var generator = new CodeGeneratorService(db);
            var orderDtoCode = await generator.GenerateDtoAsync(orderId, "Demo", "Orders");
            var orderItemDtoCode = await generator.GenerateDtoAsync(orderItemId, "Demo", "Orders");

            // 同上：Create DTO 的子表集合也带了 global:: 前缀。
            Assert.Contains(
                "public List<global::Demo.Application.Dtos.Orders.CreateOrderItemDto>? OrderItems",
                orderDtoCode);
            Assert.Contains(
                "[Range(typeof(long), \"1\", \"9223372036854775807\")]",
                orderItemDtoCode);
            Assert.DoesNotContain("[Range(1, int.MaxValue)]", orderItemDtoCode);

            var generatedRange = new RangeAttribute(
                typeof(long),
                "1",
                "9223372036854775807");
            Assert.True(generatedRange.IsValid(2_078_909_676_682_088_448L));
        }
        finally
        {
            db.Dispose();
            TryDelete(dbFile);
        }
    }

    [Fact]
    public async Task EntityGeneration_ShouldFullyQualifyCrossModuleOneToManyNavigationTypes()
    {
        var (db, dbFile) = CreateMetadataDb();
        try
        {
            db.Insertable(new[]
            {
                new ProjectModule
                {
                    Id = 10, ProjectId = 1, ModuleName = "IssueTracking", ModuleDescription = "Issue tracking",
                    Icon = "folder", RoutePath = "/issues", Remark = string.Empty, CreateBy = "test", UpdateUserId = 0
                },
                new ProjectModule
                {
                    Id = 11, ProjectId = 1, ModuleName = "Collaboration", ModuleDescription = "Collaboration",
                    Icon = "folder", RoutePath = "/collaboration", Remark = string.Empty, CreateBy = "test", UpdateUserId = 0
                }
            }).ExecuteCommand();
            db.Insertable(new[]
            {
                new ModuleEntity
                {
                    Id = 100, ProjectId = 1, ModuleId = 10, Name = "Issue", Description = "Issue",
                    HasPrimaryKey = true, CreateBy = "test", UpdateUserId = 0
                },
                new ModuleEntity
                {
                    Id = 101, ProjectId = 1, ModuleId = 11, Name = "Comment", Description = "Comment",
                    HasPrimaryKey = true, CreateBy = "test", UpdateUserId = 0
                }
            }).ExecuteCommand();
            db.Insertable(new[]
            {
                SystemField(102, 100, "Id", "long", isPrimaryKey: true),
                SystemField(103, 101, "Id", "long", isPrimaryKey: true),
                new EntityField
                {
                    Id = 104, ModuleEntityId = 101, Name = "IssueId", Description = "Issue",
                    DataType = "long", IsRequired = true, CreateBy = "test", UpdateUserId = 0
                }
            }).ExecuteCommand();
            db.Insertable(new OneToManyRelation
            {
                Id = 105, ModuleEntityId = 100, MasterField = "Id", ChildEntityId = 101,
                ChildEntityName = "Comment", ChildForeignKey = "IssueId", CreateBy = "test", UpdateUserId = 0
            }).ExecuteCommand();

            var generator = new CodeGeneratorService(db);
            var entityCode = await generator.GenerateEntityAutoAsync(100, "Demo", "IssueTracking");
            var dtoCode = await generator.GenerateDtoAsync(100, "Demo", "IssueTracking");
            var serviceCode = await generator.GenerateServiceImplementationAsync(100, "Demo", "IssueTracking");

            // 跨模块导航类型在模板里统一加了 global:: 前缀（模块名与命名空间段撞名时
            // 不加会 CS0104 引用不明确）。这里断言的是加前缀后的最终产物。
            Assert.Contains(
                "nameof(global::Demo.Domain.Entities.Collaboration.Comment.IssueId)",
                entityCode);
            Assert.Contains(
                "List<global::Demo.Domain.Entities.Collaboration.Comment>? Comments",
                entityCode);
            Assert.Contains(
                "List<global::Demo.Application.Dtos.Collaboration.CommentDto>? Comments",
                dtoCode);
            Assert.Contains(
                "List<global::Demo.Application.Dtos.Collaboration.CreateCommentDto>? Comments",
                dtoCode);
            Assert.Contains(
                "List<global::Demo.Application.Dtos.Collaboration.UpdateCommentDto>? Comments",
                dtoCode);
            Assert.Contains(
                "item.Adapt<global::Demo.Domain.Entities.Collaboration.Comment>()",
                serviceCode);
            Assert.Contains(
                "List<global::Demo.Application.Dtos.Collaboration.UpdateCommentDto> items",
                serviceCode);
            Assert.Contains(
                "_db.Deleteable<global::Demo.Domain.Entities.Collaboration.Comment>()",
                serviceCode);
        }
        finally
        {
            db.Dispose();
            TryDelete(dbFile);
        }
    }

    [Theory]
    [InlineData("Order", "orders")]
    [InlineData("OrderItem", "order_items")]
    [InlineData("Address", "address")]
    [InlineData("HTTPLog", "http_logs")]
    public void BuildTemplateContext_ShouldUseUnprefixedDefaultTableName(
        string entityName,
        string expectedTableName)
    {
        var entity = new ModuleEntity
        {
            Name = entityName,
            Description = entityName,
            TableName = null
        };

        var context = _service.BuildTemplateContext(
            entity,
            new List<EntityField>(),
            new List<OneToManyRelation>(),
            "Demo",
            "Sales");

        var entityContext = (IDictionary<string, object?>)context["entity"];
        Assert.Equal(expectedTableName, entityContext["table_name"]);
    }

    [Theory]
    [InlineData(true, false, "ReadOnlyApplicationService", "IReadOnlyApplicationService", true)]
    [InlineData(false, false, "QueryApplicationService", "IQueryApplicationService", false)]
    [InlineData(true, true, "ReadOnlyTreeApplicationService", "IReadOnlyTreeApplicationService", true)]
    public async Task ReadOnlyGeneration_ShouldFollowPrimaryKeyAndTreeCapabilities(
        bool hasPrimaryKey,
        bool isTree,
        string expectedServiceBase,
        string expectedServiceContract,
        bool expectsGetById)
    {
        var (db, dbFile) = CreateMetadataDb();
        try
        {
            const long entityId = 100;
            db.Insertable(new ModuleEntity
            {
                Id = entityId,
                ProjectId = 1,
                ModuleId = 1,
                Name = "ReadonlyReport",
                Description = "Readonly report",
                TableName = "readonly_report",
                HasPrimaryKey = hasPrimaryKey,
                IsTree = isTree,
                IsReadOnly = true,
                HasAudit = false,
                HasSoftDelete = false,
                GenerateFrontend = true,
                CreateBy = "test",
                UpdateUserId = 0
            }).ExecuteCommand();

            var fields = new List<EntityField>();
            if (hasPrimaryKey)
            {
                fields.Add(new EntityField
                {
                    Id = 101,
                    ModuleEntityId = entityId,
                    Name = "Id",
                    Description = "Primary key",
                    DataType = "long",
                    IsPrimaryKey = true,
                    IsRequired = true,
                    IsSystemField = true,
                    CreateBy = "test",
                    UpdateUserId = 0
                });
            }
            if (isTree)
            {
                fields.Add(new EntityField { Id = 102, ModuleEntityId = entityId, Name = "ParentId", Description = "Parent", DataType = "long?", IsNullable = true, IsSystemField = true, CreateBy = "test", UpdateUserId = 0 });
                fields.Add(new EntityField { Id = 103, ModuleEntityId = entityId, Name = "Ancestors", Description = "Ancestors", DataType = "string?", IsNullable = true, IsSystemField = true, CreateBy = "test", UpdateUserId = 0 });
            }
            fields.Add(new EntityField
            {
                Id = 104,
                ModuleEntityId = entityId,
                Name = "Name",
                Description = "Name",
                DataType = "string",
                ShowInList = true,
                ShowInSearch = true,
                ShowInDetail = true,
                CreateBy = "test",
                UpdateUserId = 0
            });
            db.Insertable(fields).ExecuteCommand();

            var generator = new CodeGeneratorService(db);
            var entityCode = await generator.GenerateEntityAutoAsync(entityId, "Demo", "Reports");
            var dtoCode = await generator.GenerateDtoAsync(entityId, "Demo", "Reports");
            var serviceCode = await generator.GenerateServiceImplementationAsync(entityId, "Demo", "Reports");
            var serviceInterfaceCode = await generator.GenerateServiceInterfaceAsync(entityId, "Demo", "Reports");
            var apiCode = await generator.GenerateFrontendApiAsync(entityId, "Demo", "Reports");

            Assert.Contains("/reports/readonlyreport/getlist", apiCode);
            Assert.DoesNotContain("/reports/readonlyReport/getlist", apiCode);

            Assert.Contains($"public class ReadonlyReportService : {expectedServiceBase}<", serviceCode);
            Assert.Contains(expectedServiceContract, serviceInterfaceCode);
            Assert.DoesNotContain("CreateReadonlyReportDto", dtoCode);
            Assert.DoesNotContain("UpdateReadonlyReportDto", dtoCode);
            Assert.Contains("[SugarTable(\"readonly_report\")]", entityCode);
            Assert.Contains("public partial class ReadonlyReport : IBaseEntity", entityCode);

            if (hasPrimaryKey)
            {
                Assert.Contains("IEntity", entityCode);
                Assert.Contains("public long Id", entityCode);
            }
            else
            {
                Assert.DoesNotContain("IEntity", entityCode);
                Assert.DoesNotContain("public long Id", entityCode);
            }

            if (expectsGetById)
            {
                Assert.Contains("export function getById", apiCode);
                if (isTree || hasPrimaryKey)
                    Assert.Contains("export function getDetail", apiCode);
            }
            else
                Assert.DoesNotContain("export function getById", apiCode);

            Assert.Contains("export function getOptionList", apiCode);

            Assert.DoesNotContain("export function create", apiCode);
            Assert.DoesNotContain("export function update", apiCode);
            Assert.DoesNotContain("export function deleteById", apiCode);
        }
        finally
        {
            db.Dispose();
            TryDelete(dbFile);
        }
    }

    [Fact]
    public void ViewEntityGeneration_EmitsExplicitViewMarker()
    {
        var (db, dbFile) = CreateMetadataDb();
        try
        {
            var entity = new ModuleEntity
            {
                ProjectId = 1,
                ModuleId = 1,
                Name = "OrderAnalysis",
                Description = "Order analysis",
                TableName = "order_analysis",
                HasPrimaryKey = true,
                IsReadOnly = true,
                HasAudit = false,
                HasSoftDelete = false,
                GenerationMode = EntityGenerationMode.FullStack
            };

            var generator = new CodeGeneratorService(db);
            var context = generator.BuildTemplateContext(
                entity,
                new List<EntityField>(),
                new List<OneToManyRelation>(),
                "Demo",
                "Reports");

            var entityContext = (IDictionary<string, object?>)context["entity"];
            Assert.Contains("IViewEntity", Assert.IsType<string>(entityContext["interface_list"]));
        }
        finally
        {
            db.Dispose();
            TryDelete(dbFile);
        }
    }

    [Fact]
    public async Task EntityGeneration_ShouldComposeCapabilityInterfacesAndPhysicalSystemFields()
    {
        var (db, dbFile) = CreateMetadataDb();
        try
        {
            const long entityId = 200;
            db.Insertable(new ModuleEntity
            {
                Id = entityId,
                ProjectId = 1,
                ModuleId = 1,
                Name = "AuditedRecord",
                Description = "Audited record",
                HasPrimaryKey = true,
                HasTenant = true,
                HasDataPermission = true,
                HasAudit = true,
                HasSoftDelete = true,
                CreateBy = "test",
                UpdateUserId = 0
            }).ExecuteCommand();
            db.Insertable(new[]
            {
                SystemField(201, entityId, "Id", "long", isPrimaryKey: true),
                SystemField(202, entityId, "TenantId", "long"),
                SystemField(203, entityId, "DeptId", "long?", isNullable: true),
                SystemField(204, entityId, "DeptAncestors", "string?", isNullable: true),
                SystemField(205, entityId, "CreateUserId", "long?", isNullable: true),
                SystemField(206, entityId, "CreateBy", "string?", isNullable: true),
                SystemField(207, entityId, "CreateTime", "DateTime", defaultValue: "DateTime.UtcNow"),
                SystemField(208, entityId, "UpdateUserId", "long?", isNullable: true),
                SystemField(209, entityId, "UpdateBy", "string?", isNullable: true),
                SystemField(210, entityId, "UpdateTime", "DateTime?", isNullable: true),
                SystemField(211, entityId, "IsDeleted", "bool"),
                SystemField(212, entityId, "DeleteTime", "DateTime?", isNullable: true),
                SystemField(213, entityId, "DeleteBy", "string?", isNullable: true),
                SystemField(214, entityId, "DeleteUserId", "long?", isNullable: true)
            }).ExecuteCommand();

            var code = await new CodeGeneratorService(db).GenerateEntityAutoAsync(entityId, "Demo", "Records");

            Assert.Contains("[SugarTable(\"audited_records\")]", code);
            Assert.Contains("public partial class AuditedRecord : IBaseEntity, IEntity, ITenant, IDept, IAuditEntity, ISoftDelete", code);
            Assert.Contains("public long Id", code);
            Assert.Contains("public DateTime CreateTime { get; set; } = DateTime.UtcNow;", code);
            Assert.Contains("public bool IsDeleted", code);
            Assert.DoesNotContain("EntityBase", code);
        }
        finally
        {
            db.Dispose();
            TryDelete(dbFile);
        }
    }

    [Fact]
    public async Task EntityGeneration_ShouldApplyFieldStorageMetadata()
    {
        var (db, dbFile) = CreateMetadataDb();
        try
        {
            const long entityId = 220;
            db.Insertable(new ModuleEntity
            {
                Id = entityId,
                ProjectId = 1,
                ModuleId = 1,
                Name = "StorageMetadata",
                Description = "Storage metadata",
                HasPrimaryKey = true,
                CreateBy = "test",
                UpdateUserId = 0
            }).ExecuteCommand();
            db.Insertable(new[]
            {
                SystemField(221, entityId, "Id", "long", isPrimaryKey: true),
                new EntityField
                {
                    Id = 222,
                    ModuleEntityId = entityId,
                    Name = "Code",
                    Description = "Code",
                    DataType = "string",
                    MaxLength = 64,
                    IsRequired = true,
                    DefaultValue = "   ",
                    CreateBy = "test",
                    UpdateUserId = 0
                },
                new EntityField
                {
                    Id = 223,
                    ModuleEntityId = entityId,
                    Name = "Amount",
                    Description = "Amount",
                    DataType = "decimal",
                    Precision = 18,
                    Scale = 2,
                    CreateBy = "test",
                    UpdateUserId = 0
                },
                new EntityField
                {
                    Id = 224,
                    ModuleEntityId = entityId,
                    Name = "DisplayText",
                    Description = "Display text",
                    DataType = "string",
                    IsIgnore = true,
                    DefaultValue = "planned",
                    CreateBy = "test",
                    UpdateUserId = 0
                }
            }).ExecuteCommand();

            var code = await new CodeGeneratorService(db).GenerateEntityAutoAsync(entityId, "Demo", "Records");

            Assert.Contains("[SugarColumn(ColumnName = \"code\", Length = 64)]", code);
            Assert.Contains("public string Code { get; set; } = default!;", code);
            Assert.DoesNotContain("= ;", code);
            Assert.Contains("public string DisplayText { get; set; } = @\"planned\";", code);
            Assert.Contains("[SugarColumn(ColumnName = \"amount\", Length = 18, DecimalDigits = 2)]", code);
            Assert.Contains("[SugarColumn(IsIgnore = true)]", code);
        }
        finally
        {
            db.Dispose();
            TryDelete(dbFile);
        }
    }

    [Fact]
    public void StaticChoiceControls_ShouldNormalizeOptionsAndUseChoiceDetailTemplate()
    {
        var optionsMethod = typeof(TemplateCodeGenerator).GetMethod(
            "BuildStaticChoiceOptionsLiteral",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
        var mapMethod = typeof(TemplateCodeGenerator).GetMethod(
            "MapControlType",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);

        Assert.NotNull(optionsMethod);
        Assert.NotNull(mapMethod);
        var options = (string)optionsMethod!.Invoke(null, new object?[] { "Pending,Paid" })!;
        var detailControl = (string)mapMethod!.Invoke(null, new object[] { "radio-group", "detail", "string" })!;

        Assert.Contains("\"label\":\"Pending\"", options);
        Assert.Contains("\"value\":\"Paid\"", options);
        Assert.Equal("select", detailControl);
    }

    [Fact]
    public async Task BuildTemplateContext_ShouldDescribeRemoteSelectBatchEndpoints()
    {
        var (db, dbFile) = CreateMetadataDb();
        try
        {
            db.Insertable(new ProjectModule
            {
                Id = 2,
                ProjectId = 1,
                ModuleName = "Catalog",
                ModuleDescription = "Catalog",
                Icon = "folder",
                RoutePath = "/catalog",
                LastSyncTime = DateTime.UtcNow,
                Remark = string.Empty,
                CreateBy = "test",
                UpdateUserId = 0
            }).ExecuteCommand();
            db.Insertable(new ModuleEntity
            {
                Id = 2,
                ProjectId = 1,
                ModuleId = 2,
                Name = "Category",
                Description = "Category",
                HasPrimaryKey = true,
                TableName = "catalog_category",
                CreateBy = "test",
                UpdateUserId = 0
            }).ExecuteCommand();
            db.Insertable(new[]
            {
                SystemField(201, 2, "Id", "long", isPrimaryKey: true),
                new EntityField
                {
                    Id = 202,
                    ModuleEntityId = 2,
                    Name = "Name",
                    Description = "Name",
                    DataType = "string",
                    IsRequired = true,
                    CreateBy = "test",
                    UpdateUserId = 0
                },
                new EntityField
                {
                    Id = 203,
                    ModuleEntityId = 2,
                    Name = "ParentCode",
                    Description = "Parent code",
                    DataType = "string",
                    IsNullable = true,
                    CreateBy = "test",
                    UpdateUserId = 0
                }
            }).ExecuteCommand();

            var entity = MockEntityData.GetFullFeatureEntity();
            entity.CreateBy = "test";
            entity.UpdateUserId = 0;
            var fields = new List<EntityField>
            {
                new EntityField
                {
                    Id = 300,
                    ModuleEntityId = entity.Id,
                    Name = "ProvinceCode",
                    Description = "Province",
                    DataType = "string",
                    FormControlType = "input",
                    CreateBy = "test",
                    UpdateUserId = 0,
                    OrderNum = 1
                },
                new EntityField
                {
                    Id = 301,
                    ModuleEntityId = entity.Id,
                    Name = "CategoryId",
                    Description = "Category",
                    DataType = "long",
                    FormControlType = "select",
                    SelectDataSource = "api",
                    SelectOptions = "{\"loadMode\":\"batch\",\"parentField\":\"ProvinceCode\",\"parentValueField\":\"ParentCode\"}",
                    RelatedEntityName = "Category",
                    RelatedEntityIdField = "Id",
                    RelatedEntityDisplayFields = "[\"Name\"]",
                    ShowInList = true,
                    ShowInDetail = true,
                    ShowInAddForm = true,
                    ShowInEditForm = true,
                    OrderNum = 2,
                    CreateBy = "test",
                    UpdateUserId = 0
                }
            };

            db.Insertable(entity).ExecuteCommand();
            db.Insertable(fields).ExecuteCommand();

            var context = new CodeGeneratorService(db).BuildTemplateContext(
                entity,
                fields,
                new List<OneToManyRelation>(),
                "Demo",
                "Catalog");
            var entityContext = (IDictionary<string, object?>)context["entity"];
            var remoteFields = Assert.IsType<List<Dictionary<string, object?>>>(entityContext["remote_select_fields"]);
            var remote = Assert.Single(remoteFields);

            Assert.Equal("Category", remote["source_entity_name"]);
            Assert.Equal("Id", remote["source_value_field"]);
            Assert.Equal("Name", remote["source_label_field"]);
            Assert.Equal("ParentCode", remote["source_parent_field"]);
            Assert.Equal("GetCategoryIdOptionsAsync", remote["options_method_name"]);
            Assert.Equal("GetCategoryIdLabelsAsync", remote["labels_method_name"]);
            Assert.Equal("_remoteDb", entityContext["remote_db_variable"]);

            var serviceCode = await new CodeGeneratorService(db)
                .GenerateServiceImplementationAsync(entity.Id, "Demo", "Catalog");
            Assert.Contains("GetCategoryIdOptionsAsync", serviceCode);
            Assert.Contains("GetCategoryIdLabelsAsync", serviceCode);
            Assert.Contains("using Demo.Core.DynamicApi;", serviceCode);
            Assert.Contains("[DynamicApi(HttpMethod = \"POST\")]", serviceCode);
            Assert.Contains("typedValues.Contains(item.Id)", serviceCode);
            Assert.Contains("item.ParentCode == typedParentValue", serviceCode);
            Assert.Contains("await ValidateRemoteReferencesAsync(entity);", serviceCode);
            Assert.Contains("ValidateCategoryIdReferenceAsync(entity.CategoryId)", serviceCode);
            Assert.Contains("AnyAsync(item => item.Id == value && !item.IsDeleted)", serviceCode);
            Assert.Contains("throw new BusinessException(400, \"CategoryId references a missing Category record.\")", serviceCode);
            Assert.Contains("CategoryId references a missing Category record.", serviceCode);
        }
        finally
        {
            db.Dispose();
            TryDelete(dbFile);
        }
    }

    private static EntityField SystemField(
        long id,
        long entityId,
        string name,
        string dataType,
        bool isPrimaryKey = false,
        bool isNullable = false,
        string? defaultValue = null) => new()
    {
        Id = id,
        ModuleEntityId = entityId,
        Name = name,
        Description = name,
        DataType = dataType,
        IsPrimaryKey = isPrimaryKey,
        IsRequired = isPrimaryKey,
        IsNullable = isNullable,
        IsSystemField = true,
        DefaultValue = defaultValue,
        CreateBy = "test",
        UpdateUserId = 0
    };

    internal static (ISqlSugarClient Db, string DbFile) CreateMetadataDb()
    {
        var dbFile = Path.Combine(CodeMasterTestPaths.Root, "CodeMaster_CodeGenTest_" + Guid.NewGuid().ToString("N") + ".db");
        var db = new SqlSugarClient(new ConnectionConfig
        {
            ConnectionString = $"Data Source={dbFile};Mode=ReadWriteCreate;",
            DbType = DbType.Sqlite,
            IsAutoCloseConnection = true,
            InitKeyType = InitKeyType.Attribute,
            ConfigureExternalServices = SqlSugarSetup.GetConfigureExternalServices(DbType.Sqlite)
        });
        db.CodeFirst.InitTables(
            typeof(ModuleEntity),
            typeof(EntityField),
            typeof(ProjectModule),
            typeof(OneToManyRelation),
            typeof(EntityRelation));
        return (db, dbFile);
    }

    internal static void SeedRelatedEntityMetadata(ISqlSugarClient db)
    {
        db.Insertable(new ProjectModule
        {
            Id = 2,
            ProjectId = 1,
            ModuleName = "Organization",
            ModuleDescription = "Organization",
            Icon = "folder",
            RoutePath = "/organization",
            LastSyncTime = DateTime.UtcNow,
            Remark = string.Empty,
            CreateBy = "test",
            UpdateUserId = 0
        }).ExecuteCommand();

        db.Insertable(new ModuleEntity
        {
            Id = 2,
            ProjectId = 1,
            ModuleId = 2,
            Name = "Department",
            Description = "Department",
            TableName = "sys_department",
            CreateBy = "test",
            UpdateUserId = 0
        }).ExecuteCommand();
    }

    internal static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // Best-effort cleanup for Windows file handles released after test process exits.
        }
    }
}
