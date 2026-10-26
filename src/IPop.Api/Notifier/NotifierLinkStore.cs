using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace IPop.Api.Notifier;

/// Web → iPop Notifier sign-in. The browser (already signed in) asks for a one-time code and opens
/// ipop-notifier://signin?code=…; the app on that PC trades the code for its own session. Codes live 60 seconds,
/// work once and only ever travel through the local link scheme, so a code can't reach another PC.
/// Signed-out apps also say "I'm waiting" every half minute, so the browser only opens the link when there is an
/// app on the same address to answer it. All in memory: a restart only costs a re-sent link.
public sealed class NotifierLinkStore
{
    public static readonly TimeSpan CodeLifetime = TimeSpan.FromSeconds(60);
    /// The app repeats "waiting" every 30 s; two missed beats and it no longer counts.
    public static readonly TimeSpan WaitingWindow = TimeSpan.FromSeconds(75);
    private const int MaxEntries = 5000;

    public static NotifierLinkStore Shared { get; } = new();

    /// Fresh = minted within a minute of a password sign-in in that browser (an account switch needs it).
    public sealed record LinkCode(Guid UserId, string? Ip, DateTime CreatedAt, bool Fresh);
    private sealed record Waiting(string Ip, DateTime SeenAt);

    private readonly ConcurrentDictionary<string, LinkCode> codes = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Waiting> waiting = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, (DateTime Window, int Count)> hits = new(StringComparer.Ordinal);

    public string Issue(Guid userId, string? ip, bool fresh, DateTime now)
    {
        if (codes.Count > MaxEntries) Sweep(now);
        var code = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        codes[code] = new LinkCode(userId, ip, now, fresh);
        return code;
    }

    /// Single use: the first exchange removes the code, whether it is still fresh or not.
    public LinkCode? Redeem(string? code, DateTime now)
    {
        if (string.IsNullOrEmpty(code) || !codes.TryRemove(code, out var c)) return null;
        return now - c.CreatedAt <= CodeLifetime ? c : null;
    }

    public void MarkWaiting(string deviceKey, string ip, DateTime now)
    {
        if (waiting.Count > MaxEntries) Sweep(now);
        waiting[deviceKey] = new Waiting(ip, now);
    }

    public void ClearWaiting(string deviceKey) => waiting.TryRemove(deviceKey, out _);

    public bool AnyWaiting(string? ip, DateTime now) =>
        ip is not null && waiting.Values.Any(w => w.Ip == ip && now - w.SeenAt <= WaitingWindow);

    /// Anonymous routes: at most <paramref name="perMinute"/> calls per client address and route.
    public bool Allow(string key, int perMinute, DateTime now)
    {
        if (hits.Count > MaxEntries) Sweep(now);
        var next = hits.AddOrUpdate(key, _ => (now, 1),
            (_, h) => now - h.Window >= TimeSpan.FromMinutes(1) ? (now, 1) : (h.Window, h.Count + 1));
        return next.Count <= perMinute;
    }

    private void Sweep(DateTime now)
    {
        foreach (var (k, c) in codes) if (now - c.CreatedAt > CodeLifetime) codes.TryRemove(k, out _);
        foreach (var (k, w) in waiting) if (now - w.SeenAt > WaitingWindow) waiting.TryRemove(k, out _);
        foreach (var (k, h) in hits) if (now - h.Window >= TimeSpan.FromMinutes(1)) hits.TryRemove(k, out _);
    }
}
