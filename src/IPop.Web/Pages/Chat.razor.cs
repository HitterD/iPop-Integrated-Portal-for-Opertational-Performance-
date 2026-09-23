// Chat page — state, lifecycle, and ChatState event wiring.
//
// Part of the Chat partial class. The class was split by responsibility out of a single
// ~1,500-line @code block; members were moved verbatim, never renamed or re-scoped.
// _Imports.razor only reaches .razor files, so usings are restated in each part.
using IPop.Web.Layout;
using IPop.Web.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;

namespace IPop.Web.Pages;

public partial class Chat
{
    [Parameter] public Guid? RoomId { get; set; }
    [CascadingParameter] public MainLayout Layout { get; set; } = default!;

    private ElementReference messagesRef;
    private ElementReference composerRef;
    private ElementReference composerWrapRef;
    private ElementReference qsInputRef;
    private ElementReference ctxMenuRef;
    private bool ctxNeedsPlacement;
    private bool composerKeyBound;
    private DotNetObjectReference<Chat>? selfRef;
    private bool composerPasteBound;
    private string messageText = "";
    private string roomSearchQuery = "";
    private bool createDropdownOpen;
    private RoomTab activeTab = RoomTab.Direct;

    // ---- Message grouping / unread divider / read receipts ----

    /// How long a gap may be before a same-sender run is broken into a new block.
    /// 5 minutes matches Slack's default; long enough to keep a burst together,
    /// short enough that a reply hours later still gets its own header.
    private const double GroupWindowMinutes = 5;

    /// First message the reader had not seen when the room was opened, or null when
    /// they were already caught up. Captured once per room open and deliberately NOT
    /// recomputed as new messages stream in — the divider is a landmark for where you
    /// left off, so it must stay put while you read rather than chasing the tail.
    private Guid? unreadDividerMsgId;

    /// Newest own message in the active room — the only one that renders a receipt.
    /// Pending/failed sends are excluded because they have no server-side seen record yet.
    private Guid? LastOwnMessageId => ChatSvc.ActiveMessages
        .LastOrDefault(m => m.SenderNik == GetCurrentUserNik()
                            && m.Type != "system"
                            && !ChatSvc.IsPending(m.Id)
                            && !ChatSvc.IsFailed(m.Id))?.Id;

    /// Place the divider above the first of the last `unread` messages that someone
    /// else sent. The server exposes an unread *count* rather than a read pointer, so
    /// the position is derived from it — own messages are skipped because they can
    /// never be the thing you came back to read.
    private void ComputeUnreadDivider(Guid roomId)
    {
        unreadDividerMsgId = null;
        var unread = ChatSvc.DirectRooms.Concat(ChatSvc.GroupRooms)
            .FirstOrDefault(r => r.Id == roomId)?.UnreadCount ?? 0;
        if (unread <= 0) return;

        var me = GetCurrentUserNik();
        var candidates = ChatSvc.ActiveMessages
            .Where(m => m.SenderNik != me && m.Type != "system")
            .ToList();
        if (candidates.Count == 0) return;

        // Fewer loaded than the badge claims (older page not fetched) — mark from the
        // oldest we actually have rather than dropping the divider entirely.
        var idx = Math.Max(0, candidates.Count - unread);
        unreadDividerMsgId = candidates[idx].Id;
    }

    // ---- Context Menu State (Task 13.1) ----
    private const string LikeEmoji = "👍";
    private Guid? ctxMsgId;
    private double ctxX, ctxY;

    // ---- Reply State (Task 13.2) ----
    private IPop.Application.Chat.MessageDto? replyToMsg;

    // ---- Edit State (Task 13.3) ----
    private Guid? editingMsgId;
    private string editText = "";

    // ---- Forward State (Task 13.4) ----
    private bool forwardOpen;
    private Guid? forwardMsgId;

    // ---- Poll & Voice Recording State ----
    private bool pollModalOpen;
    private bool isRecordingVoice;

    // ---- Thread State (Task 13.6) ----
    private Guid? expandedThreadId;
    private List<IPop.Application.Chat.MessageDto> threadReplies = [];
    // ipop-48: the loader used to fetch one page of 50 and drop TotalCount, while the
    // parent row rendered the server's true ThreadReplyCount (up to ChatLimits.ThreadReplies
    // = 255). A 200-reply thread showed "200 replies" above exactly 50 rows with no way to
    // reach the rest. Same two-part shape as ipop-47; same remedy, mirroring LoadMoreArchivedAsync.
    private int threadTotal;
    private int threadPage = 1;
    private bool threadLoadingMore;
    private bool HasMoreThreadReplies => threadReplies.Count < threadTotal;

    // ---- Apple Detail Panel State ----
    public enum DetailPanelView { Main, Members, Pinned, Media, Events, Person }
    private DetailPanelView panelView = DetailPanelView.Main;
    private bool personFromRoom;
    private enum ChatDrawerTab { Overview, Members, Media, Pinned, Calendar }
    private ChatDrawerTab activeDrawerTab = ChatDrawerTab.Overview;
    private bool drawerOpen;
    private bool groupInfoOpen;
    private bool pinnedPanelOpen;
    private List<IPop.Application.Chat.PinnedMessageDto> pinnedMessages = [];
    private string mediaSubTab = "all";
    private string memberFilter = "";

    private void ToggleDetailPanel()
    {
        if (drawerOpen && panelView == DetailPanelView.Main)
            CloseDetail();
        else
            OpenDetailView(DetailPanelView.Main);
    }

    private IPop.Web.Models.Employee? dmPerson;

    private async Task EnsureDmPersonLoadedAsync()
    {
        if (ChatSvc.ActiveRoom?.Type != "dm") { dmPerson = null; return; }
        var dmMember = ChatSvc.ActiveRoom.Members.FirstOrDefault(m => m.Nik != GetCurrentUserNik());
        if (dmMember is null || string.IsNullOrWhiteSpace(dmMember.Nik)) { dmPerson = null; return; }
        if (dmPerson?.Nik == dmMember.Nik) return;
        try
        {
            var d = await Auth.GetAsync<IPop.Web.Services.DirectoryEmployeeDto>($"/api/chat/users/{Uri.EscapeDataString(dmMember.Nik)}");
            if (d is not null) dmPerson = MapEmployee(d);
        }
        catch { dmPerson = null; }
        StateHasChanged();
    }

    private void OpenDetailView(DetailPanelView view)
    {
        panelView = view;
        drawerOpen = true;
        groupInfoOpen = (view == DetailPanelView.Main);
        pinnedPanelOpen = (view == DetailPanelView.Pinned);
        if (view == DetailPanelView.Pinned) _ = LoadPinnedMessagesAsync();
        if (view == DetailPanelView.Events) _ = LoadRoomEventsAsync();
        if (view == DetailPanelView.Main && ChatSvc.ActiveRoom?.Type == "dm") _ = EnsureDmPersonLoadedAsync();
        StateHasChanged();
    }

    private void GoBackToMain()
    {
        panelView = DetailPanelView.Main;
        StateHasChanged();
    }

    private void CloseDetail()
    {
        drawerOpen = false;
        panelView = DetailPanelView.Main;
        groupInfoOpen = false;
        pinnedPanelOpen = false;
        personFromRoom = false;
        profilePerson = null;
        StateHasChanged();
    }

    private async Task HandlePersonMessage()
    {
        CloseDetail();
        try { await composerRef.FocusAsync(); } catch { }
    }

    private void OpenPinnedSection()
    {
        if (drawerOpen && panelView == DetailPanelView.Pinned) CloseDetail();
        else OpenDetailView(DetailPanelView.Pinned);
    }

    private void OpenMediaSection()
    {
        if (drawerOpen && panelView == DetailPanelView.Media) CloseDetail();
        else OpenDetailView(DetailPanelView.Media);
    }

    private void OpenMembersSection()
    {
        if (drawerOpen && panelView == DetailPanelView.Members) CloseDetail();
        else OpenDetailView(DetailPanelView.Members);
    }

    private void OpenDrawerTab(ChatDrawerTab tab)
    {
        activeDrawerTab = tab;
        var view = tab switch
        {
            ChatDrawerTab.Members => DetailPanelView.Members,
            ChatDrawerTab.Pinned => DetailPanelView.Pinned,
            ChatDrawerTab.Media => DetailPanelView.Media,
            ChatDrawerTab.Calendar => DetailPanelView.Events,
            _ => DetailPanelView.Main
        };
        OpenDetailView(view);
    }

    // ---- Kalender ruang (Fase 4) ----
    private sealed record RoomEventRow(Guid Id, string Title, DateTime StartAt, DateTime EndAt, string Type, Guid? MeetingId, Guid? RoomId);
    private List<RoomEventRow> roomEvents = [];
    private bool roomEventsLoading;
    private bool roomEventFormOpen;
    private bool roomEventSaving;
    private string roomEventTitle = "";
    private DateTime roomEventStart = DateTime.Now.Date.AddDays(1).AddHours(9);

    private void ToggleRoomEventForm() => roomEventFormOpen = !roomEventFormOpen;

    private async Task LoadRoomEventsAsync()
    {
        if (ChatSvc.ActiveRoomId is null) return;
        roomEventsLoading = true;
        StateHasChanged();
        try
        {
            roomEvents = await Auth.GetAsync<List<RoomEventRow>>($"/api/meetings/room/{ChatSvc.ActiveRoomId}") ?? [];
        }
        catch { roomEvents = []; }
        roomEventsLoading = false;
        StateHasChanged();
    }

    private async Task SaveRoomEvent()
    {
        if (ChatSvc.ActiveRoomId is null || string.IsNullOrWhiteSpace(roomEventTitle) || roomEventSaving) return;
        roomEventSaving = true;
        StateHasChanged();
        try
        {
            var res = await Auth.SendAsync(HttpMethod.Post, "/api/meetings/calendar", new
            {
                title = roomEventTitle.Trim(),
                startAt = roomEventStart.ToUniversalTime(),
                endAt = roomEventStart.AddHours(1).ToUniversalTime(),
                type = "rapat",
                roomId = ChatSvc.ActiveRoomId.Value,
            });
            if (res.IsSuccessStatusCode)
            {
                roomEventTitle = "";
                roomEventFormOpen = false;
                Toasts.Show("Room event saved.", ToastKind.Success);
                await LoadRoomEventsAsync();
            }
            else
            {
                Toasts.Show("Failed to save event.", ToastKind.Error);
            }
        }
        catch { Toasts.Show("Failed to save event.", ToastKind.Error); }
        finally { roomEventSaving = false; StateHasChanged(); }
    }

    private void OpenRoomEventDetail(RoomEventRow ev)
    {
        _ = OpenRoomEventDetailAsync(ev);
    }

    // Modal detail event ruang — memuat rapat (lokasi/peserta/RSVP) dari MeetingId.
    private bool roomEventModalOpen;
    private IPop.Web.Components.Chat.EventDetailModal.EventModel? roomEventDetail;

    private async Task OpenRoomEventDetailAsync(RoomEventRow ev)
    {
        string? location = null;
        string? link = null;
        IReadOnlyList<string> attendees = ChatSvc.ActiveRoom?.Members.Select(m => m.Name).ToList() ?? [];
        if (ev.MeetingId is { } meetingId)
        {
            try
            {
                var dto = await Auth.GetAsync<MeetingDetailDto>($"/api/meetings/{meetingId}");
                if (dto is not null)
                {
                    location = dto.Location;
                    link = dto.Link;
                    attendees = dto.Attendees.Select(a => a.Name).ToList();
                }
            }
            catch { }
        }
        roomEventDetail = new IPop.Web.Components.Chat.EventDetailModal.EventModel(
            ev.Id, ev.Title, ev.StartAt, ev.EndAt, location, link, attendees);
        roomEventModalOpen = true;
        StateHasChanged();
    }

    private sealed record MeetingAttendeeDto(Guid UserId, string Name, string Nik, bool Required, string RsvpStatus, string? Note);
    private sealed record MeetingDetailDto(Guid Id, string Title, DateTime StartAt, DateTime EndAt, string? TimeZone, string? Location, string? Link,
        Guid OrganizerId, string OrganizerName, string Status, IReadOnlyList<MeetingAttendeeDto> Attendees,
        int AcceptCount, int MaybeCount, int DeclineCount, int PendingCount, string MyResponse);

    private void ToggleDrawerTab(ChatDrawerTab tab)
    {
        if (drawerOpen && activeDrawerTab == tab)
        {
            drawerOpen = false;
            groupInfoOpen = false;
            pinnedPanelOpen = false;
        }
        else
        {
            OpenDrawerTab(tab);
        }
    }

    private async Task LoadPinnedMessagesAsync()
    {
        if (ChatSvc.ActiveRoomId is null) return;
        pinnedMessages = (await Auth.GetAsync<List<IPop.Application.Chat.PinnedMessageDto>>($"/api/chat/rooms/{ChatSvc.ActiveRoomId}/pins")) ?? [];
        StateHasChanged();
    }

    private bool IsActiveRoomMuted =>
        ChatSvc.GroupRooms.Concat(ChatSvc.DirectRooms).FirstOrDefault(r => r.Id == ChatSvc.ActiveRoomId)?.IsMuted == true;

    /// Current user's per-room read receipts setting (reciprocal for DMs). Falls back to true.
    private bool IsActiveRoomReadReceiptsEnabled =>
        ChatSvc.GroupRooms.Concat(ChatSvc.DirectRooms).FirstOrDefault(r => r.Id == ChatSvc.ActiveRoomId)?.ReadReceiptsEnabled ?? true;

    private async Task CopyMessageText(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        await JS.InvokeVoidAsync("navigator.clipboard.writeText", text);
        Toasts.Show("Text copied to clipboard", ToastKind.Success);
    }

    // ---- Search State (Task 15.3) ----
    private bool searchOpen;
    private string searchQuery = "";
    private List<IPop.Application.Chat.MessageDto> searchResults = [];

    // ---- Attachment viewer state ----
    private IPop.Application.Chat.AttachmentDto? viewerAttachment;
    private IReadOnlyList<IPop.Application.Chat.AttachmentDto>? viewerSiblings;

    // ---- Emoji/Sticker Picker State (Task 16.1) ----
    private bool pickerOpen;
    private string pickerTab = "emoji";
    private static readonly string[] QuickEmoji = ["👍", "❤️", "😂", "🎉", "🔥", "✅"];
    private static readonly string[] EmojiGrid = ["👍", "❤️", "😂", "🎉", "🔥", "👀", "✅", "💯", "🚀", "⭐", "💡", "🙏", "👏", "😊", "😎", "🤔", "😅", "☕", "🏆", "💪", "📌", "📎", "🎯", "⚡"];
    private static readonly string[] StickerIcons = ["thumbs-up", "heart", "party-popper", "rocket", "flame", "coffee", "zap", "star", "check-circle", "alert-triangle", "lightbulb", "trophy", "hand-metal", "smile", "laugh", "frown", "badge-check", "brain", "target", "shield-check", "clock", "sparkles", "medal", "gift"];

    // ---- Mention Autocomplete State (Task 16.2) ----
    private bool mentionPopupOpen;
    private string mentionFilter = "";
    private List<IPop.Application.Chat.MemberDto> mentionSuggestions = [];

    // ---- Emergency Broadcast State (Task 18.1) ----
    private bool emergencyConfirmOpen;
    private string emergencyContent = "";
    private bool IsGroupAdmin => ChatSvc.ActiveRoom?.Type == "group" && ChatSvc.ActiveRoom.Members.Any(m => m.Nik == GetCurrentUserNik() && m.Role is "owner" or "admin");

    /// Backend pin rule: owner/admin only in groups; any member in DMs.
    private bool CanPinInActiveRoom => ChatSvc.ActiveRoom?.Type == "dm" || IsGroupAdmin;

    // ---- New DM Modal State ----
    private bool newDmModalOpen;
    private string newDmSearchQuery = "";
    private List<UserSearchResult> newDmSearchResults = [];

    // ---- File Upload State ----
    private InputFile? fileInput;
    private string? uploadError;
    private bool uploading;
    private bool mobileRoomsOpen;
    private bool archivedOpen;
    private bool HasGroupMention => ChatSvc.GroupRooms.Any(r => r.HasUnreadMention);

    // ---- Quick Switcher (Ctrl+K) ----
    private bool qsOpen;
    private string qsQuery = "";
    private int qsSel; // index into the filtered list below
    private List<IPop.Web.Models.PendingAttachmentItem> pendingAttachments = [];

    // ---- Add Member State (Group Info) ----
    private bool addMemberOpen;
    private string addMemberQuery = "";
    private List<UserSearchResult> addMemberResults = [];

    // ---- New Group Modal State ----
    private bool newGroupModalOpen;
    private string newGroupName = "";
    private string newGroupSearchQuery = "";
    private List<UserSearchResult> newGroupSearchResults = [];
    private List<UserSearchResult> newGroupSelectedMembers = [];

    // ---- Profile Drawer (click avatar) ----
    private IPop.Web.Models.Employee? profilePerson;

    private record UserSearchResult(Guid Id, string Nik, string Name, string? Email);

    private enum RoomTab { Direct, Groups }

    // ---- Lifecycle ----

    private string? _lastSubtitle;

    private void UpdateSubtitle()
    {
        var totalUnread = (ChatSvc.DirectRooms?.Sum(r => r.UnreadCount) ?? 0) + (ChatSvc.GroupRooms?.Sum(r => r.UnreadCount) ?? 0);
        var totalRooms = (ChatSvc.DirectRooms?.Count ?? 0) + (ChatSvc.GroupRooms?.Count ?? 0);
        var unreadPart = totalUnread == 0 ? "No unread" : totalUnread == 1 ? "1 unread" : $"{totalUnread} unread";
        var convPart = totalRooms == 1 ? "1 conversation" : $"{totalRooms} conversations";
        var subtitle = $"{unreadPart} · {convPart}";
        if (_lastSubtitle != subtitle)
        {
            _lastSubtitle = subtitle;
            InvokeAsync(() => Layout.SetSubtitle(subtitle));
        }
    }

    private void OnEscapePressed()
    {
        if (drawerOpen)
        {
            CloseDetail();
        }
    }

    protected override async Task OnInitializedAsync()
    {
        Layout.SetHeader("Messages");
        UpdateSubtitle();
        Layout.EscapePressed += OnEscapePressed;
        ChatSvc.StateChanged += OnStateChanged;
        ChatSvc.RoomListChanged += OnStateChanged;
        ChatSvc.TypingChanged += OnStateChanged;
        ChatSvc.ConnectionChanged += OnStateChanged;
        ChatSvc.MessageReceived += OnMessageReceived;
        // ipop-37: live thread replies — expanded threads used to show a stale list while
        // the reply count incremented. Append incoming replies with idempotency-key dedup.
        ChatSvc.ThreadReplyReceived += OnThreadReplyReceived;
        // ipop-34: pinnedChanged pushes from the hub had zero real listeners — the inline
        // pinned panel only fetched on open, so a colleague's pin/unpin never appeared
        // until it was closed and reopened. Reload live while the panel is open.
        ChatSvc.PinnedChanged += OnPinnedChanged;
        // ipop-32c: ProfileDrawer lives INSIDE this page. Its Chat button navigates
        // /chat?nik=…, which changes the URL without remounting us — OnInitializedAsync
        // never re-runs, so the ?nik= path would silently no-op from exactly the page
        // users open profiles on most. Re-parse per URL change (RoomId keeps precedence).
        if (!_navSubscribed)
        {
            _navSubscribed = true;
            Nav.LocationChanged += OnLocationChanged;
        }
        _ = ChatSvc.LoadArchivedRoomsAsync();
        await ChatSvc.ConnectAsync();
        if (RoomId is not null)
            await OpenRoomCore(RoomId.Value);
        else
        {
            var nik = QueryValue("nik");
            if (!string.IsNullOrWhiteSpace(nik))
                await OpenDmByNik(nik);
        }
    }

    /// ipop-32c: handle same-component URL changes. Precedence preserved:
    /// a RoomId route param always wins over a ?nik= query.
    private void OnLocationChanged(object? sender, Microsoft.AspNetCore.Components.Routing.LocationChangedEventArgs e)
    {
        var targetRoom = RoomId; // route param value already updated by Blazor before this fires
        if (targetRoom is not null) return;
        var nik = QueryValue("nik");
        if (!string.IsNullOrWhiteSpace(nik))
            _ = OpenDmByNik(nik);
    }

    /// The room this page has already requested. Compared against instead of
    /// ChatSvc.ActiveRoomId because OnParametersSetAsync re-runs on every parent render, and
    /// hub events can mutate ActiveRoomId mid-flight — using shared state as the guard let the
    /// page re-open the same room forever (open/close flicker + a request storm).
    private Guid? openedRoomId;
    private bool openingRoom;
    private bool _navSubscribed;

    protected override async Task OnParametersSetAsync()
    {
        if (RoomId is null || RoomId == openedRoomId) return;
        await OpenRoomCore(RoomId.Value);
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        try { await JS.InvokeVoidAsync("ipop.icons"); } catch { }
        // Bind paste + drag-drop when the composer is in the DOM (retry after room switch).
        if (!composerPasteBound)
        {
            try
            {
                selfRef ??= DotNetObjectReference.Create(this);
                await JS.InvokeVoidAsync("ipop.chatBindPaste", composerWrapRef, selfRef);
                composerPasteBound = true;
            }
            catch { /* composer not yet rendered (empty state) — retry next frame */ }
        }
        if (!composerKeyBound && ChatSvc.ActiveRoomId is not null)
        {
            try
            {
                selfRef ??= DotNetObjectReference.Create(this);
                await JS.InvokeVoidAsync("ipop.chatBindComposer", composerRef, selfRef);
                composerKeyBound = true;
            }
            catch { }
        }
        if (ctxNeedsPlacement && ctxMsgId is not null)
        {
            ctxNeedsPlacement = false;
            try
            {
                await JS.InvokeVoidAsync("ipop.placeContextMenu", ctxMenuRef, ctxX, ctxY);
            }
            catch { }
        }
        if (!scrollWatchBound && ChatSvc.ActiveRoomId is not null)
        {
            try
            {
                selfRef ??= DotNetObjectReference.Create(this);
                await JS.InvokeVoidAsync("ipop.bindScrollWatch", messagesRef, selfRef, 400);
                scrollWatchBound = true;
            }
            catch { /* older cached ipop.js — the jump button just stays hidden */ }
        }
        // ipop-22: bind project/task chip click delegation
        try { await JS.InvokeVoidAsync("ipop.bindMentionChips", messagesRef, selfRef); } catch { }
        // C4: global Ctrl+K / Cmd+K quick switcher
        try { await JS.InvokeVoidAsync("ipop.chatBindQuickSwitcher", selfRef); } catch { }
        if (scrollToBottom)
        {
            scrollToBottom = false;
            await ScrollToBottom();
        }
    }

    private bool scrollToBottom;

    public void Dispose()
    {
        Layout.EscapePressed -= OnEscapePressed;
        try { _ = JS.InvokeVoidAsync("ipop.chatUnbindPaste", composerWrapRef); } catch { }
        try { _ = JS.InvokeVoidAsync("ipop.chatUnbindComposer", composerRef); } catch { }
        try { _ = JS.InvokeVoidAsync("ipop.unbindScrollWatch", messagesRef); } catch { }
        // ipop-22: unbind chip delegation
        try { _ = JS.InvokeVoidAsync("ipop.unbindMentionChips", messagesRef); } catch { }
        try { _ = JS.InvokeVoidAsync("ipop.chatUnbindQuickSwitcher"); } catch { }
        Att.ReleaseAll();
        foreach (var p in pendingAttachments)
            if (!string.IsNullOrEmpty(p.PreviewUrl)) _ = JS.InvokeVoidAsync("ipop.revokeBlob", p.PreviewUrl);
        selfRef?.Dispose();
        if (_navSubscribed) Nav.LocationChanged -= OnLocationChanged;
        ChatSvc.PinnedChanged -= OnPinnedChanged;
        ChatSvc.ThreadReplyReceived -= OnThreadReplyReceived;
        ChatSvc.StateChanged -= OnStateChanged;
        ChatSvc.RoomListChanged -= OnStateChanged;
        ChatSvc.TypingChanged -= OnStateChanged;
        ChatSvc.ConnectionChanged -= OnStateChanged;
        ChatSvc.MessageReceived -= OnMessageReceived;
    }

    // ---- Event handlers ----

    private void OnStateChanged()
    {
        // Being removed from (or leaving) the open room clears ActiveRoomId. Drop the page's
        // own marker and go back to /chat so OnParametersSetAsync does not immediately try to
        // re-open a room we are no longer a member of.
        if (!openingRoom && openedRoomId is not null && ChatSvc.ActiveRoomId is null)
        {
            openedRoomId = null;
            if (RoomId is not null) Nav.NavigateTo("/chat", replace: true);
        }
        UpdateSubtitle();
        InvokeAsync(StateHasChanged);
    }

    private void OnMessageReceived(IPop.Application.Chat.MessageDto msg)
    {
        scrollToBottom = true;
        _ = InvokeAsync(StateHasChanged);
    }

    // ---- Quick Switcher (Ctrl+K) ----

    private record QsMatch(IPop.Application.Chat.RoomListDto Room, bool IsDirect);

    private List<QsMatch> QsMatches
    {
        get
        {
            var q = (qsQuery ?? "").Trim();
            var all = ChatSvc.DirectRooms.Concat(ChatSvc.GroupRooms).ToList();
            if (string.IsNullOrWhiteSpace(q)) return all.Select(r => new QsMatch(r, r.Type == "dm")).ToList();
            var lower = q.ToLowerInvariant();
            return all
                .Where(r => (r.Name ?? "").ToLowerInvariant().Contains(lower))
                .Select(r => new QsMatch(r, r.Type == "dm"))
                .ToList();
        }
    }

    private async Task OnQuickSwitcher()
    {
        qsOpen = !qsOpen;
        qsQuery = "";
        qsSel = 0;
        if (qsOpen)
        {
            await InvokeAsync(StateHasChanged);
            try { await JS.InvokeVoidAsync("ipop.icons"); } catch { }
            try { await JS.InvokeVoidAsync("setTimeout", "document.querySelector('.qs-input-row input')?.focus()", 0); } catch { }
        }
    }

    private async Task OnQsInput(ChangeEventArgs e)
    {
        qsQuery = e.Value?.ToString() ?? "";
        qsSel = 0;
        await InvokeAsync(StateHasChanged);
    }

    private async Task OnQsKey(KeyboardEventArgs e)
    {
        var matches = QsMatches;
        if (e.Key == "Escape") { CloseQuickSwitcher(); return; }
        if (e.Key == "ArrowDown") { qsSel = Math.Min(qsSel + 1, Math.Max(0, matches.Count - 1)); await InvokeAsync(StateHasChanged); return; }
        if (e.Key == "ArrowUp") { qsSel = Math.Max(qsSel - 1, 0); await InvokeAsync(StateHasChanged); return; }
        if (e.Key == "Enter" && matches.Count > 0)
        {
            var m = matches[Math.Clamp(qsSel, 0, matches.Count - 1)];
            await OpenFromQs(m.Room);
        }
    }

    private async Task OpenFromQs(IPop.Application.Chat.RoomListDto room)
    {
        qsOpen = false;
        qsQuery = "";
        qsSel = 0;
        await InvokeAsync(StateHasChanged);
        OpenRoom(room.Id);
    }

    private void CloseQuickSwitcher()
    {
        qsOpen = false;
        qsQuery = "";
        qsSel = 0;
        StateHasChanged();
    }

}
