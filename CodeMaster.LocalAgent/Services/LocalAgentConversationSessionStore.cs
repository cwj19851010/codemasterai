using System.Collections.Concurrent;
using Microsoft.Agents.AI;

namespace CodeMaster.LocalAgent.Services;

public sealed class LocalAgentConversationSessionStore
{
    private static readonly TimeSpan IdleExpiration = TimeSpan.FromMinutes(30);
    private const int MaximumEntries = 64;
    private readonly ConcurrentDictionary<string, LocalAgentConversationSessionState> _entries = new();

    internal LocalAgentConversationSessionState GetOrCreate(string serverBaseUrl, long conversationId)
    {
        PruneExpiredEntries();
        var key = BuildKey(serverBaseUrl, conversationId);
        var state = _entries.GetOrAdd(key, _ => new LocalAgentConversationSessionState());
        state.Touch();
        return state;
    }

    public bool Invalidate(string serverBaseUrl, long conversationId)
    {
        return _entries.TryRemove(BuildKey(serverBaseUrl, conversationId), out _);
    }

    internal int InvalidateProvider(string serverBaseUrl, long providerId)
    {
        var prefix = NormalizeServerBaseUrl(serverBaseUrl) + "|";
        var keys = _entries
            .Where(item => item.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                           item.Value.ProviderId == providerId)
            .Select(item => item.Key)
            .ToList();
        foreach (var key in keys)
            _entries.TryRemove(key, out _);
        return keys.Count;
    }

    internal static string BuildKey(string serverBaseUrl, long conversationId)
    {
        return $"{NormalizeServerBaseUrl(serverBaseUrl)}|{conversationId}";
    }

    private void PruneExpiredEntries()
    {
        var cutoff = DateTime.UtcNow - IdleExpiration;
        foreach (var item in _entries.Where(item => item.Value.LastAccessUtc < cutoff).ToList())
            _entries.TryRemove(item.Key, out _);

        if (_entries.Count <= MaximumEntries)
            return;
        foreach (var item in _entries.OrderBy(item => item.Value.LastAccessUtc)
                     .Take(_entries.Count - MaximumEntries)
                     .ToList())
            _entries.TryRemove(item.Key, out _);
    }

    private static string NormalizeServerBaseUrl(string serverBaseUrl)
    {
        return (serverBaseUrl ?? string.Empty).Trim().TrimEnd('/').ToLowerInvariant();
    }
}

internal sealed class LocalAgentConversationSessionState
{
    public SemaphoreSlim Gate { get; } = new(1, 1);
    public AgentSession? Session { get; private set; }
    public long ProviderId { get; private set; }
    public int ServerMessageCount { get; private set; }
    public string? ProjectStructureVersion { get; set; }
    public DateTime LastAccessUtc { get; private set; } = DateTime.UtcNow;

    public bool CanReuse(long providerId, int currentServerMessageCount)
    {
        Touch();
        return Session != null &&
               ProviderId == providerId &&
               currentServerMessageCount == ServerMessageCount + 1;
    }

    public void Reset()
    {
        Session = null;
        ProviderId = 0;
        ServerMessageCount = 0;
        ProjectStructureVersion = null;
        Touch();
    }

    public void Commit(AgentSession session, long providerId, int serverMessageCount)
    {
        Session = session;
        ProviderId = providerId;
        ServerMessageCount = serverMessageCount;
        Touch();
    }

    public void Touch()
    {
        LastAccessUtc = DateTime.UtcNow;
    }
}
