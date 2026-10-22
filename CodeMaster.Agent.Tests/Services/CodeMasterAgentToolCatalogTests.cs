using CodeMaster.Agent.Contracts;
using System.Text.Json;
using CodeMaster.Agent.Services;
using CodeMaster.Agent.Tools;
using Microsoft.Extensions.AI;

namespace CodeMaster.Agent.Tests.Services;

public class CodeMasterAgentToolCatalogTests
{
    [Fact]
    public async Task Entity_blueprint_tool_accepts_named_complex_argument()
    {
        EntityBlueprintQuery? captured = null;
        var tools = CodeMasterAgentToolCatalog.Create(
            includeProjectSummary: true,
            getProjectSummary: () => Task.FromResult("summary"),
            includeProjectStructure: true,
            getProjectStructure: () => Task.FromResult("project"),
            includeCodegenCatalog: true,
            getCodegenCatalog: () => Task.FromResult("catalog"),
            includeEntityBlueprint: true,
            getEntityBlueprint: query =>
            {
                captured = query;
                return Task.FromResult("entity");
            },
            includeUiPageBlueprint: true,
            getUiPageBlueprint: _ => Task.FromResult("page"),
            includeDictionaryRead: false,
            getProjectDictionaries: _ => Task.FromResult("dictionaries"),
            includeChangeTools: false,
            proposeProjectChangeSet: _ => Task.FromResult("change"),
            proposeUiPageEnhancement: _ => Task.FromResult("ui"),
            includeDictionaryChange: false,
            proposeDictionaryChange: _ => Task.FromResult("dictionary-change"));
        var function = tools.OfType<AIFunction>()
            .Single(item => item.Name == CodeMasterAgentToolCatalog.GetEntityBlueprint);

        var result = await function.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?>
        {
            ["query"] = new EntityBlueprintQuery
            {
                EntityId = 2077646988983275500,
                EntityName = "Order"
            }
        }));

        Assert.Equal("entity", result?.ToString());
        Assert.NotNull(captured);
        Assert.Equal(2077646988983275500, captured.EntityId);
        Assert.Equal("Order", captured.EntityName);
    }

    [Fact]
    public void Read_tools_expose_summary_and_catalog_separately_from_full_structure()
    {
        var tools = CodeMasterAgentToolCatalog.Create(
            includeProjectSummary: true,
            getProjectSummary: () => Task.FromResult("summary"),
            includeProjectStructure: true,
            getProjectStructure: () => Task.FromResult("project"),
            includeCodegenCatalog: true,
            getCodegenCatalog: () => Task.FromResult("catalog"),
            includeEntityBlueprint: true,
            getEntityBlueprint: _ => Task.FromResult("entity"),
            includeUiPageBlueprint: true,
            getUiPageBlueprint: _ => Task.FromResult("page"),
            includeDictionaryRead: false,
            getProjectDictionaries: _ => Task.FromResult("dictionaries"),
            includeChangeTools: false,
            proposeProjectChangeSet: _ => Task.FromResult("change"),
            proposeUiPageEnhancement: _ => Task.FromResult("ui"),
            includeDictionaryChange: false,
            proposeDictionaryChange: _ => Task.FromResult("dictionary-change"));

        var names = tools.Select(tool => tool.Name).ToList();

        Assert.Contains(CodeMasterAgentToolCatalog.GetProjectSummary, names);
        Assert.Contains(CodeMasterAgentToolCatalog.GetProjectStructure, names);
        Assert.Contains(CodeMasterAgentToolCatalog.GetCodegenCatalog, names);
        Assert.Equal(5, names.Count);
    }

    [Fact]
    public void Local_catalog_can_expose_only_tools_advertised_by_an_older_server()
    {
        var tools = CodeMasterAgentToolCatalog.Create(
            includeProjectSummary: false,
            getProjectSummary: () => Task.FromResult("summary"),
            includeProjectStructure: true,
            getProjectStructure: () => Task.FromResult("project"),
            includeCodegenCatalog: false,
            getCodegenCatalog: () => Task.FromResult("catalog"),
            includeEntityBlueprint: true,
            getEntityBlueprint: _ => Task.FromResult("entity"),
            includeUiPageBlueprint: true,
            getUiPageBlueprint: _ => Task.FromResult("page"),
            includeDictionaryRead: false,
            getProjectDictionaries: _ => Task.FromResult("dictionaries"),
            includeChangeTools: false,
            proposeProjectChangeSet: _ => Task.FromResult("change"),
            proposeUiPageEnhancement: _ => Task.FromResult("ui"),
            includeDictionaryChange: false,
            proposeDictionaryChange: _ => Task.FromResult("dictionary-change"));

        var names = tools.Select(tool => tool.Name).ToList();

        Assert.DoesNotContain(CodeMasterAgentToolCatalog.GetProjectSummary, names);
        Assert.DoesNotContain(CodeMasterAgentToolCatalog.GetCodegenCatalog, names);
        Assert.Contains(CodeMasterAgentToolCatalog.GetProjectStructure, names);
        Assert.Equal(3, names.Count);
    }

    [Fact]
    public void Change_set_schema_exposes_compact_copied_field_sources()
    {
        var tools = CodeMasterAgentToolCatalog.Create(
            includeProjectSummary: true,
            getProjectSummary: () => Task.FromResult("summary"),
            includeProjectStructure: false,
            getProjectStructure: () => Task.FromResult("project"),
            includeCodegenCatalog: true,
            getCodegenCatalog: () => Task.FromResult("catalog"),
            includeEntityBlueprint: true,
            getEntityBlueprint: _ => Task.FromResult("entity"),
            includeUiPageBlueprint: false,
            getUiPageBlueprint: _ => Task.FromResult("page"),
            includeDictionaryRead: false,
            getProjectDictionaries: _ => Task.FromResult("dictionaries"),
            includeChangeTools: true,
            proposeProjectChangeSet: _ => Task.FromResult("change"),
            proposeUiPageEnhancement: _ => Task.FromResult("ui"),
            includeDictionaryChange: false,
            proposeDictionaryChange: _ => Task.FromResult("dictionary-change"));
        var function = tools.OfType<AIFunction>()
            .Single(item => item.Name == CodeMasterAgentToolCatalog.ProposeProjectChangeSet);
        var schema = function.JsonSchema.ToString();

        Assert.Contains("copyFieldsFromEntityId", schema);
        Assert.Contains("copyFieldsFromEntityName", schema);
    }

    [Fact]
    public async Task Change_set_tool_accepts_native_arrays_for_json_backed_field_metadata()
    {
        ProjectChangeSetProposal? captured = null;
        var tools = CodeMasterAgentToolCatalog.Create(
            includeProjectSummary: false,
            getProjectSummary: () => Task.FromResult("summary"),
            includeProjectStructure: false,
            getProjectStructure: () => Task.FromResult("project"),
            includeCodegenCatalog: false,
            getCodegenCatalog: () => Task.FromResult("catalog"),
            includeEntityBlueprint: false,
            getEntityBlueprint: _ => Task.FromResult("entity"),
            includeUiPageBlueprint: false,
            getUiPageBlueprint: _ => Task.FromResult("page"),
            includeDictionaryRead: false,
            getProjectDictionaries: _ => Task.FromResult("dictionaries"),
            includeChangeTools: true,
            proposeProjectChangeSet: proposal =>
            {
                captured = proposal;
                return Task.FromResult("prepared");
            },
            proposeUiPageEnhancement: _ => Task.FromResult("ui"),
            includeDictionaryChange: false,
            proposeDictionaryChange: _ => Task.FromResult("dictionary-change"));
        var function = tools.OfType<AIFunction>()
            .Single(item => item.Name == CodeMasterAgentToolCatalog.ProposeProjectChangeSet);
        using var document = JsonDocument.Parse("""
        {
          "summary": "Create issue fields",
          "modules": [],
          "entities": [
            {
              "moduleId": "10",
              "name": "Issue",
              "description": "问题",
              "fields": [
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
              ]
            }
          ]
        }
        """);

        var result = await function.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?>
        {
            ["proposal"] = document.RootElement.Clone()
        }));

        Assert.Equal("prepared", result?.ToString());
        var field = Assert.Single(Assert.Single(captured!.Entities).Fields);
        Assert.Equal("{\"loadMode\":\"batch\"}", field.SelectOptions);
        Assert.Equal("[\"NickName\",\"UserName\"]", field.RelatedEntityDisplayFields);
        Assert.Contains("AssigneeName", field.ResultMappings, StringComparison.Ordinal);
    }

    [Fact]
    public void Dictionary_changes_are_batched_into_project_change_set()
    {
        var tools = CodeMasterAgentToolCatalog.Create(
            includeProjectSummary: false,
            getProjectSummary: () => Task.FromResult("summary"),
            includeProjectStructure: false,
            getProjectStructure: () => Task.FromResult("project"),
            includeCodegenCatalog: false,
            getCodegenCatalog: () => Task.FromResult("catalog"),
            includeEntityBlueprint: false,
            getEntityBlueprint: _ => Task.FromResult("entity"),
            includeUiPageBlueprint: false,
            getUiPageBlueprint: _ => Task.FromResult("page"),
            includeDictionaryRead: true,
            getProjectDictionaries: _ => Task.FromResult("dictionaries"),
            includeChangeTools: false,
            proposeProjectChangeSet: _ => Task.FromResult("change"),
            proposeUiPageEnhancement: _ => Task.FromResult("ui"),
            includeDictionaryChange: true,
            proposeDictionaryChange: _ => Task.FromResult("dictionary-change"));

        var names = tools.Select(tool => tool.Name).ToList();
        Assert.Equal([CodeMasterAgentToolCatalog.GetProjectDictionaries], names);

        var changeTools = CodeMasterAgentToolCatalog.Create(
            includeProjectSummary: false,
            getProjectSummary: () => Task.FromResult("summary"),
            includeProjectStructure: false,
            getProjectStructure: () => Task.FromResult("project"),
            includeCodegenCatalog: false,
            getCodegenCatalog: () => Task.FromResult("catalog"),
            includeEntityBlueprint: false,
            getEntityBlueprint: _ => Task.FromResult("entity"),
            includeUiPageBlueprint: false,
            getUiPageBlueprint: _ => Task.FromResult("page"),
            includeDictionaryRead: false,
            getProjectDictionaries: _ => Task.FromResult("dictionaries"),
            includeChangeTools: true,
            proposeProjectChangeSet: _ => Task.FromResult("change"),
            proposeUiPageEnhancement: _ => Task.FromResult("ui"),
            includeDictionaryChange: false,
            proposeDictionaryChange: _ => Task.FromResult("dictionary-change"));
        var schema = changeTools.OfType<AIFunction>()
            .Single(item => item.Name == CodeMasterAgentToolCatalog.ProposeProjectChangeSet)
            .JsonSchema.ToString();
        Assert.Contains("dictionaries", schema);
        Assert.Contains("dictType", schema);
        Assert.Contains("items", schema);
    }

    [Fact]
    public async Task Work_plan_tools_record_intent_without_using_an_approval_tool()
    {
        UpsertAiProjectWorkPlanRequest? captured = null;
        var tools = CodeMasterAgentToolCatalog.Create(
            includeProjectSummary: false,
            getProjectSummary: () => Task.FromResult("summary"),
            includeProjectStructure: false,
            getProjectStructure: () => Task.FromResult("project"),
            includeCodegenCatalog: false,
            getCodegenCatalog: () => Task.FromResult("catalog"),
            includeEntityBlueprint: false,
            getEntityBlueprint: _ => Task.FromResult("entity"),
            includeUiPageBlueprint: false,
            getUiPageBlueprint: _ => Task.FromResult("page"),
            includeDictionaryRead: false,
            getProjectDictionaries: _ => Task.FromResult("dictionaries"),
            includeChangeTools: false,
            proposeProjectChangeSet: _ => Task.FromResult("change"),
            proposeUiPageEnhancement: _ => Task.FromResult("ui"),
            includeDictionaryChange: false,
            proposeDictionaryChange: _ => Task.FromResult("dictionary-change"),
            includeWorkPlan: true,
            getProjectWorkPlan: () => Task.FromResult("plan"),
            getProjectCompletionStatus: () => Task.FromResult("completion"),
            getProjectWorkTask: _ => Task.FromResult("task"),
            upsertProjectWorkPlan: input =>
            {
                captured = input;
                return Task.FromResult("saved");
            });

        var names = tools.Select(tool => tool.Name).ToList();
        Assert.Equal(
            [
                CodeMasterAgentToolCatalog.GetProjectCompletionStatus,
                CodeMasterAgentToolCatalog.GetProjectWorkTask,
                CodeMasterAgentToolCatalog.GetProjectWorkPlan,
                CodeMasterAgentToolCatalog.UpsertProjectWorkPlan
            ],
            names);

        var upsert = tools.OfType<AIFunction>()
            .Single(item => item.Name == CodeMasterAgentToolCatalog.UpsertProjectWorkPlan);
        var result = await upsert.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?>
        {
            ["input"] = new UpsertAiProjectWorkPlanRequest
            {
                ConversationId = 1,
                ProjectId = 2,
                Title = "Order workflow"
            }
        }));

        Assert.Equal("saved", result?.ToString());
        Assert.Equal("Order workflow", captured?.Title);
        Assert.DoesNotContain(CodeMasterAgentToolCatalog.ProposeProjectChangeSet, names);
    }

    [Fact]
    public async Task Work_plan_can_expose_a_controlled_supersede_operation()
    {
        var tools = CodeMasterAgentToolCatalog.Create(
            includeProjectSummary: false,
            getProjectSummary: () => Task.FromResult("summary"),
            includeProjectStructure: false,
            getProjectStructure: () => Task.FromResult("project"),
            includeCodegenCatalog: false,
            getCodegenCatalog: () => Task.FromResult("catalog"),
            includeEntityBlueprint: false,
            getEntityBlueprint: _ => Task.FromResult("entity"),
            includeUiPageBlueprint: false,
            getUiPageBlueprint: _ => Task.FromResult("page"),
            includeDictionaryRead: false,
            getProjectDictionaries: _ => Task.FromResult("dictionaries"),
            includeChangeTools: false,
            proposeProjectChangeSet: _ => Task.FromResult("change"),
            proposeUiPageEnhancement: _ => Task.FromResult("ui"),
            includeDictionaryChange: false,
            proposeDictionaryChange: _ => Task.FromResult("dictionary-change"),
            includeWorkPlan: true,
            getProjectWorkPlan: () => Task.FromResult("plan"),
            upsertProjectWorkPlan: _ => Task.FromResult("saved"),
            supersedeProjectWorkPlan: input => Task.FromResult($"superseded:{input.PlanId}"));

        var supersede = tools.OfType<AIFunction>()
            .Single(item => item.Name == CodeMasterAgentToolCatalog.SupersedeProjectWorkPlan);
        var result = await supersede.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?>
        {
            ["input"] = new SupersedeAiProjectWorkPlanRequest { PlanId = 42 }
        }));

        Assert.Equal("superseded:42", result?.ToString());
    }

    [Fact]
    public async Task Work_batch_tool_is_separate_from_non_approval_plan_updates()
    {
        ProposeAiProjectWorkBatchRequest? captured = null;
        var tools = CodeMasterAgentToolCatalog.Create(
            includeProjectSummary: false,
            getProjectSummary: () => Task.FromResult("summary"),
            includeProjectStructure: false,
            getProjectStructure: () => Task.FromResult("project"),
            includeCodegenCatalog: false,
            getCodegenCatalog: () => Task.FromResult("catalog"),
            includeEntityBlueprint: false,
            getEntityBlueprint: _ => Task.FromResult("entity"),
            includeUiPageBlueprint: false,
            getUiPageBlueprint: _ => Task.FromResult("page"),
            includeDictionaryRead: false,
            getProjectDictionaries: _ => Task.FromResult("dictionaries"),
            includeChangeTools: false,
            proposeProjectChangeSet: _ => Task.FromResult("change"),
            proposeUiPageEnhancement: _ => Task.FromResult("ui"),
            includeDictionaryChange: false,
            proposeDictionaryChange: _ => Task.FromResult("dictionary-change"),
            includeWorkPlan: true,
            getProjectWorkPlan: () => Task.FromResult("plan"),
            upsertProjectWorkPlan: _ => Task.FromResult("saved"),
            includeWorkBatch: true,
            proposeProjectWorkBatch: input =>
            {
                captured = input;
                return Task.FromResult("pending");
            });

        var batchTool = tools.OfType<AIFunction>()
            .Single(item => item.Name == CodeMasterAgentToolCatalog.ProposeProjectWorkBatch);
        var result = await batchTool.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?>
        {
            ["input"] = new ProposeAiProjectWorkBatchRequest
            {
                PlanId = 42,
                ExpectedStructureVersion = 3,
                TaskKeys = ["foundation.customers"]
            }
        }));

        Assert.Equal("pending", result?.ToString());
        Assert.Equal(42, captured?.PlanId);
        Assert.Equal(["foundation.customers"], captured?.TaskKeys);
    }

    [Fact]
    public void Source_repair_tools_are_never_registered()
    {
        var tools = CodeMasterAgentToolCatalog.Create(
            includeProjectSummary: false,
            getProjectSummary: () => Task.FromResult("summary"),
            includeProjectStructure: false,
            getProjectStructure: () => Task.FromResult("project"),
            includeCodegenCatalog: false,
            getCodegenCatalog: () => Task.FromResult("catalog"),
            includeEntityBlueprint: false,
            getEntityBlueprint: _ => Task.FromResult("entity"),
            includeUiPageBlueprint: false,
            getUiPageBlueprint: _ => Task.FromResult("page"),
            includeDictionaryRead: false,
            getProjectDictionaries: _ => Task.FromResult("dictionaries"),
            includeChangeTools: false,
            proposeProjectChangeSet: _ => Task.FromResult("change"),
            proposeUiPageEnhancement: _ => Task.FromResult("ui"),
            includeDictionaryChange: false,
            proposeDictionaryChange: _ => Task.FromResult("dictionary-change"));

        var names = tools.Select(item => item.Name).ToList();
        // The agent never reads or patches CodeMaster source code (2026-10-20), so the
        // catalogue has no way to register these tools any more.
        Assert.DoesNotContain(CodeMasterAgentToolCatalog.InspectCodeMasterSource, names);
        Assert.DoesNotContain(CodeMasterAgentToolCatalog.ApplyTestSourceRepair, names);
    }

    [Fact]
    public void Submit_business_plan_tool_is_registered_when_enabled_and_absent_otherwise()
    {
        var toolsWith = CodeMasterAgentToolCatalog.Create(
            includeProjectSummary: false,
            getProjectSummary: () => Task.FromResult("summary"),
            includeProjectStructure: false,
            getProjectStructure: () => Task.FromResult("project"),
            includeCodegenCatalog: false,
            getCodegenCatalog: () => Task.FromResult("catalog"),
            includeEntityBlueprint: false,
            getEntityBlueprint: _ => Task.FromResult("entity"),
            includeUiPageBlueprint: false,
            getUiPageBlueprint: _ => Task.FromResult("page"),
            includeDictionaryRead: false,
            getProjectDictionaries: _ => Task.FromResult("dictionaries"),
            includeChangeTools: false,
            proposeProjectChangeSet: _ => Task.FromResult("change"),
            proposeUiPageEnhancement: _ => Task.FromResult("ui"),
            includeDictionaryChange: false,
            proposeDictionaryChange: _ => Task.FromResult("dictionary-change"),
            includeSubmitBusinessPlan: true,
            submitBusinessPlan: _ => Task.FromResult("submitted"));

        var toolsWithout = CodeMasterAgentToolCatalog.Create(
            includeProjectSummary: false,
            getProjectSummary: () => Task.FromResult("summary"),
            includeProjectStructure: false,
            getProjectStructure: () => Task.FromResult("project"),
            includeCodegenCatalog: false,
            getCodegenCatalog: () => Task.FromResult("catalog"),
            includeEntityBlueprint: false,
            getEntityBlueprint: _ => Task.FromResult("entity"),
            includeUiPageBlueprint: false,
            getUiPageBlueprint: _ => Task.FromResult("page"),
            includeDictionaryRead: false,
            getProjectDictionaries: _ => Task.FromResult("dictionaries"),
            includeChangeTools: false,
            proposeProjectChangeSet: _ => Task.FromResult("change"),
            proposeUiPageEnhancement: _ => Task.FromResult("ui"),
            includeDictionaryChange: false,
            proposeDictionaryChange: _ => Task.FromResult("dictionary-change"),
            includeSubmitBusinessPlan: false,
            submitBusinessPlan: null);

        Assert.Contains(CodeMasterAgentToolCatalog.SubmitBusinessPlan, toolsWith.Select(t => t.Name));
        Assert.DoesNotContain(CodeMasterAgentToolCatalog.SubmitBusinessPlan, toolsWithout.Select(t => t.Name));
    }

    [Fact]
    public async Task Submit_business_plan_tool_invokes_the_supplied_handler()
    {
        BusinessPlanDefinition? captured = null;
        var tools = CodeMasterAgentToolCatalog.Create(
            includeProjectSummary: false,
            getProjectSummary: () => Task.FromResult("summary"),
            includeProjectStructure: false,
            getProjectStructure: () => Task.FromResult("project"),
            includeCodegenCatalog: false,
            getCodegenCatalog: () => Task.FromResult("catalog"),
            includeEntityBlueprint: false,
            getEntityBlueprint: _ => Task.FromResult("entity"),
            includeUiPageBlueprint: false,
            getUiPageBlueprint: _ => Task.FromResult("page"),
            includeDictionaryRead: false,
            getProjectDictionaries: _ => Task.FromResult("dictionaries"),
            includeChangeTools: false,
            proposeProjectChangeSet: _ => Task.FromResult("change"),
            proposeUiPageEnhancement: _ => Task.FromResult("ui"),
            includeDictionaryChange: false,
            proposeDictionaryChange: _ => Task.FromResult("dictionary-change"),
            includeSubmitBusinessPlan: true,
            submitBusinessPlan: input =>
            {
                captured = input;
                return Task.FromResult("compiled");
            });

        var submit = tools.OfType<AIFunction>()
            .Single(item => item.Name == CodeMasterAgentToolCatalog.SubmitBusinessPlan);

        var input = new BusinessPlanDefinition
        {
            Modules = [new BusinessModuleDefinition { Key = "engineering", TechnicalName = "Engineering", DisplayName = "工程管理" }]
        };
        var result = await submit.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?>
        {
            ["input"] = input
        }));

        Assert.Equal("compiled", result?.ToString());
        Assert.NotNull(captured);
        Assert.Equal("engineering", captured!.Modules[0].Key);
    }
}
