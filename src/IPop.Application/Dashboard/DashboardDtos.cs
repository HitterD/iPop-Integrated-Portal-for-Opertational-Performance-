namespace IPop.Application.Dashboard;

/// Everything the v6 Dashboard renders, in one round trip.
public record DashboardDto(
    string FirstName,
    DashboardKpis Kpis,
    IReadOnlyList<DashboardAgendaItem> Agenda,
    IReadOnlyList<DashboardAttentionItem> Attention,
    IReadOnlyList<DashboardTaskItem> MyTasks,
    IReadOnlyList<DashboardProjectItem> Projects,
    IReadOnlyList<DashboardChatItem> Chats,
    IReadOnlyList<DashboardNewsItem> News,
    IReadOnlyList<DashboardPersonItem> Online,
    int OnlineCount,
    IReadOnlyList<DashboardStatusItem> Status);

public record DashboardKpis(
    int MailUnread, int MailInbox,
    int ChatUnread, int ChatRooms,
    int OpenTasks, int OverdueTasks,
    DateTime? NextMeetingAt, string? NextMeetingTitle, Guid? NextMeetingId,
    int AckPending);

/// Kind: "meeting" | "task". StartAt is UTC for meetings, the due date for tasks.
public record DashboardAgendaItem(string Kind, Guid Id, string Title, string Sub, DateTime? StartAt, DateTime? EndAt,
    bool Ended, bool Overdue, string Url, string? RoomName);

/// Kind: "ack" | "missed-call" | "mention" | "overdue".
public record DashboardAttentionItem(string Kind, string Title, string Sub, string Cta, string Url, string? PeerNik = null);

public record DashboardTaskItem(Guid Id, Guid ProjectId, string Title, string ProjectName, DateTime? DueDate, bool Overdue);

public record DashboardProjectItem(Guid Id, string Name, string Status, int Done, int Total, int Progress);

public record DashboardChatItem(Guid Id, string Type, string Name, string? LastMessage, DateTime? LastMessageAt, int Unread, string? AvatarColor);

public record DashboardNewsItem(Guid Id, string Type, string Title, DateTime? PublishedAt, bool NeedsAck);

public record DashboardPersonItem(string Nik, string Name, string Status);

/// State: "ok" | "warn" | "unknown".
public record DashboardStatusItem(string Label, string State, string Note);

public interface IDashboardService
{
    Task<DashboardDto?> GetAsync(Guid userId, CancellationToken ct = default);
}
