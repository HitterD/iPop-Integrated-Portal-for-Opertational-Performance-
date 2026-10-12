using Microsoft.Extensions.Caching.Memory;

namespace IPop.Infrastructure.MeetingRooms;

/// Wrong room passwords: at most <see cref="MaxAttempts"/> per (user, booking) in a fixed <see cref="Window"/> that
/// starts at the first try. Callers reserve an attempt with <see cref="TryConsume"/> (atomic check-and-reserve) BEFORE
/// comparing, and hand it back with <see cref="Refund"/> when the password was right, so only wrong tries count and
/// parallel requests can never evaluate more than <see cref="MaxAttempts"/> guesses.
/// Singleton, in-memory (own MemoryCache): counts reset when the API restarts and are per instance, acceptable for a
/// 1-in-a-million 6-digit guess at 5 tries per 5 minutes. When the cache is full the attempt is refused (fail closed).
public sealed class RoomPasswordLimiter(long sizeLimit = 100_000) : IDisposable
{
    public const int MaxAttempts = 5;
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(5);

    private readonly MemoryCache cache = new(new MemoryCacheOptions { SizeLimit = sizeLimit });
    private readonly Lock gate = new();

    private sealed class Counter { public int Used; }

    private static string Key(Guid userId, Guid meetingId) => $"{userId:N}:{meetingId:N}";

    /// True = one attempt reserved, go ahead and compare. False = limit reached, or the attempt could not be
    /// remembered (cache full).
    public bool TryConsume(Guid userId, Guid meetingId)
    {
        var key = Key(userId, meetingId);
        lock (gate)
        {
            if (!cache.TryGetValue<Counter>(key, out var c) || c is null)
            {
                c = new Counter();
                cache.Set(key, c, new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = Window, Size = 1 });
                // A full cache silently drops the entry: refuse rather than allow unlimited guesses.
                if (!cache.TryGetValue<Counter>(key, out var stored) || !ReferenceEquals(stored, c)) return false;
            }
            if (c.Used >= MaxAttempts) return false;
            c.Used++;
            return true;
        }
    }

    /// The reserved attempt was the right password: it does not count.
    public void Refund(Guid userId, Guid meetingId)
    {
        lock (gate)
        {
            if (cache.TryGetValue<Counter>(Key(userId, meetingId), out var c) && c is { Used: > 0 }) c.Used--;
        }
    }

    public void Dispose() => cache.Dispose();
}
