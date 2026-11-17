using System.Text.Json;
using CodeMaster.Agent.Contracts;
using CodeMaster.Agent.Serialization;
using CodeMaster.Agent.Services;
using CodeMaster.Agent.Tools;
using CodeMaster.Application.Dtos.CodeGen;
using Microsoft.Extensions.AI;

namespace CodeMaster.Agent.Tests.Services;

public class AgentClientActionTests
{
    private const long UnsafeJavascriptId = 2077646988983275501;

    [Theory]
    [InlineData(null, false, "Default", true)]
    [InlineData("Standard", false, "Standard", false)]
    [InlineData("Standard", true, "Standard", true)]
    [InlineData("Minimal", false, "Minimal", true)]
    [InlineData("Default", true, "Default", true)]
    [InlineData("unknown", true, "Default", true)]
    public void WorkflowMode_UsesExplicitMinimalOrStandardAutoApproval(
        string? input,
        bool standardAutoApproval,
        string expectedMode,
        bool expectedAutomaticApproval)
    {
        Assert.Equal(expectedMode, AiWorkflowModes.Normalize(input));
        Assert.Equal(
            expectedAutomaticApproval,
            AiWorkflowModes.ShouldAutoApprove(input, standardAutoApproval));
    }

    [Fact]
    public void WorkflowPreferences_RoundTripAndPreserveExplicitStandardAutoApproval()
    {
        var serialized = AiWorkflowPreferencesStore.Write(
            AiWorkflowPreferencesStore.Normalize(AiWorkflowModes.Standard, true));
        var restored = AiWorkflowPreferencesStore.Read(serialized);

        Assert.Equal(AiWorkflowModes.Standard, restored.WorkflowMode);
        Assert.True(restored.AutoApproveApprovals);
        Assert.True(restored.ShouldAutoApprove);
    }

    [Fact]
    public void WorkflowPreferences_LegacyConversationDefaultsToDefaultMode()
    {
        var restored = AiWorkflowPreferencesStore.Read(null);

        Assert.Equal(AiWorkflowModes.Default, restored.WorkflowMode);
        Assert.False(restored.AutoApproveApprovals);
        Assert.True(restored.ShouldAutoApprove);
    }

    [Fact]
    public void NewConversationsDefaultToAutomaticWorkflow()
    {
        var defaults = AiWorkflowPreferencesStore.NewConversationDefault;

        Assert.Equal(AiWorkflowModes.Default, defaults.WorkflowMode);
        Assert.True(defaults.ShouldAutoApprove);
    }

    [Theory]
    [InlineData(true, false, "Active", 0, 3, "已准备好一项项目变更")]
    [InlineData(false, false, "Blocked", 0, 3, "任务计划存在未完成的任务定义")]
    [InlineData(false, false, "Active", 1, 3, "已完成 1/3 项")]
    [InlineData(false, true, "Completed", 3, 3, "CodeMaster 操作已完成")]
    public void Empty_response_fallback_explains_actual_agent_state(
        bool hasPendingApproval,
        bool hasCompletedExecution,
        string workPlanStatus,
        int completedTasks,
        int totalTasks,
        string expectedText)
    {
        var message = AiConversationService.BuildEmptyResponseFallback(
            hasPendingApproval,
            hasCompletedExecution,
            workPlanStatus,
            completedTasks,
            totalTasks);

        Assert.Contains(expectedText, message);
        Assert.DoesNotContain("请重试一次", message);
    }

    [Fact]
    public void Rate_limit_failures_are_not_replayed_by_stream_fallback()
    {
        Assert.True(AiConversationService.IsRateLimitFailure(
            new InvalidOperationException("Service request failed. Status: 429 (Too Many Requests)")));
        Assert.False(AiConversationService.IsRateLimitFailure(
            new InvalidOperationException("Service request failed. Status: 504 (Gateway Timeout)")));
    }

    [Fact]
    public void Long_conversation_history_keeps_only_recent_bounded_messages()
    {
        var messages = Enumerable.Range(1, 20)
            .Select(index => new AiMessageDto
            {
                Role = index % 2 == 0 ? "assistant" : "user",
                Content = new string('x', 3000)
            })
            .ToList();

        var bounded = AiConversationHistory.BuildBounded(messages);

        Assert.InRange(bounded.Count, 1, AiConversationHistory.MaximumMessages);
        Assert.True(bounded.Sum(message => message.Text?.Length ?? 0) <= AiConversationHistory.MaximumCharacters);
        Assert.Equal(ChatRole.User, bounded[0].Role);
        Assert.Equal(messages[^1].Content, bounded[^1].Text);
    }

    [Fact]
    public void Work_plan_repair_request_uses_only_compact_repair_tools()
    {
        var tools = new List<AITool>
        {
            AIFunctionFactory.Create(() => "ok", name: CodeMasterAgentToolCatalog.GetProjectSummary),
            AIFunctionFactory.Create(() => "ok", name: CodeMasterAgentToolCatalog.GetProjectWorkTask),
            AIFunctionFactory.Create(() => "ok", name: CodeMasterAgentToolCatalog.UpsertProjectWorkPlan),
            AIFunctionFactory.Create(() => "large", name: CodeMasterAgentToolCatalog.ProposeProjectChangeSet)
        };

        var selected = AiTurnToolSelector.SelectForWorkPlanRepair(
            tools,
            "请修复当前任务计划，任务缺少可执行定义");
        var names = selected.OfType<AIFunction>().Select(tool => tool.Name).ToList();

        Assert.Contains(CodeMasterAgentToolCatalog.GetProjectSummary, names);
        Assert.Contains(CodeMasterAgentToolCatalog.GetProjectWorkTask, names);
        Assert.Contains(CodeMasterAgentToolCatalog.UpsertProjectWorkPlan, names);
        Assert.DoesNotContain(CodeMasterAgentToolCatalog.ProposeProjectChangeSet, names);
    }

    [Theory]
    [InlineData("检查项目剩余工作")]
    [InlineData("继续")]
    [InlineData("请把整个项目完成")]
    [InlineData("现在进度怎么样")]
    public void Default_completion_requests_require_authoritative_completion_tool(string content)
    {
        var tool = AiConversationService.DetermineRequiredGroundingTool(
            content,
            AiWorkflowPreferencesStore.Normalize(AiWorkflowModes.Default, false));

        Assert.Equal(CodeMasterAgentToolCatalog.GetProjectCompletionStatus, tool);
    }

    [Fact]
    public void Unrelated_requests_do_not_force_completion_tool()
    {
        Assert.Null(AiConversationService.DetermineRequiredGroundingTool(
            "继续讨论定价",
            AiWorkflowPreferencesStore.Normalize(AiWorkflowModes.Default, false)));
        Assert.Null(AiConversationService.DetermineRequiredGroundingTool(
            "解释一下字段类型",
            AiWorkflowPreferencesStore.Normalize(AiWorkflowModes.Default, false)));
    }

    [Theory]
    [InlineData("1.以上功能都需要，按你的想法来")]
    [InlineData("请创建一个类似 Jira 的项目管理系统")]
    [InlineData("开始实现完整的跨模块管理平台")]
    public void Broad_project_requests_require_the_project_work_plan(string content)
    {
        Assert.True(AiConversationService.RequiresProjectWorkPlan(content));
        Assert.Equal(
            CodeMasterAgentToolCatalog.UpsertProjectWorkPlan,
            AiConversationService.DetermineRequiredGroundingTool(
                content,
                AiWorkflowPreferencesStore.Normalize(AiWorkflowModes.Default, false)));
    }

    [Fact]
    public void Analysis_only_request_does_not_force_project_execution()
    {
        const string content = "我想做一个类似 Jira 的项目管理系统，你先帮我分析下";

        Assert.False(AiConversationService.RequiresProjectWorkPlan(content));
        Assert.Null(AiConversationService.DetermineRequiredGroundingTool(
            content,
            AiWorkflowPreferencesStore.Normalize(AiWorkflowModes.Default, false)));
    }

    [Fact]
    public void Serializer_WritesLongIdsAsStrings()
    {
        var json = JsonSerializer.Serialize(new AiClientActionDto
        {
            Action = "generateProjectIncrementalCode",
            ProjectId = UnsafeJavascriptId,
            EntityIds = { UnsafeJavascriptId }
        }, AgentJsonSerializer.Options);

        Assert.Contains($"\"projectId\":\"{UnsafeJavascriptId}\"", json, StringComparison.Ordinal);
        Assert.Contains($"\"entityIds\":[\"{UnsafeJavascriptId}\"]", json, StringComparison.Ordinal);

        var restored = JsonSerializer.Deserialize<AiClientActionDto>(json, AgentJsonSerializer.Options);
        Assert.NotNull(restored);
        Assert.Equal(UnsafeJavascriptId, restored.ProjectId);
        Assert.Equal(UnsafeJavascriptId, Assert.Single(restored.EntityIds));
    }

    [Fact]
    public void Serializer_AcceptsNativeJsonForJsonBackedFieldMetadata()
    {
        const string json = """
        {
          "name": "AssigneeId",
          "description": "负责人",
          "dataType": "long",
          "formControlType": "select-table",
          "selectOptions": { "loadMode": "batch" },
          "relatedEntityName": "SysUser",
          "relatedEntityIdField": "Id",
          "relatedEntityDisplayFields": ["NickName", "UserName"],
          "resultMappings": [
            { "sourceField": "NickName", "targetField": "AssigneeName" }
          ]
        }
        """;

        var field = JsonSerializer.Deserialize<CreateEntityFieldProposal>(json, AgentJsonSerializer.Options);

        Assert.NotNull(field);
        Assert.Equal("{\"loadMode\":\"batch\"}", field.SelectOptions);
        Assert.Equal("[\"NickName\",\"UserName\"]", field.RelatedEntityDisplayFields);
        Assert.Equal(
            "[{\"sourceField\":\"NickName\",\"targetField\":\"AssigneeName\"}]",
            field.ResultMappings);
    }

    [Fact]
    public void Tool_failure_diagnostic_explains_array_string_mismatch()
    {
        var exception = new InvalidOperationException(
            "wrapper",
            new JsonException("Cannot get the value of a token type 'StartArray' as a string.", path: "$.proposal.entities[0].fields[2].relatedEntityDisplayFields", lineNumber: 12, bytePositionInLine: 9));

        var diagnostic = AiConversationService.BuildToolFailureDiagnostic(exception);

        Assert.Contains("$.proposal.entities[0].fields[2].relatedEntityDisplayFields", diagnostic);
        Assert.Contains("JSON 数组", diagnostic);
    }

    [Fact]
    public void Failed_tool_argument_summary_preserves_shape_without_values()
    {
        using var document = JsonDocument.Parse("""
        {
          "proposal": {
            "entities": [
              {
                "name": "Issue",
                "relatedEntityDisplayFields": ["NickName", "UserName"],
                "apiToken": "secret-value"
              }
            ]
          }
        }
        """);

        var summary = AiConversationService.BuildToolArgumentShapeSummary(document.RootElement);

        Assert.Contains("relatedEntityDisplayFields", summary);
        Assert.Contains("\"type\":\"array\"", summary);
        Assert.DoesNotContain("Issue", summary);
        Assert.DoesNotContain("NickName", summary);
        Assert.DoesNotContain("secret-value", summary);
    }

    [Fact]
    public void Failed_then_partial_change_set_is_reported_from_actual_payload()
    {
        var message = AiConversationService.BuildProjectChangeSetOutcomeMessage(
            new ProjectChangeSetProposal
            {
                Modules =
                {
                    new CreateModuleProposal { ModuleName = "ProjectManagement", ModuleDescription = "项目管理" },
                    new CreateModuleProposal { ModuleName = "IssueTracking", ModuleDescription = "问题追踪" },
                    new CreateModuleProposal { ModuleName = "Collaboration", ModuleDescription = "协作" }
                }
            },
            "Completed");

        Assert.Contains("项目管理、问题追踪、协作", message);
        Assert.Contains("未包含业务实体、字段、关系或字典", message);
        Assert.DoesNotContain("9 个", message);
    }

    [Fact]
    public void Serializer_PreservesUiEnhancementIdsAndOperations()
    {
        var json = JsonSerializer.Serialize(new AiClientActionDto
        {
            Action = "enhanceUiPage",
            ProjectId = UnsafeJavascriptId,
            EntityId = UnsafeJavascriptId + 1,
            TargetKind = "EntityPage",
            PageType = "edit",
            Operations =
            {
                new ProjectUiNodeOperationDto
                {
                    Type = "SetGrid",
                    TargetGenId = $"gen_field_{UnsafeJavascriptId + 2}",
                    Md = 12
                }
            }
        }, AgentJsonSerializer.Options);

        Assert.Contains($"\"projectId\":\"{UnsafeJavascriptId}\"", json, StringComparison.Ordinal);
        Assert.Contains($"\"entityId\":\"{UnsafeJavascriptId + 1}\"", json, StringComparison.Ordinal);
        Assert.Contains($"gen_field_{UnsafeJavascriptId + 2}", json, StringComparison.Ordinal);

        var restored = JsonSerializer.Deserialize<AiClientActionDto>(json, AgentJsonSerializer.Options);
        Assert.NotNull(restored);
        Assert.Equal(UnsafeJavascriptId + 1, restored.EntityId);
        Assert.Equal(12, Assert.Single(restored.Operations).Md);
    }

    [Fact]
    public void AddClientActions_NeverQueuesGenerationOrBuild()
    {
        var proposal = new ProjectChangeSetProposal
        {
            GenerationMode = "Full",
            GenerationEntityIds = { UnsafeJavascriptId },
            BuildAfterGeneration = true,
            TestAfterGeneration = true
        };
        var result = new ProjectChangeSetExecutionResult { ProjectId = 10 };

        CodeMasterProjectChangeSetService.AddClientActions(10, proposal, Array.Empty<ModuleEntityDto>(), result);

        Assert.Empty(result.ClientActions);
    }

    [Fact]
    public void RequiresBuildPermission_IsAlwaysFalseBecauseTheAgentNeverBuilds()
    {
        Assert.False(CodeMasterProjectChangeSetService.RequiresBuildPermission(
            new ProjectChangeSetProposal { TestAfterGeneration = true }));
        Assert.False(CodeMasterProjectChangeSetService.RequiresBuildPermission(
            new ProjectChangeSetProposal { GenerationMode = "Incremental", Entities = { new CreateEntityProposal() } }));
        Assert.False(CodeMasterProjectChangeSetService.RequiresBuildPermission(
            new ProjectChangeSetProposal { GenerationMode = "Full" }));
        Assert.False(CodeMasterProjectChangeSetService.RequiresBuildPermission(
            new ProjectChangeSetProposal { BuildAfterGeneration = true }));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Metadata_approval_completes_without_waiting_for_materialization(bool hasDeferredMaterialization)
    {
        Assert.Equal(
            "Completed",
            CodeMasterAgentToolFactory.ResolveStatusAfterMetadataApplication(hasDeferredMaterialization));
    }

    [Fact]
    public void Work_batch_with_client_actions_waits_for_actual_client_completion()
    {
        Assert.Equal(
            "AwaitingClientExecution",
            CodeMasterAgentToolFactory.ResolveStatusAfterMetadataApplication(
                hasDeferredMaterialization: true,
                belongsToWorkBatch: true));
    }

    [Fact]
    public void OversizedClientActionFallback_PreservesAllActions()
    {
        var result = new ProjectChangeSetExecutionResult
        {
            ProjectId = UnsafeJavascriptId,
            Summary = "Large change set",
            ClientActions =
            {
                new AiClientActionDto { Action = "generateProjectCode", ProjectId = UnsafeJavascriptId },
                new AiClientActionDto { Action = "buildProject", ProjectId = UnsafeJavascriptId }
            }
        };

        var fallback = CodeMasterAgentToolFactory.BuildClientActionResultFallback(result);
        var json = JsonSerializer.Serialize(fallback, AgentJsonSerializer.Options);

        Assert.Contains("generateProjectCode", json, StringComparison.Ordinal);
        Assert.Contains("buildProject", json, StringComparison.Ordinal);
        Assert.Contains("resultTruncated", json, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildFailure_TriggersAutomaticRepairAnalysis()
    {
        Assert.True(AiConversationService.IsAutomaticRepairTrigger(new CompleteAiClientActionsRequest
        {
            Success = false,
            FailedAction = "buildProject",
            ErrorMessage = "dotnet build failed"
        }));
        Assert.True(AiConversationService.IsAutomaticRepairTrigger(new CompleteAiClientActionsRequest
        {
            Success = false,
            FailedAction = "buildProject"
        }));
        Assert.False(AiConversationService.IsAutomaticRepairTrigger(new CompleteAiClientActionsRequest
        {
            Success = true,
            FailedAction = "buildProject"
        }));
    }

    [Fact]
    public void AnyAllowedActionFailure_TriggersAutomaticRepair()
    {
        foreach (var action in new[] { "buildProject", "testProject", "generateProjectCode", "generateProjectIncrementalCode", "generateProjectSmartCode", "generateSmartCode", "migrateDatabase", "startProject", "stopProject", "enhanceUiPage", "downloadTemplate" })
        {
            Assert.True(
                AiConversationService.IsAutomaticRepairTrigger(new CompleteAiClientActionsRequest
                {
                    Success = false,
                    FailedAction = action,
                    ErrorMessage = "failed"
                }),
                $"Expected {action} to trigger automatic repair.");
        }
    }

    [Fact]
    public void UnknownActionFailure_KeepsLegacyKeywordBehavior()
    {
        Assert.True(AiConversationService.IsAutomaticRepairTrigger(new CompleteAiClientActionsRequest
        {
            Success = false,
            FailedAction = "randomAction",
            ErrorMessage = "build failed with errors"
        }));
        Assert.False(AiConversationService.IsAutomaticRepairTrigger(new CompleteAiClientActionsRequest
        {
            Success = false,
            FailedAction = "randomAction",
            ErrorMessage = "some other error"
        }));
        Assert.False(AiConversationService.IsAutomaticRepairTrigger(new CompleteAiClientActionsRequest
        {
            Success = false,
            FailedAction = null,
            ErrorMessage = "some other error"
        }));
    }

    [Fact]
    public void BuildFailureSignature_StripsVolatileDetailsButKeepsRootCause()
    {
        var first = AiConversationService.BuildFailureSignature(new CompleteAiClientActionsRequest
        {
            Success = false,
            FailedAction = "buildProject",
            ErrorMessage = "2026-08-06 09:30:00 error CS0103 Order.cs(42,18): The name 'Foo' does not exist in the current context"
        });
        var second = AiConversationService.BuildFailureSignature(new CompleteAiClientActionsRequest
        {
            Success = false,
            FailedAction = "buildProject",
            ErrorMessage = "2026-08-06 09:45:12 error CS0103 Order.cs(99,5): The name 'Foo' does not exist in the current context"
        });

        Assert.Equal(first, second);
        Assert.Contains("CS0103", first, StringComparison.Ordinal);
        Assert.NotEqual(string.Empty, first);
    }

    [Fact]
    public void BuildFailureSignature_DiffersForDifferentRootCauses()
    {
        var signatureA = AiConversationService.BuildFailureSignature(new CompleteAiClientActionsRequest
        {
            ErrorMessage = "error CS0103: The name 'Foo' does not exist in the current context"
        });
        var signatureB = AiConversationService.BuildFailureSignature(new CompleteAiClientActionsRequest
        {
            ErrorMessage = "error CS0246: The type or namespace name 'Bar' could not be found"
        });

        Assert.NotEqual(signatureA, signatureB);
    }

    [Fact]
    public void RepairGuidance_IsActionSpecific()
    {
        Assert.Contains("migration", AiConversationService.BuildRepairActionGuidance("migrateDatabase"), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("test", AiConversationService.BuildRepairActionGuidance("testProject"), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("generation", AiConversationService.BuildRepairActionGuidance("generateProjectCode"), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("process", AiConversationService.BuildRepairActionGuidance("startProject"), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("blueprint", AiConversationService.BuildRepairActionGuidance("enhanceUiPage"), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("metadata", AiConversationService.BuildRepairActionGuidance("unknownAction"), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NormalizeFailedAction_FallsBackToBuildProject()
    {
        Assert.Equal("buildProject", AiConversationService.NormalizeFailedAction(null));
        Assert.Equal("buildProject", AiConversationService.NormalizeFailedAction("unknown"));
        Assert.Equal("migrateDatabase", AiConversationService.NormalizeFailedAction("migrateDatabase"));
    }

    [Fact]
    public void RepairRequestIds_TrackAndLimitTheRepairAttempt()
    {
        var first = AiConversationService.BuildRepairRequestId("request-1", 10, 1);
        var third = AiConversationService.BuildRepairRequestId(first, 10, 3);

        Assert.Equal("request-1.repair1", first);
        Assert.Equal("request-1.repair3", third);
        Assert.Equal(3, AiConversationService.GetRepairAttempt(third));
        Assert.True(third.Length <= 64);
    }
}
