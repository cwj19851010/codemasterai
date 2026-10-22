using CodeMaster.LocalAgent.Services;

namespace CodeMaster.LocalAgent.Tests.Services;

public class LocalAgentConversationSessionStoreTests
{
    [Fact]
    public void GetOrCreate_reuses_normalized_server_and_conversation_key()
    {
        var store = new LocalAgentConversationSessionStore();

        var first = store.GetOrCreate("HTTP://CodeMaster.Test/", 42);
        first.ProjectStructureVersion = "VERSION-1";
        var second = store.GetOrCreate("http://codemaster.test", 42);
        var otherConversation = store.GetOrCreate("http://codemaster.test", 43);

        Assert.Same(first, second);
        Assert.Equal("VERSION-1", second.ProjectStructureVersion);
        Assert.NotSame(first, otherConversation);
    }

    [Fact]
    public void Invalidate_removes_cached_conversation_state()
    {
        var store = new LocalAgentConversationSessionStore();
        var cached = store.GetOrCreate("http://codemaster.test", 42);
        cached.ProjectStructureVersion = "VERSION-1";

        var removed = store.Invalidate("http://codemaster.test/", 42);
        var recreated = store.GetOrCreate("http://codemaster.test", 42);

        Assert.True(removed);
        Assert.NotSame(cached, recreated);
        Assert.Null(recreated.ProjectStructureVersion);
    }
}
