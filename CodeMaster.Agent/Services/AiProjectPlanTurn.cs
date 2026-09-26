using System.Text.Json;
using System.Text.RegularExpressions;
using CodeMaster.Agent.Contracts;
using CodeMaster.Agent.Tools;

namespace CodeMaster.Agent.Services;

public static class AiProjectPlanTurn
{
    public const int MaximumContinuationAttempts = 2;
    public const string UnfulfilledMessage = "本次未能保存可执行的任务计划，业务生成尚未开始。模型尚未成功提交计划。";

    private static readonly string[] BroadScope = ["系统", "平台", "软件", "多模块", "跨模块", "全套", "整个项目", "完整项目", "类似jira"];
    private static readonly string[] Execution = ["创建", "生成", "实现", "开发", "搭建", "开始做", "直接做", "完成"];
    private static readonly string[] Confirmations = ["开始吧", "开始做", "开始实现", "开始开发", "开始生成", "开始执行", "按你的", "按上述", "按以上", "按这个", "按需求", "按方案", "确认实施", "确认执行", "继续吧", "那就继续", "那你继续", "继续执行", "继续完成"];

    public static bool RequiresPlan(string? content, IEnumerable<AiMessageDto>? priorMessages = null)
    {
        var current = Normalize(content);
        if (current.Length == 0 || IsDiscussionOnly(current))
            return false;
        if (IsDirectProjectBuild(current))
            return true;
        if (!IsConfirmation(current))
            return false;

        // Carry the agreed scope across a confirmation even when the user has
        // answered one or more clarification questions since the original
        // requirement. The previous implementation stopped at the immediately
        // preceding user message; numbered answers such as "1.... 5...." were
        // therefore mistaken for a new request and "开始生成吧" fell back to a
        // normal model turn without the work-plan tool.
        foreach (var message in (priorMessages ?? []).Reverse())
        {
            if (!string.Equals(message.Role, "user", StringComparison.OrdinalIgnoreCase))
                continue;
            var previous = Normalize(message.Content);
            if (IsConfirmation(previous) && !IsDiscussionOnly(previous) && !ContainsAny(previous, BroadScope))
                continue;
            if (ContainsAny(previous, BroadScope))
                return true;
            if (IsLikelyClarificationAnswer(previous))
                continue;

            // A newer independent request replaces the older project scope.
            return false;
        }
        return false;
    }

    private static bool IsLikelyClarificationAnswer(string text)
    {
        if (text.Length == 0)
            return true;

        // Numbered, enumerated, or short acknowledgement replies are normally
        // answers to the Agent's clarification questions, not a new scope.
        if (Regex.IsMatch(text, @"^(?:\d+[\.、:)])|(?:[一二三四五六七八九十]+[、.])"))
            return true;
        if (text.Length <= 48 && !ContainsAny(text,
                ["创建", "生成", "实现", "开发", "搭建", "修改", "调整", "新增", "删除", "增加", "设计"]))
            return true;

        // Longer clarifications generally describe choices or business rules;
        // an independent implementation request contains an explicit action.
        return text.Length > 120 &&
               !ContainsAny(text,
               ["创建", "生成", "实现", "开发", "搭建", "修改", "调整", "新增", "删除", "增加", "设计", "做一个", "做一套"]);
    }

    public static bool IsDiscussionOnly(string? content)
    {
        var text = Normalize(content);
        if (ContainsAny(text, ["不要开始", "先别", "暂不", "暂时不", "不要执行", "不要生成", "不要创建", "不要修改", "停止执行", "暂停", "取消", "先不", "别开始"]))
            return true;
        if (ContainsAny(text, ["整理下需求", "整理需求", "整理一下需求", "先分析", "先帮我分析", "只分析", "继续分析", "继续讨论", "先讨论", "先评估", "先整理", "只整理"]))
            return true;
        if (ContainsAny(text, ["进度怎么样", "现在进度", "目前进度", "什么情况", "开始了吗", "完成了吗", "是否开始", "是否完成", "怎么实现", "如何实现", "为什么"]))
            return true;
        return ContainsAny(text, ["分析", "评估", "讨论", "建议", "了解", "解释"]) &&
               !ContainsAny(text, Execution) && !ContainsAny(text, ["开始吧", "开始执行"]);
    }

    private static bool IsDirectProjectBuild(string text) =>
        ContainsAny(text, ["以上功能都需要", "全部功能都需要", "全部都需要"]) ||
        ContainsAny(text, Execution) && ContainsAny(text, BroadScope);

    private static bool IsConfirmation(string text) =>
        ContainsAny(text, Confirmations) || text is "继续" or "开始" or "执行";

    private static bool ContainsAny(string text, IEnumerable<string> values) =>
        values.Any(value => text.Contains(value, StringComparison.OrdinalIgnoreCase));

    private static string Normalize(string? value) => Regex.Replace(value ?? string.Empty, @"\s+", string.Empty);

    public static void EnsureCapability(IEnumerable<string> toolNames, bool supportsTools = true)
    {
        if (!supportsTools)
            throw new InvalidOperationException("当前模型配置未启用工具调用，无法创建任务计划。请启用工具调用或选择支持工具调用的模型配置。");
        if (!toolNames.Contains(CodeMasterAgentToolCatalog.UpsertProjectWorkPlan, StringComparer.Ordinal))
            throw new InvalidOperationException("当前执行环境未提供项目方案写入能力，无法开始生成。请检查当前账号权限和 Agent 服务版本。");
    }

    public static async Task<AiProjectPlanTurnResult> EnsureSavedAsync(
        Func<CancellationToken, Task<AiProjectWorkPlanDto?>> readPlan,
        Func<string, CancellationToken, Task> continueRun,
        Func<int, CancellationToken, Task>? reportContinuation = null,
        CancellationToken cancellationToken = default)
    {
        for (var attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var plan = await readPlan(cancellationToken);
            if (plan is { TotalTasks: > 0 } || attempt >= MaximumContinuationAttempts)
                return new(plan is { TotalTasks: > 0 } ? plan : null, attempt);

            if (reportContinuation != null)
                await reportContinuation(attempt + 1, cancellationToken);
            await continueRun(
                "The user has explicitly authorized implementation of the requirements already discussed in this conversation. " +
                "The server has checked persisted state: no project plan with tasks has been saved. " +
                "Continue the same request now using upsert_project_work_plan, which is available in this run. " +
                "Submit executable task definitions for the agreed scope using the existing seven-stage workflow. " +
                "If a draft already exists, update that plan by its id rather than creating a duplicate. " +
                "If the previous submission returned success=false, correct its diagnostic and resubmit. " +
                "Do not end with a promise to start later or invent missing capabilities. Do not expand the agreed scope. " +
                "The existing workflow handles confirmation and execution after the plan is saved.",
                cancellationToken);
        }
    }

    public static string BuildOutcomeMessage(AiProjectWorkPlanDto? plan) => plan switch
    {
        null => UnfulfilledMessage,
        { RequiresConfirmation: true } => "任务计划已准备好，请点击“确认并开始执行”。",
        { Status: "Blocked" or "Failed" } => $"任务计划已保存，当前完成 {plan.CompletedTasks}/{plan.TotalTasks} 项；执行遇到阻塞，请查看具体任务的错误信息。",
        _ => $"任务计划已保存，当前完成 {plan.CompletedTasks}/{plan.TotalTasks} 项。"
    };

    public static string BuildDiagnostics(IEnumerable<string> availableTools, bool planRequired, AiProjectPlanTurnResult? result,
        AiResponseCompletionResult? answerResult = null, object[]? modelCalls = null) =>
        JsonSerializer.Serialize(new
        {
            availableTools = availableTools.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
            planRequired,
            planSaved = result?.Plan != null,
            continuationAttempts = result?.ContinuationAttempts ?? 0,
            planId = result?.Plan?.Id.ToString(),
            answerComplete = answerResult?.Complete,
            answerContinuationAttempts = answerResult?.Attempts ?? 0,
            modelCalls = modelCalls?.TakeLast(8).ToArray()
        });
}

public sealed record AiProjectPlanTurnResult(AiProjectWorkPlanDto? Plan, int ContinuationAttempts);

public static class AiToolStageResolver
{
    public static string? Resolve(string toolName) => toolName switch
    {
        CodeMasterAgentToolCatalog.UpsertProjectWorkPlan => "plan",
        CodeMasterAgentToolCatalog.SupersedeProjectWorkPlan => "plan",
        CodeMasterAgentToolCatalog.GetEntityBlueprint => "EntityFields",
        CodeMasterAgentToolCatalog.GetProjectWorkTask => "ConsistencyCheck",
        CodeMasterAgentToolCatalog.GetProjectStructure or CodeMasterAgentToolCatalog.GetCodegenCatalog => "EntityFields",
        _ => "Workflow"
    };
}
