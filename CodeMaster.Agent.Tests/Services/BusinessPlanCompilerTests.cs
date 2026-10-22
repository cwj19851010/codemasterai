using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CodeMaster.Agent.Contracts;
using CodeMaster.Agent.Services;
using Xunit;

namespace CodeMaster.Agent.Tests.Services;

public class BusinessPlanCompilerTests
{
    [Fact]
    public async Task EngineeringManagement_CompilesToStandardPlan()
    {
        var def = BuildEngineeringManagementDefinition();

        var blueprintService = new FakeBlueprintService();
        var progressEvents = new List<BusinessPlanCompileEvent>();

        var result = await BusinessPlanCompiler.CompileAsync(
            definition: def,
            projectId: 2104017857959563264,
            conversationId: 2104092621113790464,
            blueprintService: blueprintService,
            progressAsync: evt => { progressEvents.Add(evt); return Task.CompletedTask; },
            cancellationToken: CancellationToken.None);

        Assert.Equal(6, result.CompiledPlan.Stages.Count);
        var totalTasks = result.CompiledPlan.Stages.Sum(s => s.Tasks.Count);
        Assert.Equal(6, totalTasks);
        Assert.Equal(9, progressEvents.Count);
        Assert.DoesNotContain(result.CompiledPlan.Stages, s => s.StageKey == "GenerateCode");

        foreach (var stage in result.CompiledPlan.Stages)
        {
            foreach (var task in stage.Tasks)
            {
                JsonElement defElement = task.Definition ?? default;
                Assert.NotEqual(JsonValueKind.Null, defElement.ValueKind);
            }
        }

        Assert.Contains("create_engineeringmanagement_entities", result.CompiledPlan.Stages.Find(s => s.StageKey == "Relations")!.Tasks[0].DependsOnTaskKeys);
        Assert.Contains("setup_engineeringmanagement_relations", result.CompiledPlan.Stages.Find(s => s.StageKey == "FieldControls")!.Tasks[0].DependsOnTaskKeys);
        Assert.Contains("setup_engineeringmanagement_controls", result.CompiledPlan.Stages.Find(s => s.StageKey == "ComplexPages")!.Tasks[0].DependsOnTaskKeys);
        Assert.Contains("setup_engineeringmanagement_pages", result.CompiledPlan.Stages.Find(s => s.StageKey == "ConsistencyCheck")!.Tasks[0].DependsOnTaskKeys);
        Assert.Equal("ConsistencyCheck", result.CompiledPlan.Stages[^1].StageKey);
    }

    [Fact]
    public async Task EmptyDefinition_StillProducesValidPlan()
    {
        var def = new BusinessPlanDefinition();
        var result = await BusinessPlanCompiler.CompileAsync(
            def, 1L, 1L, new FakeBlueprintService(),
            progressAsync: null, cancellationToken: default);
        Assert.Equal(6, result.CompiledPlan.Stages.Count);
        Assert.NotNull(result.CompiledPlan.Stages.Single(s => s.StageKey == "ConsistencyCheck").Tasks.Single().Definition);
        Assert.DoesNotContain(result.CompiledPlan.Stages, s => s.StageKey == "GenerateCode");
    }

    [Fact]
    public async Task Compiler_emits_executable_relation_control_page_definitions()
    {
        var result = await BusinessPlanCompiler.CompileAsync(
            BuildEngineeringManagementDefinition(), 1L, 2L, new FakeBlueprintService());

        var relations = result.CompiledPlan.Stages.Single(s => s.StageKey == "Relations").Tasks.Single();
        Assert.Equal("project_change_set", relations.OperationKind);
        Assert.True(relations.Definition!.Value.GetProperty("relations").GetArrayLength() > 0);

        var controls = result.CompiledPlan.Stages.Single(s => s.StageKey == "FieldControls").Tasks.Single();
        Assert.Equal("project_change_set", controls.OperationKind);
        Assert.True(controls.Definition!.Value.GetProperty("controlBindings").GetArrayLength() > 0);

        var pages = result.CompiledPlan.Stages.Single(s => s.StageKey == "ComplexPages").Tasks.Single();
        Assert.Equal("complex_page", pages.OperationKind);
        Assert.Equal("EngineeringManagement", pages.Definition!.Value.GetProperty("definition").GetProperty("moduleName").GetString());
    }

    [Fact]
    public async Task Compiler_preserves_existing_pascal_case_names()
    {
        var result = await BusinessPlanCompiler.CompileAsync(
            BuildEngineeringManagementDefinition(), 1L, 2L, new FakeBlueprintService());
        var definition = result.CompiledPlan.Stages.Single(s => s.StageKey == "EntityFields")
            .Tasks.Single().Definition!.Value;
        var names = definition.GetProperty("entities").EnumerateArray()
            .Select(item => item.GetProperty("name").GetString())
            .ToList();
        Assert.Contains("EngineeringProject", names);
        Assert.Contains("PurchaseContract", names);
        Assert.DoesNotContain("Engineeringproject", names);
    }

    [Fact]
    public async Task EntityReferencingUnknownModule_ThrowsArgumentException()
    {
        var def = new BusinessPlanDefinition
        {
            Modules = new List<BusinessModuleDefinition> { new() { Key = "engineering", TechnicalName = "EngineeringManagement", DisplayName = "工程管理" } },
            Entities = new List<BusinessEntityDefinition>
            {
                new() { Key = "orphan", ModuleKey = "nonexistent", TechnicalName = "Orphan", DisplayName = "孤儿实体" }
            }
        };
        await Assert.ThrowsAsync<ArgumentException>(() =>
            BusinessPlanCompiler.CompileAsync(def, 1L, 1L, new FakeBlueprintService()));
    }

    [Fact]
    public async Task RelationReferencingUnknownEntity_ThrowsArgumentException()
    {
        var def = new BusinessPlanDefinition
        {
            Modules = new List<BusinessModuleDefinition> { new() { Key = "engineering", TechnicalName = "EngineeringManagement", DisplayName = "工程管理" } },
            Entities = new List<BusinessEntityDefinition>
            {
                new() { Key = "project", ModuleKey = "engineering", TechnicalName = "Project", DisplayName = "项目" }
            },
            Relations = new List<BusinessRelationDefinition>
            {
                new() { Key = "bad", Kind = "OwnedOneToMany", SourceEntityKey = "project", TargetEntityKey = "ghost" }
            }
        };
        await Assert.ThrowsAsync<ArgumentException>(() =>
            BusinessPlanCompiler.CompileAsync(def, 1L, 1L, new FakeBlueprintService()));
    }

    [Fact]
    public async Task EndToEnd_PlanIsCompilableJsonRoundtrip()
    {
        var def = BuildEngineeringManagementDefinition();
        var compiled = await BusinessPlanCompiler.CompileAsync(
            def, 1L, 1L, new FakeBlueprintService(), progressAsync: null);

        var json = JsonSerializer.Serialize(compiled.CompiledPlan, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });

        var deserialized = JsonSerializer.Deserialize<UpsertAiProjectWorkPlanRequest>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        Assert.Equal(6, deserialized!.Stages.Count);

        var allTasks = deserialized.Stages.SelectMany(s => s.Tasks).ToList();
        Assert.True(allTasks.Count > 0);
        Assert.All(allTasks, t => Assert.NotEqual(JsonValueKind.Null, (t.Definition ?? default).ValueKind));

        var entityTaskKey = "create_engineeringmanagement_entities";
        var relationsKey = "setup_engineeringmanagement_relations";
        Assert.Contains(entityTaskKey, deserialized.Stages.Find(s => s.StageKey == "Relations")!.Tasks[0].DependsOnTaskKeys);
        Assert.Contains(relationsKey, deserialized.Stages.Find(s => s.StageKey == "FieldControls")!.Tasks[0].DependsOnTaskKeys);
        Assert.Contains("setup_engineeringmanagement_controls", deserialized.Stages.Find(s => s.StageKey == "ComplexPages")!.Tasks[0].DependsOnTaskKeys);
        Assert.Contains("setup_engineeringmanagement_pages", deserialized.Stages.Find(s => s.StageKey == "ConsistencyCheck")!.Tasks[0].DependsOnTaskKeys);
        Assert.DoesNotContain(deserialized.Stages, s => s.StageKey == "GenerateCode");
    }

    static BusinessPlanDefinition BuildEngineeringManagementDefinition()
    {
        return new BusinessPlanDefinition
        {
            Modules = new List<BusinessModuleDefinition>
            {
                new() { Key = "engineering", TechnicalName = "EngineeringManagement", DisplayName = "工程管理" }
            },
            Entities = new List<BusinessEntityDefinition>
            {
                Entity("project", "engineering", "EngineeringProject", "工程项目",
                    new[] { Field("Location", "地点", "string", true), Field("ContractAmount", "合同金额", "decimal"), Field("Duration", "工期", "string") }),
                Entity("material", "engineering", "Material", "材料",
                    new[] { Field("Name", "材料名称", "string", true) }),
                Entity("purchase", "engineering", "PurchaseContract", "采购合同",
                    new[] { Field("Amount", "金额", "decimal"), Field("SupplierId", "供应商ID", "long") }),
                Entity("stock_in", "engineering", "MaterialStockIn", "材料入库",
                    new[] { Field("ActualPrice", "实际入库价", "decimal"), Field("StockInDate", "入库时间", "datetime") }),
                Entity("labor", "engineering", "LaborContract", "劳务合同",
                    new[] { Field("Amount", "合同金额", "decimal"), Field("Workers", "雇佣人数", "int"), Field("StartDate", "雇佣时间", "date") }),
                Entity("supplier", "engineering", "Supplier", "供应商",
                    new[] { Field("Name", "供应商名", "string", true) }),
                Entity("payment", "engineering", "PaymentBill", "付款对账单",
                    new[] { Field("TotalAmount", "总额", "decimal"), Field("Status", "状态", "string") }),
                Entity("receivable", "engineering", "Receivable", "应收账款",
                    new[] { Field("Amount", "金额", "decimal") })
            },
            Relations = new List<BusinessRelationDefinition>
            {
                new() { Key = "project_has_materials", Kind = "OwnedOneToMany", SourceEntityKey = "project", TargetEntityKey = "material", SourceField = "Id", TargetField = "EngineeringProjectId" }
            },
            Views = new List<BusinessViewDefinition>
            {
                new() { Key = "project_profit", EntityKey = "project", ViewName = "V_EngineeringProject_Profit", DisplayName = "工程利润视图", Sql = "SELECT Id, Name FROM EngineeringProject" }
            },
            Pages = new List<BusinessPageDefinition>
            {
                new() { Key = "project_index", EntityKey = "project", PageType = "index", DisplayName = "工程项目列表" }
            }
        };
    }

    static BusinessEntityDefinition Entity(string key, string moduleKey, string tech, string display, BusinessFieldDefinition[] fields)
        => new() { Key = key, ModuleKey = moduleKey, TechnicalName = tech, DisplayName = display, Fields = new List<BusinessFieldDefinition>(fields) };

    static BusinessFieldDefinition Field(string tech, string display, string dataType, bool required = false)
        => new() { TechnicalName = tech, DisplayName = display, DataType = dataType, Required = required };
}

public class FakeBlueprintService : ICodeMasterProjectBlueprintService
{
    public Task<AiProjectOverviewDto> GetProjectOverviewAsync(long projectId) =>
        Task.FromResult(new AiProjectOverviewDto
        {
            Project = new AiProjectSummaryDto { Id = projectId, ProjectName = "EngineeringV11" },
            Modules = new List<AiModuleOverviewDto>()
        });

    public Task<AiProjectBlueprintDto> GetProjectAsync(long projectId) =>
        Task.FromResult(new AiProjectBlueprintDto { Project = new AiProjectSummaryDto { Id = projectId, ProjectName = "EngineeringV11" } });

    public Task<AiProjectBlueprintDto> GetProjectAsync(long projectId, AiCodegenCatalogDto? catalog) =>
        Task.FromResult(new AiProjectBlueprintDto { Project = new AiProjectSummaryDto { Id = projectId, ProjectName = "EngineeringV11" } });

    public Task<AiCodegenCatalogDto> GetCatalogAsync() =>
        Task.FromResult(new AiCodegenCatalogDto());

    public Task<AiEntityBlueprintDto> GetEntityAsync(long projectId, long? entityId, string? entityName) =>
        Task.FromResult(new AiEntityBlueprintDto());

    public Task<AiUiPageBlueprintDto> GetUiPageAsync(long projectId, long? entityId, string? entityName, string pageType) =>
        Task.FromResult(new AiUiPageBlueprintDto());
}
