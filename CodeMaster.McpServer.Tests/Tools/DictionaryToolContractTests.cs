using CodeMaster.McpServer.Tools;

namespace CodeMaster.McpServer.Tests.Tools;

public sealed class DictionaryToolContractTests
{
    [Fact]
    public void Query_dictionary_contract_exposes_filters_and_string_safe_ids()
    {
        var definition = DictionaryTool.QueryDefinition;
        var schema = definition.InputSchema.ToJsonString();
        var wrapper = typeof(CodeMasterMcpTools).GetMethod(nameof(CodeMasterMcpTools.QueryDictionariesAsync));

        Assert.Equal("query_dictionaries", definition.Name);
        Assert.Contains("projectId", schema);
        Assert.Contains("tenantId", schema);
        Assert.Contains("includeItems", schema);
        Assert.Contains("includeDisabled", schema);
        Assert.NotNull(wrapper);
        Assert.Contains(wrapper!.GetCustomAttributes(false), attribute => attribute.GetType().Name == "McpServerToolAttribute");
        Assert.Equal(typeof(string), wrapper.GetParameters().Single(parameter => parameter.Name == "projectId").ParameterType);
        Assert.Equal(typeof(string), wrapper.GetParameters().Single(parameter => parameter.Name == "tenantId").ParameterType);
    }

    [Fact]
    public void Save_dictionary_contract_documents_non_destructive_item_upserts()
    {
        var definition = DictionaryTool.SaveDefinition;
        var schema = definition.InputSchema.ToJsonString();
        var wrapper = typeof(CodeMasterMcpTools).GetMethod(nameof(CodeMasterMcpTools.SaveDictionaryAsync));

        Assert.Equal("save_dictionary", definition.Name);
        Assert.Contains("Omitted entries are preserved", definition.Description);
        Assert.Contains("dictType", schema);
        Assert.Contains("dictName", schema);
        Assert.Contains("items", schema);
        Assert.Contains("value", schema);
        Assert.NotNull(wrapper);
        Assert.Contains(wrapper!.GetCustomAttributes(false), attribute => attribute.GetType().Name == "McpServerToolAttribute");
        Assert.Equal(typeof(List<DictionaryItemInput>), wrapper.GetParameters().Single(parameter => parameter.Name == "items").ParameterType);
    }
}
