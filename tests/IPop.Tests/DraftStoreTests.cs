using System.Net.Http;
using IPop.Web.Services;
using Microsoft.JSInterop;
using Xunit;

namespace IPop.Tests;

/// DraftStore: account-scoped keys, version-safe removal, no truncation, sign-out wipe.
/// The JS side is faked as an in-process runtime holding the stash + "localStorage".
public class DraftStoreTests
{
    private sealed class FakeJs : IJSInProcessRuntime
    {
        public Dictionary<string, string> Storage { get; } = [];
        public List<string> Calls { get; } = [];
        /// Lets a test change state while an async read is "in flight".
        public Action? OnGet { get; set; }

        public TResult Invoke<TResult>(string identifier, params object?[]? args)
        {
            Calls.Add(identifier);
            switch (identifier)
            {
                case "ipopDrafts.stage":
                case "ipopDrafts.set":
                    var key = (string)args![0]! + (string)args[1]!;
                    if (args[2] is string t) Storage[key] = t; else Storage.Remove(key);
                    break;
                case "ipopDrafts.remove":
                    if (args![0] is string b) Storage.Remove(b + (string)args[1]!);
                    break;
                case "ipopDrafts.clearAll":
                    Storage.Clear();
                    break;
                case "ipopDrafts.purgeLegacy":
                    foreach (var k in Storage.Keys.Where(k => k.StartsWith("ipop.chat.draft")).ToList()) Storage.Remove(k);
                    break;
                case "ipopDrafts.get":
                    OnGet?.Invoke();
                    return (TResult)(object?)Storage.GetValueOrDefault((string)args![0]! + (string)args[1]!)!;
            }
            return default!;
        }

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => new(Invoke<TValue>(identifier, args));
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) => new(Invoke<TValue>(identifier, args));
    }

    private static (DraftStore Store, FakeJs Js, AuthState Auth) Rig(string? nik = "100")
    {
        var js = new FakeJs();
        var auth = new AuthState(new HttpClient { BaseAddress = new Uri("http://localhost/") }, js);
        if (nik is not null) SetMe(auth, nik);
        return (new DraftStore(js, auth), js, auth);
    }

    private static void SetMe(AuthState auth, string? nik)
    {
        var me = nik is null ? null : System.Text.Json.JsonSerializer.Deserialize<DirectoryEmployeeDto>($$"""{"nik":"{{nik}}","name":"Jane"}""",
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        typeof(AuthState).GetProperty(nameof(AuthState.Me))!.SetValue(auth, me);
    }

    private static void RaiseSignedOut(AuthState auth)
    {
        var f = typeof(AuthState).GetField("SignedOut", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        ((Action?)f.GetValue(auth))?.Invoke();
    }

    [Fact]
    public async Task Keys_are_scoped_to_the_signed_in_account()
    {
        var (store, js, auth) = Rig("100");
        var room = Guid.NewGuid();
        await store.SaveAsync(room, "hello");
        Assert.Equal("hello", js.Storage[$"ipop.chat.d2.100.{room:D}"]);

        SetMe(auth, "200");
        Assert.Null(await store.LoadAsync(room));
        Assert.Null(store.Peek(room));
    }

    [Fact]
    public async Task No_account_means_no_reads_or_writes()
    {
        var (store, js, _) = Rig(nik: null);
        var room = Guid.NewGuid();
        Assert.Equal(0, store.Stage(room, "x"));
        await store.SaveAsync(room, "x");
        Assert.Empty(js.Storage);
        Assert.Null(await store.LoadAsync(room));
    }

    [Fact]
    public async Task Legacy_unscoped_drafts_are_purged_never_imported()
    {
        var (store, js, _) = Rig("100");
        var room = Guid.NewGuid();
        js.Storage[$"ipop.chat.draft.{room:D}"] = "someone else's text";
        js.Storage["ipop.chat.drafts"] = "{}";
        Assert.Null(await store.LoadAsync(room));
        Assert.Empty(js.Storage);
    }

    [Fact]
    public async Task Wire_text_is_stored_verbatim_without_truncation()
    {
        var (store, js, _) = Rig();
        var room = Guid.NewGuid();
        var token = $"@[Budi](u:{Guid.NewGuid():D})";
        var wire = "  hi " + new string('a', 3995) + token + "\n ";
        await store.SaveAsync(room, wire);
        Assert.Equal(wire, await store.LoadAsync(room));
        Assert.Equal(wire, js.Storage[$"ipop.chat.d2.100.{room:D}"]);
    }

    [Fact]
    public async Task Whitespace_only_removes_the_draft()
    {
        var (store, js, _) = Rig();
        var room = Guid.NewGuid();
        await store.SaveAsync(room, "x");
        await store.SaveAsync(room, "  \n");
        Assert.Empty(js.Storage);
        Assert.Null(await store.LoadAsync(room));
    }

    [Fact]
    public void RemoveIfMatches_removes_only_the_captured_version()
    {
        var (store, js, _) = Rig();
        var room = Guid.NewGuid();
        store.Stage(room, "sent text");
        var snap = store.CaptureSnapshot(room);
        store.Stage(room, "typed after send");

        Assert.False(store.RemoveIfMatches(snap));
        Assert.Equal("typed after send", store.Peek(room));

        var now = store.CaptureSnapshot(room);
        Assert.True(store.RemoveIfMatches(now));
        Assert.Null(store.Peek(room));
        Assert.Empty(js.Storage);
        Assert.False(store.RemoveIfMatches(now)); // removal bumped the version too
    }

    [Fact]
    public void Snapshot_from_another_account_never_matches()
    {
        var (store, _, auth) = Rig("100");
        var room = Guid.NewGuid();
        store.Stage(room, "a");
        var snap = store.CaptureSnapshot(room);
        SetMe(auth, "200");
        Assert.False(store.RemoveIfMatches(snap));
    }

    [Fact]
    public async Task Sign_out_wipes_drafts_and_versions()
    {
        var (store, js, auth) = Rig();
        var room = Guid.NewGuid();
        await store.SaveAsync(room, "secret");
        var snap = store.CaptureSnapshot(room);
        RaiseSignedOut(auth);
        SetMe(auth, null);
        Assert.Empty(js.Storage);
        Assert.Contains("ipopDrafts.clearAll", js.Calls);

        SetMe(auth, "100");
        Assert.Null(await store.LoadAsync(room));
        Assert.False(store.RemoveIfMatches(snap));
    }

    [Fact]
    public async Task Load_rechecks_the_account_after_the_storage_read()
    {
        var (store, js, auth) = Rig("100");
        var room = Guid.NewGuid();
        js.Storage[$"ipop.chat.d2.100.{room:D}"] = "mine";
        js.OnGet = () => SetMe(auth, "200");
        Assert.Null(await store.LoadAsync(room));
    }

    [Fact]
    public async Task Staged_text_wins_over_storage()
    {
        var (store, js, _) = Rig();
        var room = Guid.NewGuid();
        js.Storage[$"ipop.chat.d2.100.{room:D}"] = "old";
        store.Stage(room, "new");
        Assert.Equal("new", await store.LoadAsync(room));
    }

    [Fact]
    public void Existing_callers_keep_working_without_auth()
    {
        // ChatRoomLoadingTests and others construct DraftStore(js) directly.
        var store = new DraftStore(new FakeJs());
        Assert.Null(store.Peek(Guid.NewGuid()));
        Assert.True(store.RemoveAsync(Guid.NewGuid()).IsCompletedSuccessfully);
    }

    // ── DraftSendLedger ──

    private static SendOutcome Outcome(Guid room, Guid key, SendState state, Guid? temp = null) =>
        new(temp ?? Guid.NewGuid(), room, key, state);

    [Fact]
    public void Confirmed_send_removes_its_draft_version()
    {
        var (store, _, _) = Rig();
        var ledger = new DraftSendLedger();
        var room = Guid.NewGuid(); var key = Guid.NewGuid();
        store.Stage(room, "hello");
        ledger.Started(room, key, store.CaptureSnapshot(room));
        Assert.True(ledger.HoldsDraft(room, store));

        Assert.Equal(DraftSettleResult.Removed, ledger.Settled(Outcome(room, key, SendState.Confirmed), store));
        Assert.Null(store.Peek(room));
        Assert.Equal(0, ledger.Count);
    }

    [Fact]
    public void Late_confirmation_keeps_text_typed_after_the_send()
    {
        var (store, _, _) = Rig();
        var ledger = new DraftSendLedger();
        var room = Guid.NewGuid(); var key = Guid.NewGuid();
        store.Stage(room, "hello");
        ledger.Started(room, key, store.CaptureSnapshot(room));
        store.Stage(room, "next message");
        Assert.False(ledger.HoldsDraft(room, store));

        Assert.Equal(DraftSettleResult.KeptNewer, ledger.Settled(Outcome(room, key, SendState.Confirmed), store));
        Assert.Equal("next message", store.Peek(room));
    }

    [Fact]
    public void Outcome_for_another_key_or_room_is_ignored()
    {
        var (store, _, _) = Rig();
        var ledger = new DraftSendLedger();
        var room = Guid.NewGuid(); var key = Guid.NewGuid();
        store.Stage(room, "hello");
        ledger.Started(room, key, store.CaptureSnapshot(room));

        Assert.Equal(DraftSettleResult.None, ledger.Settled(Outcome(room, Guid.NewGuid(), SendState.Confirmed), store));
        Assert.Equal(DraftSettleResult.None, ledger.Settled(Outcome(Guid.NewGuid(), key, SendState.Confirmed), store));
        Assert.Equal("hello", store.Peek(room));
    }

    [Fact]
    public void Failed_and_unknown_keep_the_draft_and_warn_once()
    {
        var (store, _, _) = Rig();
        var ledger = new DraftSendLedger();
        var room = Guid.NewGuid(); var key = Guid.NewGuid(); var temp = Guid.NewGuid();
        store.Stage(room, "hello");
        ledger.Started(room, key, store.CaptureSnapshot(room),
            [new IPop.Web.Models.PendingAttachmentItem(Guid.NewGuid(), "a.png", 1, "image/png", "blob:x")]);

        Assert.Equal(DraftSettleResult.KeptUnknown, ledger.Settled(Outcome(room, key, SendState.Unknown, temp), store));
        Assert.Equal(DraftSettleResult.None, ledger.Settled(Outcome(room, key, SendState.Unknown, temp), store));
        Assert.Equal(DraftSettleResult.KeptFailed, ledger.Settled(Outcome(room, key, SendState.Failed, temp), store));
        Assert.Equal("hello", store.Peek(room));
        Assert.True(ledger.HoldsDraft(room, store));

        var atts = ledger.Take(temp);
        Assert.Equal("blob:x", Assert.Single(atts!).PreviewUrl);
        Assert.Equal(0, ledger.Count);
    }

    [Fact]
    public void Retry_confirmed_after_unknown_still_removes()
    {
        var (store, _, _) = Rig();
        var ledger = new DraftSendLedger();
        var room = Guid.NewGuid(); var key = Guid.NewGuid();
        store.Stage(room, "hello");
        ledger.Started(room, key, store.CaptureSnapshot(room));
        ledger.Settled(Outcome(room, key, SendState.Unknown), store);
        Assert.Equal(DraftSettleResult.Removed, ledger.Settled(Outcome(room, key, SendState.Confirmed), store));
    }
}
