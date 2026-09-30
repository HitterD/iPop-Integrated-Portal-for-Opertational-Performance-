// Chat page — group member administration from the Members panel: remove, promote/demote,
// and the membership history. The server re-checks every rule (ChatService.RemoveMemberAsync /
// ChangeRoleAsync); MemberRow only hides actions the caller could not perform anyway.
//
// Part of the Chat partial class. _Imports.razor only reaches .razor files, so usings are restated.
using IPop.Application.Chat;
using IPop.Web.Services;

namespace IPop.Web.Pages;

public partial class Chat
{
    private bool memberHistoryOpen;

    private bool CanManageMembers => ChatSvc.ActiveRoom?.Type == "group"
        && ChatSvc.ActiveRoom.CurrentUserRole is "owner" or "admin";

    private async Task RemoveGroupMember(MemberDto member)
    {
        var room = ChatSvc.ActiveRoom;
        if (room is null) return;
        if (!await Confirm.AskAsync($"Remove {member.Name}?",
                $"{member.Name} will be removed from {room.Name} and stop receiving its messages.", "Remove")) return;
        var res = await Auth.SendAsync(HttpMethod.Delete, $"/api/chat/rooms/{room.Id}/members/{member.UserId}");
        if (!res.IsSuccessStatusCode) { Toasts.Show($"Could not remove {member.Name}.", ToastKind.Error); return; }
        Toasts.Show($"{member.Name} was removed.", ToastKind.Success);
        await ChatSvc.LoadActiveRoomAsync();
    }

    private async Task ChangeGroupMemberRole((MemberDto Member, ChatMemberRole Role) change)
    {
        var room = ChatSvc.ActiveRoom;
        if (room is null) return;
        var (member, role) = change;
        var promote = role == ChatMemberRole.Admin;
        if (!await Confirm.AskAsync(promote ? $"Make {member.Name} an admin?" : $"Remove admin from {member.Name}?",
                promote ? "Admins can add and remove members and pin messages."
                        : $"{member.Name} will become a regular member.",
                promote ? "Make admin" : "Remove admin", danger: !promote)) return;
        var res = await Auth.SendAsync(HttpMethod.Put, $"/api/chat/rooms/{room.Id}/members/{member.UserId}/role",
            new { role = promote ? "admin" : "member" });
        if (!res.IsSuccessStatusCode) { Toasts.Show("Could not change the role.", ToastKind.Error); return; }
        Toasts.Show(promote ? $"{member.Name} is now an admin." : $"{member.Name} is now a member.", ToastKind.Success);
        await ChatSvc.LoadActiveRoomAsync();
    }

    // Hero "Add" button: the add-member popup lives in the Members view, so open it there.
    private void OpenAddMember()
    {
        OpenDetailView(DetailPanelView.Members);
        addMemberOpen = true;
    }

    // Owners/admins first, then by name — matches how the header lists people.
    private static int RoleRank(string? role) => role switch { "owner" => 0, "admin" => 1, _ => 2 };
}
