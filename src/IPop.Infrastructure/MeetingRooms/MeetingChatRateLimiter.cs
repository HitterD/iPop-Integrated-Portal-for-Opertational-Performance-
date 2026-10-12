namespace IPop.Infrastructure.MeetingRooms;

/// In-meeting chat flood guard: at most <see cref="MaxMessages"/> per user in any sliding <see cref="Window"/>.
/// Singleton, in-memory, per API instance (resets on restart, which is fine for a flood guard).
public sealed class MeetingChatRateLimiter
{
    public const int MaxMessages = 10;
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(10);
    private const int PruneAbove = 5_000;

    private readonly Dictionary<Guid, Queue<DateTime>> sent = new();
    private readonly Lock gate = new();

    /// True = the message may go (and is counted). False = the user already sent <see cref="MaxMessages"/> in the last
    /// <see cref="Window"/>.
    public bool TryAcquire(Guid userId, DateTime nowUtc)
    {
        lock (gate)
        {
            if (sent.Count > PruneAbove)
                foreach (var stale in sent.Where(kv => kv.Value.Count == 0 || kv.Value.Last() <= nowUtc - Window).Select(kv => kv.Key).ToList())
                    sent.Remove(stale);
            if (!sent.TryGetValue(userId, out var q)) sent[userId] = q = new Queue<DateTime>();
            while (q.Count > 0 && q.Peek() <= nowUtc - Window) q.Dequeue();
            if (q.Count >= MaxMessages) return false;
            q.Enqueue(nowUtc);
            return true;
        }
    }
}
