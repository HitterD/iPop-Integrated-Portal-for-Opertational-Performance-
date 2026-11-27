using IPop.Application.Chat;
using IPop.Application.Chat.Mentions;
using IPop.Web.Services;

namespace IPop.Web.Pages;

/// Who may use @all in the open group (the server enforces the same rule and the 3-per-10-minutes
/// cap, past which the token is just text), plus the composer's budget hint and the admin switch.
public partial class Chat
{
    private MentionAllQuotaDto? mentionAllQuota;
    private Guid? mentionAllQuotaRoom;
    private bool mentionAllSwitchBusy;
    /// Inline notice above the composer (mockup png 07/12): bold lead + text; clears itself.
    private (string Lead, string Text)? mentionAllNotice;
    private int mentionAllNoticeSeq;

    private void ShowMentionAllNotice(string lead, string text)
    {
        mentionAllNotice = (lead, text);
        var seq = ++mentionAllNoticeSeq;
        _ = Task.Delay(6000).ContinueWith(_ => InvokeAsync(() =>
        {
            if (seq != mentionAllNoticeSeq || _disposed) return;
            mentionAllNotice = null;
            StateHasChanged();
        }));
    }

    private bool IsGroupMember => ChatSvc.ActiveRoom?.Type == "group"
        && ChatSvc.ActiveRoom.Members.Any(m => m.Nik == GetCurrentUserNik());

    /// Owners/admins always; ordinary members unless an admin switched it off for this group.
    private bool MayMentionAll => IsGroupAdmin || (IsGroupMember && ChatSvc.ActiveRoom!.AllowMemberMentionAll);

    /// Members of a group where admins switched @all off still see it, greyed ("Turned off by a group admin").
    private bool MentionAllOff => IsGroupMember && !MayMentionAll;

    /// Loads the caller's @all budget for the open group once per room (popup hint). Fire-and-forget.
    private void EnsureMentionAllQuota(bool force = false)
    {
        var roomId = ChatSvc.ActiveRoomId;
        if (roomId is null || !MayMentionAll) return;
        if (!force && mentionAllQuotaRoom == roomId) return;
        mentionAllQuotaRoom = roomId;
        _ = LoadMentionAllQuotaAsync(roomId.Value);
    }

    private async Task LoadMentionAllQuotaAsync(Guid roomId)
    {
        try
        {
            var q = await Auth.GetAsync<MentionAllQuotaDto>($"/api/chat/rooms/{roomId}/mention-all/quota");
            if (ChatSvc.ActiveRoomId != roomId || _disposed) return;
            mentionAllQuota = q;
            await InvokeAsync(StateHasChanged);
        }
        catch { /* no hint; sending still works and the server decides */ }
    }

    /// Before a send: an @all past the cap goes out as plain text. Say so (no one is notified) and
    /// count it locally so the next popup shows the right budget; the server refresh follows.
    private void NoteMentionAllSend(string wireText)
    {
        if (ChatSvc.ActiveRoom?.Type != "group" || !MentionParser.Parse(wireText).Any(s => s is MentionSegment.All)) return;
        var q = mentionAllQuota;
        if (MentionAllOff)
            ShowMentionAllNotice("@all sent as plain text.", "@all is turned off for members in this group. Nobody was notified by @all.");
        else if (q is not null && q.Used >= q.Limit)
            ShowMentionAllNotice("@all sent as plain text.", $"You reached {q.Limit} @all in {q.WindowMinutes} minutes. Nobody was notified by @all.");
        else if (q is not null)
            mentionAllQuota = q with { Used = q.Used + 1 };
        // Re-read from the server once the message has landed (ResetsAt comes from there).
        var roomId = ChatSvc.ActiveRoomId;
        if (roomId is { } id) _ = Task.Delay(1500).ContinueWith(_ => { if (ChatSvc.ActiveRoomId == id) EnsureMentionAllQuota(force: true); });
    }

    /// Owner/admin: let ordinary members use @all (true) or not (false) in the open group.
    private async Task SetMemberMentionAll(bool allowed)
    {
        if (ChatSvc.ActiveRoomId is null || !IsGroupAdmin || mentionAllSwitchBusy) return;
        mentionAllSwitchBusy = true;
        try
        {
            var res = await Auth.SendAsync(HttpMethod.Put, $"/api/chat/rooms/{ChatSvc.ActiveRoomId}/mention-all", new { allowed });
            if (!res.IsSuccessStatusCode) { Toasts.Show("Could not change the @all setting.", ToastKind.Error); return; }
            await ChatSvc.LoadRoomsAsync();
            await ChatSvc.LoadActiveRoomAsync();
            Toasts.Show(allowed ? "Members can use @all again." : "@all is now for admins only.", ToastKind.Success);
        }
        finally { mentionAllSwitchBusy = false; }
    }
}
