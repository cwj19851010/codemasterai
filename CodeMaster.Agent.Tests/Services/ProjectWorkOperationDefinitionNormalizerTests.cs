using System.Text.Json;
using CodeMaster.Agent.Contracts;
using CodeMaster.Agent.Services;

namespace CodeMaster.Agent.Tests.Services;

public sealed class ProjectWorkOperationDefinitionNormalizerTests
{
    [Fact]
    public void Converts_simplified_sql_view_into_the_private_module_contract()
    {
        var json = """
        {
          "viewName": "order_summary",
          "displayName": "订单汇总",
          "entityName": "OrderSummary",
          "moduleName": "Reports",
          "keyColumn": "Id",
          "sql": "SELECT o.id AS Id, o.tenant_id AS TenantId, o.order_no AS OrderNo, o.total_amount AS TotalAmount, (o.total_amount-o.paid_amount) AS OutstandingAmount FROM orders o WHERE o.is_deleted=0",
          "generateFrontend": true
        }
        """;

        var normalized = ProjectWorkOperationDefinitionNormalizer.NormalizeViewDefinitionForTest(
            1,
            "order-view",
            json,
            CreateBlueprint());
        using var document = JsonDocument.Parse(normalized);
        var root = document.RootElement;

        Assert.Equal("订单汇总", root.GetProperty("summary").GetString());
        Assert.Equal("创建订单汇总只读业务视图", root.GetProperty("reason").GetString());
        Assert.Equal(10, root.GetProperty("moduleId").GetInt64());
        Assert.Equal(1, root.GetProperty("query").GetProperty("projectId").GetInt64());
        var fields = root.GetProperty("entityFields").EnumerateArray().ToList();
        Assert.Equal(5, fields.Count);
        Assert.Equal("long", fields[0].GetProperty("dataType").GetString());
        Assert.Equal("string", fields[2].GetProperty("dataType").GetString());
        Assert.Equal("decimal", fields[4].GetProperty("dataType").GetString());
    }

    [Fact]
    public void Reports_missing_module_at_the_original_path()
    {
        var json = """
        { "viewName":"v", "entityName":"V", "displayName":"视图", "moduleName":"Missing", "keyColumn":"Id", "sql":"SELECT 1 AS Id" }
        """;

        var error = Assert.Throws<InvalidOperationException>(() =>
            ProjectWorkOperationDefinitionNormalizer.NormalizeViewDefinitionForTest(1, "view", json, CreateBlueprint()));

        Assert.Contains("$.moduleName", error.Message);
    }

    [Fact]
    public void PostgreSql_view_aliases_are_normalized_to_physical_snake_case_columns()
    {
        var blueprint = CreateBlueprint();
        blueprint.Project.DatabaseType = "PostgreSQL";
        var json = """
        {
          "viewName":"order_summary",
          "entityName":"OrderSummary",
          "displayName":"订单汇总",
          "moduleName":"Reports",
          "keyColumn":"Id",
          "sql":"SELECT o.id AS Id, o.tenant_id AS TenantId, o.order_no AS OrderNo FROM orders o"
        }
        """;

        var normalized = ProjectWorkOperationDefinitionNormalizer.NormalizeViewDefinitionForTest(
            1,
            "view",
            json,
            blueprint);
        using var document = JsonDocument.Parse(normalized);
        var root = document.RootElement;

        Assert.Contains("AS tenant_id", root.GetProperty("query").GetProperty("sql").GetString());
        var fields = root.GetProperty("entityFields").EnumerateArray().ToList();
        Assert.Equal("TenantId", fields[1].GetProperty("name").GetString());
        Assert.Equal("tenant_id", fields[1].GetProperty("columnName").GetString());
    }

    [Fact]
    public async Task Canonical_view_definition_normalizes_field_names_and_adopts_existing_consumer()
    {
        var blueprint = CreateBlueprint();
        blueprint.Project.DatabaseType = "PostgreSQL";
        var reporting = blueprint.Modules[0];
        reporting.Entities.Add(new AiEntityBlueprintDto
        {
            Id = 30,
            ModuleId = reporting.Id,
            Name = "OrderSummary",
            TableName = "order_summary",
            IsReadOnly = true,
            Fields =
            [
                new AiFieldBlueprintDto { Id = 31, Name = "Id", ColumnName = "id", DataType = "long" },
                new AiFieldBlueprintDto { Id = 32, Name = "OrderNo", ColumnName = "order_reference", DataType = "string" }
            ]
        });
        var json = """
        {
          "summary":"Order summary",
          "reason":"Report",
          "query":{"projectId":"1","sql":"SELECT o.id AS id, o.order_no AS order_no FROM orders o"},
          "entityFields":[
            {"name":"id","columnName":"id","dataType":"long"},
            {"name":"order_no","columnName":"order_no","dataType":"string"}
          ],
          "viewName":"order_summary",
          "moduleId":"10",
          "entityName":"OrderSummary",
          "keyColumn":"id"
        }
        """;

        var normalizer = new ProjectWorkOperationDefinitionNormalizer(new StubBlueprintService(blueprint));
        var result = await normalizer.NormalizeAsync(1, new AiProjectWorkBatchOperation
        {
            TaskKey = "view",
            OperationKind = "view_definition",
            DefinitionJson = json
        });
        using var document = JsonDocument.Parse(result.DefinitionJson!);
        var root = document.RootElement;

        Assert.Equal(30, root.GetProperty("existingEntityId").GetInt64());
        var fields = root.GetProperty("entityFields").EnumerateArray().ToList();
        Assert.Equal("Id", fields[0].GetProperty("name").GetString());
        Assert.Equal("OrderNo", fields[1].GetProperty("name").GetString());
        Assert.Equal("order_reference", fields[1].GetProperty("columnName").GetString());
        Assert.Contains("AS order_reference", root.GetProperty("query").GetProperty("sql").GetString());
    }

    [Fact]
    public async Task Wraps_a_complete_complex_page_definition_and_fills_contract_decisions()
    {
        var json = """
        {
          "projectId":"1",
          "moduleId":"10",
          "pageKey":"booking-workspace",
          "pageName":"订舱工作台",
          "pageType":"Workspace",
          "routePath":"/booking-workspace",
          "description":"订舱独立列表工作台",
          "layoutJson":"{\"version\":1,\"root\":{\"id\":\"booking-root\",\"type\":\"page\",\"children\":[{\"id\":\"booking-table\",\"componentId\":\"booking-table\",\"type\":\"table\",\"dataSourceKey\":\"booking\",\"children\":[{\"id\":\"booking-no\",\"componentId\":\"booking-no\",\"type\":\"table-column\",\"fieldId\":21,\"children\":[]}] }]}}",
          "dataSources":[
            {"sourceKey":"booking","sourceType":"EntityService","entityId":"20","serviceName":"BookingService","methodName":"GetPagedListAsync","dtoName":"BookingDto","dataShape":"Paged","paramsJson":"{}"}
          ],
          "bindings":[
            {"bindingId":"bookingTable","componentId":"booking-table","dataSourceKey":"booking","bindingRole":"Table","controlType":"table","propsJson":"{}"},
            {"bindingId":"bookingNo","componentId":"booking-no","dataSourceKey":"booking","fieldId":"21","bindingRole":"Column","controlType":"table-column","propsJson":"{}"}
          ]
        }
        """;

        var normalizer = new ProjectWorkOperationDefinitionNormalizer(new StubBlueprintService(CreateBlueprint()));
        var result = await normalizer.NormalizeAsync(1, new AiProjectWorkBatchOperation
        {
            TaskKey = "complexpages-booking-workspace",
            OperationKind = "complex_page",
            DefinitionJson = json
        });
        using var document = JsonDocument.Parse(result.DefinitionJson!);
        var root = document.RootElement;

        Assert.Equal("订舱独立列表工作台", root.GetProperty("summary").GetString());
        Assert.False(root.GetProperty("generateAfterApproval").GetBoolean());
        var definition = root.GetProperty("definition");
        using var layout = JsonDocument.Parse(definition.GetProperty("layoutJson").GetString()!);
        Assert.Equal("booking-root", layout.RootElement.GetProperty("root").GetProperty("componentId").GetString());
        var column = layout.RootElement.GetProperty("root").GetProperty("children")[0].GetProperty("children")[0];
        Assert.False(column.TryGetProperty("fieldId", out _));
        var targets = root.GetProperty("decisions").EnumerateArray()
            .Select(item => item.GetProperty("target").GetString())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.Contains("dataSource:booking", targets);
        Assert.Contains("binding:bookingTable:control", targets);
        Assert.Contains("binding:bookingNo:control", targets);
    }

    [Fact]
    public async Task Normalizes_shorthand_complex_page_ids_layout_and_implicit_parent_parameters()
    {
        var json = """
        {
          "summary":"提单工作台",
          "generateAfterApproval":true,
          "buildAfterGeneration":true,
          "decisions":[{"target":"dataSource:items","reason":"按父项过滤"}],
          "definition":{
            "projectId":"1",
            "moduleId":"10",
            "pageKey":"bill-of-lading-workspace",
            "pageName":"提单工作台",
            "pageType":"Custom",
            "routePath":"/bill-of-lading/workspace",
            "layoutJson":"{\"layout\":\"workspace\",\"sections\":[{\"key\":\"list\"},{\"key\":\"items\"}]}",
            "dataSources":[
              {"sourceKey":"master","sourceType":"EntityService","entityId":"20","serviceName":"OrderService","methodName":"GetPagedListAsync","dtoName":"OrderDto","dataShape":"Paged","paramsJson":"{}"},
              {"sourceKey":"items","sourceType":"EntityService","entityId":"20","serviceName":"OrderService","methodName":"GetListAsync","dtoName":"OrderDto","dataShape":"Collection","paramsJson":"{\"OrderId\":\"parent.Id\"}"}
            ],
            "bindings":[
              {"componentId":"bl.workspace","bindingRole":"Container","propsJson":"{}"},
              {"componentId":"bl.table","dataSourceKey":"master","bindingRole":"Table","controlType":"table","lastKnownPath":"sections.list","propsJson":"{}"},
              {"componentId":"bl.order-no","dataSourceKey":"master","fieldId":"23","bindingRole":"Column","controlType":"table-column","lastKnownPath":"sections.list.columns","propsJson":"{}"},
              {"componentId":"items.table","dataSourceKey":"items","bindingRole":"Table","controlType":"table","lastKnownPath":"sections.items","propsJson":"{}"}
            ]
          }
        }
        """;

        var normalizer = new ProjectWorkOperationDefinitionNormalizer(new StubBlueprintService(CreateBlueprint()));
        var result = await normalizer.NormalizeAsync(1, new AiProjectWorkBatchOperation
        {
            TaskKey = "complexpages-bill-of-lading-workspace",
            OperationKind = "complex_page",
            DefinitionJson = json
        });
        using var document = JsonDocument.Parse(result.DefinitionJson!);
        var root = document.RootElement;
        var definition = root.GetProperty("definition");

        Assert.False(root.GetProperty("generateAfterApproval").GetBoolean());
        Assert.False(root.GetProperty("buildAfterGeneration").GetBoolean());
        Assert.Equal("{}", definition.GetProperty("dataSources")[1].GetProperty("paramsJson").GetString());
        var bindings = definition.GetProperty("bindings").EnumerateArray().ToList();
        Assert.Equal(3, bindings.Count);
        Assert.All(bindings, binding => Assert.DoesNotContain('.', binding.GetProperty("componentId").GetString()!));
        using var layout = JsonDocument.Parse(definition.GetProperty("layoutJson").GetString()!);
        Assert.True(layout.RootElement.TryGetProperty("root", out _));
        var targets = root.GetProperty("decisions").EnumerateArray()
            .Select(item => item.GetProperty("target").GetString())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.Contains("binding:bl-table:control", targets);
        Assert.Contains("binding:bl-order-no:control", targets);
        Assert.Contains("binding:items-table:control", targets);
        Assert.Contains("独立数据列表", root.GetProperty("decisions")[0].GetProperty("reason").GetString());
    }

    private static AiProjectBlueprintDto CreateBlueprint() => new()
    {
        Project = new AiProjectSummaryDto { Id = 1, ProjectName = "Demo" },
        Modules =
        [
            new AiModuleBlueprintDto
            {
                Id = 10,
                ModuleName = "Reports",
                ModuleDescription = "报表",
                Entities =
                [
                    new AiEntityBlueprintDto
                    {
                        Id = 20,
                        ModuleId = 10,
                        Name = "Order",
                        TableName = "orders",
                        Fields =
                        [
                            new AiFieldBlueprintDto { Id = 21, Name = "Id", DataType = "long", IsPrimaryKey = true, FormControlType = "number" },
                            new AiFieldBlueprintDto { Id = 22, Name = "TenantId", DataType = "long", FormControlType = "number" },
                            new AiFieldBlueprintDto { Id = 23, Name = "OrderNo", DataType = "string", MaxLength = 50, FormControlType = "input" },
                            new AiFieldBlueprintDto { Id = 24, Name = "TotalAmount", DataType = "decimal", Precision = 18, Scale = 2, FormControlType = "number" },
                            new AiFieldBlueprintDto { Id = 25, Name = "PaidAmount", DataType = "decimal", Precision = 18, Scale = 2, FormControlType = "number" }
                        ]
                    }
                ]
            }
        ]
    };

    private sealed class StubBlueprintService(AiProjectBlueprintDto blueprint) : ICodeMasterProjectBlueprintService
    {
        public Task<AiProjectBlueprintDto> GetProjectAsync(long projectId) => Task.FromResult(blueprint);
        public Task<AiProjectBlueprintDto> GetProjectAsync(long projectId, AiCodegenCatalogDto? catalog) => Task.FromResult(blueprint);
        public Task<AiProjectOverviewDto> GetProjectOverviewAsync(long projectId) => throw new NotSupportedException();
        public Task<AiCodegenCatalogDto> GetCatalogAsync() => throw new NotSupportedException();
        public Task<AiEntityBlueprintDto> GetEntityAsync(long projectId, long? entityId, string? entityName) => throw new NotSupportedException();
        public Task<AiUiPageBlueprintDto> GetUiPageAsync(long projectId, long? entityId, string? entityName, string pageType) => throw new NotSupportedException();
    }
}
