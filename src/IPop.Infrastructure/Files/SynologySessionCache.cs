using System.Collections.Concurrent;

namespace IPop.Infrastructure.Files;

/// <summary>
/// Process-wide store of FileStation session ids. <see cref="FileAccessService"/> is scoped (it
/// holds a DbContext), so a cache on the service itself died with every request and each
/// /api/files call logged in to the NAS again — the main cost of opening a folder, and a leaked
/// DSM session per call. Registered as a singleton so a session outlives the request.
/// </summary>
public sealed class SynologySessionCache
{
    private readonly ConcurrentDictionary<(Guid, string), string> _sids = new();
    private readonly ConcurrentDictionary<(Guid, string), SemaphoreSlim> _locks = new();

    public string? ServiceSid { get; set; }
    public SemaphoreSlim ServiceLock { get; } = new(1, 1);

    public bool TryGet(Guid userId, string server, out string sid)
    {
        var found = _sids.TryGetValue((userId, server), out var s);
        sid = s ?? "";
        return found;
    }

    public void Set(Guid userId, string server, string sid) => _sids[(userId, server)] = sid;

    public void Remove(Guid userId, string server) => _sids.TryRemove((userId, server), out _);

    public void RemoveAll(Guid userId)
    {
        foreach (var k in _sids.Keys.Where(k => k.Item1 == userId).ToList())
            _sids.TryRemove(k, out _);
    }

    private readonly ConcurrentDictionary<string, DateTime> _unreachableUntil = new(StringComparer.OrdinalIgnoreCase);
    public static readonly TimeSpan UnreachableFor = TimeSpan.FromSeconds(30);

    /// A NAS that just failed to answer (timeout, connection refused): requests in the next 30 s fail at once
    /// instead of each waiting for the connect timeout again behind the login lock (a Files page fires three).
    public void MarkUnreachable(string server, DateTime nowUtc) => _unreachableUntil[server] = nowUtc + UnreachableFor;
    public void MarkReachable(string server) => _unreachableUntil.TryRemove(server, out _);
    public bool IsUnreachable(string server, DateTime nowUtc) =>
        _unreachableUntil.TryGetValue(server, out var until) && until > nowUtc;

    /// <summary>One login per (user, server) at a time: the page fires list + storage together.</summary>
    public SemaphoreSlim LockFor(Guid userId, string server) => _locks.GetOrAdd((userId, server), _ => new SemaphoreSlim(1, 1));
}
