using CodeMaster.Application.Dtos.CodeGen;
using CodeMaster.UiComposition.Entities;

namespace CodeMaster.Agent.Tools;

/// <summary>
/// Keeps view-plan conflict decisions based on CodeMaster metadata. Physical
/// database views are intentionally checked only by the deployment/import path.
/// </summary>
internal static class ViewPlanMetadataRules
{
    public static ProjectViewDefinitionDto? FindDefinition(
        IEnumerable<ProjectViewDefinitionDto> definitions,
        string viewName)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        var normalizedName = viewName?.Trim() ?? string.Empty;
        return definitions.FirstOrDefault(item =>
            string.Equals(item.ViewName, normalizedName, StringComparison.OrdinalIgnoreCase));
    }

    public static void EnsureCanCreateOrReplace(
        ProjectViewDefinitionDto? existing,
        bool replaceExisting)
    {
        if (existing == null ||
            existing.Ownership == ProjectViewOwnership.Managed ||
            replaceExisting)
            return;

        throw new InvalidOperationException(
            $"CodeMaster 中已有外部视图定义 '{existing.ViewName}'，请明确是否接管该定义后再应用。 " +
            "目标数据库视图会在应用时单独校验。");
    }
}
