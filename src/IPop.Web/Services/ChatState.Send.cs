using IPop.Application.Chat;
using IPop.Application.Chat.Mentions;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.JSInterop;

namespace IPop.Web.Services;

/// Where an outgoing message stands. Pending/Queued show a clock; Failed (the server said no)
/// and Unknown (no answer: timeout or the connection dropped mid-send) show "Not sent" + Retry.
public enum SendState { Pending, Queued, Failed, Unknown, Confirmed }

/// Returned by SendMessageAsync and raised through SendSettled. IdempotencyKey is the one the
/// caller passed (or that was generated), so a page can tie a draft snapshot to this exact send.
public sealed record SendOutcome(Guid TempId, Guid RoomId, Guid IdempotencyKey, SendState State,
    string? Error = null, IReadOnlyList<string>? LocalPreviewUrls = null);

/// Everything needed to put a message that never went out back into the composer.
public sealed record UnsentMessage(Guid RoomId, string Content, Guid? ReplyToId, string? Type,
    IReadOnlyList<Guid>? AttachmentIds, IReadOnlyList<AttachmentDto> Attachments, IReadOnlyList<string>? LocalPreviewUrls);

// Outbox: optimistic bubbles, the offline queue, and reconciling them with server answers.
// Every server answer is tied to its message by (room, idempotency key) — never by content or
// by "the oldest pending one".
public partial class ChatState
{
    /// Longest message the server accepts (UTF-16 code units, same count the hub checks).
    public const int MaxMessageLength = MentionSyntax.MaxContentLength;

    /// How long one SendMessage call may go unanswered before its outcome is "unknown".
    internal TimeSpan SendTimeout { get; set; } = TimeSpan.FromSeconds(20);

    /// Raised once per send when it is confirmed, rejected, or its outcome becomes unknown.
    public event Action<SendOutcome>? SendSettled;

    private sealed class Outgoing
    {
        public Guid TempId { get; init; }
        public Guid Key { get; init; }
        public Guid RoomId { get; init; }
        public string Content { get; init; } = "";
        public Guid? ReplyToId { get; init; }
        public string? Type { get; init; }
        public IReadOnlyList<Guid>? AttachmentIds { get; init; }
        public IReadOnlyList<string>? LocalPreviewUrls { get; init; }
        /// Sender removed the link preview card in the composer.
        public bool LinkPreviewOff { get; init; }
        /// The optimistic bubble, rebuilt into the timeline whenever the room is repainted.
        public MessageDto Bubble { get; init; } = null!;
        public long Seq { get; init; }
        public SendState State { get; set; }
        public string? Error { get; set; }
        /// Bumped per attempt; a late failure from a superseded attempt is ignored.
        public int Attempt { get; set; }
        /// Set from the ack/result, which maps our idempotency key to the real id.
        public Guid? ServerId { get; set; }
        public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    }

    private readonly Dictionary<Guid, Outgoing> _outbox = [];
    private long _outboxSeq;
    private bool _outboxHooked;

    /// True while a message is in flight, queued offline, or confirmed but not yet echoed.
    public bool IsPending(Guid messageId) =>
        _outbox.TryGetValue(messageId, out var o) && o.State is SendState.Pending or SendState.Queued or SendState.Confirmed;

    /// True when the send was refused or its outcome is unknown; the bubble offers Retry.
    public bool IsFailed(Guid messageId) =>
        _outbox.TryGetValue(messageId, out var o) && o.State is SendState.Failed or SendState.Unknown;

    /// Readable reason for a failed bubble, or null.
    public string? SendFailureText(Guid messageId) =>
        _outbox.TryGetValue(messageId, out var o) && o.State is SendState.Failed or SendState.Unknown
            ? (o.State == SendState.Unknown ? UnknownOutcomeText : SendErrorText(o.Error))
            : null;

    /// Messages for this account still waiting to go out (queued while offline).
    public int QueuedCount => _outbox.Values.Count(o => o.State == SendState.Queued);

    public const string UnknownOutcomeText = "Not confirmed — it may not have been sent. Retry is safe: it won't send twice.";

    public static string SendErrorText(string? code) => code switch
    {
        "rate_limited" => "You're sending too fast. Wait a moment, then retry.",
        "payload_too_large" or "content_too_long" => $"Message is too long (max {MaxMessageLength:N0} characters).",
        "content_required" => "Message is empty.",
        "forbidden" => "You don't have permission to send messages.",
        "unauthorized" => "Your session expired. Sign in again, then retry.",
        "room_not_found" => "You're no longer a member of this conversation.",
        "blocked" => "You can't message this person.",
        "message_not_found" => "The message you replied to was deleted.",
        "not_project_member" => "You mentioned a project or task you're not a member of.",
        "idempotency_conflict" => "This message conflicts with one already sent. Copy it and send again.",
        _ => "Couldn't send. Retry.",
    };

    private void HookOutbox()
    {
        if (_outboxHooked) return;
        _outboxHooked = true;
        RoomGone += ForgetOutboxFor;
        _auth.SignedOut += ClearOutbox;
        _auth.SignedOut += () => _ = SignOutResetAsync();
    }

    /// Sends a message from the open room. The bubble appears immediately; the returned outcome
    /// says whether the server confirmed it. Content over MaxMessageLength is refused up front
    /// (State Failed, no bubble) so the caller can keep it in the composer.
    public async Task<SendOutcome?> SendMessageAsync(string content, Guid? replyToId = null, string? type = null,
        IReadOnlyList<Guid>? attachmentIds = null,
        IReadOnlyList<AttachmentDto>? attachmentPreviews = null,
        Guid? idempotencyKey = null,
        IReadOnlyList<string>? localPreviewUrls = null,
        bool linkPreviewOff = false)
    {
        HookOutbox();
        var key = idempotencyKey ?? Guid.NewGuid();
        var roomId = ActiveRoomId;
        if (roomId is null) return null;

        if (PracticeSend?.Invoke(roomId.Value, content) is var (mine, reply))
        {
            ActiveMessages.Add(mine);
            _fresh.Add(mine.Id);
            StateChanged?.Invoke();
            await Task.Delay(1400);
            if (ActiveRoomId == roomId) { var r = reply(); _fresh.Add(r.Id); ActiveMessages.Add(r); StateChanged?.Invoke(); }
            return new SendOutcome(mine.Id, roomId.Value, key, SendState.Confirmed);
        }

        if (content.Length > MaxMessageLength)
            return new SendOutcome(Guid.Empty, roomId.Value, key, SendState.Failed, "payload_too_large");

        var entry = AddOptimistic(roomId.Value, content, key, replyToId, type, attachmentIds, attachmentPreviews, localPreviewUrls, linkPreviewOff);
        await SendCoreAsync(entry);
        return Outcome(entry);
    }

    /// Retries a failed/unknown message with its original idempotency key, so a message that did
    /// reach the server is returned as-is instead of being sent twice.
    public async Task RetryAsync(Guid tempId)
    {
        if (!_outbox.TryGetValue(tempId, out var o) || o.State is not (SendState.Failed or SendState.Unknown)) return;
        await SendCoreAsync(o);
    }

    /// Removes a failed bubble and hands its content back (e.g. to restore into the composer).
    public UnsentMessage? DiscardFailed(Guid tempId)
    {
        if (!_outbox.TryGetValue(tempId, out var o) || o.State is not (SendState.Failed or SendState.Unknown)) return null;
        RemoveOptimistic(tempId);
        StateChanged?.Invoke();
        return new UnsentMessage(o.RoomId, o.Content, o.ReplyToId, o.Type, o.AttachmentIds, o.Bubble.Attachments, o.LocalPreviewUrls);
    }

    private async Task SendCoreAsync(Outgoing o)
    {
        var attempt = ++o.Attempt;
        o.Error = null;
        var hub = _hub;
        if (hub?.State != HubConnectionState.Connected)
        {
            o.State = SendState.Queued;
            StateChanged?.Invoke();
            return;
        }
        o.State = SendState.Pending;
        StateChanged?.Invoke();

        var request = new SendMessageHubRequest(o.RoomId, o.Content, o.Key, o.ReplyToId, o.Type ?? "text", o.AttachmentIds, o.LinkPreviewOff);
        SendMessageResultDto? result;
        using var cts = new CancellationTokenSource(SendTimeout);
        try
        {
            result = await hub.InvokeAsync<SendMessageResultDto?>("SendMessage", request, cts.Token);
        }
        catch
        {
            // Timeout or the connection dropped mid-call: the server may or may not have it.
            // Not "rejected" — a later ack, echo or page-1 reload can still confirm it.
            if (_outbox.ContainsKey(o.TempId) && o.Attempt == attempt && o.State == SendState.Pending)
                Settle(o, SendState.Unknown, null);
            return;
        }
        ApplySendResult(o.RoomId, o.Key, result, attempt);
    }

    /// Applies the hub's answer to the message it belongs to (room + idempotency key).
    internal void ApplySendResult(Guid roomId, Guid key, SendMessageResultDto? result, int? attempt = null)
    {
        var o = FindOutgoing(roomId, key);
        if (o is null) return; // already reconciled by ack/echo/reload
        if (result is { Success: true, MessageId: { } id })
        {
            Confirm(o, id);
            StateChanged?.Invoke();
            return;
        }
        if (o.State == SendState.Confirmed) return; // a success already landed
        if (attempt is { } a && a != o.Attempt) return; // a newer attempt owns the state now
        if (result is null) { Settle(o, SendState.Unknown, null); return; }
        Settle(o, SendState.Failed, result.Error ?? "unknown");
    }

    private Outgoing? FindOutgoing(Guid roomId, Guid key) =>
        _outbox.Values.FirstOrDefault(x => x.Key == key && x.RoomId == roomId);

    private static SendOutcome Outcome(Outgoing o) =>
        new(o.TempId, o.RoomId, o.Key, o.State, o.Error, o.LocalPreviewUrls);

    private void Settle(Outgoing o, SendState state, string? error)
    {
        o.State = state;
        o.Error = error;
        PersistOutbox();
        SendSettled?.Invoke(Outcome(o));
        StateChanged?.Invoke();
    }

    /// The server has the message. The bubble stays (as pending) until the real row replaces it.
    private void Confirm(Outgoing o, Guid serverId)
    {
        o.ServerId = serverId;
        if (o.State == SendState.Confirmed) return;
        o.State = SendState.Confirmed;
        o.Error = null;
        PersistOutbox();
        SendSettled?.Invoke(Outcome(o));
    }

    private Outgoing AddOptimistic(Guid roomId, string content, Guid key, Guid? replyToId, string? type,
        IReadOnlyList<Guid>? attachmentIds, IReadOnlyList<AttachmentDto>? attachments, IReadOnlyList<string>? localPreviewUrls, bool linkPreviewOff = false)
    {
        var tempId = Guid.NewGuid();
        var replyPreview = replyToId is null || roomId != ActiveRoomId
            ? null
            : ActiveMessages.FirstOrDefault(m => m.Id == replyToId) is { } src
                ? new ReplyPreviewDto(src.SenderName, ReplyQuote.Text(src.Type, src.Content, src.IsDeleted))
                : null;
        var bubble = new MessageDto(
            tempId, roomId, Guid.Empty,
            _auth.Me?.Name ?? "You", _auth.Me?.Nik ?? "",
            type ?? "text", content, DateTime.UtcNow, null, false, null,
            replyToId, replyPreview, null, 0, null,
            attachments ?? [], [], [], false, key, LinkPreviewOff: linkPreviewOff);
        var o = new Outgoing
        {
            TempId = tempId, Key = key, RoomId = roomId, Content = content, ReplyToId = replyToId, Type = type,
            AttachmentIds = attachmentIds, LocalPreviewUrls = localPreviewUrls, LinkPreviewOff = linkPreviewOff, Bubble = bubble,
            Seq = ++_outboxSeq, State = SendState.Pending,
        };
        _outbox[tempId] = o;
        PersistOutbox();

        if (roomId == ActiveRoomId)
        {
            _fresh.Add(tempId);
            ActiveMessages.Add(bubble);
            StateChanged?.Invoke();
        }
        return o;
    }

    /// Server history for a room merged with this room's unconfirmed outbox bubbles: any outbox
    /// entry whose key (or confirmed id) is in the page is confirmed and dropped; the rest stay,
    /// appended in send order. Used for page 1, cached paints and uncached opens.
    internal List<MessageDto> ReconcileOptimisticHistory(Guid roomId, IReadOnlyList<MessageDto> serverItems)
    {
        var list = serverItems.ToList();
        var ids = list.Select(m => m.Id).ToHashSet();
        var keys = list.Where(m => m.RoomId == roomId).Select(m => m.IdempotencyKey).ToHashSet();
        foreach (var o in _outbox.Values.Where(o => o.RoomId == roomId).OrderBy(o => o.Seq).ToList())
        {
            var serverId = o.ServerId is { } sid && ids.Contains(sid) ? sid
                : keys.Contains(o.Key) ? list.First(m => m.IdempotencyKey == o.Key && m.RoomId == roomId).Id
                : (Guid?)null;
            if (serverId is { } real)
            {
                Confirm(o, real);
                _outbox.Remove(o.TempId);
                continue;
            }
            list.Add(o.Bubble);
        }
        return list;
    }

    /// Finds the optimistic entry `real` is the server echo of: by confirmed id, or by our own
    /// idempotency key in the same room. Never by content.
    private Outgoing? MatchOutgoing(MessageDto real) =>
        _outbox.Values.FirstOrDefault(o => o.ServerId == real.Id)
        ?? _outbox.Values.FirstOrDefault(o => o.RoomId == real.RoomId && o.Key == real.IdempotencyKey);

    /// The room broadcast of one of our messages: confirm it and swap the bubble for the real row.
    /// Returns true when it was ours (the caller then skips the "incoming message" path).
    internal bool ReconcileEcho(MessageDto message)
    {
        var mine = MatchOutgoing(message);
        if (mine is null) return false;
        Confirm(mine, message.Id);
        _outbox.Remove(mine.TempId);
        if (message.RoomId == ActiveRoomId)
        {
            var real = message with { DeliveryStatus = message.DeliveryStatus ?? "sent" };
            var ti = ActiveMessages.FindIndex(m => m.Id == mine.TempId);
            var already = ActiveMessages.Any(m => m.Id == message.Id);
            if (ti >= 0 && !already) ActiveMessages[ti] = real;
            else if (ti >= 0) ActiveMessages.RemoveAt(ti);
            else if (!already) ActiveMessages.Add(real);
        }
        return true;
    }

    internal void OnMessageAck(MessageAckDto ack)
    {
        var entry = _outbox.Values.FirstOrDefault(o => o.Key == ack.IdempotencyKey);
        if (entry is not null)
        {
            Confirm(entry, ack.MessageId);
            // The room broadcast already delivered the real row: the bubble is a duplicate.
            if (ActiveMessages.Any(m => m.Id == ack.MessageId)) RemoveOptimistic(entry.TempId);
        }
        StateChanged?.Invoke();
    }

    private void RemoveOptimistic(Guid tempId)
    {
        if (_outbox.Remove(tempId)) PersistOutbox();
        var idx = ActiveMessages.FindIndex(m => m.Id == tempId);
        if (idx >= 0) ActiveMessages.RemoveAt(idx);
    }

    /// Sends everything queued while offline, oldest first. An entry leaves the queue only when
    /// its own attempt is answered; if the connection drops again the rest stay queued.
    /// Unknown (no answer: timeout, dropped connection) is resent too: the same idempotency key
    /// makes the server return the existing message instead of sending it twice. Failed (the
    /// server refused) waits for a manual Retry.
    private async Task DrainOfflineQueueAsync()
    {
        foreach (var o in _outbox.Values.Where(o => o.State is SendState.Queued or SendState.Unknown).OrderBy(o => o.Seq).ToList())
        {
            if (_hub?.State != HubConnectionState.Connected) break;
            if (!_outbox.ContainsKey(o.TempId) || o.State is not (SendState.Queued or SendState.Unknown)) continue;
            await SendCoreAsync(o);
        }
        StateChanged?.Invoke();
    }

    // ── Persistence: unsent messages survive a reload or closed tab (per account, 7 days) ──

    internal static readonly TimeSpan OutboxRetention = TimeSpan.FromDays(7);
    internal const string OutboxKeyPrefix = "ipop.chat.outbox.";
    /// The account whose stored outbox this tab has read back and now owns.
    private string? _outboxOwner;

    internal sealed record StoredOutgoing(Guid TempId, Guid Key, Guid RoomId, string Content, Guid? ReplyToId, string? Type,
        IReadOnlyList<Guid>? AttachmentIds, IReadOnlyList<AttachmentDto>? Attachments, bool LinkPreviewOff,
        SendState State, string? Error, DateTime CreatedAt);

    /// Entries the server has not confirmed yet, oldest first. Confirmed ones are the server's now.
    internal List<StoredOutgoing> SnapshotOutbox() => _outbox.Values
        .Where(o => o.State != SendState.Confirmed)
        .OrderBy(o => o.Seq)
        .Select(o => new StoredOutgoing(o.TempId, o.Key, o.RoomId, o.Content, o.ReplyToId, o.Type, o.AttachmentIds,
            o.Bubble.Attachments, o.LinkPreviewOff, o.State, o.Error, o.CreatedAt))
        .ToList();

    private void PersistOutbox()
    {
        // Not before this account's stored entries were read back, or an early write would wipe them.
        if (_js is null || _outboxOwner is not { } nik || _auth.Me?.Nik != nik) return;
        var items = SnapshotOutbox();
        var key = OutboxKeyPrefix + nik;
        _ = (items.Count == 0
                ? _js.InvokeVoidAsync("localStorage.removeItem", key)
                : _js.InvokeVoidAsync("localStorage.setItem", key, System.Text.Json.JsonSerializer.Serialize(items)))
            .AsTask().ContinueWith(t => _ = t.Exception, TaskScheduler.Default);
    }

    /// Reads this account's unsent messages back (once per account) so the caller's drain
    /// resends them; anything past the retention window is dropped.
    private async Task RestoreOutboxAsync()
    {
        if (_js is null || _auth.Me?.Nik is not { Length: > 0 } nik || _outboxOwner == nik) return;
        string? json = null;
        try { json = await _js.InvokeAsync<string?>("localStorage.getItem", OutboxKeyPrefix + nik); }
        catch { }
        if (_auth.Me?.Nik != nik) return;
        _outboxOwner = nik;
        List<StoredOutgoing>? stored = null;
        try { if (!string.IsNullOrEmpty(json)) stored = System.Text.Json.JsonSerializer.Deserialize<List<StoredOutgoing>>(json); }
        catch { }
        RestoreOutbox(stored ?? [], DateTime.UtcNow);
        PersistOutbox();
        StateChanged?.Invoke();
    }

    internal void RestoreOutbox(IEnumerable<StoredOutgoing> stored, DateTime now)
    {
        var known = _outbox.Values.Select(o => (o.RoomId, o.Key)).ToHashSet();
        foreach (var s in stored)
        {
            if (now - s.CreatedAt > OutboxRetention || !known.Add((s.RoomId, s.Key))) continue;
            var bubble = new MessageDto(
                s.TempId, s.RoomId, Guid.Empty, _auth.Me?.Name ?? "You", _auth.Me?.Nik ?? "",
                s.Type ?? "text", s.Content, s.CreatedAt, null, false, null,
                s.ReplyToId, null, null, 0, null,
                s.Attachments ?? [], [], [], false, s.Key, LinkPreviewOff: s.LinkPreviewOff);
            _outbox[s.TempId] = new Outgoing
            {
                TempId = s.TempId, Key = s.Key, RoomId = s.RoomId, Content = s.Content, ReplyToId = s.ReplyToId, Type = s.Type,
                AttachmentIds = s.AttachmentIds, LinkPreviewOff = s.LinkPreviewOff, Bubble = bubble,
                Seq = ++_outboxSeq, CreatedAt = s.CreatedAt,
                // A refusal stays a refusal (manual Retry); anything else is resent on this connect.
                State = s.State == SendState.Failed ? SendState.Failed : SendState.Unknown,
                Error = s.State == SendState.Failed ? s.Error : null,
            };
            if (s.RoomId == ActiveRoomId && ActiveMessages.All(m => m.Id != s.TempId)) ActiveMessages.Add(bubble);
        }
    }

    /// The room is gone for this user (403/404): nothing of it may linger or be resent.
    private void ForgetOutboxFor(Guid roomId)
    {
        foreach (var k in _outbox.Where(kv => kv.Value.RoomId == roomId).Select(kv => kv.Key).ToList())
            RemoveOptimistic(k);
    }

    /// Sign-out: unsent message bodies belong to the account that wrote them.
    private void ClearOutbox()
    {
        _receivedMessageIds.Clear();
        _receivedMessageOrder.Clear();
        foreach (var k in _outbox.Keys.ToList()) RemoveOptimistic(k);
        // Me is already gone at sign-out, so wipe the owner's stored copy by name.
        if (_js is not null && _outboxOwner is { } owner)
            _ = _js.InvokeVoidAsync("localStorage.removeItem", OutboxKeyPrefix + owner).AsTask()
                .ContinueWith(t => _ = t.Exception, TaskScheduler.Default);
        _outboxOwner = null;
        _outboxSeq = 0;
    }
}
