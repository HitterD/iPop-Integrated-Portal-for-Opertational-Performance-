using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace IPop.Api.Notifier;

/// Web → Notifier sign-in (1.4.2): a connect request, approved by a click on a card in the browser. No protocol link
/// is launched from the web any more (Chrome/Edge silently block a script-launched ipop-notifier:// in a background
/// tab, which left 1.4.1 waiting forever).
///
///   1. "Sign in with browser" (or first run): the app keeps a PKCE verifier and registers a
///      <see cref="ConnectRequest"/> (its request id, challenge, PC name) for 2 minutes, from its client address.
///   2. Open iPop tabs on that address hear about it (AppHub) and exactly one <see cref="Claim"/>s it and navigates
///      itself to /notifier/connect?request=…. If none does within ~5 s the app <see cref="Poll"/>s with reserve and
///      opens exactly one tab on that page itself.
///   3. The signed-in person clicks Connect: <see cref="Approve"/> (same address, not expired, once) mints the
///      challenge-bound one-time code for that account; Cancel = <see cref="Deny"/>.
///   4. The app's poll (device key must match) collects the code once and redeems it with its verifier
///      (<see cref="Redeem"/>), then confirms (<see cref="MarkConnected"/>) so the card can say "Connected".
/// Same address is a discovery and anti-phishing check, never authorization by itself: authorization is the signed-in
/// person's explicit click plus the verifier only the app holds. All in memory: a restart only costs a new click.
public sealed class NotifierLinkStore
{
    public static readonly TimeSpan CodeLifetime = TimeSpan.FromSeconds(60);
    /// A connect request can be approved for this long after the app's click.
    public static readonly TimeSpan RequestLifetime = TimeSpan.FromMinutes(2);
    /// Expired requests are remembered a little longer, so the card can say "expired" instead of "not found".
    public static readonly TimeSpan RequestMemory = TimeSpan.FromMinutes(10);
    /// The card may learn "connected" for this long after its code was issued.
    public static readonly TimeSpan StatusLifetime = TimeSpan.FromMinutes(3);
    private const int MaxEntries = 5000;

    public static NotifierLinkStore Shared { get; } = new();

    // Codes are bound to an app-held PKCE verifier, never to an IP or a caller's claimed current account.
    public sealed record LinkCode(Guid UserId, string Challenge, string RequestId, DateTime CreatedAt);
    public enum State { Pending, Claimed, Reserved, Approved, Collected, Denied }
    public sealed record ConnectRequest(string Id, string DeviceKey, string Ip, string Challenge, string PcName, string Scheme,
        DateTime CreatedAt, State State, Guid? ApprovedBy = null, string? Code = null);
    public sealed record LinkStatus(Guid UserId, DateTime IssuedAt, DateTime? ConnectedAt, string? Name);
    public enum Decision { Ok, NotFound, Expired, OtherAddress, Used }
    public sealed record PollResult(string State, string? Code);

    private readonly ConcurrentDictionary<string, LinkCode> codes = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, (DateTime Window, int Count)> hits = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ConnectRequest> requests = new(StringComparer.Ordinal); // by request id
    private readonly ConcurrentDictionary<string, LinkStatus> statuses = new(StringComparer.Ordinal);     // by request id
    private readonly ConcurrentDictionary<string, string> tabs = new(StringComparer.Ordinal);             // hub connection → address
    private readonly object gate = new();

    public static bool ValidSecret(string? value) => value is { Length: 43 } && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
    public static bool ValidScheme(string? scheme) => scheme is "ipop-notifier" or "ipop-notifier-qa";
    public static string Challenge(string verifier) => Convert.ToBase64String(SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(verifier)))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static string NewSecret() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static bool SameSecret(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(System.Text.Encoding.ASCII.GetBytes(a), System.Text.Encoding.ASCII.GetBytes(b));

    /// What the card shows as the PC name: letters, digits, '-', '_' and '.', at most 40 (Windows names are 15).
    public static string CleanPcName(string? name)
    {
        var clean = string.Concat((name ?? "").Where(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.').Take(40));
        return clean.Length > 0 ? clean : "this PC";
    }

    public static bool IsLive(ConnectRequest r, DateTime now) => now - r.CreatedAt <= RequestLifetime;

    // ── 1. the app's click ───────────────────────────────────────────────────────────────────────────────

    /// "Sign in with browser" (or first run). Every click is a new request with a new id (the app's own secret);
    /// an id that is already known is refused, and the device's older open requests are dropped.
    public ConnectRequest? Register(string deviceKey, string ip, string? requestId, string? challenge, string? pcName, string? scheme, DateTime now)
    {
        if (!ValidSecret(requestId) || !ValidSecret(challenge) || !ValidScheme(scheme)) return null;
        lock (gate)
        {
            Sweep(now);
            if (requests.Count >= MaxEntries || requests.ContainsKey(requestId!)) return null;
            foreach (var (k, old) in requests)
                if (old.DeviceKey == deviceKey && old.State is State.Pending or State.Claimed or State.Reserved) requests.TryRemove(k, out _);
            var r = new ConnectRequest(requestId!, deviceKey, ip, challenge!, CleanPcName(pcName), scheme!, now, State.Pending);
            requests[r.Id] = r;
            return r;
        }
    }

    // ── 2. one open tab takes it, or the app opens one ───────────────────────────────────────────────────

    /// One tab wins the newest fresh unclaimed request on its address; every other tab stands down.
    public ConnectRequest? Claim(string? ip, DateTime now)
    {
        if (ip is null) return null;
        lock (gate)
        {
            var open = requests.Values.Where(r => r.Ip == ip && IsLive(r, now) && r.State == State.Pending)
                .OrderByDescending(r => r.CreatedAt).FirstOrDefault();
            if (open is null) return null;
            return requests[open.Id] = open with { State = State.Claimed };
        }
    }

    /// The app, about once a second while it waits. Only the device that registered the request may poll it.
    /// <paramref name="reserve"/> (after ~5 s with no tab): keep it for the one tab the app is about to open, so tabs
    /// hearing about it later no longer take it. The approved code is handed out exactly once.
    public PollResult? Poll(string deviceKey, string? requestId, bool reserve, DateTime now)
    {
        if (!ValidSecret(requestId)) return null;
        lock (gate)
        {
            if (!requests.TryGetValue(requestId!, out var r) || !SameSecret(r.DeviceKey, deviceKey)) return null;
            switch (r.State)
            {
                case State.Approved:
                    requests[r.Id] = r with { State = State.Collected, Code = null };
                    return new("approved", r.Code);
                case State.Collected: return new("collected", null);
                case State.Denied: return new("denied", null);
            }
            if (!IsLive(r, now)) return new("expired", null);
            if (reserve && r.State == State.Pending) requests[r.Id] = r = r with { State = State.Reserved };
            return new(r.State == State.Claimed ? "claimed" : "pending", null);
        }
    }

    // ── 3. the card in the browser ───────────────────────────────────────────────────────────────────────

    public ConnectRequest? Find(string? requestId) => ValidSecret(requestId) && requests.TryGetValue(requestId!, out var r) ? r : null;

    private static Decision Check(ConnectRequest? r, string? ip, DateTime now)
    {
        if (r is null) return Decision.NotFound;
        if (r.State is State.Approved or State.Collected or State.Denied) return Decision.Used;
        if (!IsLive(r, now)) return Decision.Expired;
        return ip is not null && r.Ip == ip ? Decision.Ok : Decision.OtherAddress;
    }

    /// The signed-in person clicked Connect: mint the code for their account and the app's challenge. Same address
    /// as the app, still fresh, once.
    public Decision Approve(string? requestId, Guid userId, string? ip, DateTime now)
    {
        lock (gate)
        {
            var r = Find(requestId);
            var d = Check(r, ip, now);
            if (d != Decision.Ok) return d;
            var code = IssueLocked(userId, r!.Challenge, r.Id, now);
            if (code is null) return Decision.Used;
            requests[r.Id] = r with { State = State.Approved, ApprovedBy = userId, Code = code };
            return Decision.Ok;
        }
    }

    /// Cancel on the card: the app's poll answers "denied" and its window goes back to normal.
    public Decision Deny(string? requestId, string? ip, DateTime now)
    {
        lock (gate)
        {
            var r = Find(requestId);
            var d = Check(r, ip, now);
            if (d != Decision.Ok) return d;
            requests[r!.Id] = r with { State = State.Denied };
            return Decision.Ok;
        }
    }

    // ── 4. code, redeem, connected ───────────────────────────────────────────────────────────────────────

    public string? Issue(Guid userId, string challenge, string requestId, DateTime now)
    {
        lock (gate) return IssueLocked(userId, challenge, requestId, now);
    }

    private string? IssueLocked(Guid userId, string challenge, string requestId, DateTime now)
    {
        if (!ValidSecret(challenge) || !ValidSecret(requestId)) return null;
        Sweep(now);
        if (codes.Count >= MaxEntries) return null;
        var code = NewSecret();
        codes[code] = new LinkCode(userId, challenge, requestId, now);
        // The first account to mint for a request id owns its status.
        if (!statuses.TryGetValue(requestId, out var st) || st.UserId == userId)
            statuses[requestId] = new LinkStatus(userId, now, null, null);
        return code;
    }

    /// Single use; an exchange must prove ownership of the pending app challenge.
    public LinkCode? Redeem(string? code, string? verifier, string? requestId, DateTime now)
    {
        if (!ValidSecret(code) || !ValidSecret(verifier) || !ValidSecret(requestId)) return null;
        lock (gate)
        {
            if (!codes.TryGetValue(code!, out var c)) return null;
            if (now - c.CreatedAt > CodeLifetime) { codes.TryRemove(code!, out _); return null; }
            if (c.RequestId != requestId || !SameSecret(c.Challenge, Challenge(verifier!))) return null;
            return codes.TryRemove(code!, out c) ? c : null;
        }
    }

    /// The app, already holding the new session, confirms the request it redeemed. Only the account the code was
    /// minted for can confirm it: a cancelled account switch revokes that session and never confirms.
    public bool MarkConnected(string? requestId, Guid userId, string? name, DateTime now)
    {
        if (!ValidSecret(requestId)) return false;
        lock (gate)
        {
            if (!statuses.TryGetValue(requestId!, out var st) || st.UserId != userId || now - st.IssuedAt > StatusLifetime) return false;
            statuses[requestId!] = st with { ConnectedAt = now, Name = name };
            return true;
        }
    }

    /// Only the account that approved (minted the code) sees its status.
    public LinkStatus? Status(string? requestId, Guid userId, DateTime now) =>
        ValidSecret(requestId) && statuses.TryGetValue(requestId!, out var st) && st.UserId == userId && now - st.IssuedAt <= StatusLifetime ? st : null;

    // ── open iPop tabs (AppHub connections) by address, so a click reaches them within a second ──────────

    public void TabConnected(string connectionId, string? ip)
    {
        if (ip is null || tabs.Count >= MaxEntries * 4) return;
        tabs[connectionId] = ip;
    }
    public void TabDisconnected(string connectionId) => tabs.TryRemove(connectionId, out _);
    public IReadOnlyList<string> TabsAt(string ip) => tabs.Where(t => t.Value == ip).Select(t => t.Key).ToList();

    /// At most <paramref name="perMinute"/> calls per key (client address or account, and route).
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
        foreach (var (k, h) in hits) if (now - h.Window >= TimeSpan.FromMinutes(1)) hits.TryRemove(k, out _);
        foreach (var (k, r) in requests) if (now - r.CreatedAt > RequestMemory) requests.TryRemove(k, out _);
        foreach (var (k, s) in statuses) if (now - s.IssuedAt > StatusLifetime) statuses.TryRemove(k, out _);
    }
}
