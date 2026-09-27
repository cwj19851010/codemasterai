using System.Threading.Tasks;
using CodeMaster.Agent.Services;
using Xunit;

namespace CodeMaster.Agent.Tests.Services;

public class AiConversationReadCacheManagerTests
{
    [Fact]
    public void Acquire_SameConversation_ReturnsSameInstance()
    {
        var mgr = new AiConversationReadCacheManager();
        var blueprintService = new FakeBlueprintService();
        var first = mgr.Acquire(42L, blueprintService);
        var second = mgr.Acquire(42L, blueprintService);
        Assert.Same(first, second);
    }

    [Fact]
    public void Acquire_DifferentConversations_ReturnsDifferentInstances()
    {
        var mgr = new AiConversationReadCacheManager();
        var blueprintService = new FakeBlueprintService();
        var first = mgr.Acquire(42L, blueprintService);
        var second = mgr.Acquire(43L, blueprintService);
        Assert.NotSame(first, second);
    }

    [Fact]
    public void Invalidate_DoesNotRemoveCache()
    {
        var mgr = new AiConversationReadCacheManager();
        var blueprintService = new FakeBlueprintService();
        var cache = mgr.Acquire(42L, blueprintService);
        mgr.Invalidate(42L);
        Assert.Same(cache, mgr.Acquire(42L, blueprintService));
    }

    [Fact]
    public void Drop_RemovesCache()
    {
        var mgr = new AiConversationReadCacheManager();
        var blueprintService = new FakeBlueprintService();
        mgr.Acquire(42L, blueprintService);
        mgr.Drop(42L);
        // New instance after drop
        Assert.NotSame((object)mgr.Acquire(42L, blueprintService), null);
    }
}
