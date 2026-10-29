using System.Collections.Concurrent;
using IPop.Application.Notifier;

namespace IPop.Api.Notifier;

/// Web → Notifier sign-in (1.4.3, OAuth loopback style; flow in <see cref="NotifierLoopback"/>).
///
///   Authorize: the signed-in person clicked Authorize on /notifier/connect. A one-time code is minted for that
///              account and the app's PKCE challenge, plus a short code (XXXX-XXXX) for the paste fallback. Both
///              live 2 minutes and are used once.
///   Redeem:    the app trades the code (or the short code) + its verifier for its own session. A wrong verifier
///              counts as a failure; after <see cref="MaxFailures"/> the code is burned, so neither code can be
///              guessed or used without the verifier only the app holds.
/// Nothing here knows or compares network addresses. All in memory: a restart only costs a new click.
public sealed class NotifierLinkStore
{
    public static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes(2);
    public const int MaxFailures = 5;
    private const int MaxEntries = 5000;

    public static NotifierLinkStore Shared { get; } = new();

    public sealed record LinkCode(Guid UserId, string Challenge, DateTime CreatedAt, string ShortCode, int Failures = 0);
    public sealed record Minted(string Code, string ShortCode);
    public enum Outcome { Ok, Unknown, Expired, WrongVerifier, Burned }
    public sealed record RedeemResult(Outcome Outcome, LinkCode? Code = null);

    private readonly ConcurrentDictionary<string, LinkCode> codes = new(StringComparer.Ordinal);   // by code
    private readonly ConcurrentDictionary<string, string> shortCodes = new(StringComparer.Ordinal); // short → code
    private readonly ConcurrentDictionary<string, (DateTime Window, int Count)> hits = new(StringComparer.Ordinal);
    private readonly object gate = new();

    public static string Challenge(string verifier) => NotifierLoopback.Challenge(verifier);

    /// Authorize was clicked by <paramref name="userId"/> for the app's <paramref name="challenge"/>.
    /// Null = invalid challenge or the store is full.
    public Minted? Authorize(Guid userId, string? challenge, DateTime now)
    {
        if (!NotifierLoopback.IsSecret(challenge)) return null;
        lock (gate)
        {
            Sweep(now);
            if (codes.Count >= MaxEntries) return null;
            var code = NotifierLoopback.NewSecret();
            string shortCode;
            do shortCode = NotifierLoopback.NewShortCode(); while (shortCodes.ContainsKey(shortCode));
            codes[code] = new LinkCode(userId, challenge!, now, shortCode);
            shortCodes[shortCode] = code;
            return new Minted(code, shortCode);
        }
    }

    /// The app, with the code from its loopback callback.
    public RedeemResult Redeem(string? code, string? verifier, DateTime now)
    {
        if (!NotifierLoopback.IsSecret(code)) return new(Outcome.Unknown);
        lock (gate) return RedeemLocked(code!, verifier, now);
    }

    /// The app, with the short code the person pasted (any case, with or without the dash).
    public RedeemResult RedeemShort(string? shortCode, string? verifier, DateTime now)
    {
        var key = NotifierLoopback.NormalizeShortCode(shortCode);
        if (key is null) return new(Outcome.Unknown);
        lock (gate) return shortCodes.TryGetValue(key, out var code) ? RedeemLocked(code, verifier, now) : new(Outcome.Unknown);
    }

    private RedeemResult RedeemLocked(string code, string? verifier, DateTime now)
    {
        if (!codes.TryGetValue(code, out var c)) return new(Outcome.Unknown);
        if (now - c.CreatedAt > CodeLifetime) { Remove(code, c); return new(Outcome.Expired); }
        if (!NotifierLoopback.IsSecret(verifier) || !NotifierLoopback.SameSecret(c.Challenge, Challenge(verifier!)))
        {
            var failed = c with { Failures = c.Failures + 1 };
            if (failed.Failures >= MaxFailures) { Remove(code, c); return new(Outcome.Burned); }
            codes[code] = failed;
            return new(Outcome.WrongVerifier);
        }
        Remove(code, c);
        return new(Outcome.Ok, c);
    }

    private void Remove(string code, LinkCode c)
    {
        codes.TryRemove(code, out _);
        shortCodes.TryRemove(c.ShortCode, out _);
    }

    /// At most <paramref name="perMinute"/> calls per key (device key or account, and route).
    public bool Allow(string key, int perMinute, DateTime now)
    {
        if (hits.Count > MaxEntries) lock (gate) Sweep(now);
        var next = hits.AddOrUpdate(key, _ => (now, 1),
            (_, h) => now - h.Window >= TimeSpan.FromMinutes(1) ? (now, 1) : (h.Window, h.Count + 1));
        return next.Count <= perMinute;
    }

    private void Sweep(DateTime now)
    {
        foreach (var (k, c) in codes) if (now - c.CreatedAt > CodeLifetime) Remove(k, c);
        foreach (var (k, h) in hits) if (now - h.Window >= TimeSpan.FromMinutes(1)) hits.TryRemove(k, out _);
    }
}
