namespace IPop.Web.Pages;

/// Who may use @all in the open group (logic only; the server enforces the same rule and the
/// 3-per-10-minutes cap, past which the token is just text).
public partial class Chat
{
    private bool IsGroupMember => ChatSvc.ActiveRoom?.Type == "group"
        && ChatSvc.ActiveRoom.Members.Any(m => m.Nik == GetCurrentUserNik());

    /// Owners/admins always; ordinary members unless an admin switched it off for this group.
    private bool MayMentionAll => IsGroupAdmin || (IsGroupMember && ChatSvc.ActiveRoom!.AllowMemberMentionAll);

    /// Owner/admin: let ordinary members use @all (true) or not (false) in the open group.
    private async Task SetMemberMentionAll(bool allowed)
    {
        if (ChatSvc.ActiveRoomId is null || !IsGroupAdmin) return;
        await Auth.SendAsync(HttpMethod.Put, $"/api/chat/rooms/{ChatSvc.ActiveRoomId}/mention-all", new { allowed });
        await ChatSvc.LoadRoomsAsync();
        await ChatSvc.LoadActiveRoomAsync();
    }
}
