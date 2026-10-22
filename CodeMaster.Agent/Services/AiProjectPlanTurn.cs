using System.Text.Json;
using System.Text.RegularExpressions;
using CodeMaster.Agent.Contracts;
using CodeMaster.Agent.Tools;

namespace CodeMaster.Agent.Services;

public static class AiProjectPlanTurn
{
    public const int MaximumContinuationAttempts = 2;
    public const string UnfulfilledMessage = "本次未能保存可执行的任务计划，业务生成尚未开始。模型尚未成功提交计划。";

    private static readonly string[] BroadScope = ["系统", "平台", "软件", "多模块", "跨模块", "全套", "整个项目", "完整项目", "类似jira", "工程管理"];
    private static readonly string[] Execution = ["创建", "生成", "实现", "开发", "搭建", "开始做", "直接做", "完成", "包括", "要做", "想要", "需要"];
    private static readonly string[] Confirmations = ["开始吧", "开始做", "开始实现", "开始开发", "开始生成", "开始执行", "按你的", "按上述", "按以上", "按这个", "按需求", "按方案", "确认实施", "确认执行", "继续吧", "那就继续", "那你继续", "继续执行", "继续完成", "确认"];

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
        // Substantial scope-only statements (e.g. "工程管理平台") without any
        // Execution keyword are still broad-scope requirements and must NOT be
        // treated as clarification answers.
        return text.Length > 120 &&
               !ContainsAny(text,
               ["创建", "生成", "实现", "开发", "搭建", "修改", "调整", "新增", "删除", "增加", "设计", "包括", "要做", "想要", "需要"]);
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
        {
            // SupportsTools is the cached result of the last connection test. It is stored false
            // either because the test failed, or because the connection settings changed since
            // the test and the previous pass no longer applies. Either way the user needs to
            // re-run the test, so say that instead of only stating the capability is off.
            throw new InvalidOperationException(
                "当前模型配置未启用工具调用，无法创建任务计划。" +
                "该状态来自上一次连接测试：可能是测试未通过，也可能是连接配置（API 地址 / 模型名 / 密钥）变更后已失效。" +
                "请到「模型设置」中对该配置点一次「测试」，通过后即会自动开启工具调用能力；" +
                "或切换到其它已通过测试的支持工具调用的模型配置。");
        }

        var available = toolNames is ICollection<string> collection ? new HashSet<string>(collection, StringComparer.Ordinal) : new HashSet<string>(toolNames, StringComparer.Ordinal);
        if (!available.Contains(CodeMasterAgentToolCatalog.UpsertProjectWorkPlan)
            && !available.Contains(CodeMasterAgentToolCatalog.SubmitBusinessPlan))
            throw new InvalidOperationException("当前执行环境未提供项目方案写入能力，无法开始生成。请检查当前账号权限和 Agent 服务版本。");
    }

    public static async Task<AiProjectPlanTurnResult> EnsureSavedAsync(
        Func<CancellationToken, Task<AiProjectWorkPlanDto?>> readPlan,
        Func<string, CancellationToken, Task> continueRun,
        Func<long, long, CancellationToken, Task<AiProjectWorkPlanDto?>>? directUpsertAsync,
        Func<int, CancellationToken, Task>? reportContinuation = null,
        CancellationToken cancellationToken = default,
        long? fallbackProjectId = null,
        long? fallbackConversationId = null)
    {
        for (var attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var plan = await readPlan(cancellationToken);
            if (plan is { TotalTasks: > 0 })
                return new(plan, attempt);

            if (attempt >= MaximumContinuationAttempts)
            {
                if (directUpsertAsync != null)
                {
                    // The fallback delegate receives the actual project and
                    // conversation ids from the caller. The retry counter is
                    // only a local control value and must never be passed as
                    // an identifier.
                    var fallback = await directUpsertAsync(
                        fallbackProjectId ?? 0,
                        fallbackConversationId ?? 0,
                        cancellationToken);
                    if (fallback is { TotalTasks: > 0 })
                        return new(fallback, attempt);
                }
                return new(plan is { TotalTasks: > 0 } ? plan : null, attempt);
            }

            if (reportContinuation != null)
                await reportContinuation(attempt + 1, cancellationToken);
            try
            {
                await continueRun(
                    "The user has explicitly authorized implementation of the requirements already discussed in this conversation. " +
                    "The server has checked persisted state: no project plan with tasks has been saved. " +
                    "Continue the same request now using submit_business_plan, which is available in this run. " +
                    "Pass a BusinessPlanDefinition JSON (modules, entities, fields, relations, views, pages) with stable cross-block keys. " +
                    "The server compiles the six-stage executable plan from your business definition. " +
                    "If a draft already exists, the server will update that plan by its id rather than creating a duplicate. " +
                    "If the previous submission returned success=false, correct its diagnostic and resubmit. " +
                    "Do not end with a promise to start later or invent missing capabilities. Do not expand the agreed scope. " +
                    "The existing workflow handles confirmation and execution after the plan is saved.",
                    cancellationToken);
            }
            catch (Exception)
            {
                // continueRun failures (timeout, HTTP 400, parse errors) must NOT abort the loop.
                // The fallback path is invoked below once we exhaust the attempt budget.
            }
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

    /// <summary>
    /// 工具名判不出具体阶段时，用失败文案反推。
    /// </summary>
    /// <remarks>
    /// <c>propose_project_change_set</c> 是唯一真正写元数据的工具，实体、关系、控件全走它，
    /// 光看工具名只能落到 "Workflow"。而 <c>ValidateFieldControlsAsync</c> 和
    /// <c>EntityRelationGraph</c> 的报错文案是固定的，足以区分是控件阶段还是关系阶段出的问题——
    /// 红色横条上写"字段控件阶段失败"比写"工作流阶段失败"有用得多。
    /// </remarks>
    public static string? RefineFromMessage(string? stage, string? message)
    {
        // 已经能定位到具体阶段的，不动。
        if (!string.IsNullOrWhiteSpace(stage) &&
            !string.Equals(stage, "Workflow", StringComparison.Ordinal))
        {
            return stage;
        }

        if (string.IsNullOrWhiteSpace(message))
            return stage;

        if (message.Contains("Relation field types must match", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("references missing target field", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("references missing source field", StringComparison.OrdinalIgnoreCase))
        {
            return "Relations";
        }

        if (message.Contains("must have bool data type", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("must have a numeric data type", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("must have DateTime, DateOnly, or DateTimeOffset", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("must have string or text data type", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("must enable multiple selection", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("cannot enable multiple selection", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("must store its comma-separated values", StringComparison.OrdinalIgnoreCase))
        {
            return "FieldControls";
        }

        return stage;
    }
}
