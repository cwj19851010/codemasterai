using System.Collections.Concurrent;

namespace CodeMaster.Agent.Services;

/// <summary>
/// Owns per-conversation serialization and short-lived failure signatures.
/// Entries are reference-counted so an archived conversation cannot dispose a gate
/// while another request is still waiting on it.
/// </summary>
internal sealed class ConversationCoordinationStore
{
    private readonly ConcurrentDictionary<long, GateEntry> _gates = new();
    private readonly ConcurrentDictionary<long, FailureEntry> _failureSignatures = new();
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _idleExpiration;
    private readonly int _maximumEntries;
    private readonly string _busyMessage;
    private int _sweepCounter;

    public ConversationCoordinationStore(
        TimeProvider? timeProvider = null,
        TimeSpan? idleExpiration = null,
        int maximumEntries = 512,
        string? busyMessage = null)
    {
        if (maximumEntries < 1)
            throw new ArgumentOutOfRangeException(nameof(maximumEntries));

        _timeProvider = timeProvider ?? TimeProvider.System;
        _idleExpiration = idleExpiration ?? TimeSpan.FromMinutes(30);
        if (_idleExpiration <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(idleExpiration));
        _maximumEntries = maximumEntries;
        _busyMessage = string.IsNullOrWhiteSpace(busyMessage)
            ? "The conversation is busy. Wait for the current Agent request to finish and retry."
            : busyMessage;
    }

    internal int GateCount => _gates.Count;
    internal int FailureSignatureCount => _failureSignatures.Count;

    public async Task<ConversationGateLease> AcquireAsync(
        long conversationId,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        SweepIfNeeded();

        while (true)
        {
            var entry = _gates.GetOrAdd(conversationId, _ => new GateEntry(_timeProvider.GetUtcNow()));
            if (!entry.TryAddReference())
            {
                _gates.TryRemove(new KeyValuePair<long, GateEntry>(conversationId, entry));
                continue;
            }

            var acquired = false;
            try
            {
                acquired = await entry.Gate.WaitAsync(timeout, cancellationToken);
                if (!acquired)
                    throw new TimeoutException(_busyMessage);

                entry.Touch(_timeProvider.GetUtcNow());
                return new ConversationGateLease(this, conversationId, entry);
            }
            catch
            {
                Release(conversationId, entry, acquired);
                throw;
            }
        }
    }

    public void RemoveConversation(long conversationId)
    {
        _failureSignatures.TryRemove(conversationId, out _);
        if (_gates.TryRemove(conversationId, out var entry))
        {
            entry.Retire();
            DisposeIfUnused(entry);
        }
    }

    public void ClearFailureSignature(long conversationId)
        => _failureSignatures.TryRemove(conversationId, out _);

    public bool TryGetFailureSignature(long conversationId, out string signature)
    {
        if (_failureSignatures.TryGetValue(conversationId, out var entry))
        {
            var now = _timeProvider.GetUtcNow();
            if (now - entry.LastTouched <= _idleExpiration)
            {
                entry.Touch(now);
                signature = entry.Value;
                return true;
            }

            _failureSignatures.TryRemove(new KeyValuePair<long, FailureEntry>(conversationId, entry));
        }

        signature = string.Empty;
        return false;
    }

    public void SetFailureSignature(long conversationId, string signature)
    {
        if (string.IsNullOrWhiteSpace(signature))
        {
            ClearFailureSignature(conversationId);
            return;
        }

        SweepIfNeeded();
        _failureSignatures[conversationId] = new FailureEntry(signature, _timeProvider.GetUtcNow());
        TrimIdleEntries(_failureSignatures, _maximumEntries, static entry => entry.LastTouched, _timeProvider.GetUtcNow(), _idleExpiration);
    }

    internal void SweepNow()
    {
        var now = _timeProvider.GetUtcNow();
        foreach (var pair in _gates)
        {
            if (pair.Value.ReferenceCount != 0 || now - pair.Value.LastTouched <= _idleExpiration)
                continue;

            if (_gates.TryRemove(new KeyValuePair<long, GateEntry>(pair.Key, pair.Value)))
            {
                pair.Value.Retire();
                DisposeIfUnused(pair.Value);
            }
        }

        TrimIdleEntries(_failureSignatures, _maximumEntries, static entry => entry.LastTouched, now, _idleExpiration);
    }

    internal void Release(long conversationId, GateEntry entry, bool acquired)
    {
        if (acquired)
            entry.Gate.Release();

        entry.Touch(_timeProvider.GetUtcNow());
        if (entry.ReleaseReference() == 0 && entry.IsRetired)
            DisposeIfUnused(entry);

        if (Interlocked.Increment(ref _sweepCounter) % 32 == 0)
            SweepNow();
    }

    private void SweepIfNeeded()
    {
        if (Interlocked.Increment(ref _sweepCounter) % 32 == 0)
            SweepNow();
    }

    private static void TrimIdleEntries<T>(
        ConcurrentDictionary<long, T> entries,
        int maximumEntries,
        Func<T, DateTimeOffset> lastTouched,
        DateTimeOffset now,
        TimeSpan idleExpiration)
        where T : class
    {
        foreach (var pair in entries)
        {
            if (now - lastTouched(pair.Value) <= idleExpiration)
                continue;
            entries.TryRemove(new KeyValuePair<long, T>(pair.Key, pair.Value));
        }

        if (entries.Count <= maximumEntries)
            return;

        foreach (var pair in entries
                     .OrderBy(pair => lastTouched(pair.Value))
                     .Take(Math.Max(0, entries.Count - maximumEntries)))
        {
            entries.TryRemove(new KeyValuePair<long, T>(pair.Key, pair.Value));
        }
    }

    private static void DisposeIfUnused(GateEntry entry)
    {
        if (entry.ReferenceCount == 0)
            entry.Dispose();
    }

    internal sealed class ConversationGateLease : IDisposable
    {
        private ConversationCoordinationStore? _owner;
        private readonly long _conversationId;
        private readonly GateEntry _entry;

        internal ConversationGateLease(ConversationCoordinationStore owner, long conversationId, GateEntry entry)
        {
            _owner = owner;
            _conversationId = conversationId;
            _entry = entry;
        }

        public void Release() => Dispose();

        public void Dispose()
        {
            var owner = Interlocked.Exchange(ref _owner, null);
            owner?.Release(_conversationId, _entry, acquired: true);
        }
    }

    internal sealed class GateEntry : IDisposable
    {
        private int _references;
        private int _retired;
        private int _disposed;

        internal GateEntry(DateTimeOffset now)
        {
            LastTouched = now;
        }

        internal SemaphoreSlim Gate { get; } = new(1, 1);
        internal DateTimeOffset LastTouched { get; private set; }
        internal int ReferenceCount => Volatile.Read(ref _references);
        internal bool IsRetired => Volatile.Read(ref _retired) == 1;

        internal bool TryAddReference()
        {
            if (IsRetired)
                return false;

            Interlocked.Increment(ref _references);
            if (IsRetired)
            {
                Interlocked.Decrement(ref _references);
                return false;
            }

            return true;
        }

        internal int ReleaseReference() => Interlocked.Decrement(ref _references);

        internal void Retire() => Interlocked.Exchange(ref _retired, 1);

        internal void Touch(DateTimeOffset now) => LastTouched = now;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                Gate.Dispose();
        }
    }

    internal sealed class FailureEntry
    {
        internal FailureEntry(string value, DateTimeOffset now)
        {
            Value = value;
            LastTouched = now;
        }

        internal string Value { get; }
        internal DateTimeOffset LastTouched { get; private set; }

        internal void Touch(DateTimeOffset now) => LastTouched = now;
    }
}
