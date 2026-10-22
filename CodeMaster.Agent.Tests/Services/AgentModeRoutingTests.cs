using CodeMaster.Agent.Contracts;
using CodeMaster.Agent.Services;
using CodeMaster.Agent.Tools;
using Microsoft.Extensions.AI;

namespace CodeMaster.Agent.Tests.Services;

/// <summary>
/// The Agent mode is the only thing that decides whether a turn may write metadata or
/// generate code. These tests pin that contract down: requirements mode must be unable to
/// reach any mutating tool, in both the server-driven and the local-client-driven paths.
/// </summary>
public sealed class AgentModeRoutingTests
{
    private static IList<AITool> BuildToolSurface() => new List<AITool>
    {
        AIFunctionFactory.Create(() => "ok", name: CodeMasterAgentToolCatalog.GetProjectSummary),
        AIFunctionFactory.Create(() => "ok", name: CodeMasterAgentToolCatalog.GetEntityBlueprint),
        AIFunctionFactory.Create(() => "ok", name: CodeMasterAgentToolCatalog.GetCodegenCatalog),
        AIFunctionFactory.Create(() => "ok", name: CodeMasterAgentToolCatalog.GetProjectWorkPlan),
        AIFunctionFactory.Create(() => "ok", name: CodeMasterAgentToolCatalog.ProposeProjectChangeSet),
        AIFunctionFactory.Create(() => "ok", name: CodeMasterAgentToolCatalog.ProposeUiPageEnhancement),
        AIFunctionFactory.Create(() => "ok", name: CodeMasterAgentToolCatalog.UpsertProjectWorkPlan),
        AIFunctionFactory.Create(() => "ok", name: CodeMasterAgentToolCatalog.SubmitBusinessPlan),
        AIFunctionFactory.Create(() => "ok", name: CodeMasterAgentToolCatalog.SupersedeProjectWorkPlan)
    };

    [Theory]
    [InlineData(AiAgentModes.Code)]
    [InlineData("code")]
    [InlineData("CODE")]
    public void Code_mode_is_recognized_case_insensitively(string value)
    {
        Assert.Equal(AiAgentModes.Code, AiAgentModes.Normalize(value));
        Assert.True(AiAgentModes.IsCode(value));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Requirements")]
    [InlineData("nonsense")]
    public void Unknown_modes_fall_back_to_the_safe_requirements_mode(string? value)
    {
        Assert.Equal(AiAgentModes.Requirements, AiAgentModes.Normalize(value));
        Assert.False(AiAgentModes.IsCode(value));
    }

    [Fact]
    public void Requirements_mode_exposes_no_mutating_tools()
    {
        var selected = AiTurnToolSelector.SelectForMode(BuildToolSurface(), AiAgentModes.Requirements);
        var names = selected.OfType<AIFunction>().Select(tool => tool.Name).ToList();

        Assert.Contains(CodeMasterAgentToolCatalog.GetProjectSummary, names);
        Assert.Contains(CodeMasterAgentToolCatalog.GetEntityBlueprint, names);

        Assert.DoesNotContain(CodeMasterAgentToolCatalog.SubmitBusinessPlan, names);
        Assert.DoesNotContain(CodeMasterAgentToolCatalog.UpsertProjectWorkPlan, names);
        Assert.DoesNotContain(CodeMasterAgentToolCatalog.SupersedeProjectWorkPlan, names);
        Assert.DoesNotContain(CodeMasterAgentToolCatalog.ProposeProjectChangeSet, names);
        Assert.DoesNotContain(CodeMasterAgentToolCatalog.ProposeUiPageEnhancement, names);
    }

    [Fact]
    public void Code_mode_exposes_the_full_tool_surface()
    {
        var tools = BuildToolSurface();
        var selected = AiTurnToolSelector.SelectForMode(tools, AiAgentModes.Code);

        Assert.Equal(tools.Count, selected.Count);
    }

    [Theory]
    [InlineData(CodeMasterAgentToolCatalog.SubmitBusinessPlan)]
    [InlineData(CodeMasterAgentToolCatalog.UpsertProjectWorkPlan)]
    [InlineData(CodeMasterAgentToolCatalog.ProposeProjectChangeSet)]
    public void Local_client_cannot_invoke_a_generating_tool_while_in_requirements_mode(string toolName)
    {
        // AvailableTools sent to the local client is advisory. This is the check the server
        // actually uses before executing a tool on the client's behalf.
        Assert.False(AiTurnToolSelector.IsToolAllowedInMode(AiAgentModes.Requirements, toolName));
        Assert.True(AiTurnToolSelector.IsToolAllowedInMode(AiAgentModes.Code, toolName));
    }

    [Fact]
    public void Local_client_may_still_read_project_metadata_in_requirements_mode()
    {
        Assert.True(AiTurnToolSelector.IsToolAllowedInMode(
            AiAgentModes.Requirements,
            CodeMasterAgentToolCatalog.GetProjectSummary));
        Assert.True(AiTurnToolSelector.IsToolAllowedInMode(
            AiAgentModes.Requirements,
            CodeMasterAgentToolCatalog.GetEntityBlueprint));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Blank_tool_names_are_rejected(string? toolName)
    {
        Assert.False(AiTurnToolSelector.IsToolAllowedInMode(AiAgentModes.Requirements, toolName));
        Assert.False(AiTurnToolSelector.IsToolAllowedInMode(AiAgentModes.Code, toolName));
    }

    [Fact]
    public void Requirements_mode_never_reports_a_mutating_tool_as_available()
    {
        var available = AiTurnToolSelector.AllowedToolNamesInMode(
            AiAgentModes.Requirements,
            BuildToolSurface().OfType<AIFunction>().Select(tool => tool.Name));

        Assert.DoesNotContain(CodeMasterAgentToolCatalog.SubmitBusinessPlan, available);
        Assert.DoesNotContain(CodeMasterAgentToolCatalog.UpsertProjectWorkPlan, available);
        Assert.Contains(CodeMasterAgentToolCatalog.GetProjectSummary, available);
    }

    [Fact]
    public void Agent_mode_is_round_tripped_through_conversation_preferences()
    {
        var preferences = AiWorkflowPreferencesStore.Normalize(
            AiWorkflowModes.Default,
            false,
            AiAgentModes.Code);

        Assert.Equal(AiAgentModes.Code, preferences.AgentMode);
        Assert.True(preferences.IsCodeMode);

        var restored = AiWorkflowPreferencesStore.Read(AiWorkflowPreferencesStore.Write(preferences));
        Assert.Equal(AiAgentModes.Code, restored.AgentMode);
        Assert.True(restored.IsCodeMode);
    }

    [Fact]
    public void Switching_back_to_requirements_clears_the_code_mode_flag()
    {
        var preferences = AiWorkflowPreferencesStore.Normalize(
            AiWorkflowModes.Default,
            false,
            AiAgentModes.Code);

        var switched = preferences with { AgentMode = AiAgentModes.Requirements };

        var restored = AiWorkflowPreferencesStore.Read(AiWorkflowPreferencesStore.Write(switched));
        Assert.False(restored.IsCodeMode);
        Assert.Equal(AiAgentModes.Requirements, restored.AgentMode);
    }

    [Fact]
    public void Legacy_preferences_without_a_mode_load_as_requirements()
    {
        // Existing conversations were stored before the mode existed, so they must open on
        // the read-only page rather than silently gaining write access.
        var restored = AiWorkflowPreferencesStore.Read("{\"workflowMode\":\"Default\",\"autoApproveApprovals\":false}");

        Assert.Equal(AiAgentModes.Requirements, restored.AgentMode);
        Assert.False(restored.IsCodeMode);
    }

    [Fact]
    public void Generation_timestamp_survives_a_preferences_round_trip()
    {
        var stamp = new DateTime(2026, 10, 12, 3, 30, 0, DateTimeKind.Utc);
        var preferences = AiWorkflowPreferencesStore.Read("{}") with { LastGeneratedAt = stamp };

        var restored = AiWorkflowPreferencesStore.Read(AiWorkflowPreferencesStore.Write(preferences));

        Assert.Equal(stamp, restored.LastGeneratedAt);
    }

    [Fact]
    public void Switching_mode_preserves_the_generation_timestamp()
    {
        var stamp = new DateTime(2026, 10, 12, 3, 30, 0, DateTimeKind.Utc);
        var preferences = AiWorkflowPreferencesStore.Read("{}") with
        {
            AgentMode = AiAgentModes.Code,
            LastGeneratedAt = stamp
        };
        var serialized = AiWorkflowPreferencesStore.Write(preferences);

        // SwitchAgentModeAsync rewrites preferences only to change the mode; the timestamp
        // must not be lost or a re-run guard would silently reset.
        var switched = AiWorkflowPreferencesStore.WithAgentMode(serialized, AiAgentModes.Requirements);

        Assert.Equal(AiAgentModes.Requirements, switched.AgentMode);
        Assert.Equal(stamp, switched.LastGeneratedAt);
    }
}
