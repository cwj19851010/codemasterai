using System.Reflection;
using CodeMaster.Core.Extensibility;
using CodeMaster.McpServer.Tools;
using ModelContextProtocol.Server;

namespace CodeMaster.UiComposition.Tests;

public sealed class UiCompositionMcpToolContractTests
{
    [Fact]
    public void ToolType_ExposesExpectedComplexPageTools()
    {
        var names = typeof(UiCompositionMcpTools)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Select(method => method.GetCustomAttribute<McpServerToolAttribute>()?.Name)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToHashSet(StringComparer.Ordinal);

        Assert.Contains("get_complex_page_context", names);
        Assert.Contains("propose_complex_page_plan", names);
    }

    [Fact]
    public void Handler_UsesGenericOptionalExtensionBoundary()
    {
        Assert.True(typeof(ICodeMasterExtension).IsAssignableFrom(typeof(IUiCompositionMcpToolHandler)));
        Assert.True(typeof(IUiCompositionMcpToolHandler).IsAssignableFrom(typeof(UiCompositionMcpToolHandler)));
    }

    [Fact]
    public void ProposeTool_RequiresExplicitConfirmationParameter()
    {
        var method = typeof(UiCompositionMcpTools).GetMethod(nameof(UiCompositionMcpTools.ProposePlanAsync));
        var confirmation = Assert.Single(method!.GetParameters(), parameter => parameter.Name == "confirmApply");

        Assert.False((bool)confirmation.DefaultValue!);
    }
}
