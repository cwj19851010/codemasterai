using CodeMaster.UiComposition.Agent;
using CodeMaster.Application.Dtos.CodeGen;
using CodeMaster.UiComposition.Entities;

namespace CodeMaster.UiComposition.Tests;

public sealed class ViewPlanMetadataRulesTests
{
    [Fact]
    public void Missing_definition_does_not_require_a_physical_view_during_planning()
    {
        var definition = ViewPlanMetadataRules.FindDefinition(
            Array.Empty<ProjectViewDefinitionDto>(),
            "aging_analysis_reports");

        ViewPlanMetadataRules.EnsureCanCreateOrReplace(definition, replaceExisting: false);
    }

    [Fact]
    public void Existing_managed_definition_can_be_replanned_without_a_physical_view()
    {
        var definition = new ProjectViewDefinitionDto
        {
            ViewName = "aging_analysis_reports",
            Ownership = ProjectViewOwnership.Managed
        };

        ViewPlanMetadataRules.EnsureCanCreateOrReplace(definition, replaceExisting: false);
    }

    [Fact]
    public void Imported_external_definition_requires_explicit_takeover()
    {
        var definition = new ProjectViewDefinitionDto
        {
            ViewName = "aging_analysis_reports",
            Ownership = ProjectViewOwnership.ImportedExternal
        };

        var error = Assert.Throws<InvalidOperationException>(() =>
            ViewPlanMetadataRules.EnsureCanCreateOrReplace(definition, replaceExisting: false));

        Assert.Contains("已有外部视图定义", error.Message);
        Assert.Contains("目标数据库视图会在应用时单独校验", error.Message);
    }

    [Fact]
    public void Explicit_replacement_allows_an_existing_metadata_definition()
    {
        var definition = new ProjectViewDefinitionDto
        {
            ViewName = "aging_analysis_reports"
        };

        ViewPlanMetadataRules.EnsureCanCreateOrReplace(definition, replaceExisting: true);
    }
}
