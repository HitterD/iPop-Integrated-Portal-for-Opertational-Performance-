namespace IPop.Infrastructure.Auth;

/// Failed sign-ins per client address. The per-account lockout (5 tries) cannot see one address spraying a single
/// password over many accounts; this counts across accounts. Memory is bounded: expired or oldest entries are dropped.
public sealed class LoginThrottle(int failuresPerWindow, TimeSpan window, TimeProvider? clock = null, int maxTracked = 20_000)
{
    private readonly TimeProvider time = clock ?? TimeProvider.System;
    private readonly object gate = new();
    private readonly Dictionary<string, (int Count, DateTimeOffset Start)> entries = [];

    public int Tracked { get { lock (gate) return entries.Count; } }

    public bool IsBlocked(string? address)
    {
        if (string.IsNullOrEmpty(address)) return false;
        lock (gate)
        {
            if (!entries.TryGetValue(address, out var e)) return false;
            if (time.GetUtcNow() - e.Start >= window) { entries.Remove(address); return false; }
            return e.Count >= failuresPerWindow;
        }
    }

    public void RecordFailure(string? address)
    {
        if (string.IsNullOrEmpty(address)) return;
        lock (gate)
        {
            var now = time.GetUtcNow();
            if (entries.TryGetValue(address, out var e) && now - e.Start < window) entries[address] = (e.Count + 1, e.Start);
            else
            {
                if (entries.Count >= maxTracked) Trim(now);
                entries[address] = (1, now);
            }
        }
    }

    public void RecordSuccess(string? address)
    {
        if (string.IsNullOrEmpty(address)) return;
        lock (gate) entries.Remove(address);
    }

    private void Trim(DateTimeOffset now)
    {
        foreach (var k in entries.Where(p => now - p.Value.Start >= window).Select(p => p.Key).ToList()) entries.Remove(k);
        if (entries.Count < maxTracked) return;
        foreach (var k in entries.OrderBy(p => p.Value.Start).Take(Math.Max(1, maxTracked / 10)).Select(p => p.Key).ToList()) entries.Remove(k);
    }
}
