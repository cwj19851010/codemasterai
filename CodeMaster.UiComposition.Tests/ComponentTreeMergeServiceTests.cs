using System.Text.Json;
using CodeMaster.Application.Services.CodeGen;
using CodeMaster.Infrastructure.VueParser.Model;

namespace CodeMaster.UiComposition.Tests;

public class ComponentTreeMergeServiceTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private readonly ComponentTreeMergeService _service = new();

    [Fact]
    public void MergeOrder_PreservesUserOrderAndMeaningfulScriptSection()
    {
        var meaningfulScript = "{\"refs\":[{\"name\":\"selected\",\"initialValue\":\"null\"}]}";
        var oldTree = Serialize(
            Node("el-input", "gen_field_2", scriptSection: meaningfulScript),
            Node("el-input", "gen_field_1"));
        var newTree = Serialize(
            Node("el-input-number", "gen_field_1"),
            Node("el-select", "gen_field_2"),
            Node("el-input", "gen_field_3"));

        var merged = Deserialize(_service.MergeOrder(newTree, oldTree));

        Assert.Equal(new[] { "gen_field_2", "gen_field_1", "gen_field_3" }, merged.Select(node => node.GenId));
        Assert.Equal(meaningfulScript, merged[0].ScriptSection);
        Assert.Equal("el-select", merged[0].Tag);
    }

    [Fact]
    public void MergeIncremental_KeepsUnchangedGeneratedNodeAndRefreshesChangedNode()
    {
        var oldTree = Serialize(
            Node("el-input", "gen_field_101", propertyValue: "user-placeholder"),
            Node("el-input", "gen_field_102", propertyValue: "stale-placeholder"));
        var newTree = Serialize(
            Node("el-select", "gen_field_101", propertyValue: "generated-placeholder"),
            Node("el-input-number", "gen_field_102", propertyValue: "fresh-placeholder"));
        var changes = new ComponentTreeChangeSet();
        changes.ChangedFieldIds.Add(102);

        var merged = Deserialize(_service.MergeIncremental(newTree, oldTree, changes));

        Assert.Equal("el-input", merged[0].Tag);
        Assert.Equal("user-placeholder", Assert.Single(merged[0].Props!).Value);
        Assert.Equal("el-input-number", merged[1].Tag);
        Assert.Equal("fresh-placeholder", Assert.Single(merged[1].Props!).Value);
    }

    [Fact]
    public void MergeIncremental_RemovesDeletedGeneratedNodeAndPreservesCustomNode()
    {
        var customNode = Node("section", id: "custom-summary");
        customNode.Content = "user content";
        var oldTree = Serialize(
            Node("el-input", "gen_field_201"),
            Node("el-input", "gen_field_202"),
            customNode);
        var newTree = Serialize(Node("el-input", "gen_field_201"));
        var changes = new ComponentTreeChangeSet();
        changes.DeletedFieldIds.Add(202);

        var merged = Deserialize(_service.MergeIncremental(newTree, oldTree, changes));

        Assert.DoesNotContain(merged, node => node.GenId == "gen_field_202");
        var preserved = Assert.Single(merged, node => node.Id == "custom-summary");
        Assert.Equal("user content", preserved.Content);
    }

    [Fact]
    public void MergeIncremental_SupportsStableComplexPageGeneratedIds()
    {
        var oldTree = Serialize(Node("el-input", "gen_binding_binding-a", propertyValue: "custom"));
        var newTree = Serialize(Node("el-select", "gen_binding_binding-a", propertyValue: "generated"));
        var changes = new ComponentTreeChangeSet();
        changes.ChangedGeneratedIds.Add("gen_binding_binding-a");

        var merged = Deserialize(_service.MergeIncremental(newTree, oldTree, changes));

        Assert.Equal("el-select", Assert.Single(merged).Tag);
        Assert.Equal("generated", Assert.Single(Assert.Single(merged).Props!).Value);
    }

    private static Component Node(
        string tag,
        string? genId = null,
        string? id = null,
        string? propertyValue = null,
        string? scriptSection = null)
    {
        return new Component
        {
            Id = id,
            Tag = tag,
            GenId = genId,
            ScriptSection = scriptSection,
            Props = propertyValue == null
                ? null
                : new List<ComponentProp>
                {
                    new() { Key = "placeholder", Value = propertyValue }
                }
        };
    }

    private static string Serialize(params Component[] components) =>
        JsonSerializer.Serialize(components, JsonOptions);

    private static List<Component> Deserialize(string json) =>
        JsonSerializer.Deserialize<List<Component>>(json, JsonOptions)!;
}
