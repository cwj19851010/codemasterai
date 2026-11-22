using CodeMaster.McpServer.Tools;

namespace CodeMaster.UiComposition.Tests;

public sealed class UiCompositionMcpToolNameContractTests
{
    [Theory]
    [InlineData(nameof(UiCompositionMcpTools.GetViewBuilderContextAsync), "get_view_builder_context")]
    [InlineData(nameof(UiCompositionMcpTools.PreviewViewSqlAsync), "preview_view_sql")]
    [InlineData(nameof(UiCompositionMcpTools.SaveViewDefinitionAsync), "save_view_definition")]
    [InlineData(nameof(UiCompositionMcpTools.DeployViewDefinitionAsync), "deploy_view_definition")]
    [InlineData(nameof(UiCompositionMcpTools.RegisterReadOnlyViewEntityAsync), "register_read_only_view_entity")]
    [InlineData(nameof(UiCompositionMcpTools.ImportExistingViewAsync), "import_existing_view")]
    [InlineData(nameof(UiCompositionMcpTools.MaterializeProjectAsync), "materialize_project")]
    public void Exposes_the_structured_view_and_materialization_tool(string methodName, string toolName)
    {
        var method = typeof(UiCompositionMcpTools).GetMethod(methodName);

        Assert.NotNull(method);
        var attribute = method!.GetCustomAttributes(false)
            .Single(item => item.GetType().Name == "McpServerToolAttribute");
        var name = attribute.GetType().GetProperty("Name")?.GetValue(attribute)?.ToString();

        Assert.Equal(toolName, name);
    }
}
