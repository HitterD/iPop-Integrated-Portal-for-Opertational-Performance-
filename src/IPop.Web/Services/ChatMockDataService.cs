using IPop.Application.Chat;

namespace IPop.Web.Services;

/// <summary>
/// Mock data service for chat UI development and testing.
/// Provides sample data that mirrors the real DTOs for design purposes.
/// </summary>
public class ChatMockDataService
{
    private readonly string _currentUserNik = "12345";

    public List<RoomListDto> GetDirectRooms() =>
    [
        new(Guid.NewGuid(), "dm", null, "67890", "Ahmad Wijaya", "Hi, how is the project going?", DateTime.Now.AddMinutes(-5), 2, false, false, true, "online", false, false, null),
        new(Guid.NewGuid(), "dm", null, "11223", "Siti Rahayu", "I've sent the file", DateTime.Now.AddMinutes(-30), 0, false, false, false, "away", false, false, null),
        new(Guid.NewGuid(), "dm", null, "33445", "Budi Santoso", "Meeting tomorrow at 10 AM", DateTime.Now.AddHours(-2), 1, true, false, false, "busy", false, true, DateTime.Now.AddHours(2)),
        new(Guid.NewGuid(), "dm", null, "55667", "Dewi Lestari", "Thank you so much!", DateTime.Now.AddDays(-1), 0, false, false, false, null, false, false, null),
        new(Guid.NewGuid(), "dm", null, "77889", "Rizki Pratama", "All right, I'll check it later", DateTime.Now.AddDays(-2), 0, false, true, false, "offline", false, false, null),
    ];

    public List<RoomListDto> GetGroupRooms() =>
    [
        new(Guid.NewGuid(), "group", "Development Team", null, null, "Don't forget daily standup at 9 AM", DateTime.Now.AddMinutes(-15), 5, false, false, true, null, false, true, null, true, "#4f6d8f"),
        new(Guid.NewGuid(), "group", "Project Alpha", null, null, "Deployment successful! 🎉", DateTime.Now.AddHours(-1), 12, false, false, false, null, false, false, null, true, "#2e7d5b"),
        new(Guid.NewGuid(), "group", "Design Review", null, null, "Mockups are ready for review", DateTime.Now.AddHours(-3), 0, false, false, false, null, false, false, null, true, "#7d5ba6"),
        new(Guid.NewGuid(), "group", "HR Announcements", null, null, "National holiday on August 17", DateTime.Now.AddDays(-1), 3, true, false, false, null, false, false, null, false, "#8a6d3b"),
        new(Guid.NewGuid(), "group", "Random Chat", null, null, "Anyone ordering lunch?", DateTime.Now.AddDays(-3), 0, false, true, false, null, false, false, null, false, "#b3543f"),
    ];

    public List<RoomListDto> GetArchivedRooms() =>
    [
        new(Guid.NewGuid(), "dm", null, "99001", "Eko Purnomo", "Project completed, thank you!", DateTime.Now.AddDays(-30), 0, false, true, false, null, false, false, null),
        new(Guid.NewGuid(), "group", "Old Project", null, null, "Project has been archived", DateTime.Now.AddDays(-60), 0, false, true, false, null, false, false, null, false, "#5b7d2e"),
    ];

    public ChatRoomDto GetActiveRoom(Guid roomId)
    {
        var isGroup = roomId.GetHashCode() % 2 == 0;
        var members = GetMockMembers(isGroup ? 8 : 2);

        return new ChatRoomDto(
            roomId,
            isGroup ? "group" : "dm",
            isGroup ? "Development Team" : null,
            isGroup ? "Discussion on application development" : null,
            members.Count,
            isGroup ? 5 : 1,
            members,
            isGroup ? "member" : null,
            members.Select(m => new MentionUserDto(m.UserId, m.Nik, m.Name, true)).ToList(),
            true,
            isGroup ? "#4f6d8f" : null
        );
    }

    private List<MemberDto> GetMockMembers(int count)
    {
        var members = new List<MemberDto>
        {
            new(Guid.NewGuid(), _currentUserNik, "You (Current User)", "member", "online"),
            new(Guid.NewGuid(), "67890", "Ahmad Wijaya", "owner", "online"),
            new(Guid.NewGuid(), "11223", "Siti Rahayu", "admin", "away"),
            new(Guid.NewGuid(), "33445", "Budi Santoso", "member", "busy"),
            new(Guid.NewGuid(), "55667", "Dewi Lestari", "member", "online"),
        };

        if (count > 5)
        {
            members.Add(new(Guid.NewGuid(), "77889", "Rizki Pratama", "member", "offline"));
            members.Add(new(Guid.NewGuid(), "99001", "Eko Purnomo", "member", "online"));
            members.Add(new(Guid.NewGuid(), "22334", "Maya Sari", "member", "away"));
        }

        return members.Take(count).ToList();
    }

    public List<MessageDto> GetMockMessages(Guid roomId, int count = 20)
    {
        var messages = new List<MessageDto>();
        var baseTime = DateTime.Now.AddHours(-3);
        var senders = new[]
        {
            ("67890", "Ahmad Wijaya"),
            ("11223", "Siti Rahayu"),
            ("33445", "Budi Santoso"),
            (_currentUserNik, "You")
        };

        var sampleMessages = new[]
        {
            "Hello everyone, good morning!",
            "Morning! Any updates from the client?",
            "Sent via email last night",
            "Okay, let me take a look",
            "Meeting today at 2 PM",
            "Ready, I've prepared the presentation",
            "Great! Don't forget your laptop",
            "Can someone help review my code?",
            "I can help after lunch",
            "Thank you very much! 🙏",
            "Don't forget standup meeting tomorrow morning",
            "Noted, thanks for the reminder",
            "Documentation file has been uploaded",
            "Perfect, reviewing it now",
            "Bug in payment module, urgent!",
            "Checking now, hang on",
            "Fixed now, please test again",
            "Awesome! Thanks team 🎉",
            "Anyone coming in this weekend?",
            "I'll be remote from home"
        };

        for (int i = 0; i < count; i++)
        {
            var sender = senders[i % senders.Length];
            var isCurrentUser = sender.Item1 == _currentUserNik;
            var content = sampleMessages[i % sampleMessages.Length];

            if (i % 7 == 3) content = $"[b]{content}[/b]";
            if (i % 9 == 5) content = $"{content} @Ahmad Wijaya";

            messages.Add(new MessageDto(
                Guid.NewGuid(),
                roomId,
                Guid.NewGuid(),
                sender.Item2,
                sender.Item1,
                i % 15 == 0 ? "system" : "text",
                i % 15 == 0 ? "Ahmad Wijaya added 3 new members" : content,
                baseTime.AddMinutes(i * 15),
                i % 11 == 0 ? baseTime.AddMinutes(i * 15 + 5) : null,
                false,
                null,
                i % 6 == 0 && i > 0 ? messages[i - 1].Id : null,
                i % 6 == 0 && i > 0 ? new ReplyPreviewDto(messages[i - 1].SenderName, messages[i - 1].Content) : null,
                null,
                i % 8 == 0 ? 3 : 0,
                i % 8 == 0 ? baseTime.AddMinutes(i * 15 + 10) : null,
                i % 5 == 0 ? GetMockAttachments() : [],
                i % 4 == 0 ? GetMockReactions() : [],
                i % 9 == 5 ? ["67890"] : [],
                false,
                Guid.NewGuid(),
                isCurrentUser ? "read" : null
            ));
        }

        return messages;
    }

    private List<AttachmentDto> GetMockAttachments() =>
    [
        new(Guid.NewGuid(), "project-document.pdf", 1024 * 500, "application/pdf", null),
        new(Guid.NewGuid(), "design-mockup.png", 1024 * 1024 * 2, "image/png", "https://picsum.photos/200/150"),
        new(Guid.NewGuid(), "data-analysis.xlsx", 1024 * 250, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", null),
    ];

    private List<ReactionGroupDto> GetMockReactions() =>
    [
        new("👍", 3, true),
        new("❤️", 1, false),
    ];

    public List<PinnedMessageDto> GetPinnedMessages() =>
    [
        new(Guid.NewGuid(), Guid.NewGuid(), "Ahmad Wijaya", "Weekly team sync every Monday at 9 AM", DateTime.Now.AddDays(-2)),
        new(Guid.NewGuid(), Guid.NewGuid(), "Siti Rahayu", "SOP document updated, check the shared folder", DateTime.Now.AddDays(-5)),
    ];

    public int GetTotalUnread() => GetDirectRooms().Sum(r => r.UnreadCount) + GetGroupRooms().Sum(r => r.UnreadCount);
    public int GetDirectUnread() => GetDirectRooms().Sum(r => r.UnreadCount);
    public int GetGroupUnread() => GetGroupRooms().Sum(r => r.UnreadCount);
    public bool HasGroupMention() => GetGroupRooms().Any(r => r.HasUnreadMention);
}
