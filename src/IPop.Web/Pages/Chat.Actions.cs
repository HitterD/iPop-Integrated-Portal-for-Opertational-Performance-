// Chat page — message actions (context menu, reply, edit, forward, reactions, threads), drawer panels, DM/group creation, room administration.
//
// Part of the Chat partial class. The class was split by responsibility out of a single
// ~1,500-line @code block; members were moved verbatim, never renamed or re-scoped.
// _Imports.razor only reaches .razor files, so usings are restated in each part.
using System.Net.Http.Json;
using IPop.Web.Services;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;

namespace IPop.Web.Pages;

public partial class Chat
{
    // ---- Context Menu (Task 13.1) ----

    private void ShowCtx(Guid msgId, Microsoft.AspNetCore.Components.Web.MouseEventArgs e)
    {
        ctxMsgId = msgId;
        ctxX = e.ClientX;
        ctxY = e.ClientY;
    }

    private void CloseCtx() => ctxMsgId = null;

    // ---- Reply (Task 13.2) ----

    private void StartReply(Guid msgId)
    {
        replyToMsg = ChatSvc.ActiveMessages.FirstOrDefault(m => m.Id == msgId);
        ctxMsgId = null;
    }

    private void CancelReply() => replyToMsg = null;

    // ---- Edit & Delete (Task 13.3) ----

    private void StartEdit(Guid msgId)
    {
        var m = ChatSvc.ActiveMessages.FirstOrDefault(x => x.Id == msgId);
        if (m is not null)
        {
            editingMsgId = msgId;
            editText = m.Content;
        }
        ctxMsgId = null;
    }

    private async Task SaveEdit()
    {
        if (editingMsgId is null) return;
        await Auth.SendAsync(HttpMethod.Put, $"/api/chat/rooms/{ChatSvc.ActiveRoomId}/messages/{editingMsgId}", new { content = editText });
        editingMsgId = null;
        editText = "";
    }

    private void CancelEdit()
    {
        editingMsgId = null;
        editText = "";
    }

    private async Task ConfirmDelete(Guid msgId)
    {
        ctxMsgId = null;
        await Auth.SendAsync(HttpMethod.Delete, $"/api/chat/rooms/{ChatSvc.ActiveRoomId}/messages/{msgId}");
    }

    private bool CanEditMsg(Guid msgId)
    {
        var m = ChatSvc.ActiveMessages.FirstOrDefault(x => x.Id == msgId);
        return m is not null && m.SenderNik == GetCurrentUserNik() && !m.IsDeleted && (DateTime.UtcNow - m.CreatedAt).TotalHours < 24;
    }

    // ---- Forward (Task 13.4) ----

    private void StartForward(Guid msgId)
    {
        forwardMsgId = msgId;
        forwardOpen = true;
        ctxMsgId = null;
    }

    private async Task ForwardTo(Guid targetRoomId)
    {
        if (forwardMsgId is null) return;
        var res = await Auth.SendAsync(HttpMethod.Post, $"/api/chat/rooms/{ChatSvc.ActiveRoomId}/messages/{forwardMsgId}/forward", new { targetRoomId });
        if (res.IsSuccessStatusCode)
        {
            Toasts.Show("Pesan berhasil diteruskan", ToastKind.Success);
        }
        else
        {
            Toasts.Show("Gagal meneruskan pesan", ToastKind.Error);
        }
        forwardOpen = false;
        forwardMsgId = null;
    }

    private async Task OnPollCreated(string pollPayload)
    {
        pollModalOpen = false;
        if (ChatSvc.ActiveRoomId is null) return;
        await ChatSvc.SendMessageAsync(pollPayload, null, "poll");
        scrollToBottom = true;
    }

    private async Task StartVoiceRecord()
    {
        var ok = await JS.InvokeAsync<bool>("ipop.startAudioRecord");
        if (ok)
        {
            isRecordingVoice = true;
            StateHasChanged();
        }
        else
        {
            Toasts.Show("Izin mikrofon diperlukan untuk merekam suara.", ToastKind.Error);
        }
    }

    private async Task StopAndSendVoiceRecord()
    {
        selfRef ??= DotNetObjectReference.Create(this);
        await JS.InvokeVoidAsync("ipop.stopAudioRecord", selfRef);
        isRecordingVoice = false;
        StateHasChanged();
    }

    private async Task CancelVoiceRecord()
    {
        await JS.InvokeVoidAsync("ipop.cancelAudioRecord");
        isRecordingVoice = false;
        StateHasChanged();
    }

    [Microsoft.JSInterop.JSInvokable]
    public async Task OnVoiceRecorded(string base64Audio, long fileSize)
    {
        if (string.IsNullOrWhiteSpace(base64Audio) || ChatSvc.ActiveRoomId is null) return;
        try
        {
            var bytes = Convert.FromBase64String(base64Audio);
            using var form = new MultipartFormDataContent();
            var byteContent = new ByteArrayContent(bytes);
            byteContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("audio/webm");
            form.Add(byteContent, "file", $"voice_{DateTime.UtcNow:yyyyMMdd_HHmmss}.webm");

            var res = await Auth.SendMultipartAsync($"/api/chat/rooms/{ChatSvc.ActiveRoomId}/attachments", form);
            if (res.IsSuccessStatusCode)
            {
                var att = await res.Content.ReadFromJsonAsync<IPop.Application.Chat.AttachmentDto>();
                if (att is not null)
                {
                    await ChatSvc.SendMessageAsync("(voice)", null, "text", [att.Id], [att]);
                    scrollToBottom = true;
                }
            }
        }
        catch (Exception ex)
        {
            Toasts.Show("Gagal mengirim voice message: " + ex.Message, ToastKind.Error);
        }
    }

    // ---- Reactions (Task 13.5) ----

    private async Task QuickReact(Guid msgId, string emoji)
    {
        ctxMsgId = null;
        if (ChatSvc.IsPending(msgId) || ChatSvc.IsFailed(msgId)) return; // not on the server yet
        // Flip the pill immediately, then let the hub's reactionChanged settle the count.
        // Reloading page 1 here used to discard older pages and the scroll position.
        ChatSvc.ToggleReactionLocal(msgId, emoji);
        var res = await Auth.SendAsync(HttpMethod.Post, $"/api/chat/messages/{msgId}/reactions", new { emoji });
        if (!res.IsSuccessStatusCode) ChatSvc.ToggleReactionLocal(msgId, emoji); // undo
    }

    // ---- Thread (Task 13.6) ----

    // ipop-37: dedup set for live thread replies, keyed by IdempotencyKey — the hub
    // re-broadcasts every send to all group members including the sender's optimistic
    // echo, so without this a sender sees their own reply twice. Scoped to the
    // currently-expanded thread: cleared on every ToggleThread transition (ipop-37b),
    // since keys left behind would drop legitimate pushes after a collapse/expand cycle.
    private readonly HashSet<Guid> threadReplySeenKeys = [];

    /// ipop-37: hub 'threadReply' events land here. Only append while that exact thread
    /// is expanded; dedup by IdempotencyKey covers both own-sends echoed back and races.
    private void OnThreadReplyReceived(IPop.Application.Chat.MessageDto reply) => _ = InvokeAsync(() =>
    {
        if (!threadReplySeenKeys.Add(reply.IdempotencyKey)) return;
        if (expandedThreadId != reply.ThreadParentId) return;
        threadReplies.Add(reply);
        // ipop-48: a live reply grows the list without the server total moving, which would
        // make HasMoreThreadReplies read as "nothing more" one reply too early. Keep them in step.
        threadTotal++;
        StateHasChanged();
    });

    private async Task ToggleThread(Guid parentId)
    {
        if (expandedThreadId == parentId)
        {
            expandedThreadId = null;
            threadReplies = [];
            threadTotal = 0;
            threadPage = 1;
            // ipop-37b: scope the dedup set to the currently-expanded thread. Leaving
            // keys behind would drop legitimate live pushes after a collapse/expand
            // cycle (re-arming the original bug) and grow unbounded on a long-lived page.
            threadReplySeenKeys.Clear();
            return;
        }
        expandedThreadId = parentId;
        var result = await Auth.GetAsync<IPop.Application.Chat.PagedResult<IPop.Application.Chat.MessageDto>>($"/api/chat/messages/{parentId}/thread?pageSize=50");
        // ipop-37c: superseded-request guard — a room switch or collapse during the fetch
        // must not resurrect stale thread state (a hub push in that window lands in the
        // OLD list and gets cleared; without this check its reassign would undo the reset).
        if (expandedThreadId != parentId)
            return;
        threadReplySeenKeys.Clear();
        threadReplies = result?.Items.ToList() ?? [];
        threadTotal = result?.TotalCount ?? 0;
        threadPage = 1;
        // Re-seed the dedup set with what we just fetched so the next live push for
        // these replies is not double-appended (server acks may not have arrived yet).
        foreach (var r in threadReplies)
            threadReplySeenKeys.Add(r.IdempotencyKey);
    }

    /// ipop-48: fetch the next page of thread replies. Dedup goes through the SAME
    /// threadReplySeenKeys set the live-push path uses — a reply that arrived over the hub
    /// while page 1 was on screen is also on page 2 from the server, and must not double up.
    private async Task LoadMoreThreadReplies()
    {
        if (expandedThreadId is not { } parentId || threadLoadingMore) return;
        threadLoadingMore = true;
        try
        {
            var next = threadPage + 1;
            var result = await Auth.GetAsync<IPop.Application.Chat.PagedResult<IPop.Application.Chat.MessageDto>>($"/api/chat/messages/{parentId}/thread?page={next}&pageSize=50");
            // Same superseded-request guard as the initial load: a collapse or a switch to
            // another thread mid-fetch must not graft these replies onto the wrong thread.
            if (expandedThreadId != parentId) return;
            threadPage = next;
            if (result is null) return;
            // A live push already counted itself into threadTotal; the server's figure is
            // authoritative for what remains, so take the larger of the two rather than
            // letting either one strand the load-more row.
            threadTotal = Math.Max(threadTotal, result.TotalCount);
            foreach (var r in result.Items)
                if (threadReplySeenKeys.Add(r.IdempotencyKey))
                    threadReplies.Add(r);
        }
        finally
        {
            threadLoadingMore = false;
        }
    }

    // ---- Group Info (Task 15.1) ----

    private async Task LeaveGroup()
    {
        await Auth.SendAsync(HttpMethod.Delete, $"/api/chat/rooms/{ChatSvc.ActiveRoomId}/leave");
        groupInfoOpen = false;
        await ChatSvc.LoadRoomsAsync();
    }

    // ---- Pinned Panel (Task 15.2) ----

    private async Task TogglePinPanel()
    {
        ToggleDrawerTab(ChatDrawerTab.Pinned);
    }

    /// ipop-34: hub 'pinnedChanged' events now land here. Only fetch while the panel is
    /// open — a closed panel must not fire requests on every pin/unpin in any room.
    private void OnPinnedChanged() => _ = InvokeAsync(async () =>
    {
        if ((!drawerOpen || activeDrawerTab != ChatDrawerTab.Pinned) || ChatSvc.ActiveRoomId is null) return;
        await LoadPinnedMessagesAsync();
    });

    // ---- Search (Task 15.3) ----

    private async Task DoSearch()
    {
        if (searchQuery.Length < 2 || ChatSvc.ActiveRoomId is null) { searchResults = []; return; }
        var result = await Auth.GetAsync<IPop.Application.Chat.PagedResult<IPop.Application.Chat.MessageDto>>($"/api/chat/rooms/{ChatSvc.ActiveRoomId}/search?q={Uri.EscapeDataString(searchQuery)}&pageSize=20");
        searchResults = result?.Items.ToList() ?? [];
    }

    // ---- Attachment viewer ----

    private void OpenAttachment(IPop.Application.Chat.AttachmentDto att, IReadOnlyList<IPop.Application.Chat.AttachmentDto> siblings)
    {
        viewerSiblings = siblings;
        viewerAttachment = att;
    }

    private void CloseAttachment()
    {
        viewerAttachment = null;
        viewerSiblings = null;
    }

    /// Messages sent with files only carry the "(attachment)" placeholder as content —
    /// the attachment tiles say everything, so the placeholder is hidden.
    private static bool IsAttachmentPlaceholder(IPop.Application.Chat.MessageDto m) =>
        m.Attachments.Count > 0 &&
        (string.IsNullOrWhiteSpace(m.Content) || m.Content.Trim() == "(attachment)");

    /// Multiple images in one message tile into a grid; mixed or single sets stack.
    private static string AttGalleryClass(IPop.Application.Chat.MessageDto m)
    {
        var images = m.Attachments.Count(ChatAttachments.IsImage);
        if (images < 2 || images != m.Attachments.Count) return "stack";
        return images == 2 ? "grid-2" : "grid-3";
    }

    // ---- Emergency Broadcast (Task 18.1) ----

    private async Task SendEmergency()
    {
        if (string.IsNullOrWhiteSpace(emergencyContent) || ChatSvc.ActiveRoomId is null) return;
        await Auth.SendAsync(HttpMethod.Post, $"/api/chat/rooms/{ChatSvc.ActiveRoomId}/emergency", new { content = emergencyContent.Trim() });
        emergencyContent = "";
        emergencyConfirmOpen = false;
    }

    // ---- New DM Methods ----
    private async Task SearchDmUsers()
    {
        if (newDmSearchQuery.Length < 2) { newDmSearchResults = []; return; }
        var results = await Auth.GetAsync<List<UserSearchResult>>($"/api/chat/users/search?q={Uri.EscapeDataString(newDmSearchQuery)}");
        newDmSearchResults = results ?? [];
    }

    private async Task CreateDmWith(UserSearchResult user)
    {
        newDmModalOpen = false;
        var response = await Auth.SendAsync(HttpMethod.Post, "/api/chat/rooms/dm", new { recipientId = user.Id });
        if (response.IsSuccessStatusCode)
        {
            var room = await response.Content.ReadFromJsonAsync<IPop.Application.Chat.ChatRoomDto>();
            if (room is not null)
            {
                await ChatSvc.LoadRoomsAsync();
                activeTab = RoomTab.Direct;
                OpenRoom(room.Id);
            }
        }
    }

    // ---- New Group Methods ----
    private async Task SearchGroupUsers()
    {
        if (newGroupSearchQuery.Length < 2) { newGroupSearchResults = []; return; }
        var results = await Auth.GetAsync<List<UserSearchResult>>($"/api/chat/users/search?q={Uri.EscapeDataString(newGroupSearchQuery)}");
        newGroupSearchResults = (results ?? []).Where(u => !newGroupSelectedMembers.Any(m => m.Id == u.Id)).ToList();
    }

    private void AddGroupMember(UserSearchResult user)
    {
        if (!newGroupSelectedMembers.Any(m => m.Id == user.Id))
            newGroupSelectedMembers.Add(user);
        newGroupSearchQuery = "";
        newGroupSearchResults = [];
    }

    private void RemoveGroupMember(UserSearchResult user)
    {
        newGroupSelectedMembers.RemoveAll(m => m.Id == user.Id);
    }

    private async Task CreateGroup()
    {
        if (string.IsNullOrWhiteSpace(newGroupName) || newGroupSelectedMembers.Count < 2) return;
        newGroupModalOpen = false;
        var response = await Auth.SendAsync(HttpMethod.Post, "/api/chat/rooms/group", new
        {
            name = newGroupName.Trim(),
            memberIds = newGroupSelectedMembers.Select(m => m.Id).ToArray()
        });
        if (response.IsSuccessStatusCode)
        {
            var room = await response.Content.ReadFromJsonAsync<IPop.Application.Chat.ChatRoomDto>();
            if (room is not null)
            {
                await ChatSvc.LoadRoomsAsync();
                activeTab = RoomTab.Groups;
                OpenRoom(room.Id);
            }
        }
    }

    // ---- Add Member to Group (Fix 4) ----

    private async Task SearchAddMember()
    {
        if (addMemberQuery.Length < 2) { addMemberResults = []; return; }
        var results = await Auth.GetAsync<List<UserSearchResult>>($"/api/chat/users/search?q={Uri.EscapeDataString(addMemberQuery)}");
        addMemberResults = results ?? [];
    }

    private async Task AddMemberToGroup(UserSearchResult user)
    {
        if (ChatSvc.ActiveRoomId is null) return;
        await Auth.SendAsync(HttpMethod.Post, $"/api/chat/rooms/{ChatSvc.ActiveRoomId}/members", new { userId = user.Id });
        addMemberOpen = false;
        addMemberQuery = "";
        addMemberResults = [];
        await ChatSvc.LoadActiveRoomAsync();
    }

    // ---- Mute Notifications (Fix 5) ----

    private async Task ToggleMuteRoom()
    {
        if (ChatSvc.ActiveRoomId is null) return;
        var currentRoom = ChatSvc.GroupRooms.Concat(ChatSvc.DirectRooms).FirstOrDefault(r => r.Id == ChatSvc.ActiveRoomId);
        if (currentRoom?.IsMuted == true)
            await Auth.SendAsync(HttpMethod.Delete, $"/api/chat/rooms/{ChatSvc.ActiveRoomId}/mute");
        else
            await Auth.SendAsync(HttpMethod.Post, $"/api/chat/rooms/{ChatSvc.ActiveRoomId}/mute", new { duration = "forever" });
        await ChatSvc.LoadRoomsAsync();
    }

    // ---- Archive Room (Fix 6) ----

    private async Task ArchiveRoom()
    {
        if (ChatSvc.ActiveRoomId is null) return;
        await Auth.SendAsync(HttpMethod.Post, $"/api/chat/rooms/{ChatSvc.ActiveRoomId}/archive");
        groupInfoOpen = false;
        await ChatSvc.LoadRoomsAsync();
    }

    // ---- Read receipts toggle (reciprocal for DMs) ----

    private async Task ToggleReadReceiptsRoom()
    {
        if (ChatSvc.ActiveRoomId is null) return;
        var current = ChatSvc.GroupRooms.Concat(ChatSvc.DirectRooms)
            .FirstOrDefault(r => r.Id == ChatSvc.ActiveRoomId);
        bool next = !(current?.ReadReceiptsEnabled ?? true);
        await Auth.SendAsync(HttpMethod.Put, $"/api/chat/rooms/{ChatSvc.ActiveRoomId}/read-receipts", new { enabled = next });
        await ChatSvc.LoadRoomsAsync();
        await ChatSvc.LoadActiveRoomAsync();
    }

    private async Task TogglePin(IPop.Application.Chat.RoomListDto room)
    {
        if (room.IsPinned) await Auth.SendAsync(HttpMethod.Delete, $"/api/chat/rooms/{room.Id}/pin");
        else await Auth.SendAsync(HttpMethod.Post, $"/api/chat/rooms/{room.Id}/pin", new { });
        await ChatSvc.LoadRoomsAsync();
    }

    private async Task UnarchiveRoom(Guid roomId)
    {
        await Auth.SendAsync(HttpMethod.Delete, $"/api/chat/rooms/{roomId}/archive");
        await ChatSvc.LoadRoomsAsync();
        await ChatSvc.LoadArchivedRoomsAsync();
    }

    // ipop-47b: pager for the archived list — rooms past the first page were unreachable
    // by any other path (search and the main list exclude archived ids).
    private async Task LoadMoreArchived() => await ChatSvc.LoadMoreArchivedAsync();

    // ---- Pin Message (Fix 7) ----

    private async Task PinMessage(Guid msgId)
    {
        ctxMsgId = null;
        if (ChatSvc.ActiveRoomId is null) return;
        await Auth.SendAsync(HttpMethod.Post, $"/api/chat/rooms/{ChatSvc.ActiveRoomId}/pins/{msgId}");
        if (drawerOpen && activeDrawerTab == ChatDrawerTab.Pinned)
            await LoadPinnedMessagesAsync();
    }

    private async Task StartCallWith(string? nik)
    {
        if (string.IsNullOrWhiteSpace(nik)) return;
        var room = await CallStateSvc.CreateAsync(nik);
        if (room is null)
        {
            Toasts.Show("Tidak dapat memulai panggilan. Pastikan rekan Anda terdaftar dan aktif.", ToastKind.Error);
        }
    }
}
