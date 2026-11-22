using System.Reflection;
using CodeMaster.Agent.Tools;
using CodeMaster.Agent.Tools;

namespace CodeMaster.UiComposition.Tests;

public sealed class UiCompositionAgentToolContractTests
{
    [Fact]
    public void Extension_ExposesCompleteUiCompositionToolSet()
    {
        var names = typeof(UiCompositionAgentToolExtension)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral && field.FieldType == typeof(string) && field.Name.EndsWith("Tool", StringComparison.Ordinal))
            .Select(field => (string)field.GetRawConstantValue()!)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal(7, names.Count);
        Assert.Contains("get_ui_composition_context", names);
        Assert.Contains("get_view_builder_context", names);
        Assert.Contains("preview_openapi_import", names);
        Assert.Contains("propose_view_data_source_plan", names);
        Assert.Contains("propose_openapi_import", names);
        Assert.Contains("get_complex_page_context", names);
        Assert.Contains("propose_complex_page_plan", names);
    }

    [Fact]
    public void Extension_UsesGenericAgentBoundary()
    {
        Assert.True(typeof(ICodeMasterAgentToolExtension).IsAssignableFrom(typeof(UiCompositionAgentToolExtension)));
    }

    [Fact]
    public void OpenApiAgentRequest_AcceptsUrlButNotLargeDocumentBody()
    {
        var properties = typeof(OpenApiAgentImportRequestDto).GetProperties()
            .Select(property => property.Name)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Contains(nameof(OpenApiAgentImportRequestDto.SourceUrl), properties);
        Assert.DoesNotContain("DocumentContent", properties);
        Assert.DoesNotContain("ProjectId", properties);
    }

    [Fact]
    public void ViewAgentPlan_AcceptsFinalSqlAndDeclaredEntityFieldsWithoutFilesystemTarget()
    {
        var properties = typeof(ViewAgentPlanDto).GetProperties()
            .ToDictionary(property => property.Name, StringComparer.Ordinal);

        Assert.Equal(typeof(CodeMaster.Application.Dtos.CodeGen.PreviewViewSqlDto), properties[nameof(ViewAgentPlanDto.Query)].PropertyType);
        Assert.Equal(typeof(List<CodeMaster.Application.Dtos.CodeGen.ViewEntityFieldDefinitionDto>), properties[nameof(ViewAgentPlanDto.EntityFields)].PropertyType);
        Assert.DoesNotContain("FilePath", properties.Keys);
        Assert.DoesNotContain("ProjectPath", properties.Keys);
        Assert.DoesNotContain("VueSource", properties.Keys);
    }

    [Fact]
    public void NewExecutionResults_PreserveClientActionsForSharedDispatcher()
    {
        Assert.True(typeof(IAgentClientActionResult).IsAssignableFrom(typeof(ViewAgentExecutionResult)));
        Assert.True(typeof(IAgentClientActionResult).IsAssignableFrom(typeof(OpenApiAgentExecutionResult)));
    }
}
