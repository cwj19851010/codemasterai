using CodeMaster.Agent.Services;

namespace CodeMaster.Agent.Tests.Services;

public sealed class ConversationCoordinationStoreTests
{
    [Fact]
    public async Task ArchivedConversationRemovesIdleGateAndFailureSignature()
    {
        var clock = new ManualTimeProvider();
        var store = new ConversationCoordinationStore(clock, TimeSpan.FromMinutes(5), maximumEntries: 8);
        using (await store.AcquireAsync(10, TimeSpan.FromSeconds(1))) { }
        store.SetFailureSignature(10, "CS0103:Foo");

        store.RemoveConversation(10);
        store.SweepNow();

        Assert.Equal(0, store.GateCount);
        Assert.Equal(0, store.FailureSignatureCount);
    }

    [Fact]
    public async Task IdleEntriesExpireWithoutExplicitArchive()
    {
        var clock = new ManualTimeProvider();
        var store = new ConversationCoordinationStore(clock, TimeSpan.FromMinutes(5), maximumEntries: 8);
        using (await store.AcquireAsync(10, TimeSpan.FromSeconds(1))) { }
        store.SetFailureSignature(10, "CS0103:Foo");

        clock.Advance(TimeSpan.FromMinutes(6));
        store.SweepNow();

        Assert.Equal(0, store.GateCount);
        Assert.False(store.TryGetFailureSignature(10, out _));
    }

    [Fact]
    public async Task ActiveLeaseSurvivesExplicitRemovalUntilReleased()
    {
        var store = new ConversationCoordinationStore(maximumEntries: 8);
        var lease = await store.AcquireAsync(10, TimeSpan.FromSeconds(1));

        store.RemoveConversation(10);
        Assert.Equal(0, store.GateCount);
        lease.Release();

        using (await store.AcquireAsync(10, TimeSpan.FromSeconds(1))) { }
    }

    [Fact]
    public async Task Concurrent_requests_for_one_conversation_are_serialized()
    {
        var store = new ConversationCoordinationStore(maximumEntries: 8);
        using var first = await store.AcquireAsync(10, TimeSpan.FromSeconds(1));
        var secondStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = Task.Run(async () =>
        {
            using var lease = await store.AcquireAsync(10, TimeSpan.FromSeconds(2));
            secondStarted.SetResult();
        });

        await Task.Delay(50);
        Assert.False(secondStarted.Task.IsCompleted);
        first.Release();
        await second;
        Assert.True(secondStarted.Task.IsCompletedSuccessfully);
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan duration) => _now += duration;
    }
}
