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
        ctxNeedsPlacement = true;
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
            Toasts.Show("Message forwarded successfully.", ToastKind.Success);
        }
        else
        {
            Toasts.Show("Failed to forward message.", ToastKind.Error);
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
        selfRef ??= DotNetObjectReference.Create(this);
        var ok = await JS.InvokeAsync<bool>("ipop.startAudioRecord", selfRef);
        if (ok)
        {
            isRecordingVoice = true;
            StateHasChanged();
        }
        else
        {
            Toasts.Show("Microphone permission is required to record audio.", ToastKind.Error);
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
    public async Task OnVoiceRecorded(string base64Audio, long fileSize, long durationMs)
    {
        isRecordingVoice = false;
        await InvokeAsync(StateHasChanged);
        if (durationMs > 120000 || string.IsNullOrWhiteSpace(base64Audio) || ChatSvc.ActiveRoomId is null) return;
        try
        {
            var bytes = Convert.FromBase64String(base64Audio);
            using var form = new MultipartFormDataContent();
            form.Add(new StringContent(ChatSvc.ActiveRoomId.Value.ToString()), "roomId");
            var byteContent = new ByteArrayContent(bytes);
            byteContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("audio/webm");
            form.Add(byteContent, "file", $"voice_{DateTime.UtcNow:yyyyMMdd_HHmmss}.webm");

            var res = await Auth.SendMultipartAsync("/api/chat/attachments/upload", form);
            if (res.IsSuccessStatusCode)
            {
                var attachments = await res.Content.ReadFromJsonAsync<List<IPop.Application.Chat.AttachmentDto>>();
                if (attachments?.FirstOrDefault() is { } att)
                {
                    await ChatSvc.SendMessageAsync("(voice)", null, "text", [att.Id], [att]);
                    scrollToBottom = true;
                }
            }
        }
        catch (Exception ex)
        {
            Toasts.Show("Failed to send voice message: " + ex.Message, ToastKind.Error);
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

    private bool leaveConfirmOpen;
    private string leaveConfirmMessage = "";

    private void LeaveGroup()
    {
        if (ChatSvc.ActiveRoomId is null || ChatSvc.ActiveRoom is null) return;
        var role = ChatSvc.ActiveRoom.CurrentUserRole;
        var others = ChatSvc.ActiveRoom.Members.Where(m => m.Nik != GetCurrentUserNik()).ToList();
        if (role == "owner" && others.Count > 0)
        {
            var next = others.OrderBy(m => m.Role == "admin" ? 0 : 1).First();
            leaveConfirmMessage = $"You are the group owner. Ownership will transfer to {next.Name}. Leave {ChatSvc.ActiveRoom.Name}?";
        }
        else if (role == "owner" && others.Count == 0)
            leaveConfirmMessage = $"You are the last member. The group will be archived. Leave {ChatSvc.ActiveRoom.Name}?";
        else
            leaveConfirmMessage = $"Leave {ChatSvc.ActiveRoom.Name}? This conversation will be removed from your list.";
        leaveConfirmOpen = true;
    }

    private async Task ConfirmLeaveGroup()
    {
        leaveConfirmOpen = false;
        await Auth.SendAsync(HttpMethod.Delete, $"/api/chat/rooms/{ChatSvc.ActiveRoomId}/leave");
        CloseDetail();
        await ChatSvc.LoadRoomsAsync();
    }

    // ---- Pinned Panel (Task 15.2) ----

    private async Task TogglePinPanel()
    {
        ToggleDrawerTab(ChatDrawerTab.Pinned);
    }

    /// ipop-34: hub 'pinnedChanged' events now land here. Always refresh the list so
    /// the sticky pinned bar stays live; the drawer tab reads the same cached list.
    private void OnPinnedChanged() => _ = InvokeAsync(async () =>
    {
        if (ChatSvc.ActiveRoomId is null) return;
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
    private void OpenDmFromList(Guid roomId)
    {
        newDmModalOpen = false;
        activeTab = RoomTab.Direct;
        OpenRoom(roomId);
    }

    private void ClearDmSearch()
    {
        newDmSearchQuery = "";
        newDmSearchResults = [];
    }

    private void ClearGroupSearch()
    {
        newGroupSearchQuery = "";
        newGroupSearchResults = [];
    }

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
        CloseDetail();
        await ChatSvc.LoadRoomsAsync();
    }

    private bool dmUserBlocked;

    private async Task ClearRoomHistory()
    {
        if (ChatSvc.ActiveRoomId is not { } roomId) return;
        if (!await Confirm.AskAsync("Clear conversation history?", "Old messages will disappear only for you.", "Clear history")) return;
        using var res = await Auth.SendAsync(HttpMethod.Post, $"/api/chat/rooms/{roomId}/clear-history");
        if (!res.IsSuccessStatusCode) { Toasts.Show("Could not clear history.", ToastKind.Error); return; }
        await ChatSvc.LoadActiveRoomAsync();
        CloseDetail();
        Toasts.Show("Conversation history cleared for you.", ToastKind.Success);
    }

    private async Task ToggleBlockDmUser()
    {
        var other = ChatSvc.ActiveRoom?.Members.FirstOrDefault(m => m.Nik != GetCurrentUserNik());
        if (other is null) return;
        var method = dmUserBlocked ? HttpMethod.Delete : HttpMethod.Post;
        if (!dmUserBlocked && !await Confirm.AskAsync("Block contact?", $"{other.Name} will no longer be able to send you direct messages.", "Block")) return;
        using var res = await Auth.SendAsync(method, $"/api/chat/users/{other.UserId}/block");
        if (!res.IsSuccessStatusCode) { Toasts.Show("Could not update blocked contact.", ToastKind.Error); return; }
        dmUserBlocked = !dmUserBlocked;
        Toasts.Show(dmUserBlocked ? "Contact blocked." : "Contact unblocked.", ToastKind.Success);
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

    private async Task ToggleMuteRoom(IPop.Application.Chat.RoomListDto room)
    {
        if (room.IsMuted)
            await Auth.SendAsync(HttpMethod.Delete, $"/api/chat/rooms/{room.Id}/mute");
        else
            await Auth.SendAsync(HttpMethod.Post, $"/api/chat/rooms/{room.Id}/mute", new { duration = "forever" });
        await ChatSvc.LoadRoomsAsync();
    }

    private async Task ArchiveRoomById(Guid roomId)
    {
        await Auth.SendAsync(HttpMethod.Post, $"/api/chat/rooms/{roomId}/archive");
        await ChatSvc.LoadRoomsAsync();
        await ChatSvc.LoadArchivedRoomsAsync();
    }

    private async Task UnarchiveRoom(Guid roomId)
    {
        await Auth.SendAsync(HttpMethod.Delete, $"/api/chat/rooms/{roomId}/archive");
        await ChatSvc.LoadRoomsAsync();
        await ChatSvc.LoadArchivedRoomsAsync();
    }

    // Arsip tetap bisa dibuka (PRD: riwayat tetap dapat dibuka) — membuka ruang
    // arsip tidak mengeluarkannya dari arsip; itu aksi tombol Unarchive terpisah.
    private void OpenArchivedRoom(Guid roomId) => OpenRoom(roomId);

    // ipop-47b: pager for the archived list — rooms past the first page were unreachable
    // by any other path (search and the main list exclude archived ids).
    private async Task LoadMoreArchived() => await ChatSvc.LoadMoreArchivedAsync();

    // ---- Pin Message (Fix 7) ----

    private bool IsMsgPinned(Guid msgId) => pinnedMessages.Any(p => p.MessageId == msgId);

    private async Task PinMessage(Guid msgId)
    {
        ctxMsgId = null;
        if (ChatSvc.ActiveRoomId is null) return;
        if (IsMsgPinned(msgId))
            await Auth.SendAsync(HttpMethod.Delete, $"/api/chat/rooms/{ChatSvc.ActiveRoomId}/pins/{msgId}");
        else
            await Auth.SendAsync(HttpMethod.Post, $"/api/chat/rooms/{ChatSvc.ActiveRoomId}/pins/{msgId}");
        await LoadPinnedMessagesAsync();
    }

    // ---- Simpan Pesan (bookmark pribadi) ----
    // Simpanan bersifat pribadi: tidak mengubah tampilan ruang, tidak menyiarkan
    // apa pun. POST /api/chat/saved/{messageId}.

    private async Task SaveMessage(Guid msgId)
    {
        ctxMsgId = null;
        if (ChatSvc.ActiveRoomId is null) return;
        try
        {
            var res = await Auth.SendAsync(HttpMethod.Post, $"/api/chat/saved/{msgId}");
            if (res.IsSuccessStatusCode)
                Toasts.Show("Message saved to your personal saved list.", ToastKind.Success);
            else
                Toasts.Show("Could not save message.", ToastKind.Error);
        }
        catch
        {
            Toasts.Show("Failed to save message. Please try again.", ToastKind.Error);
        }
    }

    private async Task StartCallWith(string? nik)
    {
        if (string.IsNullOrWhiteSpace(nik)) return;
        var room = await CallStateSvc.CreateAsync(nik);
        if (room is null)
        {
            Toasts.Show("Could not start call. Make sure the contact is registered and active.", ToastKind.Error);
        }
    }
}
