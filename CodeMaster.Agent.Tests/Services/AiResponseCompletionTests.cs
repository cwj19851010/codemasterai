using System.Runtime.CompilerServices;
using CodeMaster.Agent.Contracts;
using CodeMaster.Agent.Services;
using CodeMaster.Agent.Tools;
using Microsoft.Extensions.AI;

namespace CodeMaster.Agent.Tests.Services;

public sealed class AiResponseCompletionTests
{
    private const string Request = "我现在要做一个工程类的管理系统，主要功能有 1.工程信息的管理，包括工程时间，地点 2.工程包括原料的使用费用，劳工的费用，以及其他的费用详情，3.工程中原料的采购记录，入库和领用 4.完整的财务收付功能 请你根据上述信息，整理下需求";
    private const string Preamble = "我会先核对当前 `工程管理软件` 项目的现有模块、实体和字段，避免把已有能力与新增需求混淆；随后按业务域、核心对象、流程、权限和验收要点整理成一份可落地的需求框架。";
    private const string Answer = "需求清单：\n1. 工程信息：名称、时间、地点、负责人。\n2. 采购与库存：采购记录、入库、领用、库存流水。\n3. 工程费用：材料、劳工及其他费用明细。\n4. 财务收付：应收应付、收付款单、账户与对账。\n5. 权限及验收：角色分工、单据追溯；确认库存计价和人工结算方式。";

    [Fact]
    public void Requirements_analysis_is_read_only_and_rejects_reported_preamble()
    {
        Assert.True(AiResponseCompletion.IsRequirementsRequest(Request));
        Assert.False(AiProjectPlanTurn.RequiresPlan(Request));
        Assert.True(AiResponseCompletion.IsPreambleOnly(Preamble));
        Assert.False(AiResponseCompletion.IsPreambleOnly(Answer));
        var tools = new List<AITool>
        {
            AIFunctionFactory.Create(() => "read", name: CodeMasterAgentToolCatalog.GetProjectSummary),
            AIFunctionFactory.Create(() => "read", name: CodeMasterAgentToolCatalog.GetProjectDictionaries),
            AIFunctionFactory.Create(() => "write", name: CodeMasterAgentToolCatalog.UpsertProjectWorkPlan),
            AIFunctionFactory.Create(() => "write", name: CodeMasterAgentToolCatalog.ProposeProjectChangeSet),
            AIFunctionFactory.Create(() => "write", name: "propose_view_data_source_plan")
        };
        var selected = AiTurnToolSelector.SelectForMode(tools, AiAgentModes.Requirements);
        var names = selected.OfType<AIFunction>().Select(item => item.Name).ToList();
        Assert.Contains(CodeMasterAgentToolCatalog.GetProjectSummary, names);
        Assert.Contains(CodeMasterAgentToolCatalog.GetProjectDictionaries, names);
        Assert.DoesNotContain(CodeMasterAgentToolCatalog.UpsertProjectWorkPlan, names);
        Assert.DoesNotContain(CodeMasterAgentToolCatalog.ProposeProjectChangeSet, names);
        Assert.DoesNotContain("propose_view_data_source_plan", names);

        // Code mode is the only mode that exposes the full surface, and it is decided by the
        // persisted Agent mode — never by message keywords.
        Assert.Equal(tools.Count, AiTurnToolSelector.SelectForMode(tools, AiAgentModes.Code).Count);
    }

    [Theory]
    [InlineData("需求只有工程编号、开工日期、地点三项。")]
    [InlineData("人工费用按天结算还是按工时结算？")]
    [InlineData("查询失败：数据库连接不可用。")]
    [InlineData("我会先说明边界。\n1. 工程管理：名称和起止日期。")]
    [InlineData("工程财务应区分应收应付和实际收付款。采购入库不等于工程领用成本。")]
    public void Short_real_answers_and_clarifications_are_preserved(string answer)
        => Assert.False(AiResponseCompletion.IsPreambleOnly(answer));

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(true, false)]
    public async Task Preamble_is_recovered_in_same_session_without_mutations(bool streaming, bool callReadTool)
    {
        var reads = 0;
        using var provider = new ScriptedClient(callReadTool);
        using var trace = new AiResponseTraceChatClient(provider);
        var tools = AiTurnToolSelector.SelectForMode(
        [
            AIFunctionFactory.Create(() => { reads++; return "{\"modules\":[],\"entities\":[]}"; }, name: CodeMasterAgentToolCatalog.GetProjectSummary),
            AIFunctionFactory.Create((Func<string>)(() => throw new InvalidOperationException("No writes authorized")), name: CodeMasterAgentToolCatalog.UpsertProjectWorkPlan)
        ], AiAgentModes.Requirements);
        var agent = AiChatClientFactory.CreateAgent(trace,
            AiResponseCompletion.BuildRequirementsInstructions("工程管理软件", 1), "test", "test", tools, "Standard");
        var session = await agent.CreateSessionAsync();
        if (streaming)
        {
            await foreach (var _ in agent.RunStreamingAsync(Request, session)) { }
        }
        else
        {
            await agent.RunAsync(Request, session);
        }
        Assert.Equal(callReadTool ? 1 : 0, reads);
        var result = await AiResponseCompletion.EnsureAsync(Request, true, () => trace.LastReply, Preamble,
            async (prompt, token) =>
            {
                await agent.RunAsync(prompt, session,
                    options: AiChatClientFactory.CreateVisibleResponseRunOptions("Standard"), cancellationToken: token);
            });
        Assert.True(result.Complete);
        Assert.Equal(1, result.Attempts);
        Assert.Equal(Answer, result.Text);
        Assert.Equal(callReadTool ? 1 : 0, reads);
        Assert.Equal(Request, provider.LastMessages.First(message => message.Role == ChatRole.User).Text);
        Assert.IsType<NoneChatToolMode>(provider.LastOptions?.ToolMode);
        Assert.DoesNotContain(provider.LastOptions!.Tools!, tool => tool.Name == CodeMasterAgentToolCatalog.UpsertProjectWorkPlan);
        Assert.Equal("stop", trace.LastReply!.FinishReason);
        if (callReadTool)
        {
            Assert.Equal("tool_calls", trace.Replies[0].FinishReason);
            Assert.Equal(string.Empty, trace.Replies[1].Text);
        }
    }

    [Fact]
    public async Task Truncated_answer_is_completed_instead_of_silently_saved()
    {
        var reply = new AiModelReply("需求一、工程管理…", "length", [], 1, true);
        var result = await AiResponseCompletion.EnsureAsync(Request, true, () => reply, "",
            (_, _) => { reply = new(Answer, "stop", [], 1, true); return Task.CompletedTask; });
        Assert.Equal(Answer, result.Text);
        Assert.Equal(1, result.Attempts);
    }

    [Fact]
    public async Task Already_complete_answer_needs_no_extra_model_request()
    {
        var result = await AiResponseCompletion.EnsureAsync(Request, true,
            () => new(Answer, "stop", [], 1, true), "",
            (_, _) => throw new InvalidOperationException("Unnecessary extra request"));
        Assert.Equal(0, result.Attempts);
        Assert.Equal(Answer, result.Text);
    }

    [Fact]
    public async Task Repeated_empty_or_preamble_answers_end_with_explicit_failure()
    {
        var result = await AiResponseCompletion.EnsureAsync(Request, true,
            () => new(Preamble, "stop", [], 1, true), "",
            (_, _) => Task.CompletedTask);
        Assert.False(result.Complete);
        Assert.Equal(AiResponseCompletion.MaximumAttempts, result.Attempts);
        Assert.Equal(AiResponseCompletion.IncompleteMessage, result.Text);
    }

    [Fact]
    public async Task Cancellation_and_rate_limit_do_not_trigger_further_recovery_calls()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AiResponseCompletion.EnsureAsync(
            Request, true, () => null, "", (_, _) => throw new InvalidOperationException(), cancellationToken: cancellation.Token));
        var calls = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(() => AiResponseCompletion.EnsureAsync(
            Request, true, () => null, "", (_, _) => { calls++; throw new InvalidOperationException("429"); }));
        Assert.Equal(1, calls);
    }

    private sealed class ScriptedClient(bool callReadTool) : IChatClient
    {
        private int _step;
        public ChatOptions? LastOptions { get; private set; }
        public List<ChatMessage> LastMessages { get; private set; } = [];

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            LastMessages = messages.ToList();
            LastOptions = options;
            var step = _step++;
            if (callReadTool && step == 0)
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant,
                    [new TextContent(Preamble), new FunctionCallContent("read-summary", CodeMasterAgentToolCatalog.GetProjectSummary, new Dictionary<string, object?>())]))
                { FinishReason = ChatFinishReason.ToolCalls });
            var text = step == 0 ? Preamble : callReadTool && step == 1 ? "" : Answer;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, text)) { FinishReason = ChatFinishReason.Stop });
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
