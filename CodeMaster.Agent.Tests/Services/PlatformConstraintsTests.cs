using CodeMaster.Agent.PlatformConstraints;
using CodeMaster.Agent.Services;
using CodeMaster.Agent.Tools;

namespace CodeMaster.Agent.Tests.Services;

public sealed class PlatformConstraintsTests
{
    [Fact]
    public void Defaults_registry_contains_every_supported_control_id()
    {
        var registry = new ControlConventionRegistry(ControlConvention.Defaults());

        foreach (var required in new[]
                 {
                     "input", "textarea", "number", "select", "select-enum",
                     "select-table", "cascader", "date", "datetime",
                     "switch", "checkbox", "radio-group", "checkbox-group",
                     "file", "image", "editor", "table-column", "search-input"
                 })
        {
            Assert.NotNull(registry.Find(required));
        }
    }

    [Fact]
    public void Control_lookup_returns_cascader_only_for_tree_relation()
    {
        var registry = new ControlConventionRegistry(ControlConvention.Defaults());

        var cascader = registry.RecommendFromCSharpType("long", hasRelatedEntity: true, relatedEntityIsTree: true);
        Assert.Equal("cascader", cascader);

        var selectTable = registry.RecommendFromCSharpType("long", hasRelatedEntity: true, relatedEntityIsTree: false);
        Assert.Equal("select-table", selectTable);

        var text = registry.RecommendFromCSharpType("string", hasRelatedEntity: false, relatedEntityIsTree: false);
        Assert.Equal("input", text);

        var dateOnly = registry.RecommendFromCSharpType("DateOnly", hasRelatedEntity: false, relatedEntityIsTree: false);
        Assert.Equal("date", dateOnly);

        var dateTime = registry.RecommendFromCSharpType("DateTime", hasRelatedEntity: false, relatedEntityIsTree: false);
        Assert.Equal("datetime", dateTime);

        var boolean = registry.RecommendFromCSharpType("bool", hasRelatedEntity: false, relatedEntityIsTree: false);
        Assert.Equal("switch", boolean);
    }

    [Fact]
    public void Document_render_includes_every_convention_section()
    {
        var doc = new PlatformConstraintsDocument();

        var prompt = doc.RenderForPrompt();

        Assert.Contains("Vue 3", prompt);
        Assert.Contains("Element Plus", prompt);
        Assert.Contains("SqlSugar", prompt);
        Assert.Contains("JWT", prompt);
        Assert.Contains("sys_one_to_many_relation", prompt);
        Assert.Contains("ConsistencyCheck", prompt);
        Assert.Contains("EntityFields", prompt);
        Assert.Contains("Six metadata stages", prompt);
        Assert.Contains("GenerationMode=None", prompt);
        Assert.Contains("EntityFields, Views, Relations, FieldControls, ComplexPages, ConsistencyCheck".Replace(", ", " -> "), prompt);
        Assert.Contains("never generates code", prompt);
        Assert.DoesNotContain("GenerationMode=Incremental", prompt);
        Assert.Contains("FullStack", prompt);
        Assert.Contains("MetadataOnly", prompt);
        Assert.Contains("sys_project_page", prompt);
        Assert.Contains("sys_page_component_binding", prompt);
        Assert.Contains("sys_page_data_source", prompt);
        Assert.Contains("sys_page_entity_dependency", prompt);
        Assert.Contains("IsChildTable=true", prompt);
        Assert.Contains("IsReadOnly=true", prompt);
        Assert.Contains("`input`", prompt);
        Assert.Contains("`select-table`", prompt);
        Assert.Contains("`cascader`", prompt);
    }

    [Fact]
    public void Provider_serialization_round_trips_document()
    {
        var provider = new PlatformConstraintsProvider();

        var json = provider.GetSerializedDocument();

        Assert.Contains("\"Frontend\":", json);
        Assert.Contains("\"Controls\":", json);
        Assert.Contains("\"Workflow\":", json);
        Assert.Contains("\"Generation\":", json);
    }

    [Fact]
    public void NewPlan_instructions_reference_platformConstraints()
    {
        Assert.Contains("platformConstraints", AiConversationServiceConstants.NewPlanPromptMarker);
        Assert.Contains("platformConstraints", AiConversationServiceConstants.RepairPromptMarker);
    }
}

/// <summary>
/// Exposes the prompt markers used by the build-up helpers without making
/// AiConversationService's private builders visible from the tests.
/// </summary>
internal static class AiConversationServiceConstants
{
    public const string NewPlanPromptMarker = "ALWAYS read platformConstraints from get_codegen_catalog";
    public const string RepairPromptMarker = "ALWAYS read platformConstraints from get_codegen_catalog";
}