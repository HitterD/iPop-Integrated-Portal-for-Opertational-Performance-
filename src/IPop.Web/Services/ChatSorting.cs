using IPop.Application.Chat;

namespace IPop.Web.Services;

public static class ChatSorting
{
    public static IOrderedEnumerable<MemberDto> SortMembers(IEnumerable<MemberDto> members)
        => members.OrderBy(m => RoleRank(m.Role)).ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase);

    private static int RoleRank(string role) => role switch { "owner" => 0, "admin" => 1, _ => 2 };

    public static IOrderedEnumerable<RoomListDto> SortPinned(IEnumerable<RoomListDto> rooms)
        => rooms.OrderByDescending(r => r.LastMessageAt ?? DateTime.MinValue);

    public static IOrderedEnumerable<SeenByDto> SortReaders(IEnumerable<SeenByDto> readers)
        => readers.OrderByDescending(r => r.ReadAt);

    public static IOrderedEnumerable<PinnedMessageDto> SortPinnedMessages(IEnumerable<PinnedMessageDto> msgs)
        => msgs.OrderByDescending(m => m.PinnedAt);
}
