using CodeMaster.Application.Services.CodeGen;
using CodeMaster.Domain.Entities.CodeGen;

namespace CodeMaster.UiComposition.Tests;

public sealed class PageLayoutTreeTests
{
    [Fact]
    public void LegacyPresetGetsStableIdsAndSupportsGridOperations()
    {
        var tree = PageLayoutTree.Parse("{\"version\":1,\"root\":{\"type\":\"page\",\"children\":[{\"type\":\"row\",\"children\":[{\"type\":\"column\",\"span\":24,\"children\":[{\"type\":\"table\"}]}]}]}}");

        var row = tree.Root.Children.Single();
        var column = row.Children.Single();
        var table = column.Children.Single();
        Assert.Equal("page_root", tree.Root.ComponentId);
        Assert.Equal("row_1", row.ComponentId);
        Assert.Equal("column_1", column.ComponentId);
        Assert.Equal("table_1", table.ComponentId);

        tree.SplitColumn(column.ComponentId, 8);
        Assert.Equal(2, row.Children.Count);
        Assert.Equal(8, row.Children[0].Span);
        Assert.Equal(16, row.Children[1].Span);

        tree.SetGrid(row.Children[1].ComponentId, new PageGridSpec { Span = 16, Md = 12, Lg = 16 });
        tree.Validate();
        Assert.Contains("\"md\": 12", tree.ToJson());
    }

    [Fact]
    public void MoveRejectsCyclesAndBindingUsesStableIds()
    {
        var tree = PageLayoutTree.Empty();
        var row = tree.AddRow();
        var column = tree.AddColumn(row.ComponentId, 24);
        var card = tree.AddComponent(column.ComponentId, "card");

        Assert.Throws<InvalidOperationException>(() => tree.Move(row.ComponentId, card.ComponentId));
        tree.BindDataSource(card.ComponentId, 10, "binding_card");
        tree.BindField(card.ComponentId, 20, "binding_card_field");

        Assert.Equal(10, card.DataSourceId);
        Assert.Equal(20, card.FieldId);
        Assert.Equal("binding_card_field", card.BindingId);
    }

    [Fact]
    public void DuplicateIdsAndInvalidSpansAreRejected()
    {
        Assert.Throws<InvalidOperationException>(() => PageLayoutTree.Parse("{\"root\":{\"type\":\"page\",\"componentId\":\"page_root\",\"children\":[{\"type\":\"row\",\"componentId\":\"same\"},{\"type\":\"row\",\"componentId\":\"same\"}]}}"));
        Assert.Throws<InvalidOperationException>(() => PageLayoutTree.Parse("{\"root\":{\"type\":\"page\",\"children\":[{\"type\":\"row\",\"children\":[{\"type\":\"column\",\"span\":25}]}]}}"));
    }

    [Fact]
    public void DuplicateBindingsAnchorsCyclesAndInvalidReferencesAreRejected()
    {
        Assert.Throws<InvalidOperationException>(() => PageLayoutTree.Parse("{\"root\":{\"type\":\"page\",\"componentId\":\"page_root\",\"children\":[{\"type\":\"table\",\"componentId\":\"first\",\"bindingId\":\"same\"},{\"type\":\"table\",\"componentId\":\"second\",\"bindingId\":\"same\"}]}}"));
        Assert.Throws<InvalidOperationException>(() => PageLayoutTree.Parse("{\"root\":{\"type\":\"page\",\"componentId\":\"page_root\",\"children\":[{\"type\":\"table\",\"componentId\":\"first\",\"genId\":\"same\"},{\"type\":\"table\",\"componentId\":\"second\",\"genId\":\"same\"}]}}"));
        Assert.Throws<InvalidOperationException>(() => PageLayoutTree.Parse("{\"root\":{\"type\":\"page\",\"componentId\":\"page_root\",\"children\":[{\"type\":\"table-column\",\"componentId\":\"field\",\"fieldId\":42,\"bindingId\":\"field_binding\"}]}}"));

        var tree = PageLayoutTree.Empty();
        var row = tree.AddRow();
        row.Children.Add(tree.Root);
        Assert.Throws<InvalidOperationException>(tree.Validate);
    }

    [Theory]
    [InlineData("8,16")]
    [InlineData("6,12,6")]
    [InlineData("16,8")]
    [InlineData("10,4,10")]
    public void NonEqualGridRatios_are_preserved(string spans)
    {
        var values = spans.Split(',').Select(int.Parse).ToArray();
        var columns = string.Join(',', values.Select((span, index) =>
            $"{{\"type\":\"column\",\"componentId\":\"column_{index}\",\"span\":{span}}}"));
        var tree = PageLayoutTree.Parse($"{{\"root\":{{\"type\":\"page\",\"componentId\":\"page_root\",\"children\":[{{\"type\":\"row\",\"componentId\":\"row_1\",\"children\":[{columns}]}}]}}}}");

        Assert.Equal(values, tree.Require("row_1").Children.Select(item => item.Span!.Value).ToArray());
    }

    [Fact]
    public void StableIdentityRegenerationIsDeterministicAndUpdatesAnchors()
    {
        var tree = PageLayoutTree.Parse("{\"root\":{\"type\":\"page\",\"componentId\":\"page_root\",\"children\":[{\"type\":\"table\",\"componentId\":\"orders\",\"genId\":\"gen_binding_binding_orders\",\"dataSourceId\":10,\"bindingId\":\"binding_orders\"}]}}");

        var identities = tree.RegenerateStableIds("Copied-Page");

        Assert.Equal("copiedpage_table_1", identities.ComponentIds["orders"]);
        Assert.Equal("binding_copiedpage_1", identities.BindingIds["binding_orders"]);
        var copied = tree.Root.Children.Single();
        Assert.Equal("gen_binding_binding_copiedpage_1", copied.GenId);
        tree.Validate();
    }
}
