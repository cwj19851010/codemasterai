using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CodeMaster.Agent.Contracts;
using CodeMaster.Agent.Services;
using Xunit;

namespace CodeMaster.Agent.Tests.Services;

public class AgentTurnRunnerTests
{
    [Fact]
    public void Context_Constructor_StoresAllFields()
    {
        var history = new List<AiMessageDto>
        {
            new() { Id = 1, Role = "user", Content = "hi", Sequence = 1 }
        };
        var ctx = new AgentTurnContext(
            ConversationId: 42,
            ProjectId: 100,
            ProviderId: 200,
            RequestId: "req-1",
            Content: "Build a CRM",
            SystemInstruction: "system",
            History: history,
            RequiresPlan: true,
            WorkflowMode: "Default");

        Assert.Equal(42, ctx.ConversationId);
        Assert.Equal(100, ctx.ProjectId);
        Assert.Equal(200, ctx.ProviderId);
        Assert.Equal("req-1", ctx.RequestId);
        Assert.Equal("Build a CRM", ctx.Content);
        Assert.Single(ctx.History);
        Assert.True(ctx.RequiresPlan);
        Assert.Equal("Default", ctx.WorkflowMode);
    }

    [Fact]
    public async Task RunAsync_Phase1_NotImplementedResult()
    {
        var runner = new AgentTurnRunner();
        var ctx = new AgentTurnContext(
            ConversationId: 1, ProjectId: 1, ProviderId: 1,
            RequestId: "r", Content: "x", SystemInstruction: "s",
            History: new List<AiMessageDto>(), RequiresPlan: false, WorkflowMode: "Default");

        var result = await runner.RunAsync(ctx, publishAsync: null, cancellationToken: default);

        Assert.Equal(AgentTurnOutcome.NotImplemented, result.Outcome);
        Assert.Equal(1L, result.ConversationId);
        Assert.False(result.PlanSaved);
        Assert.Null(result.PlanId);
        Assert.Empty(result.ToolExecutions);
    }

    [Fact]
    public async Task RunAsync_NullPublishAsync_DoesNotThrow()
    {
        var runner = new AgentTurnRunner();
        var ctx = new AgentTurnContext(
            ConversationId: 2, ProjectId: 2, ProviderId: 2,
            RequestId: "r2", Content: "y", SystemInstruction: "s",
            History: new List<AiMessageDto>(), RequiresPlan: false, WorkflowMode: "Default");

        // Should not throw even when publishAsync is null
        var result = await runner.RunAsync(ctx, publishAsync: null, cancellationToken: default);
        Assert.Equal(AgentTurnOutcome.NotImplemented, result.Outcome);
    }

    [Fact]
    public async Task RunAsync_CancellationToken_Respected()
    {
        var runner = new AgentTurnRunner();
        var ctx = new AgentTurnContext(
            ConversationId: 3, ProjectId: 3, ProviderId: 3,
            RequestId: "r3", Content: "z", SystemInstruction: "s",
            History: new List<AiMessageDto>(), RequiresPlan: false, WorkflowMode: "Default");
        using var cts = new CancellationTokenSource();

        // Just ensure the call accepts the token without throwing
        var result = await runner.RunAsync(ctx, null, cts.Token);
        Assert.NotNull(result);
    }
}
