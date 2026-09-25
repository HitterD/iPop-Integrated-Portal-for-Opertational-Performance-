using System.Net.Http.Json;
using IPop.Application.Chat;
using IPop.Application.Chat.Mentions;
using Microsoft.AspNetCore.SignalR.Client;

namespace IPop.Web.Services;

/// Real-time chat state for Blazor WASM. Manages the /hubs/chat SignalR connection,
/// room/message state, typing indicators, and exposes events for UI components.
/// Follows the same patterns as AuthState (HubConnectionBuilder, WithAutomaticReconnect, .On<T>).
public class ChatState : IAsyncDisposable
{
    private readonly AuthState _auth;
    private readonly HttpClient _http;
    private readonly DraftStore? _drafts;
    private HubConnection? _hub;
    private DateTime _lastTypingBroadcast = DateTime.MinValue;

    public ChatState(AuthState auth, HttpClient http, DraftStore? drafts = null)
    {
        _auth = auth;
        _http = http;
        _drafts = drafts;
    }

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
    public int MessagePage { get; private set; } = 1;
    public bool HasOlderMessages { get; private set; }
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
    private bool _connecting;
    private CancellationTokenSource? _openCts;
    public bool IsOpening => _openCts is not null;

    // ---- Connect ----

    public async Task ConnectAsync()
    {
        if (_hub is not null || _connecting) return;
        if (!_auth.IsAuthenticated) return;
        _connecting = true;

        // Subscribe to presence changes so room-list presence dots update in real-time
        if (!_presenceSubscribed) { _auth.PresenceChanged += OnPresenceChanged; _presenceSubscribed = true; }

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
        _hub.On<PinnedChangeDto>("pinnedChanged", pc => PinnedChanged?.Invoke());
        _hub.On<Guid>("deliveryUpdate", OnDeliveryUpdate);
        _hub.On<RoomUpdateDto>("roomUpdated", ru => { _ = SafeAsync(async () => { await LoadRoomsAsync(); await LoadActiveRoomAsync(); }); });
        _hub.On<MessageDto>("threadReply", OnThreadReply);
        _hub.On<MessageDto>("emergencyBroadcast", OnNewMessage);
        _hub.On<Guid>("removedFromRoom", roomId => _ = SafeAsync(() => OnRemovedFromRoomAsync(roomId)));

        _hub.Reconnecting += ex => { ConnectionChanged?.Invoke(); return Task.CompletedTask; };
        _hub.Reconnected += cid => { ConnectionChanged?.Invoke(); _ = RefreshOnReconnectAsync(); return Task.CompletedTask; };
        _hub.Closed += ex => { ConnectionChanged?.Invoke(); return Task.CompletedTask; };

        try
        {
            await _hub.StartAsync();
            ConnectionChanged?.Invoke();
            await LoadRoomsAsync();
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
        var directTask = _auth.GetAsync<PagedResult<RoomListDto>>("/api/chat/rooms?tab=direct&pageSize=50");
        var groupTask = _auth.GetAsync<PagedResult<RoomListDto>>("/api/chat/rooms?tab=groups&pageSize=50");
        await Task.WhenAll(directTask, groupTask);
        DirectRooms = directTask.Result?.Items.ToList() ?? [];
        GroupRooms = groupTask.Result?.Items.ToList() ?? [];
        _directTotal = directTask.Result?.TotalCount ?? DirectRooms.Count;
        _groupTotal = groupTask.Result?.TotalCount ?? GroupRooms.Count;
        _directPage = 1;
        _groupPage = 1;
        foreach (var r in DirectRooms.Concat(GroupRooms).Where(r => r.HasUnreadMention)) _mentionFlags[r.Id] = true;
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
        var next = _directPage + 1;
        var r = await _auth.GetAsync<PagedResult<RoomListDto>>($"/api/chat/rooms?tab=direct&page={next}&pageSize=50");
        if (r is null) return;
        _directTotal = r.TotalCount;
        _directPage = next;
        var fresh = r.Items.Where(i => DirectRooms.All(d => d.Id != i.Id));
        DirectRooms.AddRange(fresh);
        RoomListChanged?.Invoke();
        StateChanged?.Invoke();
    }

    public async Task LoadMoreGroupRoomsAsync()
    {
        var next = _groupPage + 1;
        var r = await _auth.GetAsync<PagedResult<RoomListDto>>($"/api/chat/rooms?tab=groups&page={next}&pageSize=50");
        if (r is null) return;
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
        if (string.IsNullOrWhiteSpace(query) || query.Trim().Length < 2)
        {
            await LoadRoomsAsync();
            return;
        }
        var r = await _auth.GetAsync<PagedResult<RoomListDto>>($"/api/chat/rooms/search?q={Uri.EscapeDataString(query.Trim())}");
        var flat = r?.Items.ToList() ?? [];
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
        var ct = cts.Token;

        ActiveRoomId = roomId;
        ActiveMessages = [];
        MessagePage = 1;
        HasOlderMessages = false;
        TypingUsers.Clear();
        StateChanged?.Invoke();

        try
        {
            // Deliberately do NOT LeaveRoom(prevRoom): the hub only auto-joins every room
            // once, on connect. Leaving a room on switch permanently stopped its newMessage
            // events, so unread badges and previews for background rooms went stale.
            await LoadActiveRoomAsync(ct);
            if (ct.IsCancellationRequested) return;
            await LoadMessagesAsync(1, ct);
            if (ct.IsCancellationRequested) return;
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
            StateChanged?.Invoke();
        }
    }

    public async Task LoadActiveRoomAsync(CancellationToken ct = default)
    {
        var roomId = ActiveRoomId;
        if (roomId is null) return;
        // ipop-44: read into a local, THEN guard, THEN write — matching LoadMessagesAsync.
        // Assigning inside the await expression let a superseded hub handler
        // (memberChanged/roomUpdated fire without a token) leave a stale room's DTO
        // in ActiveRoom after the user switched. Cosmetic-only: Pin/Emergency buttons
        // may flicker for the wrong room, but both mutations read ActiveRoomId live and
        // the server re-checks role — so no wrong-target write is possible.
        ChatRoomDto? room;
        try { room = await _auth.GetAsync<ChatRoomDto>($"/api/chat/rooms/{roomId}", ct); }
        catch (OperationCanceledException) { return; }
        if (ct.IsCancellationRequested || roomId != ActiveRoomId) return;
        ActiveRoom = room;
        StateChanged?.Invoke();
    }

    public async Task LoadMessagesAsync(int page, CancellationToken ct = default)
    {
        var roomId = ActiveRoomId;
        if (roomId is null) return;
        PagedResult<MessageDto>? result;
        try { result = await _auth.GetAsync<PagedResult<MessageDto>>($"/api/chat/rooms/{roomId}/messages?page={page}&pageSize=50", ct); }
        catch (OperationCanceledException) { return; }
        if (result is null) return;
        if (ct.IsCancellationRequested || roomId != ActiveRoomId) return;
        if (page == 1)
        {
            // A fresh page 1 is authoritative; any unreconciled optimistic bubbles for
            // this room would otherwise linger forever.
            DropOptimisticFor(roomId.Value);
            ActiveMessages = result.Items.ToList();
        }
        else
            ActiveMessages.InsertRange(0, result.Items);
        MessagePage = page;
        HasOlderMessages = page * result.PageSize < result.TotalCount;
        StateChanged?.Invoke();
    }

    public Task LoadOlderMessagesAsync()
        => HasOlderMessages ? LoadMessagesAsync(MessagePage + 1) : Task.CompletedTask;

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

    // ---- Send Message ----

    // ---- Offline Queue (Task 17.1) — H3: carry RoomId so drain doesn't mis-route on room switch
    public List<(Guid RoomId, string Content, Guid IdempotencyKey, Guid? ReplyToId, string? Type, IReadOnlyList<Guid>? AttachmentIds)> OfflineQueue { get; } = [];

    // ---- Optimistic outbox ----
    // A locally-rendered copy of the message appears the instant Send is pressed, so the
    // composer can clear without the text seeming to vanish. Entries are keyed by a
    // temporary id and reconciled when the server echoes the real message back.

    private sealed class Outgoing
    {
        public Guid TempId { get; init; }
        public Guid Key { get; init; }
        public Guid RoomId { get; init; }
        public string Content { get; init; } = "";
        public bool Failed { get; set; }
        /// Set from the server's messageAck, which maps our idempotency key to the real id.
        public Guid? ServerId { get; set; }
    }

    private readonly Dictionary<Guid, Outgoing> _outbox = [];

    /// True while a message is still in flight (or queued offline) — the UI shows a clock.
    public bool IsPending(Guid messageId) =>
        _outbox.TryGetValue(messageId, out var o) && !o.Failed;

    /// True when the send failed and the user can retry.
    public bool IsFailed(Guid messageId) =>
        _outbox.TryGetValue(messageId, out var o) && o.Failed;

    public async Task SendMessageAsync(string content, Guid? replyToId = null, string? type = null,
        IReadOnlyList<Guid>? attachmentIds = null,
        IReadOnlyList<AttachmentDto>? attachmentPreviews = null)
    {
        var key = Guid.NewGuid();
        var roomId = ActiveRoomId;
        if (roomId is null) return;

        var temp = AddOptimistic(roomId.Value, content, key, replyToId, type, attachmentPreviews);

        if (_hub?.State != HubConnectionState.Connected)
        {
            OfflineQueue.Add((roomId.Value, content, key, replyToId, type, attachmentIds));
            StateChanged?.Invoke();
            return;
        }

        var request = new SendMessageHubRequest(
            roomId.Value, content, key, replyToId, type ?? "text", attachmentIds);

        try
        {
            await _hub.InvokeAsync("SendMessage", request);
        }
        catch
        {
            // Connection dropped mid-invoke: keep the bubble, mark it retryable.
            if (_outbox.TryGetValue(temp, out var o)) o.Failed = true;
            OfflineQueue.Add((roomId.Value, content, key, replyToId, type, attachmentIds));
            StateChanged?.Invoke();
        }
    }

    private Guid AddOptimistic(Guid roomId, string content, Guid key, Guid? replyToId, string? type,
        IReadOnlyList<AttachmentDto>? attachments)
    {
        var tempId = Guid.NewGuid();
        _outbox[tempId] = new Outgoing { TempId = tempId, Key = key, RoomId = roomId, Content = content };

        if (roomId == ActiveRoomId)
        {
            var replyPreview = replyToId is null
                ? null
                : ActiveMessages.FirstOrDefault(m => m.Id == replyToId) is { } src
                    ? new ReplyPreviewDto(src.SenderName, src.Content)
                    : null;

            ActiveMessages.Add(new MessageDto(
                tempId, roomId, Guid.Empty,
                _auth.Me?.Name ?? "You", _auth.Me?.Nik ?? "",
                type ?? "text", content, DateTime.UtcNow, null, false, null,
                replyToId, replyPreview, null, 0, null,
                attachments ?? [], [], [], false, Guid.NewGuid()));
            StateChanged?.Invoke();
        }
        return tempId;
    }

    /// Finds the optimistic entry that `real` is the server echo of. The ack-supplied
    /// ServerId is authoritative; content matching only covers the case where the room
    /// broadcast overtakes the ack.
    private Outgoing? MatchOutgoing(MessageDto real)
    {
        var byId = _outbox.Values.FirstOrDefault(o => o.ServerId == real.Id);
        if (byId is not null) return byId;

        if (!string.Equals(real.SenderNik, _auth.Me?.Nik, StringComparison.OrdinalIgnoreCase)) return null;
        return _outbox.Values
            .Where(o => o.ServerId is null && o.RoomId == real.RoomId && o.Content == real.Content)
            .OrderBy(o => o.TempId)
            .FirstOrDefault();
    }

    private void RemoveOptimistic(Guid tempId)
    {
        _outbox.Remove(tempId);
        var idx = ActiveMessages.FindIndex(m => m.Id == tempId);
        if (idx >= 0) ActiveMessages.RemoveAt(idx);
    }

    /// Clears optimistic bubbles that can no longer be reconciled (room reload / switch).
    private void DropOptimisticFor(Guid roomId)
    {
        foreach (var k in _outbox.Where(kv => kv.Value.RoomId == roomId).Select(kv => kv.Key).ToList())
            _outbox.Remove(k);
    }

    /// Re-queues a failed message for another attempt.
    public async Task RetryAsync(Guid tempId)
    {
        if (!_outbox.TryGetValue(tempId, out var o) || !o.Failed) return;
        var queued = OfflineQueue.FirstOrDefault(q => q.IdempotencyKey == o.Key);
        o.Failed = false;
        StateChanged?.Invoke();
        if (_hub?.State != HubConnectionState.Connected) return;
        if (queued.Content is null) { RemoveOptimistic(tempId); return; }
        OfflineQueue.RemoveAll(q => q.IdempotencyKey == o.Key);
        try
        {
            await _hub.InvokeAsync("SendMessage", new SendMessageHubRequest(
                queued.RoomId, queued.Content, queued.IdempotencyKey, queued.ReplyToId,
                queued.Type ?? "text", queued.AttachmentIds));
        }
        catch
        {
            o.Failed = true;
            OfflineQueue.Add(queued);
            StateChanged?.Invoke();
        }
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

    private void OnNewMessage(MessageDto message)
    {
        if (message.ThreadParentId != null) return; // thread replies via OnThreadReply
        var isOwn = string.Equals(message.SenderNik, _auth.Me?.Nik, StringComparison.OrdinalIgnoreCase);
        if (!isOwn && _hub?.State == HubConnectionState.Connected)
        {
            _ = _hub.InvokeAsync("DeliveryConfirm", message.Id);
        }

        var isOpen = message.RoomId == ActiveRoomId;
        if (isOpen)
        {
            var mine = MatchOutgoing(message);
            if (mine is not null)
            {
                _outbox.Remove(mine.TempId);
                var ti = ActiveMessages.FindIndex(m => m.Id == mine.TempId);
                if (ti >= 0) ActiveMessages[ti] = message with { DeliveryStatus = message.DeliveryStatus ?? "sent" };
                else if (ActiveMessages.All(m => m.Id != message.Id)) ActiveMessages.Add(message with { DeliveryStatus = message.DeliveryStatus ?? "sent" });
            }
            else if (ActiveMessages.All(m => m.Id != message.Id))
            {
                ActiveMessages.Add(message);
                MessageReceived?.Invoke(message);
                _ = MarkReadAsync(message.Id);
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
        BumpRoomPreview(message, isOpen, incrementUnread: !isOpen && !isOwn);
        StateChanged?.Invoke();
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
            var preview = message.IsDeleted ? "This message was deleted"
                : MentionPlainText.Project(message.Content, 100);
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

    private void OnMessageAck(MessageAckDto ack)
    {
        var entry = _outbox.Values.FirstOrDefault(o => o.Key == ack.IdempotencyKey);
        if (entry is not null)
        {
            entry.ServerId = ack.MessageId;
            entry.Failed = false;
            OfflineQueue.RemoveAll(q => q.IdempotencyKey == ack.IdempotencyKey);
            // If the room broadcast already delivered the real message, the optimistic
            // copy is now a duplicate.
            if (ActiveMessages.Any(m => m.Id == ack.MessageId))
                RemoveOptimistic(entry.TempId);
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

    private void OnSeenUpdate(SeenUpdateDto update)
    {
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
        // Trigger UI re-render so presence dots in room list update immediately
        StateChanged?.Invoke();
    }

    private async Task RefreshOnReconnectAsync()
    {
        await LoadRoomsAsync();
        if (ActiveRoomId is not null)
        {
            await LoadMessagesAsync(1);
            await _hub!.InvokeAsync("JoinRoom", ActiveRoomId.Value);
        }
        // Drain offline queue (Task 17.2)
        await DrainOfflineQueueAsync();
    }

    private async Task DrainOfflineQueueAsync()
    {
        if (_hub?.State != HubConnectionState.Connected) return;
        var queue = OfflineQueue.ToList();
        OfflineQueue.Clear();
        foreach (var (roomId, content, key, replyToId, type, attachmentIds) in queue)
        {
            if (roomId == Guid.Empty) continue;
            var request = new SendMessageHubRequest(roomId, content, key, replyToId, type ?? "text", attachmentIds);
            try { await _hub.InvokeAsync("SendMessage", request); }
            catch { OfflineQueue.Add((roomId, content, key, replyToId, type, attachmentIds)); break; }
        }
        StateChanged?.Invoke();
    }

    private async Task OnRemovedFromRoomAsync(Guid roomId)
    {
        if (_drafts != null) try { await _drafts.RemoveAsync(roomId); } catch { }
        if (roomId != ActiveRoomId) { await LoadRoomsAsync(); return; }
        try { await _hub!.InvokeAsync("LeaveRoom", roomId); } catch { }
        ActiveRoomId = null;
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

    private async Task DisposeHubAsync()
    {
        _openCts?.Cancel(); _openCts?.Dispose(); _openCts = null;
        _openingRoomId = null;
        _markedRead.Clear();
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
        if (_presenceSubscribed) { _auth.PresenceChanged -= OnPresenceChanged; _presenceSubscribed = false; }
        await DisposeHubAsync();
    }
}
