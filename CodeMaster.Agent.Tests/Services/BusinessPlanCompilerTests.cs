using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CodeMaster.Agent.Contracts;
using CodeMaster.Agent.Services;

public class BusinessPlanCompilerTests
{
    [Fact]
    public async Task EngineeringManagement_CompilesToStandardPlan()
    {
        var def = BuildEngineeringManagementDefinition();
        Console.WriteLine("Input: " + def.Modules.Count + " module(s), " + def.Entities.Count + " entity/entities, " + def.Relations.Count + " relation(s), " + def.Views.Count + " view(s), " + def.Pages.Count + " page(s)");
        Console.WriteLine();

        var blueprintService = new FakeBlueprintService();
        var progressEvents = new List<BusinessPlanCompileEvent>();

        var result = await BusinessPlanCompiler.CompileAsync(
            definition: def,
            projectId: 2104017857959563264,
            conversationId: 2104092621113790464,
            blueprintService: blueprintService,
            progressAsync: evt => { progressEvents.Add(evt); Console.WriteLine("  [compile " + (evt.Percent.HasValue ? evt.Percent.Value.ToString().PadLeft(3) : "  ?") + "%] " + evt.Stage.PadRight(40) + " " + evt.Message); return Task.CompletedTask; },
            cancellationToken: CancellationToken.None);

        Console.WriteLine();
        Console.WriteLine("=== Compile result ===");
        Console.WriteLine("Stages: " + result.CompiledPlan.Stages.Count);
        var totalTasks = result.CompiledPlan.Stages.Sum(s => s.Tasks.Count);
        Console.WriteLine("Total tasks: " + totalTasks);
        Console.WriteLine("Progress events emitted: " + progressEvents.Count);
        Console.WriteLine("Warnings: " + result.Warnings.Count);
        Console.WriteLine();

        int missingDefCount = 0;
        foreach (var stage in result.CompiledPlan.Stages)
        {
            Console.WriteLine("Stage: " + stage.StageKey + " (" + stage.Title + ") - " + stage.Tasks.Count + " task(s)");
            foreach (var task in stage.Tasks)
            {
                JsonElement defElement = task.Definition ?? default;
                var defStr = defElement.ValueKind != JsonValueKind.Null ? defElement.ToString() : "(null)";
                var isMissingDef = defElement.ValueKind == JsonValueKind.Null || string.IsNullOrWhiteSpace(defStr) || defStr == "null";
                var dependsOn = string.Join(",", task.DependsOnTaskKeys);
                Console.WriteLine("  - " + task.TaskKey.PadRight(50) + " op=" + task.OperationKind.PadRight(30) + " dep=[" + dependsOn + "] def=" + (isMissingDef ? "MISSING" : "OK"));
                if (isMissingDef) missingDefCount++;
            }
        }

        Console.WriteLine();
        Console.WriteLine("=== Summary ===");
        Console.WriteLine("Plan JSON length: " + JsonSerializer.Serialize(result.CompiledPlan, new JsonSerializerOptions { WriteIndented = false }).Length + " chars");
        Console.WriteLine("Tasks missing definition: " + missingDefCount);
        Console.WriteLine("All " + totalTasks + " tasks have definitions: " + (missingDefCount == 0));
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

    [Fact]
    public async Task EmptyDefinition_StillProducesValidPlan()
    {
        var def = new BusinessPlanDefinition();
        var result = await BusinessPlanCompiler.CompileAsync(
            def, 1L, 1L, new FakeBlueprintService(),
            progressAsync: null, cancellationToken: default);
        Assert.Equal(7, result.CompiledPlan.Stages.Count);
        Assert.NotNull(result.CompiledPlan.Stages.Single(s => s.StageKey == "ConsistencyCheck").Tasks.Single().Definition);
        Assert.NotNull(result.CompiledPlan.Stages.Single(s => s.StageKey == "GenerateCode").Tasks.Single().Definition);
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
}
