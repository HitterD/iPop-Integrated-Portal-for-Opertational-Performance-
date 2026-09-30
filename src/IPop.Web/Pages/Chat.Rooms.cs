// Chat page — room pane: tabs, server-side search (ipop-52), paging, room open/navigation.
//
// Part of the Chat partial class. The class was split by responsibility out of a single
// ~1,500-line @code block; members were moved verbatim, never renamed or re-scoped.
// _Imports.razor only reaches .razor files, so usings are restated in each part.
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace IPop.Web.Pages;

public partial class Chat
{
    // ---- Room Pane ----

    private int DirectUnread => ChatSvc.DirectRooms.Sum(r => r.UnreadCount);
    private int GroupUnread => ChatSvc.GroupRooms.Sum(r => r.UnreadCount);

    // ipop-52: search runs server-side (/rooms/search — name OR member name over ALL the
    // user's rooms); its results land in DirectRooms/GroupRooms so the render path is the
    // same getter as always. Empty query restores the normal paginated lists.
    private bool searchActive => !string.IsNullOrWhiteSpace(roomSearchQuery);
    private CancellationTokenSource? roomSearchCts;

    private IReadOnlyList<IPop.Application.Chat.RoomListDto> FilteredRooms
        => activeTab == RoomTab.Direct ? ChatSvc.DirectRooms : ChatSvc.GroupRooms;

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
        await ChatSvc.SearchRoomsAsync(roomSearchQuery);
    }

    /// Thin wrappers for the load-more buttons (@onclick needs a handler; the paging
    /// logic lives on ChatState.LoadMoreDirectRoomsAsync / LoadMoreGroupRoomsAsync).
    private Task LoadMoreDirectRooms() => ChatSvc.LoadMoreDirectRoomsAsync();
    private Task LoadMoreGroupRooms() => ChatSvc.LoadMoreGroupRoomsAsync();

    private void SetTab(RoomTab tab)
    {
        activeTab = tab;
        roomSearchQuery = "";
    }

    private void ToggleCreateDropdown() => createDropdownOpen = !createDropdownOpen;

    private void StartNewDm()
    {
        createDropdownOpen = false;
        newDmModalOpen = true;
        pickerGroupMode = false;
        pickerPicked.Clear();
        newDmSearchQuery = "";
        newDmSearchResults = [];
        _ = LoadPickerPeopleAsync();
    }

    private void StartNewGroup()
    {
        createDropdownOpen = false;
        newDmModalOpen = true;
        pickerGroupMode = true;
        pickerPicked.Clear();
        newGroupName = "";
        newDmSearchQuery = "";
        newDmSearchResults = [];
        _ = LoadPickerPeopleAsync();
    }

    // ---- v6 picker: Direct ⇄ Group in one dialog -------------------------------

    private void ClosePicker()
    {
        newDmModalOpen = false;
        pickerPicked.Clear();
        newDmSearchQuery = "";
        newDmSearchResults = [];
        newGroupName = "";
        ResetPickerSearch();
    }

    private void SetPickerMode(bool group)
    {
        pickerGroupMode = group;
        // A direct message addresses exactly one person; dropping to a single pick
        // mirrors the mockup, which truncates the selection when you switch tabs.
        if (!group && pickerPicked.Count > 1)
            pickerPicked.RemoveRange(1, pickerPicked.Count - 1);
    }

    /// The default (empty-query) list: one fetch covers both modes. The endpoint caps it at
    /// 300 names, so typing 2+ characters searches the server instead (OnPickerSearchChanged).
    private async Task LoadPickerPeopleAsync()
    {
        if (pickerPeople.Count > 0) return;
        pickerLoading = true;
        var rows = await Auth.GetAsync<List<PickerPerson>>("/api/chat/users/directory");
        pickerPeople = rows ?? [];
        pickerLoading = false;
        await InvokeAsync(StateHasChanged);
    }

    /// Debounced (250ms) server search for the picker. The preloaded list only holds the
    /// first 300 staff by name, so anyone past that was unfindable by typing. Each keystroke
    /// cancels the previous request; one character still filters the preloaded list locally.
    private async Task OnPickerSearchChanged(ChangeEventArgs e)
    {
        newDmSearchQuery = e.Value?.ToString() ?? "";
        ResetPickerSearch();
        var q = newDmSearchQuery.Trim();
        if (q.Length < 2) return;

        var cts = new CancellationTokenSource();
        pickerSearchCts = cts;
        pickerSearching = true;
        try
        {
            await Task.Delay(250, cts.Token);
            var rows = await Auth.GetAsync<List<PickerPerson>>($"/api/chat/users/directory?q={Uri.EscapeDataString(q)}", cts.Token);
            if (cts.IsCancellationRequested) return;
            pickerSearchPeople = rows ?? [];
            pickerSearchFor = q;
            foreach (var p in pickerSearchPeople) pickerKnown[p.Id] = p;
        }
        catch (OperationCanceledException) { return; }
        catch
        {
            // Network hiccup: fall back to filtering the preloaded list for this query.
            if (cts.IsCancellationRequested) return;
            pickerSearchPeople = null;
        }
        pickerSearching = false;
        StateHasChanged();
    }

    private void ResetPickerSearch()
    {
        pickerSearchCts?.Cancel();
        pickerSearchCts?.Dispose();
        pickerSearchCts = null;
        pickerSearchPeople = null;
        pickerSearchFor = null;
        pickerSearching = false;
    }

    private static bool PickerMatches(string name, string nik, string q) =>
        q.Length == 0 || name.Contains(q, StringComparison.OrdinalIgnoreCase) || nik.Contains(q, StringComparison.OrdinalIgnoreCase);

    private List<PickerPerson> VisiblePickerPeople
    {
        get
        {
            var q = newDmSearchQuery.Trim();
            // Server results once they're in for this exact query; until then (and for
            // 1-char queries) the preloaded list, filtered locally.
            var rows = (pickerSearchPeople is not null && pickerSearchFor == q
                    ? pickerSearchPeople
                    : pickerPeople.Where(p => PickerMatches(p.Name, p.Nik, q)))
                .Take(60).ToList();
            // Picked people stay visible (and untickable) even when the current list
            // doesn't hold them, e.g. someone found by an earlier server search.
            var extra = pickerPicked
                .Where(m => rows.All(r => r.Id != m.Id) && PickerMatches(m.Name, m.Nik, q))
                .Select(m => pickerKnown.GetValueOrDefault(m.Id)
                             ?? new PickerPerson(m.Id, m.Nik, m.Name, m.Email, null, null, "off"));
            return extra.Concat(rows).ToList();
        }
    }

    private bool IsPickerPicked(Guid id) => pickerPicked.Any(m => m.Id == id);

    private void TogglePickerPerson(PickerPerson person)
    {
        var picked = pickerPicked.Any(m => m.Id == person.Id);
        if (pickerGroupMode)
        {
            if (picked) pickerPicked.RemoveAll(m => m.Id == person.Id);
            else pickerPicked.Add(new UserSearchResult(person.Id, person.Nik, person.Name, person.Email));
        }
        else
        {
            pickerPicked.Clear();
            if (!picked) pickerPicked.Add(new UserSearchResult(person.Id, person.Nik, person.Name, person.Email));
        }
        pickerKnown[person.Id] = person;
        newDmSearchQuery = "";
        ResetPickerSearch();
    }

    private void RemovePickerPerson(Guid id) => pickerPicked.RemoveAll(m => m.Id == id);

    private bool PickerCanConfirm => pickerGroupMode
        ? !string.IsNullOrWhiteSpace(newGroupName) && pickerPicked.Count >= 2
        : pickerPicked.Count == 1;

    private string PickerHint => pickerGroupMode
        ? $"{pickerPicked.Count} selected · min. 2"
        : "Pick one person";

    private async Task ConfirmPicker()
    {
        if (!PickerCanConfirm) return;
        if (pickerGroupMode)
        {
            var name = newGroupName.Trim();
            var members = pickerPicked.ToList();
            ClosePicker();
            await CreateGroupWith(name, members);
        }
        else
        {
            var person = pickerPicked[0];
            ClosePicker();
            await CreateDmWith(person);
        }
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
        ClearPendingPreviews();
        pendingAttachments = [];
        uploadError = null;
        composerPasteBound = false;
        composerKeyBound = false;
        mobileRoomsOpen = false;
        CloseAttachment();
        showJumpToLatest = false;
        panelView = DetailPanelView.Main;
        panelHistory.Clear();
        personFromRoom = false;
        profilePerson = null;
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
        scrollWatchBound = false;
        // Blob URLs for the previous room's attachments are dead weight once we leave it.
        Att.ReleaseAll();
        // ipop-38: persist the outgoing room's half-typed composer text BEFORE clearing,
        // and restore the incoming room's draft. Both draft ops are fire-safe (store
        // swallows storage failures); messageText clears synchronously here so a slow
        // LoadAsync can never leave room B showing room A's text.
        if (previousRoomId is not null)
            _ = Drafts.SaveAsync(previousRoomId.Value, ComposerWireText());
        messageText = "";
        replyToMsg = null;
        plusMenuOpen = false;
        pickerOpen = false;
        stickerOpen = false;
        reactPickerMsgId = null;
        try
        {
            await ChatSvc.OpenRoomAsync(roomId);
            if (ChatSvc.ActiveRoomId != roomId) return; // superseded by a newer open
            // ipop-38: superseded-request guard before assigning — a second room switch
            // during OpenRoomAsync must not paste this room's draft into that one.
            if (roomId == openedRoomId)
                LoadComposerFromWire(await Drafts.LoadAsync(roomId));
            scrollToBottom = true;
            focusComposerPending = true;
            // Must run before MarkReadAsync: marking read zeroes the room's UnreadCount,
            // which is the only signal we have for where the reader left off.
            ComputeUnreadDivider(roomId);
            // Load pins up-front so the sticky pinned bar is populated without
            // waiting for the user to open the Pinned drawer tab first.
            _ = LoadPinnedMessagesAsync();
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
            var lastMsg = ChatSvc.ActiveMessages.LastOrDefault(m => !string.Equals(m.SenderNik, Auth.Me?.Nik, StringComparison.OrdinalIgnoreCase))
                          ?? ChatSvc.ActiveMessages.LastOrDefault();
            if (lastMsg is not null)
                await ChatSvc.MarkReadAsync(lastMsg.Id);
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
        if (!ChatSvc.HasOlderMessages) return;
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
        var results = await Auth.GetAsync<List<UserSearchResult>>($"/api/chat/users/search?q={Uri.EscapeDataString(nik)}");
        var user = results?.FirstOrDefault(u => string.Equals(u.Nik, nik, StringComparison.OrdinalIgnoreCase));
        if (user is not null)
            await CreateDmWith(user);
    }

    /// Directory bulk "Create group" lands here with ?new=group&members=nik1,nik2 —
    /// open the picker in group mode with those people preselected.
    private async Task StartGroupFromQuery()
    {
        StartNewGroup();
        var niks = (QueryValue("members") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var nik in niks)
        {
            if (nik == GetCurrentUserNik()) continue;
            var results = await Auth.GetAsync<List<UserSearchResult>>($"/api/chat/users/search?q={Uri.EscapeDataString(nik)}");
            var user = results?.FirstOrDefault(u => string.Equals(u.Nik, nik, StringComparison.OrdinalIgnoreCase));
            if (user is not null && !pickerPicked.Any(m => m.Id == user.Id))
                pickerPicked.Add(user);
        }
        StateHasChanged();
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
