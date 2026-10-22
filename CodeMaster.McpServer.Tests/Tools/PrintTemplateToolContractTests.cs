using CodeMaster.McpServer.Tools;

namespace CodeMaster.McpServer.Tests.Tools;

public sealed class PrintTemplateToolContractTests
{
    [Fact]
    public void Get_print_templates_contract_exposes_project_filter()
    {
        var wrapper = typeof(CodeMasterMcpTools).GetMethod(nameof(CodeMasterMcpTools.GetPrintTemplatesAsync));

        Assert.NotNull(wrapper);
        Assert.Contains(wrapper!.GetCustomAttributes(false), attribute => attribute.GetType().Name == "McpServerToolAttribute");
        Assert.Equal(typeof(string), wrapper.GetParameters().Single(parameter => parameter.Name == "projectId").ParameterType);
    }

    [Fact]
    public void Get_print_templates_route_keeps_the_api_prefix()
    {
        // 漏掉 /api 前缀时 WebApi 返回 404，工具会静默失败。
        Assert.Equal("/api/codegen/printtemplate/getlist", CodeMasterMcpTools.BuildPrintTemplateListPath(null));
        Assert.Equal(
            "/api/codegen/printtemplate/getlist?projectId=2082292671791108096",
            CodeMasterMcpTools.BuildPrintTemplateListPath("2082292671791108096"));
    }
}