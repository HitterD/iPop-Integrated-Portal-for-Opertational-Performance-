// Chat page — the v6 right-hand drawer for the four global lists (Pinned / Saved /
// Search all conversations / Unread mentions).
//
// These used to be separate routes (/chat/pins …). The mockup keeps the conversation
// on screen and slides the list in from the right instead, so the shortcuts now switch
// local drawer state rather than navigating. The old routes stay alive for deep links.
//
// Part of the Chat partial class; _Imports.razor only reaches .razor files, so usings
// are restated here.
using IPop.Application.Chat;
using IPop.Web.Services;

namespace IPop.Web.Pages;

public partial class Chat
{
    public enum GlobalDrawerView { Pinned, Saved, Search, Mentions }

    private GlobalDrawerView? globalDrawerView;
    private bool globalDrawerLoading;
    private string? globalDrawerError;
    private List<GlobalDrawerCard> globalDrawerCards = [];

    /// Guards against a slow response from a panel the user already switched away from
    /// overwriting the list they are looking at now.
    private int globalDrawerGeneration;

    /// Exact mention count once the panel has been opened; before that the badge falls
    /// back to counting the rooms flagged unread (never the room's total unread count,
    /// which is a different number entirely).
    private int? globalMentionCount;

    private int GlobalMentionCount => globalMentionCount
        ?? ChatSvc.GroupRooms.Concat(ChatSvc.DirectRooms)
            .Count(r => r.HasUnreadMention || ChatSvc.HasUnreadMention(r.Id));

    // Search is the only panel with an input; its results replace the card list in place.
    private string globalSearchQuery = "";
    private bool globalSearching;

    private sealed record GlobalDrawerCard(
        Guid RoomId, Guid MessageId, string SenderName, string Text, DateTime When,
        string RoomLabel, string Initials, string Color,
        /// "Unpin" | "Remove" | null — null cards have no inline action (search/mentions).
        string? ActionLabel);

    private string GlobalDrawerTitle => globalDrawerView switch
    {
        GlobalDrawerView.Pinned => "Pinned messages",
        GlobalDrawerView.Saved => "Saved messages",
        GlobalDrawerView.Search => "Search all conversations",
        GlobalDrawerView.Mentions => "Unread mentions",
        _ => ""
    };

    private string GlobalDrawerIcon => globalDrawerView switch
    {
        GlobalDrawerView.Pinned => "pin",
        GlobalDrawerView.Saved => "bookmark",
        GlobalDrawerView.Search => "text-search",
        GlobalDrawerView.Mentions => "at-sign",
        _ => "pin"
    };

    private string GlobalDrawerEmptyText => globalDrawerView switch
    {
        GlobalDrawerView.Pinned => "No pinned messages yet. Hover a message and tap the pin.",
        GlobalDrawerView.Saved => "Saved messages appear here. Use More › Save message.",
        GlobalDrawerView.Search => globalSearchQuery.Trim().Length >= 2
            ? $"No messages match “{globalSearchQuery.Trim()}”."
            : "Type at least two characters to search every conversation.",
        GlobalDrawerView.Mentions => "You are all caught up.",
        _ => ""
    };

    private string GlobalDrawerHint
    {
        get
        {
            if (globalDrawerLoading) return "loading…";
            var n = globalDrawerCards.Count;
            return globalDrawerView switch
            {
                GlobalDrawerView.Pinned => "", // PinnedPanel shows its own count
                GlobalDrawerView.Saved => $"{n} saved",
                GlobalDrawerView.Search => globalSearchQuery.Trim().Length >= 2 ? $"{n} results" : "",
                GlobalDrawerView.Mentions => $"{n} mention{(n == 1 ? "" : "s")}",
                _ => ""
            };
        }
    }

    /// Opens the panel, or closes it when the same shortcut is tapped again. Toggling
    /// the room-detail panel shut keeps exactly one drawer on screen at a time.
    private async Task ToggleGlobalDrawer(GlobalDrawerView view)
    {
        if (globalDrawerView == view)
        {
            CloseGlobalDrawer();
            return;
        }
        if (drawerOpen) CloseDetail();
        globalDrawerView = view;
        // Pinned renders the shared PinnedPanel, which loads its own lists.
        if (view == GlobalDrawerView.Pinned)
        {
            globalDrawerCards = [];
            globalDrawerError = null;
            globalDrawerLoading = false;
            return;
        }
        // Search opens empty and waits for the user to type; the other three load now.
        if (view != GlobalDrawerView.Search || globalSearchQuery.Trim().Length >= 2)
            await LoadGlobalDrawerAsync();
        else
            globalDrawerCards = [];
    }

    private void CloseGlobalDrawer()
    {
        globalDrawerView = null;
        globalDrawerError = null;
        globalDrawerCards = [];
        globalSearchQuery = "";
        globalSearching = false;
        // Invalidate in-flight loads so a late response cannot repopulate a closed panel.
        globalDrawerGeneration++;
    }

    private async Task LoadGlobalDrawerAsync()
    {
        if (globalDrawerView is null) return;
        var view = globalDrawerView.Value;
        var generation = ++globalDrawerGeneration;
        globalDrawerLoading = true;
        globalDrawerError = null;
        StateHasChanged();

        try
        {
            List<GlobalDrawerCard> cards = view switch
            {
                GlobalDrawerView.Pinned => await LoadGlobalPinnedAsync(),
                GlobalDrawerView.Saved => await LoadGlobalSavedAsync(),
                GlobalDrawerView.Mentions => await LoadGlobalMentionsAsync(),
                GlobalDrawerView.Search => await LoadGlobalSearchAsync(),
                _ => []
            };
            if (generation != globalDrawerGeneration) return;
            globalDrawerCards = cards;
            if (view == GlobalDrawerView.Mentions) globalMentionCount = cards.Count;
        }
        catch
        {
            if (generation != globalDrawerGeneration) return;
            globalDrawerCards = [];
            globalDrawerError = "Could not load this list. Try again.";
        }
        finally
        {
            if (generation == globalDrawerGeneration)
            {
                globalDrawerLoading = false;
                StateHasChanged();
            }
        }
    }

    /// Pins are per-room on the server, so the global list fans out over the user's rooms.
    private async Task<List<GlobalDrawerCard>> LoadGlobalPinnedAsync()
    {
        await ChatSvc.LoadRoomsAsync();
        var rooms = ChatSvc.GroupRooms.Concat(ChatSvc.DirectRooms).ToList();
        var results = await Task.WhenAll(rooms.Select(async r =>
        {
            try
            {
                var pins = await Auth.GetAsync<List<PinnedMessageDto>>($"/api/chat/rooms/{r.Id}/pins");
                return (pins ?? []).Select(p => Card(r.Id, p.MessageId, p.SenderName, p.Content, p.PinnedAt, "Unpin"));
            }
            catch
            {
                return Enumerable.Empty<GlobalDrawerCard>();
            }
        }));
        return results.SelectMany(x => x)
                      .OrderByDescending(c => c.When)
                      .ToList();
    }

    private async Task<List<GlobalDrawerCard>> LoadGlobalSavedAsync()
    {
        await ChatSvc.LoadRoomsAsync();
        var rows = await Auth.GetAsync<List<SavedMessageDto>>("/api/chat/saved");
        return (rows ?? [])
            .Select(s => Card(s.RoomId, s.MessageId, s.SenderName, s.Content, s.SavedAt, "Remove"))
            .OrderByDescending(c => c.When)
            .ToList();
    }

    private async Task<List<GlobalDrawerCard>> LoadGlobalMentionsAsync()
    {
        await ChatSvc.LoadRoomsAsync();
        var myNik = Auth.Me?.Nik;
        try
        {
            var page = await Auth.GetAsync<PagedResult<MessageDto>>("/api/chat/mentions/unread?page=1&pageSize=50");
            return (page?.Items ?? [])
                .Select(m => Card(m.RoomId, m.Id, m.SenderName, m.Content, m.CreatedAt, null))
                .OrderByDescending(c => c.When)
                .ToList();
        }
        catch
        {
            // Older server builds may not expose the mentions endpoint; fall back to the
            // rooms flagged unread and filter their first page client-side.
            var flagged = ChatSvc.GroupRooms.Concat(ChatSvc.DirectRooms)
                .Where(r => r.HasUnreadMention || ChatSvc.HasUnreadMention(r.Id))
                .ToList();
            var results = await Task.WhenAll(flagged.Select(async r =>
            {
                try
                {
                    var page = await Auth.GetAsync<PagedResult<MessageDto>>($"/api/chat/rooms/{r.Id}/messages?page=1&pageSize=50");
                    return (page?.Items ?? (IReadOnlyList<MessageDto>)[])
                        .Where(m => !m.IsDeleted && m.Type != "system" &&
                                    (m.MentionsAll || (myNik != null && m.MentionedNiks.Contains(myNik, StringComparer.OrdinalIgnoreCase))))
                        .Select(m => Card(m.RoomId, m.Id, m.SenderName, m.Content, m.CreatedAt, null));
                }
                catch
                {
                    return Enumerable.Empty<GlobalDrawerCard>();
                }
            }));
            return results.SelectMany(x => x).OrderByDescending(c => c.When).ToList();
        }
    }

    private async Task<List<GlobalDrawerCard>> LoadGlobalSearchAsync()
    {
        var q = globalSearchQuery.Trim();
        if (q.Length < 2) return [];
        await ChatSvc.LoadRoomsAsync();
        var enc = Uri.EscapeDataString(q);
        var page = await Auth.GetAsync<PagedResult<MessageDto>>($"/api/chat/search?q={enc}&pageSize=50");
        return (page?.Items ?? [])
            .Select(m => Card(m.RoomId, m.Id, m.SenderName, m.Content, m.CreatedAt, null))
            .OrderByDescending(c => c.When)
            .ToList();
    }

    private GlobalDrawerCard Card(Guid roomId, Guid messageId, string senderName, string? content, DateTime when, string? actionLabel)
    {
        var label = GlobalRoomLabel(roomId);
        return new GlobalDrawerCard(
            roomId, messageId, senderName,
            string.IsNullOrWhiteSpace(content) ? "(attachment)" : Snippet(content),
            when, label, GetInitialsFromName(label), IPop.Web.Models.AvatarPalette.For(label), actionLabel);
    }

    private string GlobalRoomLabel(Guid roomId)
    {
        var r = ChatSvc.GroupRooms.Concat(ChatSvc.DirectRooms).FirstOrDefault(x => x.Id == roomId);
        if (r is null) return "Conversation";
        return r.Type == "dm"
            ? (r.RecipientName ?? r.RecipientNik ?? "Direct message")
            : (r.Name ?? "Group");
    }

    private static string Snippet(string content)
    {
        var text = content.StartsWith("[forwarded]") ? content["[forwarded]".Length..].TrimStart() : content;
        return text.Length <= 160 ? text : text[..160] + "…";
    }

    /// Mirrors the mockup's search box: results refresh as you type, but only once the
    /// query is long enough to be worth a request.
    private async Task OnGlobalSearchInput()
    {
        if (globalDrawerView != GlobalDrawerView.Search) return;
        var q = globalSearchQuery.Trim();
        if (q.Length < 2)
        {
            globalSearching = false;
            globalDrawerCards = [];
            globalDrawerError = null;
            return;
        }
        globalSearching = true;
        StateHasChanged();
        try
        {
            await LoadGlobalDrawerAsync();
        }
        finally
        {
            globalSearching = false;
            StateHasChanged();
        }
    }

    /// Opening a card must survive the room switch: `OpenRoom` short-circuits when the
    /// target is already active, so that case jumps directly instead of re-opening.
    private async Task OpenGlobalCard(GlobalDrawerCard card)
    {
        // The mockup keeps the results panel up while searching, and closes the list
        // panels once the message they pointed at is on screen.
        if (globalDrawerView != GlobalDrawerView.Search) CloseGlobalDrawer();

        // Already open (or already opening): no room switch will run, so jump directly.
        if (card.RoomId == ChatSvc.ActiveRoomId || card.RoomId == openedRoomId)
        {
            await JumpToMessage(card.MessageId);
            return;
        }
        // Staged before the switch: OpenRoomCore consumes it once the room finishes opening,
        // so the scroll cannot race the navigation or get lost behind a superseding open.
        jumpAfterOpen = card.MessageId;
        OpenRoom(card.RoomId);
    }

    /// The message to scroll to once the pending room switch completes; consumed by
    /// OpenRoomCore, which owns the ?jump= path.
    private Guid? jumpAfterOpen;

    private async Task UnpinGlobalCard(GlobalDrawerCard card)
    {
        try
        {
            var res = await Auth.SendAsync(HttpMethod.Delete, $"/api/chat/rooms/{card.RoomId}/pins/{card.MessageId}");
            if (!res.IsSuccessStatusCode)
            {
                // Groups: only whoever pinned it or an owner/admin may unpin (403).
                Toasts.Show(res.StatusCode == System.Net.HttpStatusCode.Forbidden
                    ? "Only whoever pinned it or a group admin can unpin this message."
                    : "Could not unpin that message.", ToastKind.Error);
                return;
            }
            // The "X unpinned a message" row shows even if this tab's hub is down. Applying raises
            // PinnedChanged, which already reloads the open room's pins; otherwise keep the open
            // room's own pin list in sync when the card came from there.
            var applied = await ChatSvc.ApplyPinResponseAsync(res, card.MessageId, isPinned: false);
            if (!applied && card.RoomId == ChatSvc.ActiveRoomId) await LoadPinnedMessagesAsync();
            globalDrawerCards = globalDrawerCards.Where(c => c.MessageId != card.MessageId).ToList();
            StateHasChanged();
        }
        catch
        {
            Toasts.Show("Could not unpin that message.", ToastKind.Error);
        }
    }

    private async Task RemoveGlobalSavedCard(GlobalDrawerCard card)
    {
        try
        {
            var res = await Auth.SendAsync(HttpMethod.Delete, $"/api/chat/saved/{card.MessageId}");
            if (!res.IsSuccessStatusCode)
            {
                Toasts.Show("Could not remove that saved message.", ToastKind.Error);
                return;
            }
            globalDrawerCards = globalDrawerCards.Where(c => c.MessageId != card.MessageId).ToList();
            StateHasChanged();
        }
        catch
        {
            Toasts.Show("Could not remove that saved message.", ToastKind.Error);
        }
    }

    /// Enter/Space activate a card, matching what a real button would do for keyboard users.
    private Task OnGlobalCardKey(GlobalDrawerCard card, Microsoft.AspNetCore.Components.Web.KeyboardEventArgs e)
    {
        // KeyboardEventArgs has no PreventDefault; the card is a plain div, so Space may
        // also scroll the list by a line — the activation below still runs.
        if (e.Key is not ("Enter" or " ")) return Task.CompletedTask;
        return OpenGlobalCard(card);
    }

    private Task GlobalCardAction(GlobalDrawerCard card)
        => card.ActionLabel switch
        {
            "Remove" => RemoveGlobalSavedCard(card),
            "Unpin" => UnpinGlobalCard(card),
            _ => Task.CompletedTask
        };
}
