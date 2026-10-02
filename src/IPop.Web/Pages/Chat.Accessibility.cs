// Chat page — keyboard accessibility and the polite live status region.
//
// Part of the Chat partial class. Owns:
//  - js/chat-accessibility.js (ES module): roving tabindex over message rows, Arrow/Home/End/
//    PageUp/PageDown between rows, Shift+F10 / Menu key opening the existing message menu
//    anchored to the bubble rect, menu focus + arrow keys, focus restore when the menu closes.
//  - ChatLiveAnnouncer: concise, coalesced "new message from X" / "failed to send" / "X is
//    typing" text for a visually hidden role="status" region. Never message content.
// _Imports.razor only reaches .razor files, so usings are restated in each part.
using IPop.Application.Chat;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;

namespace IPop.Web.Pages;

public partial class Chat
{
    private const string A11yModulePath = "./js/chat-accessibility.js?v=20261002-a11y";

    private IJSObjectReference? a11yModule;
    private Task<IJSObjectReference>? a11yModuleLoad;
    private string? a11yBoundListId;
    private readonly ChatLiveAnnouncer a11yLive = new(() => DateTime.UtcNow);

    /// Message id whose context menu was opened from the keyboard (Shift+F10, Menu key, or
    /// Enter/Space on the row's "More" button). Drives menu focus and the focus restore.
    private Guid? a11yKbdCtxFor;
    private bool a11yFocusMenuPending;
    /// True when the menu came from the "More" button: its click carries no usable
    /// coordinates, so the JS side anchors it to the bubble after render.
    private bool a11yAnchorPending;

    /// Text of the polite live region. Rendered by Chat.razor (see A11yLiveText usage).
    private string A11yLiveText => a11yLive.Text;

    // ---- Rendering helpers ----

    /// Concise accessible name for a message row: sender, time, delivery problem. Never content
    /// (private; the bubble stays readable in browse mode). Empty for system rows.
    private string A11yRowLabel(MessageDto msg) =>
        ChatA11yLabels.RowLabel(msg, msg.SenderNik == GetCurrentUserNik(),
            ChatSvc.IsPending(msg.Id), ChatSvc.IsFailed(msg.Id));

    // ---- Keyboard context menu ----

    /// Shift+F10 / Menu key on a focused message row (or a button inside it). x/y is the bubble
    /// anchor computed from its client rect by chat-accessibility.js.
    [JSInvokable]
    public Task OnA11yContextKey(string msgId, double x, double y) => InvokeAsync(() =>
    {
        if (!Guid.TryParse(msgId, out var id)) return;
        if (ChatSvc.ActiveMessages.All(m => m.Id != id)) return;
        ctxMsgId = id;
        ctxX = x;
        ctxY = y;
        ctxNeedsPlacement = true;
        a11yKbdCtxFor = id;
        a11yFocusMenuPending = true;
        a11yAnchorPending = false;
        StateHasChanged();
    });

    /// Escape inside a keyboard-opened message menu. chat-accessibility.js catches it in the
    /// capture phase (before the layout's global Escape) and calls here, so one key closes the
    /// menu and its backdrop and nothing else. The shared 200 ms chat Escape guard is stamped
    /// so a racing layout Escape cannot peel a second layer. Focus returns to the row in
    /// A11yAfterRenderAsync (a11yKbdCtxFor is still set, ctxMsgId is now null).
    [JSInvokable]
    public Task OnA11yMenuEscape() => InvokeAsync(() =>
    {
        if (_disposed || ctxMsgId is null) return;
        TryBeginChatEscape();
        CloseCtx();
        StateHasChanged();
    });

    /// Drop-in for ShowCtx on the row's "More" button. A keyboard activation (Enter/Space) of a
    /// button produces a click with Detail 0 at 0,0: anchor to the bubble and move focus into the
    /// menu. Real pointer clicks behave exactly like ShowCtx.
    private void ShowCtxFromButton(Guid msgId, MouseEventArgs e)
    {
        ShowCtx(msgId, e);
        if (ChatA11yLabels.IsKeyboardClick(e))
        {
            a11yKbdCtxFor = msgId;
            a11yFocusMenuPending = true;
            a11yAnchorPending = true;
        }
        else
        {
            a11yKbdCtxFor = null;
        }
    }

    /// Pointer-opened menus must not steal focus or trigger a restore.
    private void A11yPointerCtx() => a11yKbdCtxFor = null;

    // ---- Live announcements ----

    /// Call from OnMessageReceived. Live hub messages only; history loads never raise
    /// MessageReceived, and the announcer stays quiet while the room is (re)loading.
    private void AnnounceIncoming(MessageDto msg)
    {
        if (msg.RoomId != ChatSvc.ActiveRoomId) return;
        var own = string.Equals(msg.SenderNik, GetCurrentUserNik(), StringComparison.OrdinalIgnoreCase);
        a11yLive.Incoming(msg, own, quiet: ChatSvc.IsLoadingMessages || openingRoom);
    }

    /// Call from OnStateChanged (which also receives TypingChanged): announces messages that just
    /// failed to send and the start of typing. Cheap: scans only the newest rows.
    private void A11yOnStateChanged()
    {
        var room = ChatSvc.ActiveRoomId;
        a11yLive.SetRoom(room);
        if (room is null) return;
        var msgs = ChatSvc.ActiveMessages;
        var failed = new List<Guid>();
        for (var i = msgs.Count - 1; i >= 0 && i >= msgs.Count - ChatLiveAnnouncer.FailureScanDepth; i--)
            if (ChatSvc.IsFailed(msgs[i].Id)) failed.Add(msgs[i].Id);
        a11yLive.Failures(failed, quiet: ChatSvc.IsLoadingMessages || openingRoom);
        a11yLive.Typing(ChatSvc.TypingUsers.ToList(), quiet: ChatSvc.IsLoadingMessages || openingRoom);
    }

    // ---- Lifecycle ----

    /// Call at the END of OnAfterRenderAsync (after the composer/edit focus and the existing
    /// ipop.placeContextMenu block), so a deliberate focus move wins over the restore.
    private async Task A11yAfterRenderAsync(bool paneReady)
    {
        if (_disposed) return;
        if (!paneReady) a11yBoundListId = null;
        if (paneReady && messagesRef.Id is { } listId && listId != a11yBoundListId)
        {
            var module = await A11yModuleAsync();
            if (module is null || _disposed) return;
            try
            {
                selfRef ??= DotNetObjectReference.Create(this);
                await module.InvokeVoidAsync("bind", messagesRef, selfRef);
                a11yBoundListId = listId;
            }
            catch (JSException) { }
            catch (JSDisconnectedException) { }
            catch (TaskCanceledException) { }
        }

        if (a11yKbdCtxFor is not { } kbdFor) return;
        var module2 = a11yModule;
        if (module2 is null) return;
        try
        {
            if (ctxMsgId == kbdFor)
            {
                if (a11yAnchorPending)
                {
                    a11yAnchorPending = false;
                    await module2.InvokeVoidAsync("placeAtRow", messagesRef, ctxMenuRef, kbdFor.ToString());
                }
                if (a11yFocusMenuPending)
                {
                    a11yFocusMenuPending = false;
                    selfRef ??= DotNetObjectReference.Create(this);
                    await module2.InvokeAsync<bool>("focusMenu", ctxMenuRef, selfRef);
                }
            }
            else
            {
                // Menu closed (Escape, backdrop, an action) or retargeted by a pointer. Restore
                // only on close, and only if focus fell to <body>; the composer, an inline
                // editor or a dialog keep theirs.
                a11yKbdCtxFor = null;
                a11yFocusMenuPending = a11yAnchorPending = false;
                if (ctxMsgId is null)
                    await module2.InvokeAsync<bool>("restoreRow", messagesRef, kbdFor.ToString());
            }
        }
        catch (JSException) { }
        catch (JSDisconnectedException) { }
        catch (TaskCanceledException) { }
    }

    private async Task<IJSObjectReference?> A11yModuleAsync()
    {
        if (a11yModule is not null) return a11yModule;
        try
        {
            a11yModuleLoad ??= JS.InvokeAsync<IJSObjectReference>("import", A11yModulePath).AsTask();
            a11yModule = await a11yModuleLoad;
            if (_disposed) { await A11yDisposeModuleAsync(); return null; }
            return a11yModule;
        }
        catch
        {
            // Older cached shell, offline, or prerender: keyboard extras simply stay off.
            a11yModuleLoad = null;
            return null;
        }
    }

    /// Call from Dispose() after setting _disposed. Fire-and-forget; never throws.
    private void A11yDispose()
    {
        _ = A11yDisposeModuleAsync();
    }

    private async Task A11yDisposeModuleAsync()
    {
        var module = a11yModule;
        a11yModule = null;
        a11yBoundListId = null;
        if (module is null) return;
        try { await module.InvokeVoidAsync("unbind", messagesRef); } catch { }
        try { await module.DisposeAsync(); } catch { }
    }
}

/// Pure label helpers (tested without a renderer).
internal static class ChatA11yLabels
{
    public static string RowLabel(MessageDto msg, bool isSent, bool isPending, bool isFailed)
    {
        if (msg.Type == "system") return "";
        var who = isSent ? "You" : (string.IsNullOrWhiteSpace(msg.SenderName) ? "Someone" : msg.SenderName.Trim());
        var label = $"{who}, {msg.CreatedAt.ToLocalTime():HH:mm}";
        if (msg.Type == "emergency") label = "Emergency, " + label;
        if (msg.IsDeleted) label += ", deleted";
        else if (isFailed) label += ", failed to send";
        else if (isPending) label += ", sending";
        return label;
    }

    /// A click synthesized by Enter/Space on a focused button: no click count, no coordinates.
    public static bool IsKeyboardClick(MouseEventArgs e) =>
        e.Detail == 0 && e.ClientX == 0 && e.ClientY == 0 && e.ScreenX == 0 && e.ScreenY == 0;
}

/// Builds the polite live-region text. Rules:
///  - never message content; sender names and counts only;
///  - incoming messages in a short window coalesce ("3 new messages from Budi");
///  - quiet while history loads and for a moment after a room opens (no backlog flood);
///  - a failure is announced once per message;
///  - typing is announced when it starts (or the typists change), at most once per cooldown,
///    and never over a fresh message announcement.
internal sealed class ChatLiveAnnouncer(Func<DateTime> now)
{
    public static readonly TimeSpan CoalesceWindow = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan RoomSettle = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan TypingCooldown = TimeSpan.FromSeconds(15);
    public const int FailureScanDepth = 50;
    private const string Toggle = " ";

    private Guid? room;
    private DateTime roomSince = DateTime.MinValue;
    private DateTime lastIncomingAt = DateTime.MinValue;
    private DateTime lastAnnounceAt = DateTime.MinValue;
    private DateTime lastTypingAt = DateTime.MinValue;
    private int burstCount;
    private readonly HashSet<string> burstSenders = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<Guid> knownFailed = [];
    private string typingKey = "";
    private bool flip;

    public string Text { get; private set; } = "";

    /// Room switch resets bursts, failure baseline and typing; also starts the settle window.
    public void SetRoom(Guid? roomId)
    {
        if (roomId == room) return;
        room = roomId;
        roomSince = now();
        burstCount = 0;
        burstSenders.Clear();
        knownFailed.Clear();
        typingKey = "";
        Text = "";
    }

    public void Incoming(MessageDto msg, bool own, bool quiet)
    {
        SetRoom(msg.RoomId);
        if (own || msg.Type == "system" || msg.IsDeleted) return;
        var t = now();
        if (quiet || t - roomSince < RoomSettle) return;

        if (t - lastIncomingAt > CoalesceWindow) { burstCount = 0; burstSenders.Clear(); }
        lastIncomingAt = t;
        burstCount++;
        burstSenders.Add(string.IsNullOrWhiteSpace(msg.SenderName) ? "someone" : msg.SenderName.Trim());

        string text;
        if (burstCount == 1)
            text = msg.Type == "emergency" ? $"Emergency message from {burstSenders.First()}" : $"New message from {burstSenders.First()}";
        else if (burstSenders.Count == 1)
            text = $"{burstCount} new messages from {burstSenders.First()}";
        else
            text = $"{burstCount} new messages";
        Set(text, t);
    }

    public void Failures(IReadOnlyCollection<Guid> failedIds, bool quiet)
    {
        var fresh = failedIds.Where(id => !knownFailed.Contains(id)).ToList();
        // Retried messages leave the failed set; forget them so a second failure speaks again.
        knownFailed.IntersectWith(failedIds);
        foreach (var id in fresh) knownFailed.Add(id);
        if (fresh.Count == 0 || quiet) return;
        Set(fresh.Count == 1 ? "Message failed to send. Retry is available." : $"{fresh.Count} messages failed to send. Retry is available.", now());
    }

    public void Typing(IReadOnlyList<string> users, bool quiet)
    {
        var key = string.Join("\u001F", users.OrderBy(u => u, StringComparer.OrdinalIgnoreCase));
        var changed = key != typingKey;
        typingKey = key;
        if (!changed || users.Count == 0 || quiet) return;
        var t = now();
        if (t - roomSince < RoomSettle) return;
        if (t - lastTypingAt < TypingCooldown) return;
        if (t - lastAnnounceAt < TimeSpan.FromSeconds(2)) return;
        lastTypingAt = t;
        Set(users.Count switch
        {
            1 => $"{users[0]} is typing",
            2 => $"{users[0]} and {users[1]} are typing",
            _ => $"{users.Count} people are typing"
        }, t);
    }

    private void Set(string text, DateTime t)
    {
        // Identical consecutive text would not be re-read: alternate a trailing no-break space.
        flip = !flip;
        Text = flip ? text : text + Toggle;
        lastAnnounceAt = t;
    }
}
