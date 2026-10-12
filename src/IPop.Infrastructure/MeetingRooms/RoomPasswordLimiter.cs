using Microsoft.Extensions.Caching.Memory;

namespace IPop.Infrastructure.MeetingRooms;

/// Wrong room passwords: at most <see cref="MaxAttempts"/> per (user, booking) in a fixed <see cref="Window"/> that
/// starts at the first wrong try. Singleton, in-memory (own MemoryCache): counts reset when the API restarts, which
/// is acceptable for a 1-in-a-million 6-digit guess at 5 tries per 5 minutes. A second API instance would need a
/// shared store.
public sealed class RoomPasswordLimiter : IDisposable
{
    public const int MaxAttempts = 5;
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(5);

    private readonly MemoryCache cache = new(new MemoryCacheOptions { SizeLimit = 100_000 });
    private readonly Lock gate = new();

    private sealed class Counter { public int Fails; }

    private static string Key(Guid userId, Guid meetingId) => $"{userId:N}:{meetingId:N}";

    public bool IsBlocked(Guid userId, Guid meetingId) =>
        cache.TryGetValue<Counter>(Key(userId, meetingId), out var c) && c is not null && Volatile.Read(ref c.Fails) >= MaxAttempts;

    public void Fail(Guid userId, Guid meetingId)
    {
        lock (gate)
        {
            var c = cache.GetOrCreate(Key(userId, meetingId), e =>
            {
                e.AbsoluteExpirationRelativeToNow = Window;
                e.Size = 1;
                return new Counter();
            })!;
            Interlocked.Increment(ref c.Fails);
        }
    }

    public void Dispose() => cache.Dispose();
}
