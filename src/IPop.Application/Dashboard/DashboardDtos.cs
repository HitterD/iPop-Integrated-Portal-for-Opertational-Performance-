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
    IReadOnlyList<DashboardStatusItem> Status,
    IReadOnlyList<DashboardNoteItem> Notes,
    IReadOnlyList<DashboardTransferItem> Transfers,
    bool IsAdmin,
    // Same rule as NewsService.CanManageAsync (Admin, news.publish or news.approve): gates "New post".
    bool CanPostNews = false);

public record DashboardKpis(
    int MailUnread, int MailInbox,
    int ChatUnread, int ChatRooms,
    int OpenTasks, int OverdueTasks,
    DateTime? NextMeetingAt, string? NextMeetingTitle, Guid? NextMeetingId,
    int AckPending,
    int MissedCalls);

/// Kind follows the calendar feed; timed bounds are UTC, date-only entries set AllDay.
public record DashboardAgendaItem(string Kind, Guid Id, string Title, string Sub, DateTime? StartAt, DateTime? EndAt,
    bool Ended, bool Overdue, string Url, string? RoomName, bool AllDay = false, string? Source = null);

/// Kind: "ack" | "missed-call" | "mention" | "note" | "overdue".
public record DashboardAttentionItem(string Kind, string Title, string Sub, string Cta, string Url, string? PeerNik = null);

public record DashboardTaskItem(Guid Id, Guid ProjectId, string Title, string ProjectName, DateTime? DueDate, bool Overdue);

public record DashboardProjectItem(Guid Id, string Name, string Status, int Done, int Total, int Progress);

/// PeerNik: the other person in a DM (for their photo); null for groups.
public record DashboardChatItem(Guid Id, string Type, string Name, string? LastMessage, DateTime? LastMessageAt, int Unread, string? AvatarColor,
    string? PeerNik = null);

public record DashboardNewsItem(Guid Id, string Type, string Title, DateTime? PublishedAt, bool NeedsAck);

public record DashboardPersonItem(string Nik, string Name, string Status);

/// Pages I opened lately (or own), newest first. Sub is "Edited by …" / "You" plus when.
public record DashboardNoteItem(Guid Id, string Title, string? Emoji, string EditedBy, DateTime UpdatedAt, bool Favorite);

/// Background copy/move/delete jobs from Files. Status: "Queued" | "Running" | "Completed" | "CompletedWithErrors" | "Failed".
public record DashboardTransferItem(Guid Id, string Kind, string Status, int TotalFiles, int CompletedFiles, int FailedFiles,
    string Server, string? DestDir, DateTime? FinishedAt);

/// State: "ok" | "warn" | "unknown".
public record DashboardStatusItem(string Label, string State, string Note);

public interface IDashboardService
{
    Task<DashboardDto?> GetAsync(Guid userId, CancellationToken ct = default);

    /// Just the "Team online" card. Presence changes re-read this, not the whole dashboard:
    /// every open Dashboard reacts to every sign-in, so a full reload there stalled the API.
    Task<DashboardOnlineDto> OnlineAsync(Guid userId, CancellationToken ct = default);
}

public record DashboardOnlineDto(IReadOnlyList<DashboardPersonItem> Online, int OnlineCount);
