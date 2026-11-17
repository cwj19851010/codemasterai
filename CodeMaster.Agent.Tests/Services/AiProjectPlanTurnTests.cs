using System.Runtime.CompilerServices;
using System.Text.Json;
using CodeMaster.Agent.Contracts;
using CodeMaster.Agent.Services;
using CodeMaster.Agent.Tools;
using Microsoft.Extensions.AI;

namespace CodeMaster.Agent.Tests.Services;

public sealed class AiProjectPlanTurnTests
{
    private const string Requirements = "我现在要做一个工程类的管理系统，工程信息、材料采购入库领用、劳工费用和完整财务收付，请你根据上述信息，整理下需求";
    private const string Confirmation = "可以，按你的需求开始吧，尽量一次性完成目前分析到的功能";

    private static AiMessageDto Message(string role, string text) => new() { Role = role, Content = text };
    private static AiProjectWorkPlanDto SavedPlan() => new() { Id = 17, TotalTasks = 7, Status = "Active" };

    [Fact]
    public void Engineering_conversation_transitions_from_analysis_to_execution()
    {
        Assert.False(AiProjectPlanTurn.RequiresPlan(Requirements));
        var history = new List<AiMessageDto>
        {
            Message("user", Requirements),
            Message("assistant", "需求方案：工程、材料、人工、财务和成本管理。")
        };
        Assert.True(AiProjectPlanTurn.RequiresPlan(Confirmation, history));
        history.Add(Message("user", Confirmation));
        history.Add(Message("assistant", "我将先确认控件类型，然后提交计划。"));
        Assert.True(AiProjectPlanTurn.RequiresPlan("那你开始吧", history));
        history.Add(Message("user", "开始开发吧"));
        history.Add(Message("assistant", "方案边界已确认，可以进入开发阶段。"));
        Assert.True(AiProjectPlanTurn.RequiresPlan("继续吧", history));
        Assert.True(AiProjectPlanTurn.RequiresPlan("那你继续", history));
    }

    [Fact]
    public void Numbered_business_clarifications_do_not_hide_the_original_scope()
    {
        var history = new List<AiMessageDto>
        {
            Message("user", Requirements),
            Message("assistant", "还有几项业务口径需要确认。"),
            Message("user", "1.付款可以合并材料和劳务，但必须是同一个抬头\n2.默认剩余金额，可以由财务修改\n3.应收按进度收款\n4.其他费用选择公司\n5.供应商和劳务公司不跨工程"),
            Message("assistant", "业务口径已确认。")
        };

        Assert.True(AiProjectPlanTurn.RequiresPlan("开始生成吧", history));
        Assert.True(AiProjectPlanTurn.RequiresPlan("开始开发吧", history));
        Assert.True(AiProjectPlanTurn.RequiresPlan("开始生成", history));
    }

    [Theory]
    [InlineData("请创建一个类似 Jira 的项目管理系统")]
    [InlineData("开始实现完整的跨模块管理平台")]
    [InlineData("请创建工程进度管理系统，记录材料使用情况和是否启用")]
    [InlineData("能不能帮我创建一个工程管理系统")]
    [InlineData("以上功能都需要，按你的想法来")]
    public void Direct_project_implementation_requires_plan(string content)
        => Assert.True(AiProjectPlanTurn.RequiresPlan(content));

    [Theory]
    [InlineData("先分析完整项目，不要生成")]
    [InlineData("全部功能都需要，但先整理需求")]
    [InlineData("继续讨论这个系统的需求")]
    [InlineData("先帮我分析下怎么实现")]
    [InlineData("开始了吗？")]
    [InlineData("目前进度怎么样")]
    [InlineData("暂时不要开始")]
    [InlineData("暂停执行")]
    public void Discussion_and_status_do_not_authorize_plan(string content)
        => Assert.False(AiProjectPlanTurn.RequiresPlan(content, [Message("user", Requirements)]));

    [Fact]
    public void Unrelated_new_request_does_not_inherit_old_project_scope()
    {
        Assert.False(AiProjectPlanTurn.RequiresPlan("那你开始吧", [
            Message("user", Requirements),
            Message("assistant", "完整系统需求"),
            Message("user", "先帮我调整一个按钮颜色"),
            Message("assistant", "好的")
        ]));
        Assert.False(AiProjectPlanTurn.RequiresPlan("那你开始吧", [Message("assistant", Requirements)]));
        Assert.False(AiProjectPlanTurn.RequiresPlan("那你开始吧"));
    }

    [Fact]
    public void Plan_requests_do_not_load_optional_ui_tools_unless_scope_requires_them()
    {
        var tools = new List<AITool>
        {
            AIFunctionFactory.Create(() => "ok", name: CodeMasterAgentToolCatalog.GetProjectSummary),
            AIFunctionFactory.Create(() => "ok", name: CodeMasterAgentToolCatalog.GetProjectWorkPlan),
            AIFunctionFactory.Create(() => "ok", name: CodeMasterAgentToolCatalog.UpsertProjectWorkPlan),
            AIFunctionFactory.Create(() => "ok", name: CodeMasterAgentToolCatalog.SubmitBusinessPlan),
            AIFunctionFactory.Create(() => "ok", name: CodeMasterAgentToolCatalog.GetComplexPageContext),
            AIFunctionFactory.Create(() => "ok", name: CodeMasterAgentToolCatalog.GetUiCompositionContext)
        };

        var selected = AiTurnToolSelector.SelectForNewPlanRequest(tools, "开始生成工程管理平台");
        var names = selected.OfType<AIFunction>().Select(item => item.Name).ToList();

        Assert.Contains(CodeMasterAgentToolCatalog.SubmitBusinessPlan, names);
        Assert.DoesNotContain(CodeMasterAgentToolCatalog.GetComplexPageContext, names);
        Assert.DoesNotContain(CodeMasterAgentToolCatalog.GetUiCompositionContext, names);
    }

    /// <summary>
    /// A new-plan turn must not reach the hand-written plan writer. With both writers reachable,
    /// one model turn created two plans: a coarse hand-written one and then the compiled one, the
    /// second silently superseding the first and orphaning its in-flight batch. That orphan is
    /// what later replayed stale metadata and produced a wall of "already exists" errors.
    /// </summary>
    [Fact]
    public void New_plan_turn_exposes_only_the_compiled_plan_writer()
    {
        var tools = new List<AITool>
        {
            AIFunctionFactory.Create(() => "ok", name: CodeMasterAgentToolCatalog.UpsertProjectWorkPlan),
            AIFunctionFactory.Create(() => "ok", name: CodeMasterAgentToolCatalog.SubmitBusinessPlan),
            AIFunctionFactory.Create(() => "ok", name: CodeMasterAgentToolCatalog.SupersedeProjectWorkPlan)
        };

        var names = AiTurnToolSelector
            .SelectForNewPlanRequest(tools, "开始生成工程管理平台")
            .OfType<AIFunction>()
            .Select(item => item.Name)
            .ToList();

        Assert.Contains(CodeMasterAgentToolCatalog.SubmitBusinessPlan, names);
        Assert.DoesNotContain(CodeMasterAgentToolCatalog.UpsertProjectWorkPlan, names);
    }

    /// <summary>
    /// The repair prompt tells the model to read get_codegen_catalog before drafting any task, so
    /// the repair tool set has to contain it. A narrowed set that omits a tool the prompt demands
    /// makes the model ask for something it cannot call.
    /// </summary>
    [Fact]
    public void Repair_turn_keeps_the_tools_its_prompt_names()
    {
        var tools = new List<AITool>
        {
            AIFunctionFactory.Create(() => "ok", name: CodeMasterAgentToolCatalog.GetCodegenCatalog),
            AIFunctionFactory.Create(() => "ok", name: CodeMasterAgentToolCatalog.GetProjectWorkTask),
            AIFunctionFactory.Create(() => "ok", name: CodeMasterAgentToolCatalog.UpsertProjectWorkPlan),
            AIFunctionFactory.Create(() => "ok", name: CodeMasterAgentToolCatalog.GetComplexPageContext)
        };

        var names = AiTurnToolSelector
            .SelectForWorkPlanRepair(tools, "修复当前任务计划：create-core-entities 缺少可执行定义")
            .OfType<AIFunction>()
            .Select(item => item.Name)
            .ToList();

        Assert.Contains(CodeMasterAgentToolCatalog.GetCodegenCatalog, names);
        Assert.Contains(CodeMasterAgentToolCatalog.GetProjectWorkTask, names);
        Assert.Contains(CodeMasterAgentToolCatalog.UpsertProjectWorkPlan, names);
        Assert.DoesNotContain(CodeMasterAgentToolCatalog.GetComplexPageContext, names);
    }

    [Fact]
    public async Task Existing_plan_is_not_submitted_again()
    {
        var plan = SavedPlan();
        var result = await AiProjectPlanTurn.EnsureSavedAsync(
            _ => Task.FromResult<AiProjectWorkPlanDto?>(plan),
            (_, _) => throw new InvalidOperationException("Must not resubmit a saved plan."));
        Assert.Same(plan, result.Plan);
        Assert.Equal(0, result.ContinuationAttempts);
    }

    [Fact]
    public async Task Rejected_submission_is_retried_and_verified_against_persisted_state()
    {
        AiProjectWorkPlanDto? saved = null;
        var attempts = 0;
        var result = await AiProjectPlanTurn.EnsureSavedAsync(
            _ => Task.FromResult(saved),
            (_, _) =>
            {
                attempts++;
                if (attempts == 2) saved = SavedPlan();
                return Task.CompletedTask;
            });
        Assert.NotNull(result.Plan);
        Assert.Equal(2, result.ContinuationAttempts);
    }

    [Fact]
    public async Task Empty_draft_and_repeated_promises_do_not_count_as_saved_plan()
    {
        var result = await AiProjectPlanTurn.EnsureSavedAsync(
            _ => Task.FromResult<AiProjectWorkPlanDto?>(new() { Id = 1, Status = "Draft", TotalTasks = 0 }),
            (_, _) => Task.CompletedTask);
        Assert.Null(result.Plan);
        Assert.Equal(AiProjectPlanTurn.MaximumContinuationAttempts, result.ContinuationAttempts);
    }

    /// <summary>
    /// A model that keeps writing prose instead of submitting a plan must end the turn with nothing
    /// generated. The server used to substitute a built-in engineering template at this point, so the
    /// user was told "the task plan is ready" while the metadata had nothing to do with the requirement
    /// and no template marker anywhere the user could see.
    /// </summary>
    [Fact]
    public async Task Model_that_never_submits_a_plan_generates_nothing()
    {
        var result = await AiProjectPlanTurn.EnsureSavedAsync(
            _ => Task.FromResult<AiProjectWorkPlanDto?>(null),
            (_, _) => Task.CompletedTask);
        Assert.Null(result.Plan);
        Assert.Equal(AiProjectPlanTurn.MaximumContinuationAttempts, result.ContinuationAttempts);
    }

    /// <summary>
    /// "No plan was saved" on its own cannot be acted on: a provider outage, a rejected tool schema and
    /// a model that ignores the tool all look the same. The outcome message has to carry the last
    /// continuation failure so the difference is visible without reading server logs.
    /// </summary>
    [Fact]
    public async Task Unfulfilled_outcome_reports_the_last_failure_reason()
    {
        Exception? lastFailure = null;
        var result = await AiProjectPlanTurn.EnsureSavedAsync(
            _ => Task.FromResult<AiProjectWorkPlanDto?>(null),
            (_, _) => throw new HttpRequestException("tool_choice refers to an unknown function"),
            reportFailure: (_, exception) => lastFailure = exception);

        Assert.Null(result.Plan);
        Assert.NotNull(lastFailure);

        var message = AiProjectPlanTurn.BuildOutcomeMessage(result.Plan, lastFailure);
        Assert.Contains("未成功提交计划", message);
        Assert.Contains("unknown function", message);
    }

    [Fact]
    public async Task Cancellation_and_provider_errors_are_not_replayed()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AiProjectPlanTurn.EnsureSavedAsync(
            _ => Task.FromResult<AiProjectWorkPlanDto?>(null),
            (_, _) => throw new InvalidOperationException("Must not run after cancellation."),
            cancellationToken: cancellation.Token));

        var calls = 0;
        var result = await AiProjectPlanTurn.EnsureSavedAsync(
            _ => Task.FromResult<AiProjectWorkPlanDto?>(null),
            (_, _) => { calls++; throw new InvalidOperationException("429 Too Many Requests"); });
        Assert.Null(result.Plan);
        Assert.Equal(2, calls);
    }

    [Fact]
    public void Missing_capability_is_detected_by_runtime()
    {
        Assert.Throws<InvalidOperationException>(() => AiProjectPlanTurn.EnsureCapability([CodeMasterAgentToolCatalog.GetProjectSummary]));
        Assert.Throws<InvalidOperationException>(() => AiProjectPlanTurn.EnsureCapability([CodeMasterAgentToolCatalog.UpsertProjectWorkPlan], false));
        AiProjectPlanTurn.EnsureCapability([CodeMasterAgentToolCatalog.UpsertProjectWorkPlan]);
        AiProjectPlanTurn.EnsureCapability([CodeMasterAgentToolCatalog.SubmitBusinessPlan]);
        AiProjectPlanTurn.EnsureCapability([CodeMasterAgentToolCatalog.UpsertProjectWorkPlan, CodeMasterAgentToolCatalog.SubmitBusinessPlan]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Agent_continues_after_preamble_with_history_and_tools_intact(bool streaming)
    {
        AiProjectWorkPlanDto? saved = null;
        var writes = 0;
        using var client = new ScriptedChatClient();
        var tools = new List<AITool>
        {
            AIFunctionFactory.Create(() => "project is empty", name: CodeMasterAgentToolCatalog.GetProjectSummary),
            AIFunctionFactory.Create(() =>
            {
                writes++;
                saved = SavedPlan();
                return "{\"success\":true}";
            }, name: CodeMasterAgentToolCatalog.UpsertProjectWorkPlan)
        };
        var agent = AiChatClientFactory.CreateAgent(client, "Execute confirmed requirements.", "test", "test", tools, "Standard");
        var session = await agent.CreateSessionAsync();
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, Requirements),
            new(ChatRole.Assistant, "工程、材料、人工、财务和成本管理"),
            new(ChatRole.User, Confirmation)
        };
        if (streaming)
        {
            await foreach (var _ in agent.RunStreamingAsync(messages, session)) { }
        }
        else
        {
            await agent.RunAsync(messages, session);
        }
        Assert.Null(saved);

        var result = await AiProjectPlanTurn.EnsureSavedAsync(
            _ => Task.FromResult(saved),
            async (prompt, token) =>
            {
                await agent.RunAsync(prompt, session,
                    options: AiChatClientFactory.CreateRunOptions("Standard", CodeMasterAgentToolCatalog.UpsertProjectWorkPlan),
                    cancellationToken: token);
            });
        Assert.NotNull(result.Plan);
        Assert.Equal(1, result.ContinuationAttempts);
        Assert.Equal(1, writes);
        Assert.All(client.ToolNames, names => Assert.Contains(CodeMasterAgentToolCatalog.UpsertProjectWorkPlan, names));
        Assert.Contains(Requirements, client.LastMessages.Select(message => message.Text));
        using var diagnostic = JsonDocument.Parse(AiProjectPlanTurn.BuildDiagnostics(tools.Select(tool => tool.Name), true, result));
        Assert.True(diagnostic.RootElement.GetProperty("planSaved").GetBoolean());
        Assert.Equal("17", diagnostic.RootElement.GetProperty("planId").GetString());
    }

    private sealed class ScriptedChatClient : IChatClient
    {
        private int _step;
        public List<string[]> ToolNames { get; } = [];
        public List<ChatMessage> LastMessages { get; private set; } = [];

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            LastMessages = messages.ToList();
            ToolNames.Add(options?.Tools?.Select(tool => tool.Name).ToArray() ?? []);
            var step = _step++;
            if (step is 0 or 2)
            {
                var name = step == 0 ? CodeMasterAgentToolCatalog.GetProjectSummary : CodeMasterAgentToolCatalog.UpsertProjectWorkPlan;
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant,
                    [new FunctionCallContent($"call-{step}", name, new Dictionary<string, object?>())]))
                { FinishReason = ChatFinishReason.ToolCalls });
            }
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant,
                step == 1 ? "我将开始提交计划。" : "项目方案已保存。")) { FinishReason = ChatFinishReason.Stop });
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var response = await GetResponseAsync(messages, options, cancellationToken);
            foreach (var update in response.ToChatResponseUpdates()) yield return update;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) =>
            serviceType == typeof(ChatClientMetadata) ? new ChatClientMetadata("test") : null;
        public void Dispose() { }
    }
}
