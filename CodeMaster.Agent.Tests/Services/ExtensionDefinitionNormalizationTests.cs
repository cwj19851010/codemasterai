using System.Text.Json;
using CodeMaster.Agent.Tools;

namespace CodeMaster.Agent.Tests.Services;

public sealed class ExtensionDefinitionNormalizationTests
{
    [Fact]
    public void Uses_display_name_as_a_deterministic_summary()
    {
        var normalized = CodeMasterAgentToolFactory.NormalizeExtensionDefinitionForTest(
            "create-view",
            """{"viewName":"aging","displayName":"应收账龄分析"}""");

        using var document = JsonDocument.Parse(normalized);
        Assert.Equal("应收账龄分析", document.RootElement.GetProperty("summary").GetString());
        Assert.Equal("aging", document.RootElement.GetProperty("viewName").GetString());
    }

    [Fact]
    public void Preserves_an_existing_summary()
    {
        var normalized = CodeMasterAgentToolFactory.NormalizeExtensionDefinitionForTest(
            "page",
            """{"summary":"原摘要","pageName":"工作台"}""");

        using var document = JsonDocument.Parse(normalized);
        Assert.Equal("原摘要", document.RootElement.GetProperty("summary").GetString());
    }
}
