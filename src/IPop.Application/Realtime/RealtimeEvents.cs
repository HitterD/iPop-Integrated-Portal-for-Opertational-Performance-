namespace IPop.Application.Realtime;

/// Hub event names. This file is also compiled into IPop.Web (linked), so client and
/// server cannot drift. Keep it dependency-free.
public static class RealtimeEvents
{
    public const string PresenceChanged = "presenceChanged";   // (nik, status)
    public const string DirectoryChanged = "directoryChanged"; // ()
    public const string AuditChanged = "auditChanged";         // ()
    public const string BadgeChanged = "badgeChanged";         // (key, count)
    public const string ProjectChanged = "projectChanged";     // (projectId)
    public const string TimerChanged = "timerChanged";         // () — to the user: their running timer started/stopped
    public const string FileOperationProgress = "fileOperationProgress"; // (operationId, completed, total, bytes)
    public const string FileOperationFinished = "fileOperationFinished"; // (operationId, status)
    // Refresh signals: payload-less (or a hint), the client re-fetches over REST.
    public const string MailChanged = "mailChanged";                   // () — to affected users: new/read/moved/deleted
    public const string MailReceiptChanged = "mailReceiptChanged";     // ({ mailId }) — to sender + visible recipients: someone saw it
    public const string NewsChanged = "newsChanged";                   // () — to all: post created/edited/deleted/published
    public const string CalendarChanged = "calendarChanged";           // () — to affected users
    public const string MeetingChanged = "meetingChanged";             // (meetingId) — to organizer + attendees
    public const string FilesChanged = "filesChanged";                 // (scope) — to users who can see that scope
    public const string NotificationsChanged = "notificationsChanged"; // () — to the user: read/cleared in another tab
    public const string ChatRoomsChanged = "chatRoomsChanged";         // () — to the user: per-user mute/archive/pin/read state
    public const string NotesChanged = "notesChanged";                 // () — to everyone who can see a page: sidebar/sharing changed

    public static string UserGroup(string nik) => $"user:{nik}";
}
