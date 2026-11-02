// Chat page — room pane: tabs, server-side search (ipop-52), paging, room open/navigation.
//
// Part of the Chat partial class. The class was split by responsibility out of a single
// ~1,500-line @code block; members were moved verbatim, never renamed or re-scoped.
// _Imports.razor only reaches .razor files, so usings are restated in each part.
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using IPop.Web.Components.Chat;

namespace IPop.Web.Pages;

public partial class Chat
{
    // ---- Room Pane ----

    /// Conversations with something unread (the chip counts chats, not messages).
    private int UnreadRoomCount => ChatSvc.DirectRooms.Concat(ChatSvc.GroupRooms).GroupBy(r => r.Id).Select(g => g.First()).Count(r => r.UnreadCount > 0 || r.HasUnreadMention);

    // ipop-52: search runs server-side (/rooms/search — name OR member name over ALL the
    // user's rooms); its results land in DirectRooms/GroupRooms so the render path is the
    // same getter as always. Empty query restores the normal paginated lists.
    private bool searchActive => !string.IsNullOrWhiteSpace(roomSearchQuery);
    private CancellationTokenSource? roomSearchCts;

    /// All = direct and group chats together, newest activity first (pinned ones are split
    /// out by the markup). Unread and Groups narrow that same list.
    private IReadOnlyList<IPop.Application.Chat.RoomListDto> FilteredRooms
    {
        get
        {
            IEnumerable<IPop.Application.Chat.RoomListDto> rooms = searchActive && ChatSvc.RoomSearchResults is { } found
                ? found
                : ChatSvc.DirectRooms.Concat(ChatSvc.GroupRooms);
            rooms = roomFilter switch
            {
                RoomFilter.Unread => rooms.Where(r => r.UnreadCount > 0 || r.HasUnreadMention),
                RoomFilter.Groups => rooms.Where(r => r.Type == "group"),
                _ => rooms
            };
            return rooms.GroupBy(r => r.Id).Select(g => g.First())
                .OrderByDescending(r => r.LastMessageAt ?? DateTime.MinValue).ToList();
        }
    }

    /// Leaves search everywhere the box empties (X, tab switch, "Chat now", leaving /chat):
    /// a pending debounce must not re-apply it, and ChatState must forget the query.
    private void LeaveRoomSearch()
    {
        roomSearchCts?.Cancel();
        roomSearchCts?.Dispose();
        roomSearchCts = null;
        roomSearchQuery = "";
        searchPeople = [];
        ChatSvc.ClearRoomSearch();
    }

    /// ipop-52: debounced (250ms, Mail/Admin pattern) server search. The old client-side
    /// filter could only see the loaded 50-room window, so conversations at position 51+
    /// returned "nothing" — indistinguishable from not existing.
    private async Task OnRoomSearchChanged(ChangeEventArgs e)
    {
        roomSearchQuery = e.Value?.ToString() ?? "";
        roomSearchCts?.Cancel();
        roomSearchCts?.Dispose();
        var t = new CancellationTokenSource();
        roomSearchCts = t;
        try { await Task.Delay(250, t.Token); }
        catch (OperationCanceledException) { return; }
        if (t.IsCancellationRequested) return;
        await Task.WhenAll(ChatSvc.SearchRoomsAsync(roomSearchQuery), SearchPeopleAsync(roomSearchQuery, t.Token));
    }

    // ---- "People" under the search results: colleagues you have no DM with yet ----

    private const int MaxSearchPeople = 8;
    private List<NewChatPerson> searchPeople = [];
    private Guid? startingSearchDm;

    /// Directory matches for the room search. Rooms are filtered later (PeopleWithoutDm) so
    /// a DM that shows up in the conversation results is never offered twice.
    private async Task SearchPeopleAsync(string query, CancellationToken ct)
    {
        var q = query.Trim();
        if (q.Length < 2) { searchPeople = []; return; }
        try
        {
            var rows = await Auth.GetAsync<List<NewChatPerson>>($"/api/chat/users/directory?q={Uri.EscapeDataString(q)}", ct);
            if (!ct.IsCancellationRequested) searchPeople = rows ?? [];
        }
        catch (OperationCanceledException) { }
        catch { searchPeople = []; }
    }

    private IReadOnlyList<NewChatPerson> PeopleWithoutDm
    {
        get
        {
            if (!searchActive || searchPeople.Count == 0) return [];
            var dmNiks = ChatSvc.DirectRooms.Concat(ChatSvc.RoomSearchResults ?? []).Concat(ChatSvc.ArchivedRooms)
                .Where(r => r.Type == "dm" && r.RecipientNik is not null)
                .Select(r => r.RecipientNik!)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            return searchPeople.Where(p => !dmNiks.Contains(p.Nik)).Take(MaxSearchPeople).ToList();
        }
    }

    /// "Chat now": opens (or creates — the endpoint is idempotent) the DM and leaves search.
    private async Task ChatNow(NewChatPerson p)
    {
        if (startingSearchDm is not null) return;
        startingSearchDm = p.Id;
        try
        {
            LeaveRoomSearch();
            await CreateDmWith(new UserSearchResult(p.Id, p.Nik, p.Name, p.Email));
        }
        finally { startingSearchDm = null; }
    }

    /// Thin wrappers for the load-more buttons (@onclick needs a handler; the paging
    /// logic lives on ChatState.LoadMoreDirectRoomsAsync / LoadMoreGroupRoomsAsync).
    private Task LoadMoreDirectRooms() => ChatSvc.LoadMoreDirectRoomsAsync();
    private Task LoadMoreGroupRooms() => ChatSvc.LoadMoreGroupRoomsAsync();

    /// Chips: All / Unread / Groups filter the list; Mentions opens its own drawer, so
    /// picking a list filter closes it again.
    private void SetFilter(RoomFilter filter)
    {
        roomFilter = filter;
        if (globalDrawerView == GlobalDrawerView.Mentions) CloseGlobalDrawer();
    }

    private async Task OpenFromRoomMenu(GlobalDrawerView view)
    {
        roomMoreOpen = false;
        if (globalDrawerView != view) await ToggleGlobalDrawer(view);
    }

    /// "Search messages for …" under the room results: the old "Search all conversations"
    /// drawer, pre-filled with what was typed in the room search.
    private async Task SearchMessagesFromRoomSearch()
    {
        var q = roomSearchQuery.Trim();
        if (globalDrawerView != GlobalDrawerView.Search) await ToggleGlobalDrawer(GlobalDrawerView.Search);
        globalSearchQuery = q;
        await OnGlobalSearchInput();
    }

    // ---- Pinned bar ----

    private IReadOnlyList<IPop.Application.Chat.PinnedMessageDto> PinsNewestFirst
        => pinnedMessages.OrderByDescending(p => p.PinnedAt).ToList();

    /// Up to four rail segments; with more pins the lit one shows roughly where you are.
    private static int RailSlot(int index, int count)
        => count <= 4 ? index : (int)Math.Round(index * 3.0 / (count - 1));

    private async Task ShowPinnedAndAdvance()
    {
        var pins = PinsNewestFirst;
        if (pins.Count == 0) return;
        var i = Math.Clamp(pinCursor, 0, pins.Count - 1);
        pinCursor = (i + 1) % pins.Count;
        await JumpToMessage(pins[i].MessageId);
    }

    private void ToggleCreateDropdown() => createDropdownOpen = !createDropdownOpen;

    // ---- New chat panel (WhatsApp-style; takes over the conversation-list column) ----

    private void StartNewDm() => OpenNewChat(group: false);

    private void StartNewGroup() => OpenNewChat(group: true);

    private void OpenNewChat(bool group)
    {
        createDropdownOpen = false;
        newChatGroupMode = group;
        newChatPreselected = [];
        // A fresh component per open: every open starts on its first step with an empty search.
        newChatKey++;
        newChatOpen = true;
    }

    private void CloseNewChat()
    {
        newChatOpen = false;
        newChatPreselected = [];
    }

    private Task StartDmFromPanel(NewChatPerson p) =>
        CreateDmWith(new UserSearchResult(p.Id, p.Nik, p.Name, p.Email));

    private async Task<bool> CreateGroupFromPanel(NewGroupDraft draft)
    {
        var members = draft.Members.Select(m => new UserSearchResult(m.Id, m.Nik, m.Name, m.Email)).ToList();
        return await CreateGroupWith(draft.Name, members, draft.Photo, draft.PhotoContentType, draft.PhotoFileName);
    }

    private void ClearPendingPreviews()
    {
        foreach (var p in pendingAttachments)
            if (!string.IsNullOrEmpty(p.PreviewUrl))
                _ = JS.InvokeVoidAsync("ipop.revokeBlob", p.PreviewUrl);
    }

    /// Selecting a room only changes the URL. The route parameter is the single source of
    /// truth and OnParametersSetAsync does the actual opening.
    ///
    /// This matters: the previous version opened the room first and updated the URL afterwards,
    /// behind an early `return`. When that return fired, the URL still held the *old* room id,
    /// so OnParametersSetAsync re-opened the previous room, which flipped ActiveRoomId back and
    /// forth between the two rooms forever (the group <-> direct request storm). With one source
    /// of truth the two can no longer disagree.
    private void OpenRoom(Guid roomId)
    {
        if (roomId == openedRoomId) return;
        mobileRoomsOpen = false;
        var target = $"/chat/{roomId}";
        if (Nav.Uri.EndsWith(target, StringComparison.OrdinalIgnoreCase))
        {
            // Already at this URL (so no navigation event will fire) — open directly.
            _ = OpenRoomCore(roomId);
            return;
        }
        mRoomPushed = true; // phone "‹" can pop this entry instead of stacking another /chat
        Nav.NavigateTo(target);
    }

    private async Task OpenRoomCore(Guid roomId)
    {
        if (openingRoom || roomId == openedRoomId) return;
        openingRoom = true;
        var previousRoomId = openedRoomId;
        openedRoomId = roomId;
        _ = Notifs.MarkReadByTargetAsync("chat", roomId);
        CancelComposerUpload();
        ClearPendingPreviews();
        pendingAttachments = [];
        uploadError = null;
        failedUpload = null;
        composerPasteBoundId = null;
        composerKeyBoundId = null;
        mobileRoomsOpen = false;
        CloseAttachment();
        showJumpToLatest = false;
        panelView = DetailPanelView.Main;
        panelHistory.Clear();
        personFromRoom = false;
        profilePerson = null;
        roomAttachments = [];
        // ipop-37c: thread state belongs to the room it was opened in — reset all of it
        // on room switch so the dedup set doesn't pollute across rooms and no future
        // code reads 'expandedThreadId is not null' as 'a thread is open in THIS room'.
        expandedThreadId = null;
        threadReplies = [];
        threadReplySeenKeys.Clear();
        // The divider belongs to the room it was computed for; leaving it set would
        // paint "New messages" at an arbitrary point in the next room's history.
        unreadDividerMsgId = null;
        // The messages element is replaced on room switch, so re-bind the scroll watcher.
        try { await JS.InvokeVoidAsync("ipop.unbindScrollWatch", messagesRef); } catch { }
        scrollWatchBoundId = null;
        newBelowCount = 0;
        showJumpToLatest = false;
        // Attachment blobs stay cached (LRU-bounded in ChatAttachments) so coming back is instant.
        // ipop-38: persist the outgoing room's half-typed composer text BEFORE clearing,
        // and restore the incoming room's draft. Both draft ops are fire-safe (store
        // swallows storage failures); messageText clears synchronously here so a slow
        // LoadAsync can never leave room B showing room A's text.
        if (previousRoomId is not null)
            FlushComposerDraft(previousRoomId.Value);
        messageText = "";
        ResetComposerLink();
        replyToMsg = null;
        plusMenuOpen = false;
        pickerOpen = false;
        stickerOpen = false;
        reactPickerMsgId = null;
        // A cached room paints during OpenRoomAsync; land it at the bottom straight away.
        scrollToBottom = true;
        try
        {
            await ChatSvc.OpenRoomAsync(roomId);
            if (ChatSvc.ActiveRoomId != roomId) return; // superseded by a newer open
            // ipop-38: superseded-request guard before assigning — a second room switch
            // during OpenRoomAsync must not paste this room's draft into that one.
            if (roomId == openedRoomId)
                {
                    var draft = await SafeLoadDraftAsync(roomId);
                    if (openedRoomId == roomId && ChatSvc.ActiveRoomId == roomId && ChatSvc.RoomError != IPop.Web.Services.ChatLoadError.Gone)
                        LoadComposerFromWire(draft);
                }
            scrollToBottom = true;
            focusComposerPending = true;
            // Must run before MarkReadAsync: marking read zeroes the room's UnreadCount,
            // which is the only signal we have for where the reader left off.
            ComputeUnreadDivider(roomId);
            // Load pins up-front so the sticky pinned bar is populated without
            // waiting for the user to open the Pinned drawer tab first.
            _ = LoadPinnedMessagesAsync();
            _ = LoadRoomAttachmentsAsync();
            _ = LoadGroupCallAsync(roomId);
            _ = EnsureDmPersonLoadedAsync();
            dmUserBlocked = false;
            if (ChatSvc.ActiveRoom?.Type == "dm")
            {
                var other = ChatSvc.ActiveRoom.Members.FirstOrDefault(m => m.Nik != GetCurrentUserNik());
                if (other is not null)
                {
                    var blocked = await Auth.GetAsync<List<Guid>>("/api/chat/blocked-users");
                    dmUserBlocked = blocked?.Contains(other.UserId) == true;
                }
            }
            // No-ops if the hub is still connecting (fresh tab); ChatState re-runs it on connect.
            await ChatSvc.MarkActiveRoomReadAsync();
            // Entry point for cross-page jumps (e.g. /chat/pins "open in chat"):
            // ?jump=<messageId> scrolls to that message, pulling older pages as needed.
            var jumpRaw = QueryValue("jump");
            if (Guid.TryParse(jumpRaw, out var jumpId))
            {
                _ = JumpToMessage(jumpId);
            }
            else if (jumpAfterOpen is { } pendingJump)
            {
                // Set by a global-drawer card that targeted a different room: the room
                // switch and the scroll land together instead of two competing navigations.
                jumpAfterOpen = null;
                _ = JumpToMessage(pendingJump);
            }
            // Old /chat/group/{id}/settings links land here as ?panel=settings.
            if (QueryValue("panel") == "settings" && ChatSvc.ActiveRoom?.Type == "group" && roomId == openedRoomId)
            {
                OpenDetailView(DetailPanelView.GroupSettings);
                Nav.NavigateTo($"/chat/{roomId}", replace: true);
            }
        }
        finally
        {
            openingRoom = false;
            await InvokeAsync(StateHasChanged);
        }
    }

    /// Prepending a page would otherwise leave scrollTop where it was, jerking the view to
    /// the new top rows. Record the distance from the bottom first; OnAfterRenderAsync puts
    /// the reader back at that same distance once the older rows are in the DOM.
    private async Task LoadOlder()
    {
        if (!ChatSvc.HasOlderMessages || loadingOlder) return;
        loadingOlder = true;
        StateHasChanged();
        try { await LoadOlderCore(); }
        finally { loadingOlder = false; }
    }

    private async Task RetryOlder()
    {
        if (loadingOlder) return;
        loadingOlder = true;
        StateHasChanged();
        try
        {
            try { anchorFromBottom = (await JS.InvokeAsync<ScrollInfo>("ipop.scrollInfo", messagesRef)).FromBottom; } catch { }
            anchorBaseCount = ChatSvc.ActiveMessages.Count;
            await ChatSvc.RetryOlderMessagesAsync();
            if (ChatSvc.ActiveMessages.Count <= anchorBaseCount) anchorFromBottom = null;
        }
        finally { loadingOlder = false; }
    }

    private async Task LoadOlderCore()
    {
        var roomId = ChatSvc.ActiveRoomId;
        try { anchorFromBottom = (await JS.InvokeAsync<ScrollInfo>("ipop.scrollInfo", messagesRef)).FromBottom; } catch { }
        // Armed BEFORE the load: ChatState renders the prepended rows itself, and the
        // anchor has to apply on that very render, not one frame later.
        anchorBaseCount = ChatSvc.ActiveMessages.Count;
        await ChatSvc.LoadOlderMessagesAsync();
        // Nothing was prepended (end of history, error, room switched): disarm.
        if (ChatSvc.ActiveRoomId != roomId || ChatSvc.ActiveMessages.Count <= anchorBaseCount)
            anchorFromBottom = null;
    }

    private record ScrollInfo(double FromBottom, double ScrollTop, double ScrollHeight);

    /// Pending "keep this distance from the bottom" once older messages are prepended;
    /// applied on the first render where the list has grown past anchorBaseCount.
    private double? anchorFromBottom;
    private int anchorBaseCount;

    private async Task OpenDmByNik(string nik)
    {
        var user = await LookupUserAsync(nik);
        if (user is not null)
            await CreateDmWith(user);
    }

    /// Directory bulk "Create group" lands here with ?new=group&members=nik1,nik2 —
    /// open the picker in group mode with those people preselected.
    private async Task StartGroupFromQuery()
    {
        StartNewGroup();
        var found = new List<NewChatPerson>();
        var niks = PLinks.Niks(QueryValue("members"));
        foreach (var nik in niks)
        {
            if (nik == GetCurrentUserNik()) continue;
            var user = await LookupUserAsync(nik);
            if (user is not null && newChatPreselected.All(m => m.Id != user.Id))
                found.Add(new NewChatPerson(user.Id, user.Nik, user.Name, user.Email, null, null, "off"));
        }
        // New list instance so the panel's parameter change is noticed.
        newChatPreselected = [.. newChatPreselected, .. found];
        StateHasChanged();
    }

    /// Exact lookup (search no longer matches NIKs for non-admins).
    private async Task<UserSearchResult?> LookupUserAsync(string nik)
    {
        try
        {
            var d = await Auth.GetAsync<IPop.Web.Services.DirectoryEmployeeDto>($"/api/chat/users/{Uri.EscapeDataString(nik)}");
            return d?.UserId is { } id ? new UserSearchResult(id, d.Nik, d.Name, d.Email) : null;
        }
        catch { return null; }
    }

    private string? QueryValue(string key)
    {
        var q = Nav.ToAbsoluteUri(Nav.Uri).Query.TrimStart('?');
        foreach (var part in q.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=');
            if (eq < 0) continue;
            if (!part[..eq].Equals(key, StringComparison.OrdinalIgnoreCase)) continue;
            return Uri.UnescapeDataString(part[(eq + 1)..]);
        }
        return null;
    }

}
