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
    // Id of the textarea the JS Enter/Escape handlers are bound to. The textarea is a new
    // element after a skeleton, a room switch or a voice recording, so binding follows the
    // element itself rather than a one-shot flag (ipop.chatBindComposer is idempotent).
    private string? composerKeyBoundId;
    private DotNetObjectReference<Chat>? selfRef;
    private string? composerPasteBoundId;
    private string messageText = "";
    private string roomSearchQuery = "";
    private bool createDropdownOpen;
    private RoomFilter roomFilter = RoomFilter.All;
    private bool roomMoreOpen;
    /// Which pin the pinned bar shows: 0 = newest. A tap jumps to it and moves to the next older one.
    private int pinCursor;

    // ---- Message grouping / unread divider / read receipts ----

    /// First message the reader had not seen when the room was opened, or null when
    /// they were already caught up. Captured once per room open and deliberately NOT
    /// recomputed as new messages stream in — the divider is a landmark for where you
    /// left off, so it must stay put while you read rather than chasing the tail.
    private Guid? unreadDividerMsgId;

    /// Newest own message in the active room — the only one that renders a receipt.
    /// Pending/failed sends are excluded because they have no server-side seen record yet.
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
    private Guid forwardOperationKey;
    private bool forwarding;

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
    public enum DetailPanelView { Main, Members, Pinned, Media, Events, Person, GroupSettings }
    private DetailPanelView panelView = DetailPanelView.Main;
    private bool personFromRoom;
    /// Views the user drilled through to reach panelView; Back pops one. Header shortcuts
    /// open a view fresh (empty stack, no Back button), drawer rows push onto it.
    private readonly Stack<DetailPanelView> panelHistory = new();
    /// Scope the drawer's Pinned view opens with; the header 📌 always means "this conversation".
    private IPop.Web.Components.Chat.PinnedPanel.PinnedScope pinnedScope = IPop.Web.Components.Chat.PinnedPanel.PinnedScope.Room;
    private enum ChatDrawerTab { Overview, Members, Media, Pinned, Calendar }
    private ChatDrawerTab activeDrawerTab = ChatDrawerTab.Overview;
    private Guid? menuRoomId;
    private bool drawerOpen;
    private bool groupInfoOpen;
    private bool pinnedPanelOpen;
    private List<IPop.Application.Chat.PinnedMessageDto> pinnedMessages = [];
    private List<IPop.Application.Chat.AttachmentDto> roomAttachments = [];
    private IReadOnlyList<IPop.Application.Chat.AttachmentDto> AllRoomAttachments =>
        ChatSvc.ActiveMessages.AsEnumerable().Reverse().SelectMany(m => m.Attachments)
            .Concat(roomAttachments)
            .DistinctBy(a => a.Id)
            .ToList();
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

    /// Opens a view fresh (header buttons, phone menu, deep links): history is cleared, so
    /// the view is the drawer's root and shows no Back button unless it is a sub-view.
    private void OpenDetailView(DetailPanelView view)
    {
        panelHistory.Clear();
        // Sub-views opened straight from the header still get a way "up" to the info page.
        if (view != DetailPanelView.Main && view != DetailPanelView.Person) panelHistory.Push(DetailPanelView.Main);
        ShowDetailView(view);
    }

    /// Drills into a view from inside the drawer: the current view goes on the history
    /// stack so Back returns to exactly where the user was.
    private void NavigateDetail(DetailPanelView view)
    {
        if (!drawerOpen) { OpenDetailView(view); return; }
        if (panelView != view) panelHistory.Push(panelView);
        ShowDetailView(view);
    }

    private void ShowDetailView(DetailPanelView view)
    {
        // One drawer at a time: the room/detail panel replaces the global list.
        if (globalDrawerView is not null) CloseGlobalDrawer();
        // profilePerson survives leaving the Person view so Back can return to that card;
        // CloseDetail / OpenRoomCore clear it.
        panelView = view;
        drawerOpen = true;
        groupInfoOpen = (view == DetailPanelView.Main);
        pinnedPanelOpen = (view == DetailPanelView.Pinned);
        if (view == DetailPanelView.Pinned) _ = LoadPinnedMessagesAsync();
        if (view == DetailPanelView.Events) _ = LoadRoomEventsAsync();
        if (view is DetailPanelView.Main or DetailPanelView.Media) _ = LoadRoomAttachmentsAsync();
        if (view == DetailPanelView.Main && ChatSvc.ActiveRoom?.Type == "dm") _ = EnsureDmPersonLoadedAsync();
        StateHasChanged();
    }

    private bool CanGoBackDetail => panelHistory.Count > 0;

    /// Back: pop to the previous view. Returning to a Person view re-shows the contact that
    /// is still loaded; everything else re-runs its loaders via ShowDetailView.
    private void GoBackDetail()
    {
        if (panelHistory.Count == 0) { CloseDetail(); return; }
        ShowDetailView(panelHistory.Pop());
    }

    /// The Back button names where it goes ("Group info", "Members", …).
    private string BackLabel => panelHistory.Count == 0 ? "Back" : DetailViewTitle(panelHistory.Peek());

    private string DetailViewTitle(DetailPanelView view) => view switch
    {
        DetailPanelView.Main => ChatSvc.ActiveRoom?.Type == "dm" ? "Contact info" : "Group info",
        DetailPanelView.Members => "Members",
        DetailPanelView.Pinned => "Pinned messages",
        DetailPanelView.Media => "Files & Media",
        DetailPanelView.Events => "Events",
        DetailPanelView.Person => "Contact info",
        DetailPanelView.GroupSettings => "Group settings",
        _ => "Details"
    };

    private void CloseDetail()
    {
        drawerOpen = false;
        panelView = DetailPanelView.Main;
        panelHistory.Clear();
        groupInfoOpen = false;
        pinnedPanelOpen = false;
        personFromRoom = false;
        profilePerson = null;
        StateHasChanged();
    }

    /// "Message" on a contact card: already in that DM → just get back to typing;
    /// anyone else → open (or create) the DM with them.
    private async Task MessagePerson(string? nik)
    {
        var inThatDm = ChatSvc.ActiveRoom?.Type == "dm"
            && (string.IsNullOrWhiteSpace(nik)
                || ChatSvc.ActiveRoom.Members.Any(m => m.Nik != GetCurrentUserNik() && string.Equals(m.Nik, nik, StringComparison.OrdinalIgnoreCase)));
        CloseDetail();
        if (inThatDm || string.IsNullOrWhiteSpace(nik))
        {
            try { await composerRef.FocusAsync(); } catch { }
            return;
        }
        if (string.Equals(nik, GetCurrentUserNik(), StringComparison.OrdinalIgnoreCase)) return;
        await OpenDmByNik(nik);
    }

    /// The DM partner as a contact card: the directory record once loaded (live presence
    /// from the room member), or a minimal card from the member row while it loads.
    private IPop.Web.Models.Employee? DmCardPerson(IPop.Application.Chat.MemberDto? other)
    {
        if (other is null) return dmPerson;
        var status = string.IsNullOrWhiteSpace(other.PresenceStatus) ? "off" : other.PresenceStatus;
        if (dmPerson is not null && string.Equals(dmPerson.Nik, other.Nik, StringComparison.OrdinalIgnoreCase))
            return dmPerson.Status == status ? dmPerson : dmPerson with { Status = status };
        return new IPop.Web.Models.Employee(other.Name, "—", other.Nik, "—", "—", "—", null, "—", null, "—", "",
            status, IPop.Web.Models.AvatarPalette.For(other.Nik));
    }

    /// A pin from another conversation in the unified Pinned panel: switch room, then jump.
    private async Task OpenPinFromPanel(IPop.Web.Components.Chat.PinnedPanel.PinnedCard card)
    {
        if (globalDrawerView is not null) CloseGlobalDrawer();
        if (drawerOpen) CloseDetail();
        if (card.RoomId == ChatSvc.ActiveRoomId || card.RoomId == openedRoomId)
        {
            await JumpToMessage(card.MessageId);
            return;
        }
        jumpAfterOpen = card.MessageId;
        OpenRoom(card.RoomId);
    }

    private async Task JumpToMessageFromGlobalPinned(Guid messageId)
    {
        CloseGlobalDrawer();
        await JumpToMessage(messageId);
    }

    /// Same compose URL everywhere a contact's Mail button appears.
    private void MailPerson(string? nik)
    {
        if (string.IsNullOrWhiteSpace(nik)) return;
        Nav.NavigateTo(PLinks.Mail(nik));
    }

    private void OpenPinnedSection()
    {
        if (drawerOpen && panelView == DetailPanelView.Pinned) { CloseDetail(); return; }
        pinnedScope = IPop.Web.Components.Chat.PinnedPanel.PinnedScope.Room;
        OpenDetailView(DetailPanelView.Pinned);
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

    private void OpenGroupSettings()
    {
        if (ChatSvc.ActiveRoom?.Type != "group") return;
        if (drawerOpen && panelView == DetailPanelView.GroupSettings) { CloseDetail(); return; }
        // From inside the drawer the settings row drills in; from the header it opens fresh.
        if (drawerOpen) NavigateDetail(DetailPanelView.GroupSettings);
        else OpenDetailView(DetailPanelView.GroupSettings);
    }

    /// Settings changed name/photo/colour: refresh the open room so header + hero follow.
    private async Task OnGroupSettingsChanged()
    {
        await ChatSvc.LoadActiveRoomAsync();
        StateHasChanged();
    }

    /// Archived or left from the settings view: the room is gone from the list.
    private Task OnGroupSettingsRoomRemoved()
    {
        CloseDetail();
        CloseActiveConversation();
        StateHasChanged();
        return Task.CompletedTask;
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

    private void ToggleRoomEventForm() => Nav.NavigateTo(MeetingFromRoom());

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
        pinCursor = 0;
        StateHasChanged();
    }

    private async Task LoadRoomAttachmentsAsync()
    {
        if (ChatSvc.ActiveRoomId is not { } roomId) return;
        try
        {
            var items = await Auth.GetAsync<List<IPop.Application.Chat.AttachmentDto>>($"/api/chat/rooms/{roomId}/attachments");
            if (ChatSvc.ActiveRoomId != roomId) return;
            roomAttachments = items ?? [];
            StateHasChanged();
        }
        catch { }
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
    // v6 composer: "+" menu, sticker sheet, and the mockup's 24-emoji popover / 6-emoji react row.
    private bool plusMenuOpen;
    private bool stickerOpen;
    private string? stickerError;
    private IPop.Web.Components.Chat.StickerEditor? stickerEditor;
    private Guid? reactPickerMsgId;
    private static readonly string[] ReactEmoji = ["👍", "❤️", "😂", "😮", "🙏", "✅"];

    // ---- Mention Autocomplete State (Task 16.2) ----
    private bool mentionPopupOpen;
    private string mentionFilter = "";
    private List<IPop.Application.Chat.MemberDto> mentionSuggestions = [];

    // ---- Emergency Broadcast State (Task 18.1) ----
    private bool emergencyConfirmOpen;
    private string emergencyContent = "";
    private bool IsGroupAdmin => ChatSvc.ActiveRoom?.Type == "group" && ChatSvc.ActiveRoom.Members.Any(m => m.Nik == GetCurrentUserNik() && m.Role is "owner" or "admin");

    /// Backend pin rule: any member may pin, in DMs and groups.
    private bool CanPinInActiveRoom => ChatSvc.ActiveRoom is not null;

    /// Backend unpin rule: any member in a DM; in a group, whoever pinned it or an owner/admin.
    private bool CanUnpin(IPop.Application.Chat.PinnedMessageDto pin)
    {
        var room = ChatSvc.ActiveRoom;
        if (room is null) return false;
        if (room.Type != "group" || IsGroupAdmin) return true;
        var me = room.Members.FirstOrDefault(m => m.Nik == GetCurrentUserNik());
        return me is not null && pin.PinnedByUserId == me.UserId;
    }

    /// Pin/unpin toggle for one message: pinning is open to any member, unpinning follows CanUnpin.
    private bool CanTogglePin(Guid msgId)
    {
        if (!CanPinInActiveRoom) return false;
        var pin = pinnedMessages.FirstOrDefault(p => p.MessageId == msgId);
        return pin is null || CanUnpin(pin);
    }

    // ---- New chat panel ----
    // Replaces the conversation list while open (WhatsApp Web "New chat"). newChatKey
    // forces a fresh NewChatPanel per open so its step/search/selection reset.
    private bool newChatOpen;
    private bool newChatGroupMode;
    private int newChatKey;
    private IReadOnlyList<IPop.Web.Components.Chat.NewChatPerson> newChatPreselected = [];
    private IPop.Web.Components.Chat.NewChatPanel? newChatPanel;

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

    // ---- Profile Drawer (click avatar) ----
    private IPop.Web.Models.Employee? profilePerson;

    private record UserSearchResult(Guid Id, string Nik, string Name, string? Email);

    private enum RoomFilter { All, Unread, Groups }

    // ---- Lifecycle ----

    private string? _lastSubtitle;

    private void UpdateSubtitle()
    {
        var totalUnread = (ChatSvc.DirectRooms is null && ChatSvc.GroupRooms is null) ? 0 : UnreadRoomCount;
        var totalRooms = (ChatSvc.DirectRooms ?? []).Concat(ChatSvc.GroupRooms ?? []).Select(r => r.Id).Distinct().Count();
        var unreadPart = totalUnread == 0 ? "No unread" : totalUnread == 1 ? "1 unread" : $"{totalUnread} unread";
        var convPart = totalRooms == 1 ? "1 conversation" : $"{totalRooms} conversations";
        var subtitle = $"{unreadPart} · {convPart}";
        if (_lastSubtitle != subtitle)
        {
            _lastSubtitle = subtitle;
            InvokeAsync(() => Layout.SetSubtitle(subtitle));
        }
    }

    /// One Escape press can reach both the composer's own key handler and the document-level
    /// shortcut in MainLayout. Both funnel here, so this marks the key consumed and lets the
    /// duplicate within the same tick fall through — otherwise a single press would peel two
    /// layers (e.g. close the drawer *and* the conversation).
    private long lastEscapeTicks;

    private bool TryBeginChatEscape()
    {
        var now = Environment.TickCount64;
        if (now - lastEscapeTicks < 200) return false;
        lastEscapeTicks = now;
        return true;
    }

    private void OnEscapePressed()
    {
        if (!TryBeginChatEscape()) return;
        if (HandleChatEscape()) StateHasChanged();
    }

    /// Escape peels one layer at a time, top-most first, and finally dismisses the open
    /// conversation so the page lands back on the conversation list. Returns true when it
    /// consumed the key, so callers can skip their own work.
    ///
    /// Order mirrors what is visually on top: viewer → modals/popovers → attachment/reply
    /// context → text search → open panel → the conversation itself.
    private bool HandleChatEscape()
    {
        // 1. Full-screen attachment viewer (its own overlay also listens, but the page may
        //    run first; clearing here keeps the two in sync).
        if (viewerAttachment is not null) { CloseAttachment(); return true; }

        // 2. Modal dialogs and popovers layered over the page.
        if (emergencyConfirmOpen) { emergencyConfirmOpen = false; return true; }
        if (leaveConfirmOpen) { leaveConfirmOpen = false; return true; }
        if (pollModalOpen) { pollModalOpen = false; return true; }
        if (forwardOpen) { forwardOpen = false; return true; }
        if (roomEventModalOpen) { roomEventModalOpen = false; return true; }
        if (addMemberOpen) { addMemberOpen = false; return true; }
        // New chat panel: Escape walks back one step (Details → Members → New chat → closed).
        if (newChatOpen)
        {
            if (newChatPanel is not null) _ = newChatPanel.Back();
            else CloseNewChat();
            return true;
        }

        // 3. Composer-adjacent state: menus, mention autocomplete, reply quote, inline edit.
        if (ctxMsgId is not null || plusMenuOpen || pickerOpen || stickerOpen || reactPickerMsgId is not null)
        {
            CloseCtx();
            return true;
        }
        if (mentionPopupOpen) { mentionPopupOpen = false; return true; }
        if (replyToMsg is not null) { CancelReply(); return true; }
        if (editingMsgId is not null) { CancelEdit(); return true; }

        // 4. Thread panel, in-conversation search, then the open side panel.
        if (expandedThreadId is not null)
        {
            expandedThreadId = null;
            threadReplies = [];
            threadTotal = 0;
            threadPage = 1;
            threadReplySeenKeys.Clear();
            return true;
        }
        if (searchOpen)
        {
            searchOpen = false;
            searchResults = [];
            searchQuery = string.Empty;
            return true;
        }
        if (globalDrawerView is not null) { CloseGlobalDrawer(); return true; }
        if (drawerOpen) { CloseDetail(); return true; }

        // 5. Nothing else is open: dismiss the conversation itself.
        return CloseActiveConversation();
    }

    /// Returns to the conversation list, restoring the room's draft the way any other
    /// exit does. Mobile already collapses the list, so it re-opens to show the picker.
    private bool CloseActiveConversation()
    {
        if (ChatSvc.ActiveRoomId is null && ChatSvc.ActiveRoom is null) return false;

        var roomId = ChatSvc.ActiveRoomId;
        if (roomId is not null)
        {
            // Persist before clearing, exactly like a room switch: Half-typed text must
            // survive Escape and come back when the conversation is reopened.
            FlushComposerDraft(roomId.Value);
        }
        messageText = "";
        replyToMsg = null;
        editingMsgId = null;
        expandedThreadId = null;
        threadReplies = [];
        threadReplySeenKeys.Clear();
        unreadDividerMsgId = null;
        CloseAttachment();
        ClearPendingPreviews();
        pendingAttachments = [];
        uploadError = null;
        openedRoomId = null;
        // Keep RoomId out of the reopen path until the URL actually reaches /chat.
        closedRoomId = roomId;
        ChatSvc.CloseActiveRoom();
        if (RoomId is not null) Nav.NavigateTo("/chat", replace: true);
        mobileRoomsOpen = true;
        return true;
    }

    protected override async Task OnInitializedAsync()
    {
        InitializeDraftLifecycle();
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
        // Coming back to /chat from another page (sidebar): ChatSvc still holds the last open
        // room, so its messages render immediately, but nothing here asked to scroll. Without
        // this the reader lands at the start of the conversation instead of the newest message.
        if (RoomId is null && ChatSvc.ActiveRoomId is not null) scrollToBottom = true;
        _ = ChatSvc.LoadArchivedRoomsAsync();
        if (Auth.Me is null) await Auth.LoadMeAsync();
        await ChatSvc.ConnectAsync();
        if (RoomId is not null)
            await OpenRoomCore(RoomId.Value);
        else if (ChatSvc.ActiveRoomId is { } retainedRoom && string.IsNullOrWhiteSpace(QueryValue("new")) && string.IsNullOrWhiteSpace(QueryValue("nik")) && string.IsNullOrWhiteSpace(QueryValue("p")))
            await OpenRoomCore(retainedRoom);
        else if (QueryValue("new") == "group")
            await StartGroupFromQuery();
        else if (QueryValue("new") == "dm")
            StartNewDm();
        else
        {
            var nik = PLinks.Nik(QueryValue("p") ?? QueryValue("nik"));
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
        var nik = PLinks.Nik(QueryValue("p") ?? QueryValue("nik"));
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

    /// Room Escape dismissed. Cleared as soon as the URL reaches /chat, and used to stop the
    /// still-stale RoomId parameter from reopening the room during that same transition.
    private Guid? closedRoomId;

    protected override async Task OnParametersSetAsync()
    {
        // Navigation to /chat has not landed yet, so RoomId still points at the room we just
        // dismissed. Without this the guard below would see a "new" room and reopen it.
        if (RoomId is null)
        {
            closedRoomId = null;
            // Phone: /chat/{id} -> /chat (phone Back or the in-app "‹") means "back to the list".
            // Desktop keeps the room open exactly as before.
            if (openedRoomId is not null && !openingRoom && await IsMobileAsync())
                CloseActiveConversation();
            return;
        }
        if (RoomId == closedRoomId) return;
        if (RoomId == openedRoomId) return;
        await OpenRoomCore(RoomId.Value);
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        try { await JS.InvokeVoidAsync("ipop.icons"); } catch { }
        await MicroAfterRenderAsync();
        // The conversation pane (composer, messages list) only exists once the room's
        // details are in; until then the refs point at nothing and binding would no-op.
        var paneReady = ChatSvc.ActiveRoom is not null && ChatSvc.ActiveRoomId is not null;
        if (firstRender)
        {
            selfRef ??= DotNetObjectReference.Create(this);
            // C4: global Ctrl+K / Cmd+K quick switcher (document-level, bound once)
            try { await JS.InvokeVoidAsync("ipop.chatBindQuickSwitcher", selfRef); } catch { }
            // Read receipts only while this page is up and its tab is attended (see ChatState.CanMarkRead).
            try { await ChatSvc.SetAttentiveAsync(await JS.InvokeAsync<bool>("ipop.watchAttention", selfRef)); } catch { }
            if (!_disposed) await ChatSvc.SetChatPageMountedAsync(true);
        }
        if (!paneReady)
        {
            // Skeleton / empty state: whatever was bound is gone (or about to be), so the
            // next ready pane binds afresh even if a stale render bound the old room's DOM.
            composerPasteBoundId = null;
            composerKeyBoundId = null;
            scrollWatchBoundId = null;
        }
        // Bind paste + drag-drop to the composer actually in the DOM (rebinds when it changes).
        if (paneReady && composerWrapRef.Id is { } wrapId && wrapId != composerPasteBoundId)
        {
            try
            {
                selfRef ??= DotNetObjectReference.Create(this);
                await JS.InvokeVoidAsync("ipop.chatBindPaste", composerWrapRef, selfRef);
                composerPasteBoundId = wrapId;
            }
            catch { /* composer not yet rendered (empty state) — retry next frame */ }
        }
        // The textarea is absent while recording voice; it comes back as a new element.
        if (paneReady && composerRef.Id is { } keyId && keyId != composerKeyBoundId && !isRecordingVoice)
        {
            try
            {
                selfRef ??= DotNetObjectReference.Create(this);
                await JS.InvokeVoidAsync("ipop.chatBindComposer", composerRef, selfRef);
                composerKeyBoundId = keyId;
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
        await BindPhoneLongPressAsync();
        if (paneReady && messagesRef.Id is { } listId && listId != scrollWatchBoundId)
        {
            selfRef ??= DotNetObjectReference.Create(this);
            try
            {
                await JS.InvokeVoidAsync("ipop.bindScrollWatch", messagesRef, selfRef, 400);
                scrollWatchBoundId = listId;
            }
            catch { /* older cached ipop.js — the jump button just stays hidden */ }
            // ipop-22: project/task chip click delegation lives on the same messages element,
            // so it re-binds with the scroll watch on room switch instead of every render.
            try { await JS.InvokeVoidAsync("ipop.bindMentionChips", messagesRef, selfRef); } catch { }
        }
        if (anchorFromBottom is { } keep && ChatSvc.ActiveMessages.Count > anchorBaseCount)
        {
            // Older rows were prepended: hold the reader's distance from the bottom so the
            // message they were looking at stays put (LoadOlder).
            anchorFromBottom = null;
            try
            {
                var info = await JS.InvokeAsync<ScrollInfo>("ipop.scrollInfo", messagesRef);
                var clientHeight = info.ScrollHeight - info.ScrollTop - info.FromBottom;
                await JS.InvokeVoidAsync("ipop.setScrollTop", messagesRef, Math.Max(0, info.ScrollHeight - clientHeight - keep));
            }
            catch { }
        }
        else if (scrollToBottom)
        {
            scrollToBottom = false;
            pendingIncoming = 0;
            await ScrollToBottom();
        }
        else if (pendingIncoming > 0)
            await FollowNewMessages();
        if (focusEditPending && editingMsgId is not null)
        {
            // Caret at the end so a long message can be amended without hunting for it.
            focusEditPending = false;
            try { await JS.InvokeVoidAsync("ipop.setCaret", editRef, editText.Length, editText.Length); } catch { }
        }
        else if (focusComposerPending && ChatSvc.ActiveRoomId is not null && !openingRoom && editingMsgId is null)
        {
            focusComposerPending = false;
            var force = focusComposerOnTouch;
            focusComposerOnTouch = false;
            if (force || !await IsTouchLayoutAsync())
                try { await composerRef.FocusAsync(); } catch { }
        }
        await SyncMobileChromeAsync();
        await A11yAfterRenderAsync(paneReady);
    }

    private bool scrollToBottom;

    // Set when a room finishes opening and after a send; desktop focuses the composer on
    // the next render. Phones/tablets skip it so the on-screen keyboard doesn't pop up.
    private bool focusComposerPending;
    /// Reply/edit focus even on touch layouts, where the keyboard is exactly what's wanted.
    private bool focusComposerOnTouch;
    private bool focusEditPending;
    private ElementReference editRef;

    private async Task<bool> IsTouchLayoutAsync()
    {
        if (await IsMobileAsync()) return true;
        try
        {
            // Tablets are wider than the phone breakpoint but still raise a keyboard.
            return await JS.GetValueAsync<int>("navigator.maxTouchPoints") > 0
                && await JS.InvokeAsync<double>("ipop.viewportWidth") <= 1024;
        }
        catch { return false; }
    }

    // ── Phone layout (<= 767px): list / open room are one screen each (CONTRACT.md) ──
    // The open room is encoded in the URL (/chat/{id}), so phone Back pops to /chat and
    // OnParametersSetAsync closes the room. Side panels/drawers get their own history entry
    // via ipop.mobile.pushDetail so Back peels them first.
    private bool mTabbarHidden;
    private bool mPanelPushed;
    private bool mRoomPushed;
    private bool mHeaderMenuOpen;
    private Guid? mToolsMsgId;

    private async Task<bool> IsMobileAsync()
    {
        try { return await JS.InvokeAsync<bool>("ipop.mobile.isMobile"); }
        catch { return false; }
    }

    private async Task SyncMobileChromeAsync()
    {
        try
        {
            var roomOpen = ChatSvc.ActiveRoomId is not null && !newChatOpen;
            if (roomOpen != mTabbarHidden)
            {
                mTabbarHidden = roomOpen;
                await JS.InvokeVoidAsync("ipop.mobile.hideTabbar", roomOpen);
            }
            var panelOpen = drawerOpen || globalDrawerView is not null;
            if (panelOpen && !mPanelPushed)
            {
                selfRef ??= DotNetObjectReference.Create(this);
                mPanelPushed = await JS.InvokeAsync<bool>("ipop.mobile.pushDetail", selfRef, nameof(MobilePanelBack));
            }
            else if (!panelOpen && mPanelPushed)
            {
                mPanelPushed = false;
                await JS.InvokeVoidAsync("ipop.mobile.popDetail");
            }
        }
        catch { /* older cached mobile.js or prerender — phone chrome just stays as-is */ }
    }

    [JSInvokable]
    public Task OnAttentionChanged(bool attentive) => ChatSvc.SetAttentiveAsync(attentive);

    /// Phone Back while a side panel / global drawer is open: close it, keep the room.
    [JSInvokable]
    public Task MobilePanelBack() => InvokeAsync(() =>
    {
        mPanelPushed = false;
        if (globalDrawerView is not null) CloseGlobalDrawer();
        // Phone Back walks the drawer's own history first (re-pushed on the next render);
        // at the drawer's root it closes the drawer and keeps the room.
        if (drawerOpen && CanGoBackDetail) GoBackDetail();
        else if (drawerOpen) CloseDetail();
        StateHasChanged();
    });

    /// In-app "‹" in the conversation header. When we pushed /chat/{id} ourselves, go back
    /// through history so the stack stays balanced; a deep link just closes in place.
    private async Task MobileBackToList()
    {
        mHeaderMenuOpen = false;
        mToolsMsgId = null;
        if (mRoomPushed)
        {
            mRoomPushed = false;
            try { await JS.InvokeVoidAsync("history.back"); return; } catch { }
        }
        CloseActiveConversation();
    }

    /// Tap on a message (phone) shows its react/reply/pin/… toolbar; tap again hides it.
    /// Desktop has no CSS for .m-tools, so this is inert there.
    private void ToggleMobileTools(Guid msgId) => mToolsMsgId = mToolsMsgId == msgId ? null : msgId;

    private bool _disposed;

    public void Dispose()
    {
        DisposeDraftLifecycle();
        CancelComposerUpload();
        // ChatState outlives the page: a search left in the box must not stay applied.
        LeaveRoomSearch();
        if (isRecordingVoice) MicroVoiceStopped();
        _disposed = true;
        DisposePhone();
        A11yDispose();
        _ = ChatSvc.SetChatPageMountedAsync(false);
        try { _ = JS.InvokeVoidAsync("ipop.unwatchAttention"); } catch { }
        Layout.EscapePressed -= OnEscapePressed;
        try { _ = JS.InvokeVoidAsync("ipop.chatUnbindPaste", composerWrapRef); } catch { }
        try { _ = JS.InvokeVoidAsync("ipop.chatUnbindComposer", composerRef); } catch { }
        try { _ = JS.InvokeVoidAsync("ipop.unbindScrollWatch", messagesRef); } catch { }
        // ipop-22: unbind chip delegation
        try { _ = JS.InvokeVoidAsync("ipop.unbindMentionChips", messagesRef); } catch { }
        try { _ = JS.InvokeVoidAsync("ipop.chatUnbindQuickSwitcher"); } catch { }
        try { _ = JS.InvokeVoidAsync("ipop.mobile.hideTabbar", false); } catch { }
        if (mPanelPushed) try { _ = JS.InvokeVoidAsync("ipop.mobile.clearDetail"); } catch { }
        foreach (var p in pendingAttachments)
            if (!string.IsNullOrEmpty(p.PreviewUrl)) _ = JS.InvokeVoidAsync("ipop.revokeBlob", p.PreviewUrl);
        selfRef?.Dispose();
        if (_navSubscribed) Nav.LocationChanged -= OnLocationChanged;
        ChatSvc.PinnedChanged -= OnPinnedChanged;
        ChatSvc.ThreadReplyReceived -= OnThreadReplyReceived;
        UnsubscribeGroupCall();
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
            // Removed from (or left) the open room. Same transition as Escape-dismiss: keep
            // RoomId out of the reopen path until the URL lands on /chat.
            closedRoomId = openedRoomId;
            openedRoomId = null;
            if (RoomId is not null) Nav.NavigateTo("/chat", replace: true);
        }
        A11yOnStateChanged();
        UpdateSubtitle();
        // One hub event raises StateChanged + RoomListChanged (and often TypingChanged) back to
        // back; each used to re-render the whole page. Yield once and paint them together.
        if (renderQueued) return;
        renderQueued = true;
        _ = InvokeAsync(async () =>
        {
            await Task.Yield();
            renderQueued = false;
            StateHasChanged();
        });
    }

    private bool renderQueued;

    private void OnMessageReceived(IPop.Application.Chat.MessageDto msg)
    {
        // Soft: never drag a reader who scrolled up back to the bottom (OnAfterRenderAsync).
        AnnounceIncoming(msg);
        pendingIncoming++;
        if (msg.Attachments.Count > 0 && msg.RoomId == ChatSvc.ActiveRoomId)
            _ = LoadRoomAttachmentsAsync();
        _ = InvokeAsync(StateHasChanged);
    }

    /// Incoming messages since the last render, followed softly after it.
    private int pendingIncoming;

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

    /// Calendar everywhere: the one meeting form with this group (or the other person in a DM). Created from a
    /// message, its first line becomes the title and a long message goes into the notes.
    private string MeetingFromRoom(string? fromMessage = null)
    {
        var room = ChatSvc.ActiveRoom;
        if (room is null) return CalLinks.NewMeeting();
        string? title = null;
        if (!string.IsNullOrWhiteSpace(fromMessage))
        {
            var first = fromMessage.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "";
            title = first.Length > 120 ? first[..120] : first;
        }
        var notes = fromMessage is { Length: > 120 } ? fromMessage : null;
        if (room.Type == "dm")
        {
            var other = room.Members.FirstOrDefault(m => m.Nik != GetCurrentUserNik());
            return CalLinks.NewMeeting(title, other is null ? null : [other.Nik], notes: notes, from: $"From chat with {other?.Name ?? "this person"}");
        }
        return CalLinks.NewMeeting(title, roomId: room.Id, notes: notes, from: $"From chat {room.Name ?? "group"}");
    }

}
