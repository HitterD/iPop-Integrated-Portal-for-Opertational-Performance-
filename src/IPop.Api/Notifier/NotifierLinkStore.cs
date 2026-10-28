using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace IPop.Api.Notifier;

/// Web → Notifier sign-in. A browser-local nonce authorizes the fixed callback; the app keeps a pending
/// PKCE verifier and trades a single-use challenge-bound code for its own session. Codes expire after 60 seconds.
/// Possessing a protocol URL alone cannot redeem a session; IP addresses are discovery hints, not authority.
/// Signed-out apps also say "I'm waiting" every half minute, so the browser only opens the link when there is an
/// app on the same address to answer it. All in memory: a restart only costs a re-sent link.
public sealed class NotifierLinkStore
{
    public static readonly TimeSpan CodeLifetime = TimeSpan.FromSeconds(60);
    /// The app repeats "waiting" every 30 s; two missed beats and it no longer counts.
    public static readonly TimeSpan WaitingWindow = TimeSpan.FromSeconds(75);
    private const int MaxEntries = 5000;

    public static NotifierLinkStore Shared { get; } = new();

    // Codes are bound to an app-held PKCE verifier, never to an IP or a caller's claimed current account.
    public sealed record LinkCode(Guid UserId, string Challenge, string RequestId, DateTime CreatedAt);
    private sealed record Waiting(string Ip, DateTime SeenAt);

    private readonly ConcurrentDictionary<string, LinkCode> codes = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Waiting> waiting = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, (DateTime Window, int Count)> hits = new(StringComparer.Ordinal);

    public static bool ValidSecret(string? value) => value is { Length: 43 } && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
    public static string Challenge(string verifier) => Convert.ToBase64String(SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(verifier)))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private readonly object codeGate = new();
    public string? Issue(Guid userId, string challenge, string requestId, DateTime now)
    {
        if (!ValidSecret(challenge) || !ValidSecret(requestId)) return null;
        lock (codeGate)
        {
            Sweep(now);
            if (codes.Count >= MaxEntries) return null;
            var code = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            codes[code] = new LinkCode(userId, challenge, requestId, now);
            return code;
        }
    }

    /// Single use; an exchange must prove ownership of the pending app challenge.
    public LinkCode? Redeem(string? code, string? verifier, string? requestId, DateTime now)
    {
        if (!ValidSecret(code) || !ValidSecret(verifier) || !ValidSecret(requestId)) return null;
        lock (codeGate)
        {
            if (!codes.TryGetValue(code!, out var c)) return null;
            if (now - c.CreatedAt > CodeLifetime) { codes.TryRemove(code!, out _); return null; }
            if (c.RequestId != requestId || !CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.ASCII.GetBytes(c.Challenge), System.Text.Encoding.ASCII.GetBytes(Challenge(verifier!)))) return null;
            return codes.TryRemove(code!, out c) ? c : null;
        }
    }

    public void MarkWaiting(string deviceKey, string ip, DateTime now)
    {
        lock (codeGate)
        {
            if (waiting.Count >= MaxEntries) Sweep(now);
            if (waiting.Count >= MaxEntries && !waiting.ContainsKey(deviceKey)) return;
            waiting[deviceKey] = new Waiting(ip, now);
        }
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
