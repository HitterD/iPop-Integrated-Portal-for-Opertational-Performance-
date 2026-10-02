using Microsoft.JSInterop;
using IPop.Application.Chat.Mentions;

namespace IPop.Web.Services;

/// A draft version a send consumed. RemoveIfMatches only deletes the draft if it is still
/// exactly this version, so a late confirmation never wipes text typed after the send.
public readonly record struct DraftSnapshot(string Nik, Guid RoomId, long Version);

/// Chat composer drafts, per account and room, in localStorage (js/chat-drafts.js).
///
/// - Keys are scoped to Auth.Me.Nik; with no signed-in account nothing is read or written.
///   Legacy unscoped drafts ("ipop.chat.draft.*") can't be attributed to anyone, so they are
///   purged on first use and never imported.
/// - Stage is synchronous (WASM in-process interop): the JS side stashes the latest text and
///   writes it after a ~400 ms debounce, on Flush, and on pagehide / visibility hidden.
/// - Every Stage/Remove bumps a per-room version (CaptureSnapshot / RemoveIfMatches).
/// - Wire text is stored exactly as given (whitespace and mention tokens intact); nothing is
///   truncated. Only whitespace-only text counts as "no draft".
/// - Sign-out drops every draft and anything staged; awaits re-check the account afterwards.
public sealed class DraftStore : IDisposable
{
    public const int MaxDrafts = 50;
    /// Drafts bigger than this stay in memory only (localStorage is ~5 MB for the origin).
    public const int MaxPersistChars = 64 * 1024;
    public const string KeyPrefix = "ipop.chat.d2.";

    private readonly IJSRuntime _js;
    private readonly AuthState? _auth;
    /// (nik, room) → latest version and text staged this session (null text = removed).
    private readonly Dictionary<(string Nik, Guid Room), (long Version, string? Text)> _mem = [];
    private long _rev;
    private bool _legacyPurged;

    public DraftStore(IJSRuntime js) : this(js, null) { }

    public DraftStore(IJSRuntime js, AuthState? auth)
    {
        _js = js;
        _auth = auth;
        if (_auth is not null) _auth.SignedOut += OnSignedOut;
    }

    private string? Nik => _auth?.Me?.Nik is { Length: > 0 } nik ? nik : null;
    private static string Base(string nik) => $"{KeyPrefix}{nik}.";
    private static string Room(Guid roomId) => roomId.ToString("D");

    // ── synchronous API (parent lifecycle) ──

    /// Records the composer's latest wire text for a room and schedules the debounced write.
    /// Returns the new version, or 0 when there is no signed-in account.
    public long Stage(Guid roomId, string? wire)
    {
        if (Nik is not { } nik) return 0;
        PurgeLegacyOnce();
        var text = string.IsNullOrWhiteSpace(wire) ? null : wire;
        var v = ++_rev;
        _mem[(nik, roomId)] = (v, text);
        Call("ipopDrafts.stage", Base(nik), Room(roomId), text is null || text.Length > MaxPersistChars ? null : text);
        return v;
    }

    /// The draft version currently held for a room (0 = untouched this session).
    public DraftSnapshot CaptureSnapshot(Guid roomId)
    {
        var nik = Nik ?? "";
        return new(nik, roomId, nik.Length > 0 && _mem.TryGetValue((nik, roomId), out var e) ? e.Version : 0);
    }

    /// True when the room's draft is still exactly the snapshot's version, for the same account.
    public bool IsCurrent(DraftSnapshot s) => s.Nik.Length > 0 && Nik == s.Nik && CaptureSnapshot(s.RoomId).Version == s.Version;

    /// Removes the draft only if nothing was staged since the snapshot. Returns whether it did.
    public bool RemoveIfMatches(DraftSnapshot s)
    {
        if (!IsCurrent(s)) return false;
        RemoveNow(s.RoomId);
        return true;
    }

    /// Writes everything staged now (dispose, room switch, before navigation).
    public void Flush() => Call("ipopDrafts.flush");

    /// Latest draft for a room, synchronously: this session's stage first, then storage.
    public string? Peek(Guid roomId)
    {
        if (Nik is not { } nik) return null;
        if (_mem.TryGetValue((nik, roomId), out var e)) return e.Text;
        PurgeLegacyOnce();
        return _js is IJSInProcessRuntime sync ? Try(() => sync.Invoke<string?>("ipopDrafts.get", Base(nik), Room(roomId))) : null;
    }

    /// Drops the room's draft (stash and storage) and bumps its version.
    public void RemoveNow(Guid roomId)
    {
        var nik = Nik;
        if (nik is not null) _mem[(nik, roomId)] = (++_rev, null);
        Call("ipopDrafts.remove", nik is null ? null : Base(nik), Room(roomId));
    }

    // ── async API (existing callers) ──

    public async Task<string?> LoadAsync(Guid roomId)
    {
        if (Nik is not { } nik) return null;
        if (_mem.TryGetValue((nik, roomId), out var e)) return e.Text;
        PurgeLegacyOnce();
        string? text;
        try { text = await _js.InvokeAsync<string?>("ipopDrafts.get", Base(nik), Room(roomId)); }
        catch { return null; }
        // Signed out or switched account while storage answered: not this account's draft.
        if (Nik != nik) return null;
        // Staged while we waited: the newer text wins.
        if (_mem.TryGetValue((nik, roomId), out e)) return e.Text;
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    /// Stages and writes immediately. Whitespace-only content removes the draft.
    public Task SaveAsync(Guid roomId, string content)
    {
        Stage(roomId, content);
        Flush();
        return Task.CompletedTask;
    }

    public Task RemoveAsync(Guid roomId)
    {
        RemoveNow(roomId);
        return Task.CompletedTask;
    }

    /// Kept for callers/tests; the JS side prunes to MaxDrafts on every write.
    public Task PruneAsync() => Task.CompletedTask;

    /// Clips at a mention-token boundary. Not used for storage (drafts are never truncated);
    /// kept for callers that must fit a hard limit.
    public static string TrimToLimit(string content, int limit = 4000)
    {
        if (content.Length <= limit) return content;
        var cut = limit;
        var pos = 0;
        foreach (var seg in MentionParser.Parse(content))
        {
            var raw = seg is MentionSegment.Text t ? t.Value : MentionPrinter.PrintToken(seg);
            if (pos + raw.Length > cut)
            {
                if (seg is not MentionSegment.Text) cut = pos;
                break;
            }
            pos += raw.Length;
        }
        return content[..Math.Min(cut, content.Length)];
    }

    // ── internals ──

    private void OnSignedOut()
    {
        _mem.Clear();
        Call("ipopDrafts.clearAll");
    }

    private void PurgeLegacyOnce()
    {
        if (_legacyPurged) return;
        _legacyPurged = true;
        Call("ipopDrafts.purgeLegacy");
    }

    /// Sync in WASM (so pagehide/dispose ordering holds); fire-and-forget elsewhere.
    private void Call(string fn, params object?[] args)
    {
        try
        {
            if (_js is IJSInProcessRuntime sync) sync.InvokeVoid(fn, args);
            else _ = _js.InvokeVoidAsync(fn, args).AsTask().ContinueWith(t => _ = t.Exception, TaskScheduler.Default);
        }
        catch { /* storage blocked or script missing: the composer still has the text */ }
    }

    private static T? Try<T>(Func<T> f) { try { return f(); } catch { return default; } }

    public void Dispose()
    {
        if (_auth is not null) _auth.SignedOut -= OnSignedOut;
    }
}
