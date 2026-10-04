using System.Linq;
using System.Threading.Tasks;
using CodeMaster.Agent.Services;
using Xunit;

namespace CodeMaster.Agent.Tests.Services;

public class AiAgentEventStoreTests
{
    [Fact]
    public async Task RecordAsync_PersistsFields()
    {
        IAgentEventStore store = new InMemoryAgentEventStore();
        var record = new AgentEventRecord(
            ConversationId: 1,
            ProjectId: 100,
            UserId: 42,
            RequestId: "req-1",
            EventType: "TurnStarted",
            Sequence: 1,
            TurnId: "turn-1",
            Status: "Running",
            Message: "begin");

        var saved = await store.RecordAsync(record);
        Assert.Equal(1L, saved.Id);
        Assert.Equal(1L, saved.ConversationId);
        Assert.Equal("req-1", saved.RequestId);
        Assert.Equal("TurnStarted", saved.EventType);
        Assert.Equal(1, saved.Sequence);
        Assert.Equal("turn-1", saved.TurnId);
        Assert.Equal("Running", saved.Status);
        Assert.Equal("begin", saved.Message);
    }

    [Fact]
    public async Task RecordAsync_IsIdempotent_OnRequestIdAndSequence()
    {
        IAgentEventStore store = new InMemoryAgentEventStore();
        var record = new AgentEventRecord(
            ConversationId: 1, ProjectId: 1, UserId: 1,
            RequestId: "req-1", EventType: "StepStarted", Sequence: 1,
            Message: "first");

        var first = await store.RecordAsync(record);
        var second = await store.RecordAsync(record with { Message = "second" });

        Assert.Equal(first.Id, second.Id);
        Assert.Equal("first", second.Message); // First write wins
    }

    [Fact]
    public async Task RecordAsync_DifferentSequences_AreSeparate()
    {
        IAgentEventStore store = new InMemoryAgentEventStore();
        var base_ = new AgentEventRecord(
            ConversationId: 1, ProjectId: 1, UserId: 1,
            RequestId: "req-1", EventType: "X");

        var first = await store.RecordAsync(base_ with { Sequence = 1, Message = "a" });
        var second = await store.RecordAsync(base_ with { Sequence = 2, Message = "b" });
        var third = await store.RecordAsync(base_ with { Sequence = 3, Message = "c" });

        Assert.NotEqual(first.Id, second.Id);
        Assert.NotEqual(second.Id, third.Id);
        Assert.Equal("a", first.Message);
        Assert.Equal("b", second.Message);
        Assert.Equal("c", third.Message);
    }

    [Fact]
    public async Task RecordAsync_NoSequence_PersistsWithoutDedup()
    {
        // When sequence is null/0 (e.g. one-off events) we don't dedup; each call creates a new event.
        IAgentEventStore store = new InMemoryAgentEventStore();
        var record = new AgentEventRecord(
            ConversationId: 1, ProjectId: 1, UserId: 1,
            RequestId: "req-1", EventType: "ToolFailed", Sequence: null,
            Message: "boom");

        var first = await store.RecordAsync(record);
        var second = await store.RecordAsync(record);
        Assert.NotEqual(first.Id, second.Id);
    }

    [Fact]
    public async Task RecordAsync_CarriesPlanAndTaskReferences()
    {
        IAgentEventStore store = new InMemoryAgentEventStore();
        var record = new AgentEventRecord(
            ConversationId: 1, ProjectId: 1, UserId: 1,
            RequestId: "req-1", EventType: "TaskCompleted",
            Sequence: 1, PlanId: 555L, StageKey: "EntityFields",
            TaskKey: "create_project_entities", Status: "Completed");

        var saved = await store.RecordAsync(record);
        Assert.Equal(555L, saved.PlanId);
        Assert.Equal("EntityFields", saved.StageKey);
        Assert.Equal("create_project_entities", saved.TaskKey);
        Assert.Equal("Completed", saved.Status);
    }
}
