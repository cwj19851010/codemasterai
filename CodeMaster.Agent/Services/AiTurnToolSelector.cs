using CodeMaster.Agent.Tools;
using Microsoft.Extensions.AI;

namespace CodeMaster.Agent.Services;

public static class AiTurnToolSelector
{
    private static readonly HashSet<string> RequirementsReadTools = new(StringComparer.Ordinal)
    {
        CodeMasterAgentToolCatalog.GetProjectSummary,
        CodeMasterAgentToolCatalog.GetProjectStructure,
        CodeMasterAgentToolCatalog.GetCodegenCatalog,
        CodeMasterAgentToolCatalog.GetProjectWorkPlan,
        CodeMasterAgentToolCatalog.GetProjectCompletionStatus
    };

    private static readonly HashSet<string> NewPlanTools = new(StringComparer.Ordinal)
    {
        CodeMasterAgentToolCatalog.GetProjectSummary,
        CodeMasterAgentToolCatalog.GetCodegenCatalog,
        CodeMasterAgentToolCatalog.GetProjectCompletionStatus,
        CodeMasterAgentToolCatalog.GetProjectWorkPlan,
        CodeMasterAgentToolCatalog.UpsertProjectWorkPlan,
        CodeMasterAgentToolCatalog.SupersedeProjectWorkPlan
    };

    private static readonly HashSet<string> WorkPlanRepairTools = new(StringComparer.Ordinal)
    {
        CodeMasterAgentToolCatalog.GetProjectSummary,
        CodeMasterAgentToolCatalog.GetEntityBlueprint,
        CodeMasterAgentToolCatalog.GetProjectWorkPlan,
        CodeMasterAgentToolCatalog.GetProjectCompletionStatus,
        CodeMasterAgentToolCatalog.GetProjectWorkTask,
        CodeMasterAgentToolCatalog.UpsertProjectWorkPlan,
        CodeMasterAgentToolCatalog.SupersedeProjectWorkPlan
    };

    public static bool IsWorkPlanRepairRequest(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return false;

        return content.Contains("修复当前任务计划", StringComparison.OrdinalIgnoreCase) ||
               content.Contains("缺少可执行定义", StringComparison.OrdinalIgnoreCase) ||
               content.Contains("缺少执行定义", StringComparison.OrdinalIgnoreCase) ||
               content.Contains("missing an executable definition", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A new implementation request should spend the model turn building and
    /// saving the executable work plan. Optional UI-composition and proposal
    /// tools make the model explore large context payloads before it submits the
    /// plan, which can turn a short "开始生成吧" request into a long timeout.
    /// Keep those tools opt-in for requests that explicitly need a dashboard,
    /// visual composition, or a database view.
    /// </summary>
    public static IList<AITool> SelectForNewPlanRequest(IList<AITool> tools, string? content)
    {
        var selected = new HashSet<string>(NewPlanTools, StringComparer.Ordinal);
        var text = content ?? string.Empty;
        var needsPageContext = text.Contains("仪表板", StringComparison.OrdinalIgnoreCase) ||
                               text.Contains("dashboard", StringComparison.OrdinalIgnoreCase) ||
                               text.Contains("复杂页面", StringComparison.OrdinalIgnoreCase) ||
                               text.Contains("看板", StringComparison.OrdinalIgnoreCase);
        var needsViewContext = text.Contains("视图", StringComparison.OrdinalIgnoreCase) ||
                              text.Contains("报表", StringComparison.OrdinalIgnoreCase) ||
                              text.Contains("统计", StringComparison.OrdinalIgnoreCase);

        if (needsPageContext)
        {
            selected.Add(CodeMasterAgentToolCatalog.GetComplexPageContext);
            selected.Add(CodeMasterAgentToolCatalog.GetUiCompositionContext);
        }
        if (needsViewContext)
            selected.Add(CodeMasterAgentToolCatalog.GetUiCompositionContext);

        return tools
            .Where(tool => tool is AIFunction function && selected.Contains(function.Name))
            .ToList();
    }

    public static IList<AITool> SelectForRequest(IList<AITool> tools, string? content)
    {
        if (AiResponseCompletion.IsRequirementsRequest(content))
            return tools.Where(tool => tool is AIFunction function &&
                RequirementsReadTools.Contains(function.Name)).ToList();

        if (!IsWorkPlanRepairRequest(content))
            return tools;

        var allowed = new HashSet<string>(WorkPlanRepairTools, StringComparer.Ordinal);
        if (content?.Contains("字典", StringComparison.OrdinalIgnoreCase) == true ||
            content?.Contains("field-control", StringComparison.OrdinalIgnoreCase) == true)
        {
            allowed.Add(CodeMasterAgentToolCatalog.GetCodegenCatalog);
            allowed.Add(CodeMasterAgentToolCatalog.GetProjectDictionaries);
        }
        if (content?.Contains("view", StringComparison.OrdinalIgnoreCase) == true ||
            content?.Contains("视图", StringComparison.OrdinalIgnoreCase) == true)
        {
            allowed.Add("get_ui_composition_context");
            allowed.Add("get_view_builder_context");
        }
        if (content?.Contains("dashboard", StringComparison.OrdinalIgnoreCase) == true ||
            content?.Contains("仪表板", StringComparison.OrdinalIgnoreCase) == true ||
            content?.Contains("复杂页面", StringComparison.OrdinalIgnoreCase) == true)
        {
            allowed.Add("get_complex_page_context");
        }

        return tools
            .Where(tool => tool is AIFunction function && allowed.Contains(function.Name))
            .ToList();
    }
}
