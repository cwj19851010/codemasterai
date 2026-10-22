using CodeMaster.McpServer.Tools;
using System.ComponentModel;

namespace CodeMaster.McpServer.Tests;

public class ProjectOperationToolTests
{
    [Fact]
    public void ResolveTargetPath_UsesWorkspaceWhenTargetPathIsMissing()
    {
        var workspace = Path.Combine(Path.GetTempPath(), "CodeMasterProject");

        var result = ProjectOperationTool.ResolveTargetPath(null, workspace);

        Assert.Equal(Path.GetFullPath(workspace), result);
    }

    [Fact]
    public void ResolveTargetPath_PrefersExplicitTargetPath()
    {
        var workspace = Path.Combine(Path.GetTempPath(), "Workspace");
        var target = Path.Combine(Path.GetTempPath(), "Target");

        var result = ProjectOperationTool.ResolveTargetPath(target, workspace);

        Assert.Equal(Path.GetFullPath(target), result);
    }

    [Fact]
    public void RunProjectOperationDescriptionsIncludeDatabaseConfigurationSync()
    {
        Assert.Contains("database configuration sync", ProjectOperationTool.Definition.Description, StringComparison.OrdinalIgnoreCase);

        var method = typeof(CodeMasterMcpTools).GetMethod(nameof(CodeMasterMcpTools.RunProjectOperationAsync));
        var description = method?.GetCustomAttributes(typeof(DescriptionAttribute), inherit: true)
            .OfType<DescriptionAttribute>()
            .SingleOrDefault()?.Description;

        Assert.Contains("database configuration sync", description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RunProjectOperationDescriptionsIncludePrintingRuntimeDistribution()
    {
        Assert.Contains("printing runtime distribution", ProjectOperationTool.Definition.Description, StringComparison.OrdinalIgnoreCase);

        var method = typeof(CodeMasterMcpTools).GetMethod(nameof(CodeMasterMcpTools.RunProjectOperationAsync));
        var description = method?.GetCustomAttributes(typeof(DescriptionAttribute), inherit: true)
            .OfType<DescriptionAttribute>()
            .SingleOrDefault()?.Description;

        Assert.Contains("printing runtime distribution", description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PrintingOperation_MapsToLocalDistributionAction()
    {
        Assert.Equal("distributePrintingRuntime", ProjectOperationTool.ResolveAction("printing", 0));
        Assert.Null(ProjectOperationTool.ResolveAction("initialize_step", 12));
    }
}
