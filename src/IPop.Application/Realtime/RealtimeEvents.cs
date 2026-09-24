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

    public static string UserGroup(string nik) => $"user:{nik}";
}
