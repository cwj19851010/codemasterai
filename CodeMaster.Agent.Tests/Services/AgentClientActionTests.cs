using System.Text.Json;
using CodeMaster.Agent.Contracts;
using CodeMaster.Agent.Serialization;
using CodeMaster.Agent.Services;
using CodeMaster.Agent.Tools;
using CodeMaster.Application.Dtos.CodeGen;

namespace CodeMaster.Agent.Tests.Services;

public class AgentClientActionTests
{
    private const long UnsafeJavascriptId = 2077646988983275501;

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
    public void AddClientActions_CollapsesFullProjectGenerationToOneProjectAction()
    {
        var proposal = new ProjectChangeSetProposal { GenerationMode = "Full" };
        var result = new ProjectChangeSetExecutionResult { ProjectId = 10 };
        var entities = new[]
        {
            new ModuleEntityDto { Id = UnsafeJavascriptId, Name = "Order" },
            new ModuleEntityDto { Id = UnsafeJavascriptId + 1, Name = "OrderItem" }
        };

        CodeMasterProjectChangeSetService.AddClientActions(10, proposal, entities, result);

        Assert.Equal(3, result.ClientActions.Count);
        var action = result.ClientActions[0];
        Assert.Equal("generateProjectCode", action.Action);
        Assert.Equal(10, action.ProjectId);
        Assert.Empty(action.EntityIds);
        Assert.Equal("buildProject", result.ClientActions[1].Action);
        Assert.Equal("testProject", result.ClientActions[2].Action);
    }

    [Fact]
    public void AddClientActions_CollapsesIncrementalGenerationAndKeepsExactIds()
    {
        var proposal = new ProjectChangeSetProposal
        {
            GenerationMode = "Incremental",
            GenerationEntityIds = { UnsafeJavascriptId, UnsafeJavascriptId + 1 }
        };
        var result = new ProjectChangeSetExecutionResult { ProjectId = 10 };

        CodeMasterProjectChangeSetService.AddClientActions(10, proposal, Array.Empty<ModuleEntityDto>(), result);

        Assert.Equal(3, result.ClientActions.Count);
        var action = result.ClientActions[0];
        Assert.Equal("generateProjectIncrementalCode", action.Action);
        Assert.Equal(new[] { UnsafeJavascriptId, UnsafeJavascriptId + 1 }, action.EntityIds);
        Assert.Equal("buildProject", result.ClientActions[1].Action);
        Assert.Equal("testProject", result.ClientActions[2].Action);
    }

    [Fact]
    public void AddClientActions_SkipsTestWhenTestAfterGenerationDisabled()
    {
        var proposal = new ProjectChangeSetProposal
        {
            GenerationMode = "Incremental",
            GenerationEntityIds = { UnsafeJavascriptId },
            TestAfterGeneration = false
        };
        var result = new ProjectChangeSetExecutionResult { ProjectId = 10 };

        CodeMasterProjectChangeSetService.AddClientActions(10, proposal, Array.Empty<ModuleEntityDto>(), result);

        Assert.Equal(2, result.ClientActions.Count);
        Assert.Equal("generateProjectIncrementalCode", result.ClientActions[0].Action);
        Assert.Equal("buildProject", result.ClientActions[1].Action);
    }

    [Fact]
    public void RequiresBuildPermission_OnlyRequiresBuildWhenClientActionsWillRun()
    {
        Assert.False(CodeMasterProjectChangeSetService.RequiresBuildPermission(
            new ProjectChangeSetProposal { TestAfterGeneration = true }));
        Assert.True(CodeMasterProjectChangeSetService.RequiresBuildPermission(
            new ProjectChangeSetProposal { GenerationMode = "Incremental", Entities = { new CreateEntityProposal() } }));
        Assert.True(CodeMasterProjectChangeSetService.RequiresBuildPermission(
            new ProjectChangeSetProposal { GenerationMode = "Full" }));
        Assert.True(CodeMasterProjectChangeSetService.RequiresBuildPermission(
            new ProjectChangeSetProposal { BuildAfterGeneration = true }));
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
        foreach (var action in new[] { "buildProject", "testProject", "generateProjectCode", "generateProjectIncrementalCode", "migrateDatabase", "startProject", "stopProject", "enhanceUiPage", "downloadTemplate" })
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
