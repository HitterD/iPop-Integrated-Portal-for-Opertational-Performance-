using System.Net.Http.Json;
using IPop.Application.Chat;
using IPop.Application.Chat.Mentions;
using IPop.Application.Realtime;
using Microsoft.AspNetCore.SignalR.Client;

namespace IPop.Web.Services;

/// Real-time chat state for Blazor WASM. Manages the /hubs/chat SignalR connection,
/// room/message state, typing indicators, and exposes events for UI components.
/// Follows the same patterns as AuthState (HubConnectionBuilder, WithAutomaticReconnect, .On<T>).
public partial class ChatState : IAsyncDisposable
{
    private readonly AuthState _auth;
    private readonly HttpClient _http;
    private readonly DraftStore? _drafts;
    private readonly Microsoft.JSInterop.IJSRuntime? _js;
    private HubConnection? _hub;
    private DateTime _lastTypingBroadcast = DateTime.MinValue;

    public ChatState(AuthState auth, HttpClient http, DraftStore? drafts = null, Microsoft.JSInterop.IJSRuntime? js = null)
    {
        _auth = auth;
        _http = http;
        _drafts = drafts;
        _js = js;
        HookOutbox();
    }

    // ---- Custom stickers ----

    public StickerListDto Stickers { get; private set; } = new([], [], [], []);
    private bool _stickersLoaded;
    public event Action? StickersChanged;

    /// Anonymous, immutable image URL (an <img> cannot carry the bearer token).
    public string StickerImageUrl(Guid id) => new Uri(_http.BaseAddress!, $"api/chat/stickers/{id}/image").ToString();

    public bool IsStickerMine(Guid id) => Stickers.Mine.Any(s => s.Id == id);

    /// Last picker tab, kept for the session so the sheet reopens where the user left it.
    public string? StickerTab { get; set; }

    public bool IsStickerFavorite(string key) => Stickers.Favorites.Contains(key);

    /// Optimistic star / un-star; rolls back when the server refuses.
    public async Task<bool> SetStickerFavoriteAsync(string key, bool favorite)
    {
        var before = Stickers;
        var favs = Stickers.Favorites.Where(k => k != key).ToList();
        if (favorite) favs.Insert(0, key);
        Stickers = Stickers with { Favorites = favs };
        StickersChanged?.Invoke();
        try
        {
            using var res = await _auth.SendAsync(HttpMethod.Put, "/api/chat/stickers/favorites", new { key, favorite });
            if (res.IsSuccessStatusCode) return true;
        }
        catch { /* fall through to the rollback */ }
        Stickers = before;
        StickersChanged?.Invoke();
        return false;
    }

    /// The server records use on send; mirror it locally so Recent is right without a reload.
    public void NoteStickerUsed(string key)
    {
        var recent = Stickers.Recent.Where(k => k != key).Prepend(key).Take(StickerLimits.MaxRecent).ToList();
        Stickers = Stickers with { Recent = recent };
        StickersChanged?.Invoke();
    }

    public async Task LoadStickersAsync(bool force = false)
    {
        if (_stickersLoaded && !force) return;
        try
        {
            if (await _auth.GetAsync<StickerListDto>("/api/chat/stickers") is { } list)
            {
                Stickers = list;
                _stickersLoaded = true;
                StickersChanged?.Invoke();
            }
        }
        catch { /* the picker shows the built-in pack only */ }
    }

    /// Returns an error code, or null on success.
    public async Task<string?> StickerActionAsync(Func<Task<HttpResponseMessage>> call)
    {
        try
        {
            using var res = await call();
            if (!res.IsSuccessStatusCode)
            {
                try { return (await res.Content.ReadFromJsonAsync<ErrorCode>())?.Code ?? "failed"; }
                catch { return "failed"; }
            }
            await LoadStickersAsync(force: true);
            return null;
        }
        catch { return "failed"; }
    }

    private sealed record ErrorCode(string? Code);

    // ---- Connection State ----

    public HubConnection? Hub => _hub;
    public bool IsConnected => _hub?.State == HubConnectionState.Connected;
    public string ConnectionStatus => _hub?.State.ToString() ?? "Disconnected";

    // ---- Data State ----

    public List<RoomListDto> DirectRooms { get; private set; } = [];
    public List<RoomListDto> GroupRooms { get; private set; } = [];
    public Guid? ActiveRoomId { get; private set; }
    public ChatRoomDto? ActiveRoom { get; private set; }
    public List<MessageDto> ActiveMessages { get; private set; } = [];

    /// Set while practising: sends stay in the sample room and the sample person answers;
    /// real incoming messages are only counted (PracticeHeld) until practice ends.
    public Func<Guid, string, (MessageDto Mine, Func<MessageDto> Reply)?>? PracticeSend { get; set; }
    public event Action? PracticeHeld;
    public int MessagePage { get; private set; } = 1;
    public bool HasOlderMessages { get; private set; }
    /// True while a room is opening with nothing cached to show yet (drives the skeleton).
    public bool IsLoadingMessages { get; private set; }
    /// False until the first room-list fetch has answered (drives the room-list skeleton).
    public bool RoomsLoaded { get; private set; }

    /// Last-seen state of recently opened rooms, so switching back paints at once and
    /// page 1 refreshes silently behind it. Only the newest page is kept per room.
    private sealed record RoomSnapshot(ChatRoomDto? Room, List<MessageDto> Messages, bool HasOlder);
    private readonly Dictionary<Guid, RoomSnapshot> _roomCache = [];
    private readonly LinkedList<Guid> _roomCacheOrder = new();
    private const int RoomCacheLimit = 30;

    private void SnapshotActiveRoom()
    {
        if (ActiveRoomId is not { } id || ActiveMessages.Count == 0 || PracticeSend is not null) return;
        // Optimistic bubbles belong to the outbox, not the server's history.
        var msgs = ActiveMessages.Where(m => !_outbox.ContainsKey(m.Id)).TakeLast(50).ToList();
        if (msgs.Count == 0) return;
        _roomCache[id] = new RoomSnapshot(ActiveRoom, msgs, HasOlderMessages || ActiveMessages.Count > msgs.Count);
        _roomCacheOrder.Remove(id);
        _roomCacheOrder.AddFirst(id);
        while (_roomCacheOrder.Count > RoomCacheLimit)
        {
            _roomCache.Remove(_roomCacheOrder.Last!.Value);
            _roomCacheOrder.RemoveLast();
        }
    }

    /// Messages that arrived (or were sent) while the room was open. Only these play the
    /// entrance animation; history painted on open must not cascade in row by row.
    private readonly HashSet<Guid> _fresh = [];
    public bool IsFresh(Guid messageId) => _fresh.Contains(messageId);

    private void ForgetRoom(Guid roomId)
    {
        _roomCache.Remove(roomId);
        _roomCacheOrder.Remove(roomId);
    }
    public int TotalUnread => DirectRooms.Sum(r => r.UnreadCount) + GroupRooms.Sum(r => r.UnreadCount);
    public HashSet<string> TypingUsers { get; } = [];

    // ---- Events (subscribed by Blazor components) ----

    public event Action? StateChanged;
    public event Action? RoomListChanged;
    public event Action<MessageDto>? MessageReceived;
    public event Action<MessageDto>? MessageEdited;
    public event Action<Guid, string>? MessageDeleted;
    public event Action? TypingChanged;
    public event Action? ConnectionChanged;
    public event Action<SeenUpdateDto>? SeenUpdated;
    public event Action<ReactionDto>? ReactionChanged;
    public event Action? MembersChanged;
    public event Action? PinnedChanged;
    public event Action<MessageDto>? ThreadReplyReceived;
    /// Someone else's message landed in a room that isn't on screen — the shell turns it into a banner.
    public event Action<IncomingChatMessage>? IncomingMessage;
    public event Action<Guid>? RoomOpened;
    private readonly Dictionary<Guid, bool> _mentionFlags = [];
    public bool HasUnreadMention(Guid roomId) => _mentionFlags.TryGetValue(roomId, out var v) && v;
    public void ClearMentionFlag(Guid roomId) { _mentionFlags[roomId] = false; StateChanged?.Invoke(); }
    public List<RoomListDto> ArchivedRooms { get; private set; } = [];
    // ipop-47: the server's TotalCount, not the loaded page size — ArchivedCount used to
    // saturate at 50 and silently lie to the user (rooms past the first page unreachable).
    public int ArchivedTotal { get; private set; }
    public int ArchivedCount => ArchivedTotal;
    // ipop-47b: archived rooms past the first page are unreachable by ANY other path —
    // search and the main list both exclude archived ids, and auto-unarchive needs a
    // third party to post first. HasMoreArchived drives the load-more row.
    private int _archivedPage = 1;
    public bool HasMoreArchived => ArchivedRooms.Count < ArchivedTotal;
    public async Task LoadArchivedRoomsAsync(int page = 1)
    {
        var r = await _auth.GetAsync<PagedResult<RoomListDto>>($"/api/chat/rooms/archived?page={page}&pageSize=50");
        var items = r?.Items.ToList() ?? [];
        ArchivedRooms = page == 1 ? items : MergeArchived(items);
        ArchivedTotal = r?.TotalCount ?? 0;
        _archivedPage = page;
        StateChanged?.Invoke();
    }
    private List<RoomListDto> MergeArchived(List<RoomListDto> incoming)
    {
        // A room can be archived by someone else between pages; skip ids already shown.
        var fresh = incoming.Where(i => ArchivedRooms.All(a => a.Id != i.Id)).ToList();
        var merged = new List<RoomListDto>(ArchivedRooms);
        merged.AddRange(fresh);
        return merged;
    }
    public Task LoadMoreArchivedAsync() => LoadArchivedRoomsAsync(_archivedPage + 1);

    private bool _presenceSubscribed;
    private IDisposable? _roomsChangedSub;
    private string _roomSearch = "";
    private CancellationTokenSource? _roomsReloadCts;
    private bool _connecting;
    private CancellationTokenSource? _openCts;
    public bool IsOpening => _openCts is not null;

    // ---- Connect ----

    public async Task ConnectAsync()
    {
        if (_hub is not null || _connecting) return;
        if (!_auth.IsAuthenticated || _auth.IsSuspended) return;
        _connecting = true;

        // Subscribe to presence changes so room-list presence dots update in real-time
        if (!_presenceSubscribed)
        {
            _auth.PresenceChanged += OnPresenceChanged;
            _auth.HubReconnected += OnAppHubReconnected;
            _presenceSubscribed = true;
        }
        // Per-user room state (mute/archive/pin/read) changed in another of this user's tabs.
        _roomsChangedSub ??= _auth.On(RealtimeEvents.ChatRoomsChanged, ScheduleRoomsReload);

        var baseUrl = _http.BaseAddress!;
        var hubUrl = new Uri(baseUrl, "hubs/chat");

        _hub = new HubConnectionBuilder()
            .WithUrl(hubUrl, o => o.AccessTokenProvider = () => Task.FromResult(_auth.AccessToken))
            .WithAutomaticReconnect([TimeSpan.Zero, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30)])
            .Build();

        // Subscribe to server-to-client events
        _hub.On<MessageDto>("newMessage", OnNewMessage);
        _hub.On<MessageDto>("messageEdited", OnMessageEdited);
        _hub.On<DeletedDto>("messageDeleted", OnMessageDeleted);
        _hub.On<MessageAckDto>("messageAck", OnMessageAck);
        _hub.On<TypingDto>("typingStarted", dto => OnTyping(dto, true));
        _hub.On<TypingDto>("typingStopped", dto => OnTyping(dto, false));
        _hub.On<SeenUpdateDto>("seenUpdate", OnSeenUpdate);
        _hub.On<ReactionDto>("reactionChanged", OnReactionChanged);
        _hub.On<MemberChangeDto>("memberChanged", mc => { _ = SafeAsync(async () => { await LoadActiveRoomAsync(); await LoadRoomsAsync(); MembersChanged?.Invoke(); }); });
        _hub.On<PinnedChangeDto>("pinnedChanged", ApplyPinnedChange);
        _hub.On<Guid>("deliveryUpdate", OnDeliveryUpdate);
        _hub.On<RoomUpdateDto>("roomUpdated", ru => { _ = SafeAsync(async () => { await LoadRoomsAsync(); await LoadActiveRoomAsync(); }); });
        _hub.On<MessageDto>("threadReply", OnThreadReply);
        _hub.On<MessageDto>("emergencyBroadcast", OnNewMessage);
        _hub.On<Guid>("addedToRoom", roomId => _ = SafeAsync(async () =>
        {
            // Hub groups are joined on connect; a room created/joined mid-session needs this
            // explicit subscribe or its messages never arrive until the next reconnect.
            await _hub!.InvokeAsync("JoinRoom", roomId);
            await LoadRoomsAsync();
        }));
        _hub.On<Guid>("removedFromRoom",roomId => _ = SafeAsync(() => OnRemovedFromRoomAsync(roomId)));

        _hub.Reconnecting += ex => { ConnectionChanged?.Invoke(); return Task.CompletedTask; };
        _hub.Reconnected += cid => { ConnectionChanged?.Invoke(); _ = RefreshOnReconnectAsync(); return Task.CompletedTask; };
        var hub = _hub;
        _hub.Closed += ex =>
        {
            ConnectionChanged?.Invoke();
            // Auto-reconnect gave up: keep retrying so chat never dies until a page refresh.
            _ = HubRetry.RunAsync(hub, () => _hub == hub && _auth.IsAuthenticated && !_auth.IsSuspended,
                () => { ConnectionChanged?.Invoke(); _ = SafeAsync(RefreshOnReconnectAsync); });
            return Task.CompletedTask;
        };

        try
        {
            await _hub.StartAsync();
            ConnectionChanged?.Invoke();
            await LoadRoomsAsync();
            // Messages a previous tab/session never got confirmed: back into the outbox, then resent.
            await RestoreOutboxAsync();
            await DrainOfflineQueueAsync();
            // A room opened before the hub was up (e.g. /chat/{room} straight from a push
            // notification) had its MarkRead dropped; mark it now so the sender sees "read".
            await MarkActiveRoomReadAsync();
        }
        catch
        {
            await DisposeHubAsync();
        }
        finally { _connecting = false; }
    }

    // ---- Room Operations ----

    public async Task LoadRoomsAsync()
    {
        var gen = ++_roomsGen;
        var directTask = _auth.GetResultAsync<PagedResult<RoomListDto>>("/api/chat/rooms?tab=direct&pageSize=50");
        var groupTask = _auth.GetResultAsync<PagedResult<RoomListDto>>("/api/chat/rooms?tab=groups&pageSize=50");
        await Task.WhenAll(directTask, groupTask);
        if (gen != _roomsGen) return; // a newer load/search owns the lists now
        var direct = directTask.Result;
        var group = groupTask.Result;
        // A failed side keeps what is already on screen: an error must never read as
        // "No conversations yet". A real empty list still comes back Ok with Items = [].
        if (direct is { IsOk: true, Value: { } d })
        {
            DirectRooms = d.Items.ToList();
            _directTotal = d.TotalCount;
            _directPage = 1;
        }
        if (group is { IsOk: true, Value: { } g })
        {
            GroupRooms = g.Items.ToList();
            _groupTotal = g.TotalCount;
            _groupPage = 1;
        }
        foreach (var r in DirectRooms.Concat(GroupRooms).Where(r => r.HasUnreadMention)) _mentionFlags[r.Id] = true;
        RoomsError = direct.IsOk && group.IsOk ? ChatLoadError.None : ChatLoadError.Unavailable;
        // Only a complete answer ends the skeleton; a failure shows RoomsError instead.
        if (RoomsError == ChatLoadError.None) RoomsLoaded = true;
        RoomListChanged?.Invoke();
        StateChanged?.Invoke();
    }

    // ipop-52: main room list was hard-truncated at 50/page with TotalCount discarded —
    // and the sidebar's client-side search could only ever see that window, so a real
    // conversation at position 51+ returned "nothing" from search. Expose paging totals
    // (convention matches HasMoreArchived/HasMoreThreadReplies) and revive the server's
    // /rooms/search endpoint, which was fully built with zero callers.
    private int _directTotal, _groupTotal;
    private int _directPage = 1, _groupPage = 1;
    public bool HasMoreDirectRooms => DirectRooms.Count < _directTotal;
    public bool HasMoreGroupRooms => GroupRooms.Count < _groupTotal;

    public async Task LoadMoreDirectRoomsAsync()
    {
        var gen = _roomsGen;
        var next = _directPage + 1;
        var res = await _auth.GetResultAsync<PagedResult<RoomListDto>>($"/api/chat/rooms?tab=direct&page={next}&pageSize=50");
        if (gen != _roomsGen) return; // the list was reloaded/searched meanwhile
        if (res is not { IsOk: true, Value: { } r })
        {
            // Page counter not advanced: the next attempt asks for the same page.
            RoomsError = ChatLoadError.Unavailable;
            StateChanged?.Invoke();
            return;
        }
        RoomsError = ChatLoadError.None;
        _directTotal = r.TotalCount;
        _directPage = next;
        var fresh = r.Items.Where(i => DirectRooms.All(d => d.Id != i.Id));
        DirectRooms.AddRange(fresh);
        RoomListChanged?.Invoke();
        StateChanged?.Invoke();
    }

    public async Task LoadMoreGroupRoomsAsync()
    {
        var gen = _roomsGen;
        var next = _groupPage + 1;
        var res = await _auth.GetResultAsync<PagedResult<RoomListDto>>($"/api/chat/rooms?tab=groups&page={next}&pageSize=50");
        if (gen != _roomsGen) return; // the list was reloaded/searched meanwhile
        if (res is not { IsOk: true, Value: { } r })
        {
            // Page counter not advanced: the next attempt asks for the same page.
            RoomsError = ChatLoadError.Unavailable;
            StateChanged?.Invoke();
            return;
        }
        RoomsError = ChatLoadError.None;
        _groupTotal = r.TotalCount;
        _groupPage = next;
        var fresh = r.Items.Where(i => GroupRooms.All(d => d.Id != i.Id));
        GroupRooms.AddRange(fresh);
        RoomListChanged?.Invoke();
        StateChanged?.Invoke();
    }

    /// ipop-52: server-backed search over ALL the user's rooms (name OR member name),
    /// replacing the client-side filter that could only see the loaded 50. Populates
    /// DirectRooms/GroupRooms from the one flat result so FilteredRooms keeps working
    /// unchanged while a query is active; empty query restores the normal lists.
    public async Task SearchRoomsAsync(string query)
    {
        _roomSearch = query;
        if (string.IsNullOrWhiteSpace(query) || query.Trim().Length < 2)
        {
            await LoadRoomsAsync();
            return;
        }
        var gen = ++_roomsGen;
        var res = await _auth.GetResultAsync<PagedResult<RoomListDto>>($"/api/chat/rooms/search?q={Uri.EscapeDataString(query.Trim())}");
        if (gen != _roomsGen) return; // superseded by a newer query
        if (res is not { IsOk: true, Value: { } r })
        {
            // Keep the previous results rather than a false "No conversations match".
            RoomsError = ChatLoadError.Unavailable;
            StateChanged?.Invoke();
            return;
        }
        RoomsError = ChatLoadError.None;
        var flat = r.Items.ToList();
        DirectRooms = flat.Where(x => x.Type == "dm").ToList();
        GroupRooms = flat.Where(x => x.Type == "group").ToList();
        foreach (var room in flat.Where(room => room.HasUnreadMention)) _mentionFlags[room.Id] = true;
        RoomListChanged?.Invoke();
        StateChanged?.Invoke();
    }

    /// The room an OpenRoomAsync call is currently working on. Guards against re-entry from
    /// render-driven lifecycle hooks: ActiveRoomId is shared mutable state that hub events can
    /// change mid-flight, so it alone is not a safe "already open" test.
    private Guid? _openingRoomId;

    public async Task OpenRoomAsync(Guid roomId)
    {
        if (roomId == ActiveRoomId || roomId == _openingRoomId) return;
        _openingRoomId = roomId;
        _openCts?.Cancel();
        _openCts?.Dispose();
        var cts = new CancellationTokenSource();
        _openCts = cts;

        SnapshotActiveRoom();
        _fresh.Clear();
        ActiveRoomId = roomId;
        ResetRoomLoadState();
        var gen = _roomGen;
        RoomOpened?.Invoke(roomId);
        MessagePage = 1;
        TypingUsers.Clear();
        if (_roomCache.TryGetValue(roomId, out var snap))
        {
            // Paint what we saw last time; the fetches below replace it silently. Unconfirmed
            // sends for this room come back with it.
            ActiveRoom = snap.Room;
            ActiveMessages = ReconcileOptimisticHistory(roomId, snap.Messages);
            HasOlderMessages = snap.HasOlder;
            IsLoadingMessages = false;
        }
        else
        {
            ActiveRoom = null;
            ActiveMessages = ReconcileOptimisticHistory(roomId, []);
            HasOlderMessages = false;
            IsLoadingMessages = true;
        }
        StateChanged?.Invoke();

        await LoadOpenedRoomAsync(roomId, cts, gen);
    }

    /// Room details + page 1 for a room that was just opened (or is being retried), then the
    /// hub join. Shared by OpenRoomAsync and RetryActiveRoomAsync; `gen` is the room-load
    /// generation the attempt belongs to.
    private async Task LoadOpenedRoomAsync(Guid roomId, CancellationTokenSource cts, int gen)
    {
        var ct = cts.Token;
        try
        {
            // Deliberately do NOT LeaveRoom(prevRoom): the hub only auto-joins every room
            // once, on connect. Leaving a room on switch permanently stopped its newMessage
            // events, so unread badges and previews for background rooms went stale.
            // Room details and page 1 are independent: fetch them side by side.
            await Task.WhenAll(LoadActiveRoomCoreAsync(roomId, gen, ct), LoadMessagesCoreAsync(roomId, 1, gen, ct));
            if (ct.IsCancellationRequested || !IsCurrent(roomId, gen) || RoomError != ChatLoadError.None) return;
            if (ActiveRoom is { Type: "group" }) _ = RefreshGroupReadCountsAsync(roomId, immediate: true);
            if (_hub?.State == HubConnectionState.Connected)
            {
                await _hub.InvokeAsync("JoinRoom", roomId);
                var incomingToConfirm = ActiveMessages
                    .Where(m => !string.Equals(m.SenderNik, _auth.Me?.Nik, StringComparison.OrdinalIgnoreCase))
                    .TakeLast(20);
                foreach (var inc in incomingToConfirm)
                {
                    _ = _hub.InvokeAsync("DeliveryConfirm", inc.Id);
                }
            }
        }
        catch (OperationCanceledException) { return; }
        finally
        {
            if (_openCts == cts) { _openCts.Dispose(); _openCts = null; }
            else cts.Dispose();
            if (_openingRoomId == roomId) _openingRoomId = null;
            // A newer attempt (switch or retry) owns the loading flag now.
            if (IsCurrent(roomId, gen)) IsLoadingMessages = false;
            StateChanged?.Invoke();
        }
    }

    public Task LoadActiveRoomAsync(CancellationToken ct = default)
        => ActiveRoomId is { } roomId ? LoadActiveRoomCoreAsync(roomId, _roomGen, ct) : Task.CompletedTask;

    private async Task LoadActiveRoomCoreAsync(Guid roomId, int gen, CancellationToken ct)
    {
        // ipop-44: read into a local, THEN guard, THEN write — matching LoadMessagesAsync.
        // Assigning inside the await expression let a superseded hub handler
        // (memberChanged/roomUpdated fire without a token) leave a stale room's DTO
        // in ActiveRoom after the user switched. Cosmetic-only: Pin/Emergency buttons
        // may flicker for the wrong room, but both mutations read ActiveRoomId live and
        // the server re-checks role — so no wrong-target write is possible.
        GetResult<ChatRoomDto> res;
        try { res = await _auth.GetResultAsync<ChatRoomDto>($"/api/chat/rooms/{roomId}", ct); }
        catch (OperationCanceledException) { return; }
        if (ct.IsCancellationRequested || !IsCurrent(roomId, gen)) return;
        // 404 is how this endpoint says "deleted or not a member" (the messages endpoint
        // answers an empty page instead), so this is the membership check that matters.
        if (res.IsGone) { HandleRoomGone(roomId, definitive: true); return; }
        if (res is not { IsOk: true, Value: { } room })
        {
            // Cached details (if any) stay on screen; the pane shows a refresh banner, or
            // the error state instead of a skeleton when there are no details at all.
            SetPartFailed(details: true, failed: true);
            StateChanged?.Invoke();
            return;
        }
        ActiveRoom = room;
        SetPartFailed(details: true, failed: false);
        StateChanged?.Invoke();
    }

    public Task LoadMessagesAsync(int page, CancellationToken ct = default)
        => ActiveRoomId is { } roomId ? LoadMessagesCoreAsync(roomId, page, _roomGen, ct) : Task.CompletedTask;

    private async Task LoadMessagesCoreAsync(Guid roomId, int page, int gen, CancellationToken ct)
    {
        GetResult<PagedResult<MessageDto>> res;
        try { res = await _auth.GetResultAsync<PagedResult<MessageDto>>(MessagesUrl(roomId, page), ct); }
        catch (OperationCanceledException) { return; }
        if (ct.IsCancellationRequested || !IsCurrent(roomId, gen)) return;
        if (res.IsGone) { HandleRoomGone(roomId, definitive: false); return; }
        if (res is not { IsOk: true, Value: { Items: not null } result })
        {
            // History already on screen stays; MessagePage/HasOlder are untouched.
            SetPartFailed(details: false, failed: true);
            IsLoadingMessages = false;
            StateChanged?.Invoke();
            return;
        }
        if (page == 1)
        {
            // A fresh page 1 is authoritative for server rows; unconfirmed sends for this
            // room are carried over by the outbox, never silently dropped.
            ActiveMessages = ReconcileOptimisticHistory(roomId, result.Items);
        }
        else
        {
            // Offset pages shift as messages arrive; never let a re-served row duplicate an @key.
            var known = ActiveMessages.Select(m => m.Id).ToHashSet();
            ActiveMessages.InsertRange(0, result.Items.Where(m => known.Add(m.Id)));
        }
        MessagePage = page;
        HasOlderMessages = page * result.PageSize < result.TotalCount;
        IsLoadingMessages = false;
        SetPartFailed(details: false, failed: false);
        StateChanged?.Invoke();
        // Group bubbles show reader avatars; the page payload only carries counts.
        // (OpenRoomAsync fetches the room alongside page 1 and refreshes once both land.)
        if (ActiveRoom is { Type: "group" } && _openingRoomId != roomId) _ = RefreshGroupReadCountsAsync(roomId, immediate: true);
    }

    /// Single-flight: a double click (or JumpToMessage looping while a click is in flight)
    /// used to fetch the same page twice, and the duplicate ids crashed the renderer on
    /// @key. Callers awaiting while a load runs share that load instead of starting another.
    private Task? _olderLoad;
    private Guid? _olderLoadRoomId;

    /// After a failure this stays quiet (no automatic re-fetch on every scroll to the top)
    /// until RetryOlderMessagesAsync clears OlderError.
    public Task LoadOlderMessagesAsync()
    {
        if (!HasOlderMessages || OlderError != ChatLoadError.None) return Task.CompletedTask;
        if (_olderLoad is { IsCompleted: false } && _olderLoadRoomId == ActiveRoomId) return _olderLoad;
        _olderLoadRoomId = ActiveRoomId;
        return _olderLoad = LoadOlderCoreAsync();
    }

    /// Keyset paging (?before=<oldest CreatedAt>): new messages arriving while you read no
    /// longer shift the offset window, which used to re-serve rows already on screen.
    private async Task LoadOlderCoreAsync()
    {
        // Optimistic bubbles are always newest, so the oldest loaded row is a server row.
        var oldest = ActiveMessages.FirstOrDefault();
        if (ActiveRoomId is not { } roomId || oldest is null) return;
        var gen = _roomGen;
        var ct = _openCts?.Token ?? CancellationToken.None;
        // Server timestamps are UTC; an offset-less value must not be shifted by the local zone.
        var cursor = oldest.CreatedAt.Kind == DateTimeKind.Local
            ? oldest.CreatedAt.ToUniversalTime()
            : DateTime.SpecifyKind(oldest.CreatedAt, DateTimeKind.Utc);
        var before = Uri.EscapeDataString(cursor.ToString("O"));
        GetResult<PagedResult<MessageDto>> res;
        try { res = await _auth.GetResultAsync<PagedResult<MessageDto>>($"/api/chat/rooms/{roomId}/messages?before={before}&pageSize=50", ct); }
        catch (OperationCanceledException) { return; }
        if (ct.IsCancellationRequested || !IsCurrent(roomId, gen)) return;
        if (res.IsGone) { HandleRoomGone(roomId, definitive: false); return; }
        if (res is not { IsOk: true, Value: { Items: not null } result })
        {
            // Cursor, page and HasOlder stay as they were: Retry asks for the same page.
            OlderError = ChatLoadError.Unavailable;
            StateChanged?.Invoke();
            return;
        }
        // The list was replaced meanwhile (room re-opened, reconnect reloaded page 1):
        // this page no longer lines up with what's on screen.
        if (ActiveMessages.Count == 0 || ActiveMessages[0].Id != oldest.Id) return;
        // De-dup by id anyway: a message can land via the hub between the two requests,
        // and a duplicate @key takes the whole message list down.
        var known = ActiveMessages.Select(m => m.Id).ToHashSet();
        var fresh = result.Items.Where(m => known.Add(m.Id)).ToList();
        ActiveMessages.InsertRange(0, fresh);
        MessagePage++;
        // The cursor is inclusive, so a page that only re-served what's on screen means the start.
        HasOlderMessages = fresh.Count > 0 && result.Items.Count < result.TotalCount;
        StateChanged?.Invoke();
        if (ActiveRoom is { Type: "group" }) _ = RefreshGroupReadCountsAsync(roomId, immediate: true);
    }

    public async Task JoinRoomAsync(Guid roomId)
    {
        if (_hub?.State == HubConnectionState.Connected)
            await _hub.InvokeAsync("JoinRoom", roomId);
    }

    public async Task LeaveRoomAsync(Guid roomId)
    {
        if (_hub?.State == HubConnectionState.Connected)
            await _hub.InvokeAsync("LeaveRoom", roomId);
    }

    /// Dismisses the open conversation without leaving its hub group, so unread badges and
    /// previews keep streaming for it in the background. Used by Escape-to-dismiss; the
    /// page then returns to the conversation list.
    public void CloseActiveRoom()
    {
        if (ActiveRoomId is null && ActiveRoom is null) return;
        SnapshotActiveRoom();
        ActiveRoomId = null;
        ResetRoomLoadState();
        IsLoadingMessages = false;
        ActiveRoom = null;
        ActiveMessages = [];
        MessagePage = 1;
        HasOlderMessages = false;
        _openingRoomId = null;
        // An in-flight open must not paint the room we just dismissed. Its finally block
        // still owns the CTS disposal.
        _openCts?.Cancel();
        foreach (var cts in _typingCts.Values) { cts.Cancel(); cts.Dispose(); }
        _typingCts.Clear();
        TypingUsers.Clear();
        StateChanged?.Invoke();
    }

    // ---- Typing ----

    public async Task SendTypingAsync()
    {
        if (_hub?.State != HubConnectionState.Connected || ActiveRoomId is null) return;

        // Debounce: max 1 event per 3 seconds
        if ((DateTime.UtcNow - _lastTypingBroadcast).TotalSeconds < 3) return;
        _lastTypingBroadcast = DateTime.UtcNow;

        await _hub.InvokeAsync("StartTyping", ActiveRoomId.Value);
    }

    public async Task StopTypingAsync()
    {
        if (_hub?.State != HubConnectionState.Connected || ActiveRoomId is null) return;
        _lastTypingBroadcast = DateTime.MinValue;
        await _hub.InvokeAsync("StopTyping", ActiveRoomId.Value);
    }

    // ---- Mark Read ----

    // Every MarkRead makes the server broadcast seenUpdate to the whole room, which raises
    // StateChanged and re-renders the page. Re-marking the same message therefore feeds a
    // render -> MarkRead -> broadcast -> render loop, so each message is only ever marked once.
    private readonly HashSet<Guid> _markedRead = [];

    // A message only counts as read while someone is actually looking at it: the Chat page is
    // mounted (ActiveRoomId outlives it, so Mail/Dashboard would otherwise keep marking the last
    // room read) and the tab is visible with the window focused.
    private bool _chatPageMounted;
    private bool _attentive = true;

    public bool CanMarkRead => _chatPageMounted && _attentive;

    /// Chat page mount/unmount. Mounting marks the open room read if the tab is attended.
    public Task SetChatPageMountedAsync(bool mounted)
    {
        _chatPageMounted = mounted;
        return mounted ? MarkActiveRoomReadAsync() : Task.CompletedTask;
    }

    /// Tab visibility + window focus, reported by the Chat page's JS watcher. Coming back to
    /// an attended tab marks whatever arrived meanwhile.
    public Task SetAttentiveAsync(bool attentive)
    {
        var was = _attentive;
        _attentive = attentive;
        return attentive && !was ? MarkActiveRoomReadAsync() : Task.CompletedTask;
    }

    /// The message whose read receipt covers the open room: the newest one from someone else,
    /// else the newest confirmed one. Optimistic bubbles (outbox temp ids) never qualify.
    internal static MessageDto? LatestReadable(IReadOnlyList<MessageDto> messages, string? myNik, Func<Guid, bool> isPending)
    {
        MessageDto? latestAny = null;
        for (var i = messages.Count - 1; i >= 0; i--)
        {
            var m = messages[i];
            if (m.Id == Guid.Empty || isPending(m.Id)) continue;
            if (!string.Equals(m.SenderNik, myNik, StringComparison.OrdinalIgnoreCase)) return m;
            latestAny ??= m;
        }
        return latestAny;
    }

    /// Marks the open room read up to its newest message. Safe to call repeatedly: MarkReadAsync
    /// dedupes, and it no-ops while the hub is down (the next connect/reconnect calls this again).
    public Task MarkActiveRoomReadAsync()
    {
        if (ActiveRoomId is null || PracticeSend is not null || !CanMarkRead) return Task.CompletedTask;
        var latest = LatestReadable(ActiveMessages, _auth.Me?.Nik, _outbox.ContainsKey);
        return latest is null ? Task.CompletedTask : MarkReadAsync(latest.Id);
    }

    public async Task MarkReadAsync(Guid messageId)
    {
        if (messageId == Guid.Empty) return;
        if (_hub?.State != HubConnectionState.Connected) return;
        if (!_markedRead.Add(messageId)) return;
        try
        {
            await _hub.InvokeAsync("MarkRead", messageId);
            // Clear mention flag for the active room (Req 7.4)
            if (ActiveRoomId.HasValue) ClearMentionFlag(ActiveRoomId.Value);
            // Also clear the RoomListDto flag
            foreach (var list in new[] { DirectRooms, GroupRooms })
            {
                var idx = list.FindIndex(r => r.Id == ActiveRoomId);
                if (idx >= 0 && list[idx].HasUnreadMention)
                    list[idx] = list[idx] with { HasUnreadMention = false };
            }
            RoomListChanged?.Invoke();
        }
        catch { _markedRead.Remove(messageId); }
    }

    // ---- Event Handlers ----

    internal void OnNewMessage(MessageDto message)
    {
        if (PracticeSend is not null) { PracticeHeld?.Invoke(); return; } // real chat waits until practice ends
        if (message.ThreadParentId != null) return; // thread replies via OnThreadReply
        var isOwn = string.Equals(message.SenderNik, _auth.Me?.Nik, StringComparison.OrdinalIgnoreCase);
        if (!isOwn && _hub?.State == HubConnectionState.Connected)
        {
            _ = _hub.InvokeAsync("DeliveryConfirm", message.Id);
        }

        var isOpen = message.RoomId == ActiveRoomId;
        // Open but unattended (background tab, other page): it stays unread and banners like any other room.
        var isSeen = isOpen && CanMarkRead;
        // Our own echo is matched by room + idempotency key in any room, open or not.
        var wasOutgoing = ReconcileEcho(message);
        if (isOpen)
        {
            if (wasOutgoing) { }
            else if (ActiveMessages.All(m => m.Id != message.Id))
            {
                ActiveMessages.Add(message);
                _fresh.Add(message.Id);
                MessageReceived?.Invoke(message);
                if (isSeen) _ = MarkReadAsync(message.Id);
            }
        }
        else
        {
            var myNik = _auth.Me?.Nik;
            if (myNik != null && (message.MentionsAll || message.MentionedNiks.Contains(myNik, StringComparer.OrdinalIgnoreCase)))
            {
                _mentionFlags[message.RoomId] = true;
                // also flag room list DTO
                foreach (var list in new[] { DirectRooms, GroupRooms })
                {
                    var idx = list.FindIndex(r => r.Id == message.RoomId);
                    if (idx >= 0) list[idx] = list[idx] with { HasUnreadMention = true };
                }
            }
        }
        BumpRoomPreview(message, isSeen, incrementUnread: !isSeen && !isOwn);
        if (!isOwn && !message.IsDeleted) RaiseIncoming(message, isSeen);
        StateChanged?.Invoke();
    }

    /// A pin/unpin landed (hub event, or this tab's own HTTP response). Its persisted
    /// "X pinned/unpinned a message" row joins the timeline and the room preview, but it is
    /// not a chat message to anyone: no unread bump, no banner/sound, no delivery/read calls.
    /// Idempotent by message id, so the hub echo of this tab's own action is a no-op.
    public void ApplyPinnedChange(PinnedChangeDto change)
    {
        if (change.SystemMessage is { } sys && sys.RoomId == change.RoomId)
        {
            var isOpen = sys.RoomId == ActiveRoomId;
            if (isOpen && MergeSystemMessage(ActiveMessages, sys))
            {
                _fresh.Add(sys.Id);
                MessageReceived?.Invoke(sys);
            }
            // Preview text only: the server orders rooms by real activity, which a pin is not,
            // so the room keeps its place and its unread count.
            if (SetPreview(DirectRooms) | SetPreview(GroupRooms)) RoomListChanged?.Invoke();
            StateChanged?.Invoke();

            bool SetPreview(List<RoomListDto> list)
            {
                var idx = list.FindIndex(r => r.Id == sys.RoomId);
                if (idx < 0) return false;
                var preview = RoomPreview.Build(list[idx].Type, sys.Type, sys.Content, sys.IsDeleted, sys.SenderName, false);
                if (list[idx].LastMessage == preview) return false;
                list[idx] = list[idx] with { LastMessage = preview };
                return true;
            }
        }
        PinnedChanged?.Invoke();
    }

    /// The actor's fallback for a successful pin/unpin HTTP call: applies the persisted system row
    /// from the response body. The hub echo may land before or after it; either way it is merged
    /// once by id, and an actor whose hub is down still sees it. True when a change was applied
    /// (PinnedChanged was raised), so callers can skip their own pin-list refresh.
    public async Task<bool> ApplyPinResponseAsync(HttpResponseMessage res, Guid messageId, bool isPinned)
    {
        if (!res.IsSuccessStatusCode) return false;
        PinnedResultDto? result;
        try { result = await res.Content.ReadFromJsonAsync<PinnedResultDto>(); }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or NotSupportedException or InvalidOperationException) { return false; }
        if (result is not { Changed: true, SystemMessage: { } sys, RoomId: { } roomId }) return false;
        ApplyPinnedChange(new PinnedChangeDto(roomId, messageId, isPinned, sys.SenderName, sys));
        return true;
    }

    /// Inserts a system row in time order unless it is already there. True when added.
    public static bool MergeSystemMessage(List<MessageDto> messages, MessageDto sys)
    {
        if (messages.Any(m => m.Id == sys.Id)) return false;
        var at = messages.FindLastIndex(m => m.CreatedAt <= sys.CreatedAt);
        messages.Insert(at + 1, sys);
        return true;
    }

    private void RaiseIncoming(MessageDto message, bool isActiveRoom)
    {
        var room = DirectRooms.Concat(GroupRooms).FirstOrDefault(r => r.Id == message.RoomId);
        var myNik = _auth.Me?.Nik;
        var mentionsMe = myNik != null
            && (message.MentionsAll || message.MentionedNiks.Contains(myNik, StringComparer.OrdinalIgnoreCase));
        var isMuted = room is { IsMuted: true } && (room.MuteExpiresAt is null || room.MuteExpiresAt > DateTime.UtcNow);
        // ActiveRoomId survives navigating away from /chat, so the shell still checks the route.
        IncomingMessage?.Invoke(new IncomingChatMessage(message, room, isActiveRoom, mentionsMe, isMuted));
    }

    private void OnThreadReply(MessageDto reply)
    {
        // Bump parent counter
        var idx = ActiveMessages.FindIndex(m => m.Id == reply.ThreadParentId);
        if (idx >= 0)
        {
            var parent = ActiveMessages[idx];
            ActiveMessages[idx] = parent with { ThreadReplyCount = parent.ThreadReplyCount + 1, LastThreadReplyAt = reply.CreatedAt };
        }
        // dedup by idempotency key handled by subscriber (Chat.razor, ipop-37)
        ThreadReplyReceived?.Invoke(reply);
        StateChanged?.Invoke();
    }

    private void BumpRoomPreview(MessageDto message, bool isOpen, bool incrementUnread)
    {
        if (!TryBump(DirectRooms) && !TryBump(GroupRooms))
            _ = LoadRoomsAsync();
        RoomListChanged?.Invoke();

        bool TryBump(List<RoomListDto> list)
        {
            var idx = list.FindIndex(r => r.Id == message.RoomId);
            if (idx < 0) return false;
            var r = list[idx];
            var preview = RoomPreview.Build(r.Type, message.Type, message.Content, message.IsDeleted, message.SenderName,
                string.Equals(message.SenderNik, _auth.Me?.Nik, StringComparison.OrdinalIgnoreCase));
            var updated = r with
            {
                LastMessage = preview,
                LastMessageAt = message.CreatedAt,
                UnreadCount = isOpen ? 0 : incrementUnread ? r.UnreadCount + 1 : r.UnreadCount
            };
            list.RemoveAt(idx);
            var insertAt = updated.IsPinned ? 0 : list.FindIndex(x => !x.IsPinned);
            if (insertAt < 0) list.Add(updated); else list.Insert(insertAt, updated);
            return true;
        }
    }

    private void OnMessageEdited(MessageDto message)
    {
        if (message.RoomId == ActiveRoomId)
        {
            var idx = ActiveMessages.FindIndex(m => m.Id == message.Id);
            if (idx >= 0) ActiveMessages[idx] = message;
            MessageEdited?.Invoke(message);
        }
        StateChanged?.Invoke();
    }

    private void OnMessageDeleted(DeletedDto dto)
    {
        if (ActiveRoomId is not null)
        {
            var idx = ActiveMessages.FindIndex(m => m.Id == dto.MessageId);
            if (idx >= 0)
            {
                var old = ActiveMessages[idx];
                ActiveMessages[idx] = old with { IsDeleted = true, Content = dto.Replacement };
            }
            MessageDeleted?.Invoke(dto.MessageId, dto.Replacement);
        }
        StateChanged?.Invoke();
    }

    private void OnDeliveryUpdate(Guid messageId)
    {
        var targetMsg = ActiveMessages.FirstOrDefault(m => m.Id == messageId);
        var targetCreatedAt = targetMsg?.CreatedAt ?? DateTime.MinValue;

        bool changed = false;
        for (int i = 0; i < ActiveMessages.Count; i++)
        {
            var m = ActiveMessages[i];
            if (string.Equals(m.SenderNik, _auth.Me?.Nik, StringComparison.OrdinalIgnoreCase))
            {
                if (m.Id == messageId || (targetMsg != null && m.CreatedAt <= targetCreatedAt))
                {
                    if (m.DeliveryStatus == "sent")
                    {
                        ActiveMessages[i] = m with { DeliveryStatus = "delivered" };
                        changed = true;
                    }
                }
            }
        }

        if (changed) StateChanged?.Invoke();
    }

    internal void OnSeenUpdate(SeenUpdateDto update)
    {
        // Our own receipt (this or another tab) says nothing about whether the others read us.
        if (_auth.Me?.UserId is { } myId && update.Reader.UserId == myId) return;
        var isDirect = ActiveRoom?.Type == "dm" || ActiveRoom?.Type == "direct";
        var isGroup = ActiveRoom?.Type == "group";
        var targetMsg = ActiveMessages.FirstOrDefault(m => m.Id == update.MessageId);
        var targetCreatedAt = targetMsg?.CreatedAt ?? DateTime.MinValue;

        // In DM: when recipient reads, message is read.
        // In Group: all other active members must have read.
        bool isAllRead = isDirect || (isGroup && update.TotalReadCount >= Math.Max(1, update.TotalMemberCount - 1));

        bool changed = false;
        for (int i = 0; i < ActiveMessages.Count; i++)
        {
            var m = ActiveMessages[i];
            if (string.Equals(m.SenderNik, _auth.Me?.Nik, StringComparison.OrdinalIgnoreCase))
            {
                if (m.Id == update.MessageId || (targetMsg != null && m.CreatedAt <= targetCreatedAt))
                {
                    if (isAllRead && m.DeliveryStatus != "read")
                    {
                        ActiveMessages[i] = m with { DeliveryStatus = "read" };
                        changed = true;
                    }
                    else if (!isAllRead && m.DeliveryStatus == "sent")
                    {
                        ActiveMessages[i] = m with { DeliveryStatus = "delivered" };
                        changed = true;
                    }
                }
            }
        }

        SeenUpdated?.Invoke(update);
        if (changed) StateChanged?.Invoke();
        // A read marks every earlier message too, so refresh the per-bubble counts for all own messages.
        if (isGroup && ActiveRoom is { } room) _ = RefreshGroupReadCountsAsync(room.Id);
    }

    private int _readCountsGen;

    /// Pulls "read by N of M" for the viewer's own messages in the open group and patches the bubbles.
    private async Task RefreshGroupReadCountsAsync(Guid roomId, bool immediate = false)
    {
        var gen = ++_readCountsGen;
        if (!immediate) await Task.Delay(250); // coalesce bursts of seenUpdate (one per reader, per message)
        if (gen != _readCountsGen || ActiveRoom?.Id != roomId) return;
        var ids = ActiveMessages
            .Where(m => string.Equals(m.SenderNik, _auth.Me?.Nik, StringComparison.OrdinalIgnoreCase)
                        && m.Type != "system" && !m.IsDeleted && !IsPending(m.Id) && !IsFailed(m.Id))
            .Select(m => m.Id).TakeLast(200).ToList();
        if (ids.Count == 0) return;
        List<MessageReadCountDto>? counts;
        try { counts = await _auth.GetAsync<List<MessageReadCountDto>>($"/api/chat/rooms/{roomId}/read-counts?ids={string.Join(',', ids)}"); }
        catch { return; }
        if (counts is null || ActiveRoom?.Id != roomId) return;
        var map = counts.ToDictionary(c => c.MessageId);
        bool changed = false;
        for (int i = 0; i < ActiveMessages.Count; i++)
        {
            var m = ActiveMessages[i];
            if (!map.TryGetValue(m.Id, out var c)) continue;
            var samePreview = (m.ReadPreview ?? []).Select(p => p.UserId).SequenceEqual((c.Preview ?? []).Select(p => p.UserId));
            if (m.ReadCount == c.ReadCount && m.ReadTotal == c.ReadTotal && samePreview) continue;
            var status = c.ReadTotal > 0 && c.ReadCount >= c.ReadTotal ? "read"
                : m.DeliveryStatus == "sent" && c.ReadCount > 0 ? "delivered" : m.DeliveryStatus;
            ActiveMessages[i] = m with { ReadCount = c.ReadCount, ReadTotal = c.ReadTotal, ReadPreview = c.Preview, DeliveryStatus = status };
            changed = true;
        }
        if (changed) StateChanged?.Invoke();
    }

    /// Applies an authoritative reaction count from the hub, patching the single affected
    /// message. The old behaviour reloaded page 1, discarding any older pages the user had
    /// scrolled back through along with their scroll position.
    ///
    /// `CurrentUserReacted` is preserved rather than recomputed: ReactionDto carries the
    /// actor's user id but the client never learns its own, and our own toggles are already
    /// reflected locally by ToggleReactionLocal.
    private void OnReactionChanged(ReactionDto reaction)
    {
        PatchReactions(reaction.MessageId, reaction.Emoji, groups =>
        {
            var gi = groups.FindIndex(g => g.Emoji == reaction.Emoji);
            if (reaction.Count <= 0)
            {
                if (gi >= 0) groups.RemoveAt(gi);
            }
            else if (gi >= 0)
            {
                groups[gi] = groups[gi] with { Count = reaction.Count };
            }
            else
            {
                groups.Add(new ReactionGroupDto(reaction.Emoji, reaction.Count, false));
            }
        });

        ReactionChanged?.Invoke(reaction);
        StateChanged?.Invoke();
    }

    /// Optimistically flips our own reaction so the pill responds immediately; the hub
    /// event that follows corrects the count.
    public void ToggleReactionLocal(Guid messageId, string emoji)
    {
        PatchReactions(messageId, emoji, groups =>
        {
            var gi = groups.FindIndex(g => g.Emoji == emoji);
            if (gi < 0)
            {
                groups.Add(new ReactionGroupDto(emoji, 1, true));
                return;
            }
            var g = groups[gi];
            if (g.CurrentUserReacted)
            {
                if (g.Count <= 1) groups.RemoveAt(gi);
                else groups[gi] = g with { Count = g.Count - 1, CurrentUserReacted = false };
            }
            else
            {
                groups[gi] = g with { Count = g.Count + 1, CurrentUserReacted = true };
            }
        });
        StateChanged?.Invoke();
    }

    private void PatchReactions(Guid messageId, string emoji, Action<List<ReactionGroupDto>> mutate)
    {
        var idx = ActiveMessages.FindIndex(m => m.Id == messageId);
        if (idx < 0) return;
        var msg = ActiveMessages[idx];
        var groups = msg.Reactions.ToList();
        mutate(groups);
        ActiveMessages[idx] = msg with { Reactions = groups };
    }

    private readonly Dictionary<string, CancellationTokenSource> _typingCts = new();

    private void OnTyping(TypingDto dto, bool isTyping)
    {
        if (dto.RoomId != ActiveRoomId) return;
        // Defence-in-depth: never show our own "is typing" — the server excludes the caller,
        // but a second connection on the same account (or a backplane timing race) could echo back.
        if (string.Equals(dto.UserNik, _auth.Me?.Nik, StringComparison.OrdinalIgnoreCase)) return;
        if (_typingCts.TryGetValue(dto.DisplayName, out var old)) { old.Cancel(); old.Dispose(); _typingCts.Remove(dto.DisplayName); }

        if (isTyping)
            TypingUsers.Add(dto.DisplayName);
        else
            TypingUsers.Remove(dto.DisplayName);

        TypingChanged?.Invoke();
        StateChanged?.Invoke();

        if (!isTyping) return;
        var cts = new CancellationTokenSource();
        _typingCts[dto.DisplayName] = cts;
        var name = dto.DisplayName;
        var token = cts.Token;
        _ = Task.Delay(8000, token).ContinueWith(t =>
        {
            if (t.IsCanceled) return;
            _typingCts.Remove(name);
            if (TypingUsers.Remove(name))
            {
                TypingChanged?.Invoke();
                StateChanged?.Invoke();
            }
        }, TaskScheduler.Default);
    }

    private void OnPresenceChanged(string nik, string status)
    {
        // Patch the pushed status into the room lists (DM partner) and the open room's
        // members, so dots, header status and "N online" update without a reload.
        DirectRooms = PatchRoomPresence(DirectRooms, nik, status);
        GroupRooms = PatchRoomPresence(GroupRooms, nik, status);
        ArchivedRooms = PatchRoomPresence(ArchivedRooms, nik, status);
        if (ActiveRoom is not null) ActiveRoom = PatchMemberPresence(ActiveRoom, nik, status);
        StateChanged?.Invoke();
    }

    internal static List<RoomListDto> PatchRoomPresence(List<RoomListDto> rooms, string nik, string status)
    {
        var i = rooms.FindIndex(r => r.Type == "dm" && string.Equals(r.RecipientNik, nik, StringComparison.OrdinalIgnoreCase));
        if (i < 0) return rooms;
        var copy = new List<RoomListDto>(rooms);
        // A NIK has one DM with the viewer, but patch every match in case of duplicates.
        for (; i < copy.Count; i++)
            if (copy[i].Type == "dm" && string.Equals(copy[i].RecipientNik, nik, StringComparison.OrdinalIgnoreCase)
                && copy[i].PresenceStatus != status)
                copy[i] = copy[i] with { PresenceStatus = status };
        return copy;
    }

    /// OnlineCount mirrors the server (ChatService.GetRoomAsync): members whose status is "on".
    internal static ChatRoomDto PatchMemberPresence(ChatRoomDto room, string nik, string status)
    {
        if (!room.Members.Any(m => string.Equals(m.Nik, nik, StringComparison.OrdinalIgnoreCase) && m.PresenceStatus != status))
            return room;
        var members = room.Members
            .Select(m => string.Equals(m.Nik, nik, StringComparison.OrdinalIgnoreCase) ? m with { PresenceStatus = status } : m)
            .ToList();
        return room with { Members = members, OnlineCount = members.Count(m => m.PresenceStatus == "on") };
    }

    /// presenceChanged/chatRoomsChanged pushes sent while the app hub was down are lost:
    /// re-read the room list and the open room's members (not its messages).
    private void OnAppHubReconnected()
    {
        ScheduleRoomsReload();
        if (ActiveRoomId is not null) _ = SafeAsync(() => LoadActiveRoomAsync());
    }

    /// chatRoomsChanged: reload only the room lists (debounced 300ms so a burst of mark-reads is
    /// one fetch). Never touches ActiveMessages or scroll.
    private void ScheduleRoomsReload()
    {
        _roomsReloadCts?.Cancel();
        var cts = _roomsReloadCts = new CancellationTokenSource();
        _ = Task.Delay(300, cts.Token).ContinueWith(t =>
        {
            if (t.IsCanceled || !_auth.IsAuthenticated) return Task.CompletedTask;
            return SafeAsync(async () =>
            {
                // An active sidebar search stays applied rather than snapping back to the full list.
                await SearchRoomsAsync(_roomSearch);
                if (ArchivedRooms.Count > 0 || ArchivedTotal > 0) await LoadArchivedRoomsAsync();
            });
        }, TaskScheduler.Default);
    }

    private async Task RefreshOnReconnectAsync()
    {
        await LoadRoomsAsync();
        if (ActiveRoomId is not null)
        {
            await LoadMessagesAsync(1);
            await _hub!.InvokeAsync("JoinRoom", ActiveRoomId.Value);
            // Messages that landed while the hub was down arrived via the page-1 reload, not
            // newMessage, so nothing marked them read.
            await MarkActiveRoomReadAsync();
        }
        // Drain offline queue (Task 17.2)
        await DrainOfflineQueueAsync();
    }

    private async Task OnRemovedFromRoomAsync(Guid roomId)
    {
        if (_drafts != null) try { await _drafts.RemoveAsync(roomId); } catch { }
        ForgetRoom(roomId);
        if (roomId != ActiveRoomId) { await LoadRoomsAsync(); return; }
        try { await _hub!.InvokeAsync("LeaveRoom", roomId); } catch { }
        if (roomId != ActiveRoomId) { await LoadRoomsAsync(); return; } // switched away meanwhile
        _openCts?.Cancel();
        ActiveRoomId = null;
        ResetRoomLoadState();
        IsLoadingMessages = false;
        _openingRoomId = null;
        ActiveRoom = null;
        ActiveMessages = [];
        foreach (var cts in _typingCts.Values) { cts.Cancel(); cts.Dispose(); }
        _typingCts.Clear();
        TypingUsers.Clear();
        await LoadRoomsAsync();
        StateChanged?.Invoke();
    }

    private static async Task SafeAsync(Func<Task> fn) { try { await fn(); } catch { /* ponytail: log to telemetry when available */ } }

    // ---- Dispose ----

    /// Session moved to another tab (SessionLockState); ConnectAsync brings it back.
    public async Task DisconnectAsync()
    {
        await DisposeHubAsync();
        ConnectionChanged?.Invoke();
    }

    private async Task DisposeHubAsync()
    {
        _openCts?.Cancel(); _openCts?.Dispose(); _openCts = null;
        _openingRoomId = null;
        _markedRead.Clear();
        _roomCache.Clear();
        _roomCacheOrder.Clear();
        foreach (var cts in _typingCts.Values) { cts.Cancel(); cts.Dispose(); }
        _typingCts.Clear();
        TypingUsers.Clear();
        if (_hub is not null)
        {
            await _hub.DisposeAsync();
            _hub = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_presenceSubscribed)
        {
            _auth.PresenceChanged -= OnPresenceChanged;
            _auth.HubReconnected -= OnAppHubReconnected;
            _presenceSubscribed = false;
        }
        _roomsChangedSub?.Dispose(); _roomsChangedSub = null;
        _roomsReloadCts?.Cancel();
        await DisposeHubAsync();
    }
}

/// A message from someone else that arrived while its room wasn't the active one.
public sealed record IncomingChatMessage(MessageDto Message, RoomListDto? Room, bool IsActiveRoom, bool MentionsMe, bool RoomMuted);
