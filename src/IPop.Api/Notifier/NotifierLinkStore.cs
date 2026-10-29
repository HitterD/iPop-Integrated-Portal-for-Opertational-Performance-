using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace IPop.Api.Notifier;

/// Web → Notifier sign-in. A browser-local nonce authorizes the hand-off; the app keeps a pending PKCE verifier and
/// trades a single-use challenge-bound code for its own session. Codes expire after 60 seconds. Possessing a protocol
/// URL alone cannot redeem a session; IP addresses are discovery hints, not authority.
///
/// One tab, one prompt (1.4.0):
///   1. The app's "Sign in with browser" click (or first run) registers an <see cref="Intent"/> for 2 minutes. Only a
///      fresh intent lets a browser show "Open iPop Notifier?"; the 30 s "waiting" heartbeat is discovery only.
///   2. Open iPop tabs on the same address hear about it at once (AppHub) and exactly one <see cref="ClaimIntent"/>s
///      it. If none does within ~5 s the app reserves it (<see cref="CheckIntent"/>) and opens exactly one tab.
///   3. The tab launches ipop-notifier://prepare?nonce=…; the app answers by posting its PKCE request for that nonce
///      (<see cref="PutHandoff"/>, bound to the account that claimed the intent) instead of opening a second tab; the
///      same tab takes it and mints the code.
///   4. After redeeming (and any local account-switch confirmation) the app confirms (<see cref="MarkConnected"/>), so
///      the tab can say "connected" and, if the app opened it, close itself.
/// All in memory: a restart only costs a re-sent link.
public sealed class NotifierLinkStore
{
    public static readonly TimeSpan CodeLifetime = TimeSpan.FromSeconds(60);
    /// The app repeats "waiting" every 30 s; two missed beats and it no longer counts.
    public static readonly TimeSpan WaitingWindow = TimeSpan.FromSeconds(75);
    /// A click on "Sign in with browser" lets a browser on the same address prompt for this long.
    public static readonly TimeSpan IntentLifetime = TimeSpan.FromMinutes(2);
    /// The tab may learn "connected" for this long after its code was issued.
    public static readonly TimeSpan StatusLifetime = TimeSpan.FromMinutes(3);
    private const int MaxEntries = 5000;

    public static NotifierLinkStore Shared { get; } = new();

    // Codes are bound to an app-held PKCE verifier, never to an IP or a caller's claimed current account.
    public sealed record LinkCode(Guid UserId, string Challenge, string RequestId, DateTime CreatedAt);
    private sealed record Waiting(string Ip, DateTime SeenAt);
    public enum Claim { None, Tab, Reserved }
    public sealed record Intent(string Id, string Ip, string Scheme, DateTime CreatedAt, Claim State, Guid? ClaimedBy = null, bool HandedOff = false);
    public sealed record Handoff(string RequestId, string Challenge, DateTime CreatedAt, Guid TakerId, string Ip);
    public sealed record LinkStatus(Guid UserId, DateTime IssuedAt, DateTime? ConnectedAt, string? Name);

    private readonly ConcurrentDictionary<string, LinkCode> codes = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Waiting> waiting = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, (DateTime Window, int Count)> hits = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Intent> intents = new(StringComparer.Ordinal);      // by device key
    private readonly ConcurrentDictionary<string, Handoff> handoffs = new(StringComparer.Ordinal);    // by SHA-256(nonce)
    private readonly ConcurrentDictionary<string, LinkStatus> statuses = new(StringComparer.Ordinal); // by request id
    private readonly ConcurrentDictionary<string, string> tabs = new(StringComparer.Ordinal);         // hub connection → address

    public static bool ValidSecret(string? value) => value is { Length: 43 } && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
    public static bool ValidScheme(string? scheme) => scheme is "ipop-notifier" or "ipop-notifier-qa";
    public static string Challenge(string verifier) => Convert.ToBase64String(SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(verifier)))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static string NewSecret() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static bool SameSecret(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(System.Text.Encoding.ASCII.GetBytes(a), System.Text.Encoding.ASCII.GetBytes(b));

    private readonly object codeGate = new();
    public string? Issue(Guid userId, string challenge, string requestId, DateTime now)
    {
        if (!ValidSecret(challenge) || !ValidSecret(requestId)) return null;
        lock (codeGate)
        {
            Sweep(now);
            if (codes.Count >= MaxEntries) return null;
            var code = NewSecret();
            codes[code] = new LinkCode(userId, challenge, requestId, now);
            // The request id is the app's own secret; the first account to mint for it owns its status.
            if (!statuses.TryGetValue(requestId, out var st) || st.UserId == userId)
                statuses[requestId] = new LinkStatus(userId, now, null, null);
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
            if (c.RequestId != requestId || !SameSecret(c.Challenge, Challenge(verifier!))) return null;
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

    // ── intent: a click in the app is the only thing that lets a browser prompt ────────────────────────────

    /// "Sign in with browser" (or first run). Every call starts a new attempt with a new id: an "Open browser again"
    /// must be claimable again even though an earlier tab claimed the previous attempt.
    public Intent? RegisterIntent(string deviceKey, string ip, string scheme, DateTime now)
    {
        if (!ValidScheme(scheme)) return null;
        lock (codeGate)
        {
            Sweep(now);
            if (intents.Count >= MaxEntries && !intents.ContainsKey(deviceKey)) return null;
            return intents[deviceKey] = new Intent(NewSecret(), ip, scheme, now, Claim.None);
        }
    }

    public bool HasFreshIntent(string? ip, DateTime now) =>
        ip is not null && intents.Values.Any(i => i.Ip == ip && now - i.CreatedAt <= IntentLifetime);

    /// One tab wins a fresh unclaimed intent on its address; every other tab stands down. A tab the app opened itself
    /// (<paramref name="opened"/>, ?notifier=signin) may also take the intent the app reserved for it.
    public Intent? ClaimIntent(string? ip, bool opened, Guid userId, DateTime now)
    {
        if (ip is null) return null;
        lock (codeGate)
        {
            var open = intents.Where(p => p.Value.Ip == ip && now - p.Value.CreatedAt <= IntentLifetime
                    && (p.Value.State == Claim.None || (opened && p.Value.State == Claim.Reserved)))
                .OrderByDescending(p => p.Value.CreatedAt).FirstOrDefault();
            if (open.Key is null) return null;
            return intents[open.Key] = open.Value with { State = Claim.Tab, ClaimedBy = userId };
        }
    }

    /// The app polls this right after its click. True = an iPop tab took it, do not open another one.
    /// <paramref name="reserve"/> (the app's last poll, ~5 s): nobody took it, so atomically keep it for the one tab
    /// the app is about to open; tabs that hear about it later no longer claim it.
    public bool? CheckIntent(string deviceKey, string? intentId, bool reserve, DateTime now)
    {
        lock (codeGate)
        {
            if (intentId is null || !intents.TryGetValue(deviceKey, out var i) || !ValidSecret(intentId) || !SameSecret(i.Id, intentId)
                || now - i.CreatedAt > IntentLifetime) return null;
            if (i.State == Claim.Tab) return true;
            if (reserve && i.State == Claim.None) intents[deviceKey] = i with { State = Claim.Reserved };
            return false;
        }
    }

    public Intent? IntentOf(string deviceKey) => intents.TryGetValue(deviceKey, out var i) ? i : null;
    public void ClearIntent(string deviceKey) => intents.TryRemove(deviceKey, out _);

    // ── hand-off: the app answers prepare?nonce=… to the same tab, instead of opening a second one ─────────

    /// The app answers the prepare link of the tab that claimed ITS intent. The answer is bound to that intent's
    /// claimer (account + address), is accepted once per intent, and first answer wins per nonce: a web page that
    /// launches prepare with a nonce of its own cannot collect the PKCE request unless it is the tab that claimed the
    /// app's fresh, clicked-for intent.
    public bool PutHandoff(string deviceKey, string? intentId, string? nonce, string? requestId, string? challenge, DateTime now)
    {
        if (!ValidSecret(intentId) || !ValidSecret(nonce) || !ValidSecret(requestId) || !ValidSecret(challenge)) return false;
        lock (codeGate)
        {
            Sweep(now);
            if (!intents.TryGetValue(deviceKey, out var i) || !SameSecret(i.Id, intentId!) || now - i.CreatedAt > IntentLifetime
                || i.State != Claim.Tab || i.ClaimedBy is not { } taker || i.HandedOff) return false;
            if (handoffs.Count >= MaxEntries) return false;
            if (!handoffs.TryAdd(Challenge(nonce!), new Handoff(requestId!, challenge!, now, taker, i.Ip))) return false;
            intents[deviceKey] = i with { HandedOff = true };
            return true;
        }
    }

    /// Single use, 60 seconds, and only for the account (and address) that claimed the intent.
    public Handoff? TakeHandoff(string? nonce, Guid userId, string? ip, DateTime now)
    {
        if (!ValidSecret(nonce)) return null;
        lock (codeGate)
        {
            var key = Challenge(nonce!);
            if (!handoffs.TryGetValue(key, out var h) || h.TakerId != userId || h.Ip != ip) return null;
            handoffs.TryRemove(key, out _);
            return now - h.CreatedAt <= CodeLifetime ? h : null;
        }
    }

    // ── status: the tab learns that the app is connected ────────────────────────────────────────────────

    /// The app, already holding the new session, confirms the request it redeemed. Only the account the code was
    /// minted for can confirm it: a cancelled account switch revokes that session and never confirms.
    public bool MarkConnected(string? requestId, Guid userId, string? name, DateTime now)
    {
        if (!ValidSecret(requestId)) return false;
        lock (codeGate)
        {
            if (!statuses.TryGetValue(requestId!, out var st) || st.UserId != userId || now - st.IssuedAt > StatusLifetime) return false;
            statuses[requestId!] = st with { ConnectedAt = now, Name = name };
            return true;
        }
    }

    /// Only the account that minted the code sees its status.
    public LinkStatus? Status(string? requestId, Guid userId, DateTime now) =>
        ValidSecret(requestId) && statuses.TryGetValue(requestId!, out var st) && st.UserId == userId && now - st.IssuedAt <= StatusLifetime ? st : null;

    // ── open iPop tabs (AppHub connections) by address, so an intent reaches them within a second ─────────

    public void TabConnected(string connectionId, string? ip)
    {
        if (ip is null || tabs.Count >= MaxEntries * 4) return;
        tabs[connectionId] = ip;
    }
    public void TabDisconnected(string connectionId) => tabs.TryRemove(connectionId, out _);
    public IReadOnlyList<string> TabsAt(string ip) => tabs.Where(t => t.Value == ip).Select(t => t.Key).ToList();

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
        foreach (var (k, i) in intents) if (now - i.CreatedAt > IntentLifetime) intents.TryRemove(k, out _);
        foreach (var (k, h) in handoffs) if (now - h.CreatedAt > CodeLifetime) handoffs.TryRemove(k, out _);
        foreach (var (k, s) in statuses) if (now - s.IssuedAt > StatusLifetime) statuses.TryRemove(k, out _);
    }
}
