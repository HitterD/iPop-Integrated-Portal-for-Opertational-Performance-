using System.Collections.Concurrent;

namespace IPop.Api.Chat;

/// One send limiter for every way a message can enter a room (hub, REST send, thread reply,
/// forward): 30 per 60 s per user. In-memory, so per API process; with more than one replica
/// the effective cap is 30 x replicas (see PR note).
public static class ChatSendLimiter
{
    public const int Limit = 30;
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(60);

    private static readonly ConcurrentDictionary<string, (int Count, DateTime WindowStart)> Windows = new();

    public static bool TryAcquire(string key, DateTime? utcNow = null)
    {
        var now = utcNow ?? DateTime.UtcNow;
        var allowed = true;

        Windows.AddOrUpdate(key,
            _ => (1, now),
            (_, prev) =>
            {
                if (now - prev.WindowStart > Window) return (1, now);
                if (prev.Count >= Limit) { allowed = false; return prev; }
                return (prev.Count + 1, prev.WindowStart);
            });

        // Opportunistic eviction of long-idle entries once the table is large.
        if (Windows.Count > 500)
            foreach (var kv in Windows)
                if ((now - kv.Value.WindowStart).TotalMinutes > 10)
                    Windows.TryRemove(kv.Key, out _);

        return allowed;
    }

    public static void Reset() => Windows.Clear();
}
