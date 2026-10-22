using CodeMaster.Agent.Contracts;
using CodeMaster.Agent.Services;
using CodeMaster.Domain.Entities.Ai;
using System.Text.Json;

namespace CodeMaster.Agent.Tests.Services;

public sealed class AiProjectWorkPlanServiceTests
{
    [Theory]
    [InlineData(AiProjectWorkPlanStatus.Draft)]
    [InlineData(AiProjectWorkPlanStatus.AwaitingConfirmation)]
    public void Empty_draft_or_unconfirmed_plan_is_not_a_blocking_active_plan(AiProjectWorkPlanStatus status)
    {
        Assert.True(AiProjectWorkPlanService.ShouldRetireEmptyPlanForTest(status, 0));
    }

    [Theory]
    [InlineData(AiProjectWorkPlanStatus.Active)]
    [InlineData(AiProjectWorkPlanStatus.Completed)]
    [InlineData(AiProjectWorkPlanStatus.Failed)]
    [InlineData(AiProjectWorkPlanStatus.Blocked)]
    public void Non_empty_or_executing_plan_states_are_not_retired_as_empty(AiProjectWorkPlanStatus status)
    {
        Assert.False(AiProjectWorkPlanService.ShouldRetireEmptyPlanForTest(status, 0));
        Assert.False(AiProjectWorkPlanService.ShouldRetireEmptyPlanForTest(AiProjectWorkPlanStatus.Draft, 1));
    }

    [Fact]
    public void EnsureAcyclic_accepts_dependency_ordered_tasks()
    {
        var dependencies = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["foundation"] = [],
            ["relations"] = ["foundation"],
            ["views"] = ["foundation"],
            ["pages"] = ["relations", "views"]
        };

        AiProjectWorkPlanService.EnsureAcyclic(dependencies);
    }

    [Fact]
    public void EnsureAcyclic_reports_the_dependency_cycle_path()
    {
        var dependencies = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["foundation"] = ["pages"],
            ["relations"] = ["foundation"],
            ["pages"] = ["relations"]
        };

        var error = Assert.Throws<ArgumentException>(() => AiProjectWorkPlanService.EnsureAcyclic(dependencies));

        Assert.Contains("foundation -> pages -> relations -> foundation", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(14, false)]
    [InlineData(15, true)]
    public void Stale_execution_recovery_uses_the_configured_timeout(int elapsedMinutes, bool expected)
    {
        var now = DateTime.UtcNow;
        Assert.Equal(
            expected,
            AiProjectWorkPlanService.IsStaleExecutionForTest(
                now,
                now.AddMinutes(-elapsedMinutes),
                timeoutMinutes: 15));
    }

    [Fact]
    public void Semantic_references_resolve_project_scoped_ids_and_report_missing_objects()
    {
        var blueprint = new AiProjectOverviewDto
        {
            Project = new AiProjectSummaryDto { Id = 9, ProjectName = "Orders" },
            Modules =
            [
                new AiModuleOverviewDto
                {
                    Id = 10,
                    ModuleName = "Sales",
                    Entities =
                    [
                        new AiEntityOverviewDto
                        {
                            Id = 11,
                            Name = "Order",
                            Fields = [new AiFieldOverviewDto { Id = 12, Name = "OrderNo" }],
                            Relations = [new AiRelationOverviewDto { Id = 13, RelationName = "Customer" }]
                        }
                    ]
                }
            ]
        };

        var result = AiProjectWorkPlanService.ResolveSemanticReferencesForTest(
            blueprint,
            ["project", "module:Sales", "entity:Order", "field:Order.OrderNo", "relation:Order.Customer", "entity:Missing"]);

        Assert.Equal([9L, 10L, 11L, 12L, 13L], result.Resolved.Select(item => item.ObjectId));
        Assert.Single(result.Errors);
        Assert.Contains("entity:Missing", result.Errors[0]);
    }

    [Fact]
    public void Missing_definition_is_not_executable()
    {
        var error = AiProjectWorkPlanService.GetDefinitionValidationErrorForTest(
            new AiProjectWorkTask { TaskKey = "foundation-audit" });

        Assert.NotNull(error);
        Assert.Contains("missing an executable definition", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Stored_definition_payload_is_accepted()
    {
        var task = new AiProjectWorkTask
        {
            TaskKey = "foundation-audit",
            DefinitionPayload = AiToolPayloadCodec.EncodeBinary("{\"summary\":\"audit\"}")
        };

        var error = AiProjectWorkPlanService.GetDefinitionValidationErrorForTest(task);

        Assert.Null(error);
    }

    [Fact]
    public void Corrupt_definition_payload_is_not_executable()
    {
        var task = new AiProjectWorkTask
        {
            TaskKey = "foundation-audit",
            DefinitionJson = "gzip:not-valid"
        };

        var error = AiProjectWorkPlanService.GetDefinitionValidationErrorForTest(task);

        Assert.NotNull(error);
        Assert.Contains("invalid stored definition payload", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Declared_standard_changes_are_preserved_for_display_and_execution_review()
    {
        using var document = JsonDocument.Parse("""
        {
          "changes": [
            {
              "changeId": "field-order-customerid-update",
              "operation": "update",
              "resourceType": "field",
              "target": { "entityName": "Order", "fieldName": "CustomerId" },
              "values": { "displayName": "客户", "businessDescription": "订单所属客户" },
              "differences": [
                { "property": "displayName", "before": "客户ID", "after": "客户" }
              ],
              "reason": "使用业务名称展示"
            }
          ],
          "payload": { "opaque": true }
        }
        """);

        var changes = AiProjectWorkPlanService.ExtractNormalizedChangesForTest(
            document.RootElement.Clone(),
            "custom_operation",
            new AiProjectBlueprintDto(),
            "field-controls");

        var change = Assert.Single(changes);
        Assert.Equal("field-order-customerid-update", change.ChangeId);
        Assert.Equal("update", change.Operation);
        Assert.Equal("field", change.ResourceType);
        Assert.Equal("Order", change.Target?.GetProperty("entityName").GetString());
        Assert.Equal("客户", change.Values?.GetProperty("displayName").GetString());
        Assert.Equal("使用业务名称展示", change.Reason);
    }

    [Fact]
    public void Project_change_set_is_projected_into_field_level_add_update_delete_rows()
    {
        var blueprint = new AiProjectBlueprintDto
        {
            Project = new AiProjectSummaryDto { Id = 9, ProjectName = "Orders" },
            Modules =
            [
                new AiModuleBlueprintDto
                {
                    Id = 10,
                    ModuleName = "Sales",
                    ModuleDescription = "销售",
                    Entities =
                    [
                        new AiEntityBlueprintDto
                        {
                            Id = 11,
                            ModuleId = 10,
                            Name = "Order",
                            Description = "订单",
                            Fields =
                            [
                                new AiFieldBlueprintDto
                                {
                                    Id = 12,
                                    Name = "LegacyCode",
                                    Description = "旧编码",
                                    BusinessDescription = "已经停用的历史编码",
                                    DataType = "string",
                                    FormControlType = "input"
                                },
                                new AiFieldBlueprintDto
                                {
                                    Id = 13,
                                    Name = "CustomerId",
                                    Description = "客户ID",
                                    DataType = "long",
                                    FormControlType = "number"
                                }
                            ]
                        }
                    ]
                }
            ]
        };
        using var document = JsonDocument.Parse("""
        {
          "summary": "完善订单字段",
          "modules": [],
          "entities": [],
          "dictionaries": [],
          "moduleUpdates": [],
          "entityUpdates": [
            {
              "entityId": 11,
              "newFields": [
                {
                  "name": "CurrencyCode",
                  "description": "币种",
                  "businessDescription": "订单结算币种",
                  "dataType": "string",
                  "formControlType": "input"
                }
              ],
              "updatedFields": [
                {
                  "fieldId": 13,
                  "description": "客户",
                  "businessDescription": "订单所属客户",
                  "formControlType": "select-table"
                }
              ],
              "deletedFieldIds": [12],
              "newRelations": [],
              "updatedRelations": [],
              "deletedRelationIds": []
            }
          ],
          "deleteEntityIds": [],
          "deleteModuleIds": [],
          "generationMode": "None",
          "generationEntityIds": [],
          "buildAfterGeneration": false,
          "testAfterGeneration": true
        }
        """);

        var changes = AiProjectWorkPlanService.ExtractNormalizedChangesForTest(
            document.RootElement.Clone(),
            "project_change_set",
            blueprint,
            "entity-fields");

        Assert.Contains(changes, item => item.ChangeId == "field:add:Order.CurrencyCode" && item.Operation == "add");
        var updated = Assert.Single(changes, item => item.ChangeId == "field:update:Order.13");
        Assert.Equal("客户ID", updated.Target?.GetProperty("displayName").GetString());
        Assert.Equal("客户", updated.Values?.GetProperty("description").GetString());
        Assert.NotNull(updated.Differences);
        var deleted = Assert.Single(changes, item => item.ChangeId == "field:delete:Order.12");
        Assert.Equal("旧编码", deleted.Target?.GetProperty("displayName").GetString());
        Assert.Equal("已经停用的历史编码", deleted.Target?.GetProperty("businessDescription").GetString());
    }

    [Fact]
    public void Seven_stage_workflow_rejects_generation_before_the_final_stage()
    {
        var request = CreateSevenStageRequest();
        var entityTask = request.Stages[0].Tasks[0];
        entityTask.RequiresGeneration = true;

        var error = Assert.Throws<ArgumentException>(() =>
            AiProjectWorkPlanService.ValidateSevenStageWorkflowForTest(request));

        Assert.Contains("cannot generate or build", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Seven_stage_workflow_requires_consistency_and_generation_adapters()
    {
        var request = CreateSevenStageRequest();
        request.Stages[5].Tasks[0].OperationKind = "project_change_set";

        var error = Assert.Throws<ArgumentException>(() =>
            AiProjectWorkPlanService.ValidateSevenStageWorkflowForTest(request));

        Assert.Contains("project_consistency_check", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Seven_stage_workflow_accepts_metadata_then_check_then_one_generation()
    {
        var request = CreateSevenStageRequest();

        AiProjectWorkPlanService.ValidateSevenStageWorkflowForTest(request);
    }

    private static UpsertAiProjectWorkPlanRequest CreateSevenStageRequest()
    {
        static AiProjectWorkStageInput Stage(
            string key,
            string kind,
            string taskKey,
            string operationKind,
            int order,
            bool requiresGeneration = false,
            bool requiresBuild = false)
            => new()
            {
                StageKey = key,
                Kind = kind,
                Title = key,
                SortOrder = order,
                Tasks =
                [
                    new AiProjectWorkTaskInput
                    {
                        TaskKey = taskKey,
                        Title = taskKey,
                        OperationKind = operationKind,
                        RequiresGeneration = requiresGeneration,
                        RequiresBuild = requiresBuild,
                        Definition = operationKind == "project_change_set"
                            ? JsonSerializer.SerializeToElement(new { summary = taskKey, generationMode = "None" })
                            : JsonSerializer.SerializeToElement(new { summary = taskKey })
                    }
                ]
            };

        return new UpsertAiProjectWorkPlanRequest
        {
            ProjectId = 1,
            ConversationId = 2,
            Title = "标准计划",
            Stages =
            [
                Stage("entity-fields", "EntityFields", "save-entities", "project_change_set", 100),
                Stage("views", "Views", "save-views", "project_change_set", 200),
                Stage("relations", "Relations", "save-relations", "project_change_set", 300),
                Stage("field-controls", "FieldControls", "save-controls", "project_change_set", 400),
                Stage("complex-pages", "ComplexPages", "save-pages", "project_change_set", 500),
                Stage("consistency-check", "ConsistencyCheck", "check-project", "project_consistency_check", 600),
                Stage("generate-code", "GenerateCode", "generate-project", "generate_project", 700, true, true)
            ]
        };
    }
}
