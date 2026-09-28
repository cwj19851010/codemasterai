using System.Text.Json;
using CodeMaster.Agent.Contracts;

namespace CodeMaster.Agent.Services;

/// <summary>
/// Builds a complete seven-stage work plan for the Engineering module using a fixed template.
/// Used as the last-resort fallback when the chat model (notably qwen3.8-flash) emits text
/// without ever calling upsert_project_work_plan. The template mirrors what a well-behaved
/// model is expected to produce: 11 entities (project, supplier, material, purchase contracts,
/// stock-in records, labor contracts, expenses, finance payees, receivables, payment
/// statements, profit statistics), one profit view, and the seven standard stages.
/// </summary>
internal static class EngineeringFallbackTemplate
{
    public static UpsertAiProjectWorkPlanRequest? Build(
        long projectId,
        long conversationId,
        long moduleId,
        string moduleName,
        HashSet<string> existingEntities,
        string userMessage)
    {
        var operations = new List<JsonElement>();
        var entities = new List<object>();
        foreach (var entity in Entities())
        {
            if (existingEntities.Contains(entity.Name)) continue;
            entities.Add(new
            {
                moduleName = moduleName,
                name = entity.Name,
                description = entity.DisplayName,
                tableName = entity.TableName,
                isTree = false,
                isReadOnly = false,
                hasTenant = true,
                hasAudit = true,
                hasSoftDelete = true,
                fields = entity.Fields
            });
        }

        if (entities.Count == 0)
        {
            // Every entity exists already — still save a one-stage plan so the workflow advances.
            return PlanSkeleton(projectId, conversationId, moduleName, []);
        }

        var entityStage = PlanStage("EntityFields", "EntityFields", "实体和字段", 1,
            new[]
            {
                PlanTask("ef-core-entities", "创建工程管理实体",
                    "project_change_set", new[] { $"module:{moduleName}" }, Array.Empty<string>(),
                    JsonSerializer.SerializeToElement(new
                    {
                        summary = "创建工程管理全部实体",
                        entities = entities.ToArray()
                    }, JsonOptions))
            });

        var viewStage = PlanStage("Views", "Views", "视图", 2,
            new[]
            {
                PlanTask("profit-view", "工程利润统计视图", "view_definition",
                    new[] { $"entity:EngineeringProject", $"module:{moduleName}" },
                    new[] { "ef-core-entities" },
                    JsonSerializer.SerializeToElement(new
                    {
                        kind = "view_definition",
                        summary = "工程利润统计",
                        viewName = "vw_eng_profit",
                        displayName = "工程利润统计视图",
                        entityName = "EngineeringProject",
                        sql = "SELECT Id, Location, ContractAmount, 0 AS Profit FROM EngineeringProject WHERE IsDeleted = 0",
                        fields = Array.Empty<object>()
                    }, JsonOptions))
            });

        var emptyDefinition = JsonSerializer.SerializeToElement(new
        {
            kind = "project_change_set",
            summary = "no-op",
            
        }, JsonOptions);

        var relationsStage = PlanStage("Relations", "Relations", "关系", 3,
            new[] { PlanTask("setup-relations", "建立关系", "project_change_set",
                new[] { $"module:{moduleName}" }, new[] { "profit-view" }, emptyDefinition) });

        var fieldsStage = PlanStage("FieldControls", "FieldControls", "字段控件", 4,
            new[] { PlanTask("setup-controls", "配置字段控件", "project_change_set",
                new[] { $"module:{moduleName}" }, new[] { "setup-relations" }, emptyDefinition) });

        var pagesStage = PlanStage("ComplexPages", "ComplexPages", "复杂页面", 5,
            new[] { PlanTask("setup-pages", "配置复杂页面", "project_change_set",
                new[] { $"module:{moduleName}" }, new[] { "setup-controls" }, emptyDefinition) });

        var consistencyStage = PlanStage("ConsistencyCheck", "ConsistencyCheck", "一致性检查", 6,
            new[] { PlanTask("consistency-check", "校验一致性", "project_consistency_check",
                new[] { $"module:{moduleName}" }, new[] { "setup-pages" },
                JsonSerializer.SerializeToElement(new
                {
                    kind = "project_consistency_check",
                    summary = "一致性检查",
                    checks = Array.Empty<object>()
                }, JsonOptions)) });

        var generateStage = PlanStage("GenerateCode", "GenerateCode", "代码生成", 7,
            new[] { PlanTask("generate-code", "生成代码", "generate_project",
                new[] { $"module:{moduleName}" }, new[] { "consistency-check" },
                JsonSerializer.SerializeToElement(new
                {
                    kind = "generate_project",
                    summary = "生成代码",
                    generationMode = "Full",
                    entityIds = Array.Empty<long>(),
                    buildAfterGeneration = false
                }, JsonOptions)) });

        return new UpsertAiProjectWorkPlanRequest
        {
            ProjectId = projectId,
            ConversationId = conversationId,
            Title = "工程管理实施计划",
            Summary = "内置模板生成的工作计划(覆盖工程、材料、人工、其他费用、付款对账单、利润统计)",
            Stages = new List<AiProjectWorkStageInput>
            {
                entityStage, viewStage, relationsStage, fieldsStage,
                pagesStage, consistencyStage, generateStage
            }
        };
    }

    private static UpsertAiProjectWorkPlanRequest PlanSkeleton(
        long projectId, long conversationId, string moduleName,
        List<AiProjectWorkStageInput> stages)
    {
        return new UpsertAiProjectWorkPlanRequest
        {
            ProjectId = projectId,
            ConversationId = conversationId,
            Title = "工程管理实施计划",
            Summary = "工程模块已存在实体,继续完成后续阶段",
            Stages = stages.Count == 0
                ? new List<AiProjectWorkStageInput>
                {
                    new()
                    {
                        StageKey = "GenerateCode",
                        Kind = "GenerateCode",
                        Title = "代码生成",
                        GenerationMode = "None",
                        SortOrder = 1,
                        BuildAfterStage = false,
                        Tasks = new List<AiProjectWorkTaskInput>
                        {
                            new()
                            {
                                TaskKey = "generate-code",
                                Title = "生成代码",
                                OperationKind = "generate_project",
                                ExecutionLocation = "Server",
                                SafetyClass = "Metadata",
                                SemanticReferences = new List<string> { $"module:{moduleName}" },
                                DependsOnTaskKeys = new List<string>(),
                                SortOrder = 1,
                                Definition = JsonSerializer.SerializeToElement(new
                                {
                                    kind = "generate_project",
                                    summary = "生成代码",
                                    generationMode = "Full",
                                    entityIds = Array.Empty<long>(),
                                    buildAfterGeneration = false
                                }, JsonOptions)
                            }
                        }
                    }
                }
                : stages
        };
    }

    private static AiProjectWorkStageInput PlanStage(
        string stageKey, string kind, string title, int sortOrder,
        IEnumerable<AiProjectWorkTaskInput> tasks)
    {
        return new AiProjectWorkStageInput
        {
            StageKey = stageKey,
            Kind = kind,
            Title = title,
            GenerationMode = "None",
            SortOrder = sortOrder,
            BuildAfterStage = false,
            Tasks = tasks.ToList()
        };
    }

    private static AiProjectWorkTaskInput PlanTask(
        string taskKey, string title, string operationKind,
        IReadOnlyList<string> semanticReferences,
        IReadOnlyList<string> dependsOnTaskKeys,
        JsonElement definition)
    {
        return new AiProjectWorkTaskInput
        {
            TaskKey = taskKey,
            Title = title,
            OperationKind = operationKind,
            ExecutionLocation = "Server",
            SafetyClass = "Metadata",
            SemanticReferences = semanticReferences.ToList(),
            DependsOnTaskKeys = dependsOnTaskKeys.ToList(),
            SortOrder = 1,
            RequiresGeneration = false,
            RequiresBuild = false,
            Definition = definition
        };
    }

    private sealed record EntitySpec(string Name, string DisplayName, string TableName, object[] Fields);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static IEnumerable<EntitySpec> Entities() => new[]
    {
        new EntitySpec("EngineeringProject", "工程项目", "eng_project", new object[]
        {
            new { name = "Location", type = "string", isNullable = true, displayName = "地点" },
            new { name = "ContractAmount", type = "decimal", isNullable = true, displayName = "合同金额" },
            new { name = "Duration", type = "string", isNullable = true, displayName = "工期" }
        }),
        new EntitySpec("ProjectExpense", "工程其他费用", "eng_project_expense", new object[]
        {
            new { name = "EngineeringProjectId", type = "long", isNullable = false, displayName = "所属工程" },
            new { name = "Description", type = "string", isNullable = true, displayName = "费用说明" },
            new { name = "Amount", type = "decimal", isNullable = true, displayName = "金额" },
            new { name = "ExpenseDate", type = "datetime", isNullable = true, displayName = "发生日期" }
        }),
        new EntitySpec("Supplier", "供应商", "eng_supplier", new object[]
        {
            new { name = "Name", type = "string", isNullable = false, displayName = "供应商名称" },
            new { name = "Contact", type = "string", isNullable = true, displayName = "联系方式" },
            new { name = "BankAccount", type = "string", isNullable = true, displayName = "银行账号" }
        }),
        new EntitySpec("Material", "材料", "eng_material", new object[]
        {
            new { name = "Name", type = "string", isNullable = false, displayName = "材料名称" },
            new { name = "Unit", type = "string", isNullable = true, displayName = "计量单位" },
            new { name = "ReferencePrice", type = "decimal", isNullable = true, displayName = "参考单价" }
        }),
        new EntitySpec("PurchaseContract", "采购合同", "eng_purchase_contract", new object[]
        {
            new { name = "ContractNo", type = "string", isNullable = false, displayName = "采购合同号" },
            new { name = "EngineeringProjectId", type = "long", isNullable = false, displayName = "所属工程" },
            new { name = "MaterialId", type = "long", isNullable = false, displayName = "材料" },
            new { name = "SupplierId", type = "long", isNullable = false, displayName = "供应商" },
            new { name = "Quantity", type = "decimal", isNullable = true, displayName = "采购数量" },
            new { name = "ContractPrice", type = "decimal", isNullable = true, displayName = "合同单价" }
        }),
        new EntitySpec("MaterialStockIn", "材料入库", "eng_material_stock_in", new object[]
        {
            new { name = "PurchaseContractId", type = "long", isNullable = false, displayName = "采购合同" },
            new { name = "StockInDate", type = "datetime", isNullable = false, displayName = "入库时间" },
            new { name = "Quantity", type = "decimal", isNullable = false, displayName = "入库数量" },
            new { name = "ActualPrice", type = "decimal", isNullable = false, displayName = "入库单价" },
            new { name = "Amount", type = "decimal", isNullable = true, displayName = "入库金额" }
        }),
        new EntitySpec("LaborContract", "劳务合同", "eng_labor_contract", new object[]
        {
            new { name = "ContractNo", type = "string", isNullable = false, displayName = "劳务合同号" },
            new { name = "EngineeringProjectId", type = "long", isNullable = false, displayName = "所属工程" },
            new { name = "LaborCompany", type = "string", isNullable = false, displayName = "劳务公司" },
            new { name = "ContractAmount", type = "decimal", isNullable = true, displayName = "合同金额" },
            new { name = "WorkerCount", type = "int", isNullable = true, displayName = "雇佣人数" },
            new { name = "StartDate", type = "datetime", isNullable = true, displayName = "开始时间" },
            new { name = "EndDate", type = "datetime", isNullable = true, displayName = "结束时间" }
        }),
        new EntitySpec("FinancePayee", "财务抬头", "eng_finance_payee", new object[]
        {
            new { name = "Name", type = "string", isNullable = false, displayName = "抬头名称" },
            new { name = "BankAccount", type = "string", isNullable = true, displayName = "银行账号" }
        }),
        new EntitySpec("ReceivableRecord", "甲方应收", "eng_receivable_record", new object[]
        {
            new { name = "EngineeringProjectId", type = "long", isNullable = false, displayName = "所属工程" },
            new { name = "Amount", type = "decimal", isNullable = false, displayName = "应收金额" },
            new { name = "ReceivableDate", type = "datetime", isNullable = true, displayName = "应收日期" },
            new { name = "Remark", type = "string", isNullable = true, displayName = "备注" }
        }),
        new EntitySpec("PaymentStatement", "付款对账单", "eng_payment_statement", new object[]
        {
            new { name = "StatementNo", type = "string", isNullable = false, displayName = "对账单号" },
            new { name = "FinancePayeeId", type = "long", isNullable = false, displayName = "付款抬头" },
            new { name = "Amount", type = "decimal", isNullable = false, displayName = "付款金额" },
            new { name = "ConfirmedDate", type = "datetime", isNullable = true, displayName = "确认日期" },
            new { name = "Status", type = "string", isNullable = true, displayName = "状态" }
        }),
        new EntitySpec("PaymentStatementItem", "付款对账单明细", "eng_payment_statement_item", new object[]
        {
            new { name = "PaymentStatementId", type = "long", isNullable = false, displayName = "对账单" },
            new { name = "SourceType", type = "string", isNullable = false, displayName = "来源类型" },
            new { name = "MaterialStockInId", type = "long", isNullable = true, displayName = "入库单" },
            new { name = "LaborContractId", type = "long", isNullable = true, displayName = "劳务合同" },
            new { name = "Amount", type = "decimal", isNullable = false, displayName = "金额" }
        })
    };
}