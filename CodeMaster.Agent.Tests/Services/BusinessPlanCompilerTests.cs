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

    /// <summary>
    /// A foreign key that points at another entity must be bound to a picker, not a number box.
    /// <c>InferControlType</c> only sees the C# type, so before this the generated add/edit form asked the
    /// user to type a raw id even though the relation already named the target entity.
    /// </summary>
    [Fact]
    public async Task Reference_foreign_key_field_is_bound_to_select_table()
    {
        var def = new BusinessPlanDefinition
        {
            Modules = [new BusinessModuleDefinition { Key = "engineering", TechnicalName = "EngineeringManagement", DisplayName = "工程管理" }],
            Entities =
            [
                Entity("project", "engineering", "EngineeringProject", "工程项目",
                    [Field("Name", "工程名称", "string", true), Field("ContractAmount", "合同金额", "decimal")]),
                Entity("supplier", "engineering", "Supplier", "供应商",
                    [Field("Name", "供应商名称", "string", true)]),
                Entity("purchase", "engineering", "PurchaseContract", "采购合同",
                    [Field("ContractNo", "采购合同号", "string", true),
                     Field("EngineeringProjectId", "所属工程", "long"),
                     Field("SupplierId", "供应商", "long")])
            ],
            Relations =
            [
                new() { Key = "purchase_project", Kind = "ManyToOne", SourceEntityKey = "purchase", SourceField = "EngineeringProjectId", TargetEntityKey = "project", TargetField = "Id" },
                new() { Key = "purchase_supplier", Kind = "ManyToOne", SourceEntityKey = "purchase", SourceField = "SupplierId", TargetEntityKey = "supplier", TargetField = "Id" }
            ]
        };

        var result = await BusinessPlanCompiler.CompileAsync(def, 1L, 1L, new FakeBlueprintService());

        var bindings = result.CompiledPlan.Stages.Single(s => s.StageKey == "FieldControls")
            .Tasks.Single().Definition!.Value.GetProperty("controlBindings").EnumerateArray()
            .Select(item => (field: item.GetProperty("field").GetString(), item))
            .ToList();

        var projectFk = bindings.Single(b => b.field == "EngineeringProjectId").item;
        Assert.Equal("select-table", projectFk.GetProperty("formControlType").GetString());
        Assert.Equal("EngineeringProject", projectFk.GetProperty("relatedEntityName").GetString());
        Assert.Equal("Id", projectFk.GetProperty("relatedEntityIdField").GetString());
        Assert.Equal("Name", projectFk.GetProperty("relatedEntityDisplayFields")[0].GetString());

        var supplierFk = bindings.Single(b => b.field == "SupplierId").item;
        Assert.Equal("select-table", supplierFk.GetProperty("formControlType").GetString());
        Assert.Equal("Supplier", supplierFk.GetProperty("relatedEntityName").GetString());

        // A decimal is not a reference: it keeps the numeric control and carries no link.
        var amount = bindings.Single(b => b.field == "ContractAmount").item;
        Assert.Equal("number", amount.GetProperty("formControlType").GetString());
        Assert.Equal(JsonValueKind.Null, amount.GetProperty("relatedEntityName").ValueKind);
    }

    /// <summary>
    /// A read-only reporting entity has no add/edit form, and its field list is rebuilt from the view's
    /// SELECT when the view is created — the key column comes back as Id, so a binding compiled from the
    /// declared fields can name a column that no longer exists. Emitting one anyway failed the whole
    /// field-control stage during change-set normalization.
    /// </summary>
    [Fact]
    public async Task ReadOnly_view_entity_gets_no_control_bindings()
    {
        var report = Entity("profit", "engineering", "VProjectProfit", "工程利润视图",
            [Field("ProjectId", "所属工程", "long"), Field("Profit", "利润", "decimal")]);
        report.IsReadOnly = true;

        var def = new BusinessPlanDefinition
        {
            Modules = [new BusinessModuleDefinition { Key = "engineering", TechnicalName = "EngineeringManagement", DisplayName = "工程管理" }],
            Entities =
            [
                Entity("project", "engineering", "EngineeringProject", "工程项目",
                    [Field("Name", "工程名称", "string", true), Field("ContractAmount", "合同金额", "decimal")]),
                report
            ],
            Relations =
            [
                new() { Key = "profit_project", Kind = "ManyToOne", SourceEntityKey = "profit", SourceField = "ProjectId", TargetEntityKey = "project", TargetField = "Id" }
            ],
            Views = [new() { Key = "profit_view", EntityKey = "profit", ViewName = "V_ProjectProfit", DisplayName = "工程利润视图", Sql = "SELECT p.Id AS ProjectId, 0 AS Profit FROM EngineeringProject p" }]
        };

        var result = await BusinessPlanCompiler.CompileAsync(def, 1L, 1L, new FakeBlueprintService());

        var controlStage = result.CompiledPlan.Stages.SingleOrDefault(s => s.StageKey == "FieldControls");
        Assert.NotNull(controlStage);
        var entities = controlStage!.Tasks.Single().Definition!.Value.GetProperty("controlBindings")
            .EnumerateArray()
            .Select(item => item.GetProperty("entity").GetString())
            .ToList();
        Assert.DoesNotContain("VProjectProfit", entities);
    }

    /// <summary>
    /// Models often express "a project has many contracts" from the parent side instead of declaring the
    /// foreign key on the child. The key is then <c>targetField</c> and the picker must point back at the
    /// source — otherwise the child's form still asks for a raw id.
    /// </summary>
    [Fact]
    public async Task OneToMany_relation_from_parent_still_binds_child_foreign_key_to_picker()
    {
        var def = new BusinessPlanDefinition
        {
            Modules = [new BusinessModuleDefinition { Key = "engineering", TechnicalName = "EngineeringManagement", DisplayName = "工程管理" }],
            Entities =
            [
                Entity("project", "engineering", "EngineeringProject", "工程项目",
                    [Field("ProjectName", "工程名称", "string", true)]),
                Entity("purchase", "engineering", "PurchaseContract", "采购合同",
                    [Field("ContractNo", "采购合同号", "string", true), Field("ProjectId", "所属工程", "long")])
            ],
            Relations =
            [
                new() { Key = "project_contracts", Kind = "OneToMany", SourceEntityKey = "project", SourceField = "Id", TargetEntityKey = "purchase", TargetField = "ProjectId" }
            ]
        };

        var result = await BusinessPlanCompiler.CompileAsync(def, 1L, 1L, new FakeBlueprintService());

        var bindings = result.CompiledPlan.Stages.Single(s => s.StageKey == "FieldControls")
            .Tasks.Single().Definition!.Value.GetProperty("controlBindings").EnumerateArray()
            .ToList();
        var fk = bindings.Single(b => b.GetProperty("field").GetString() == "ProjectId");
        Assert.Equal("PurchaseContract", fk.GetProperty("entity").GetString());
        Assert.Equal("select-table", fk.GetProperty("formControlType").GetString());
        Assert.Equal("EngineeringProject", fk.GetProperty("relatedEntityName").GetString());
        Assert.Equal("ProjectName", fk.GetProperty("relatedEntityDisplayFields")[0].GetString());
    }

    /// <summary>
    /// 模型只在 relations 里命名外键（targetField: ProjectId），却没在"多"方实体声明同名列：
    /// 首次编译会让 Relations 阶段引用不存在的列，change-set 校验整批报
    /// "references missing target field 'ProjectId'"，关系一条都写不进去。
    /// 编译器必须自己把列补进 EntityFields 阶段，而不是等失败后重编译一次。
    /// </summary>
    [Fact]
    public async Task Missing_relation_foreign_key_is_created_before_relations_stage()
    {
        var def = new BusinessPlanDefinition
        {
            Modules = [new BusinessModuleDefinition { Key = "engineering", TechnicalName = "EngineeringManagement", DisplayName = "工程管理" }],
            Entities =
            [
                Entity("project", "engineering", "EngineeringProject", "工程项目",
                    [Field("ProjectName", "工程名称", "string", true)]),
                // 故意不声明 ProjectId —— 这正是线上报错时的实体形态
                Entity("purchase", "engineering", "PurchaseContract", "采购合同",
                    [Field("ContractNo", "采购合同号", "string", true)])
            ],
            Relations =
            [
                new() { Key = "project_contracts", Kind = "OneToMany", SourceEntityKey = "project", SourceField = "Id", TargetEntityKey = "purchase", TargetField = "ProjectId" }
            ]
        };

        var result = await BusinessPlanCompiler.CompileAsync(def, 1L, 3L, new FakeBlueprintService());

        var entityTask = result.CompiledPlan.Stages.Single(s => s.StageKey == "EntityFields").Tasks.Single();
        var purchase = entityTask.Definition!.Value.GetProperty("entities").EnumerateArray()
            .Single(e => e.GetProperty("name").GetString() == "PurchaseContract");
        var fk = Assert.Single(purchase.GetProperty("fields").EnumerateArray(),
            f => f.GetProperty("name").GetString() == "ProjectId");
        Assert.Equal("long", fk.GetProperty("dataType").GetString());

        var relation = result.CompiledPlan.Stages.Single(s => s.StageKey == "Relations")
            .Tasks.Single().Definition!.Value.GetProperty("relations")[0];
        Assert.Equal("ProjectId", relation.GetProperty("targetField").GetString());

        // 补列后 ApplyReferenceControls 的前置检查通过，外键不会退化成纯数字输入框
        var bindings = result.CompiledPlan.Stages.Single(s => s.StageKey == "FieldControls")
            .Tasks.Single().Definition!.Value.GetProperty("controlBindings").EnumerateArray().ToList();
        Assert.Equal("select-table", bindings.Single(b => b.GetProperty("field").GetString() == "ProjectId")
            .GetProperty("formControlType").GetString());

        Assert.Contains(result.Warnings, w => w.Contains("ProjectId"));
    }

    [Fact]
    public async Task Dictionaries_compile_into_a_leading_entity_task()
    {
        var def = BuildEngineeringManagementDefinition();
        def.Dictionaries = new List<BusinessDictionaryDefinition>
        {
            new()
            {
                DictType = "payment_bill_status",
                DictName = "付款单状态",
                Items = new List<BusinessDictionaryItemDefinition>
                {
                    new() { Label = "草稿", Value = "draft" },
                    new() { Label = "已审批", Value = "approved", IsDefault = true }
                }
            }
        };

        var result = await BusinessPlanCompiler.CompileAsync(def, 1L, 2L, new FakeBlueprintService());

        var entityStage = result.CompiledPlan.Stages.Single(s => s.StageKey == "EntityFields");
        var dictionaryTask = entityStage.Tasks[0];
        Assert.Equal("create_business_dictionaries", dictionaryTask.TaskKey);
        Assert.Equal("project_change_set", dictionaryTask.OperationKind);
        Assert.Empty(dictionaryTask.DependsOnTaskKeys);

        var dictionaries = dictionaryTask.Definition!.Value.GetProperty("dictionaries");
        Assert.Equal(1, dictionaries.GetArrayLength());
        var first = dictionaries[0];
        Assert.Equal("payment_bill_status", first.GetProperty("dictType").GetString());
        Assert.Equal("付款单状态", first.GetProperty("dictName").GetString());
        Assert.False(string.IsNullOrWhiteSpace(first.GetProperty("summary").GetString()));
        Assert.Equal(2, first.GetProperty("items").GetArrayLength());
        Assert.Equal("approved", first.GetProperty("items")[1].GetProperty("value").GetString());
        Assert.Equal(1, first.GetProperty("items")[1].GetProperty("isDefault").GetInt32());

        Assert.Equal(1, result.Report.Dictionaries);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public async Task Field_referencing_an_undeclared_dictionary_warns_without_failing()
    {
        var def = new BusinessPlanDefinition
        {
            Modules = new List<BusinessModuleDefinition>
            {
                new() { Key = "engineering", TechnicalName = "EngineeringManagement", DisplayName = "工程管理" }
            },
            Entities = new List<BusinessEntityDefinition>
            {
                Entity("payment", "engineering", "PaymentBill", "付款单",
                    new[]
                    {
                        new BusinessFieldDefinition
                        {
                            TechnicalName = "Status",
                            DisplayName = "状态",
                            DataType = "string",
                            Dictionary = "payment_status"
                        }
                    })
            }
        };

        var result = await BusinessPlanCompiler.CompileAsync(def, 1L, 2L, new FakeBlueprintService());

        Assert.Contains(result.Warnings, warning => warning.Contains("payment_status", StringComparison.Ordinal));
        Assert.DoesNotContain(
            result.CompiledPlan.Stages.Single(s => s.StageKey == "EntityFields").Tasks,
            task => task.TaskKey == "create_business_dictionaries");
    }

    [Fact]
    public async Task Compiled_dictionary_task_passes_change_set_validation()
    {
        var def = BuildEngineeringManagementDefinition();
        def.Dictionaries = new List<BusinessDictionaryDefinition>
        {
            new() { DictType = "payment_bill_status", DictName = "付款单状态" }
        };

        var compiled = await BusinessPlanCompiler.CompileAsync(def, 1L, 2L, new FakeBlueprintService());
        var task = compiled.CompiledPlan.Stages.Single(s => s.StageKey == "EntityFields").Tasks[0];

        var blueprint = new AiProjectBlueprintDto
        {
            Project = new AiProjectSummaryDto { Id = 1, ProjectName = "EngineeringV11" }
        };
        var proposal = ProjectChangeSetDefinitionNormalizer.NormalizeForTest(
            task.TaskKey,
            task.Definition!.Value.GetRawText(),
            blueprint);

        var dictionary = Assert.Single(proposal.Dictionaries);
        Assert.Equal("payment_bill_status", dictionary.DictType);
        Assert.Equal("付款单状态", dictionary.DictName);

        var validation = CodeMasterProjectChangeSetRules.Validate(blueprint, proposal, "EngineeringV11");
        Assert.True(validation.IsValid, string.Join(" | ", validation.Errors));
        Assert.Equal(1, validation.DictionaryCount);
    }

    [Fact]
    public async Task Swapped_dictType_and_dictName_are_repaired_from_the_field_reference()
    {
        var def = new BusinessPlanDefinition
        {
            Modules = new List<BusinessModuleDefinition>
            {
                new() { Key = "finance", TechnicalName = "Finance", DisplayName = "财务" }
            },
            Entities = new List<BusinessEntityDefinition>
            {
                Entity("invoice", "finance", "Invoice", "发票",
                    new[]
                    {
                        new BusinessFieldDefinition
                        {
                            TechnicalName = "InvoiceType",
                            DisplayName = "发票类型",
                            DataType = "string",
                            Dictionary = "invoice_type"
                        }
                    })
            },
            Dictionaries = new List<BusinessDictionaryDefinition>
            {
                // What the model actually emits: the data type lands in dictType and the code in dictName.
                new() { DictType = "string", DictName = "invoice_type", Remark = "发票类型" }
            }
        };

        var result = await BusinessPlanCompiler.CompileAsync(def, 1L, 2L, new FakeBlueprintService());

        var task = result.CompiledPlan.Stages.Single(s => s.StageKey == "EntityFields").Tasks[0];
        var dictionary = task.Definition!.Value.GetProperty("dictionaries")[0];
        Assert.Equal("invoice_type", dictionary.GetProperty("dictType").GetString());
        Assert.Equal("发票类型", dictionary.GetProperty("dictName").GetString());
        Assert.Contains(result.Warnings, warning => warning.Contains("写反", StringComparison.Ordinal));
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
