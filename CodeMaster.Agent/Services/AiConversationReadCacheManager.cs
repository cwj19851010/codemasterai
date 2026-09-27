using System.Collections.Concurrent;

namespace CodeMaster.Agent.Services;

/// <summary>
/// Per-conversation cache manager. Holds an <see cref="AiProjectReadCache"/> instance per
/// conversation so repeated turns do not re-fetch the project blueprint. Each cache
/// survives across turns until invalidated (e.g. after a project mutation).
/// </summary>
public sealed class AiConversationReadCacheManager
{
    private readonly ConcurrentDictionary<long, CacheEntry> _caches = new();

    public AiProjectReadCache Acquire(long conversationId, ICodeMasterProjectBlueprintService blueprintService)
    {
        var entry = _caches.GetOrAdd(conversationId, _ => new CacheEntry(new AiProjectReadCache(blueprintService)));
        return entry.Cache;
    }

    public void Invalidate(long conversationId)
    {
        if (_caches.TryGetValue(conversationId, out var entry))
            entry.Cache.InvalidateMetadata();
    }

    public void Drop(long conversationId)
    {
        _caches.TryRemove(conversationId, out _);
    }

    private sealed class CacheEntry
    {
        public AiProjectReadCache Cache { get; }
        public CacheEntry(AiProjectReadCache cache) => Cache = cache;
    }
}
