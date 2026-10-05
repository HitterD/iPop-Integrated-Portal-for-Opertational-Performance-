namespace IPop.Web.Services;

/// Which iPop module a notification belongs to, so banners and the bell tray can wear that
/// module's colour, icon and name. The shell item only carries the coarse type
/// (chat/mail/news/project/system), so the target link refines it: meetings, calendar, notes,
/// files and admin all arrive as "system" but deep-link into their own page.
public sealed record NotificationModule(string Key, string Label, string Icon);

public static class NotificationModules
{
    public static readonly NotificationModule Chat = new("chat", "Chat", "message-circle");
    public static readonly NotificationModule Mention = new("mention", "Mention", "at-sign");
    public static readonly NotificationModule Mail = new("mail", "Mail", "mail");
    public static readonly NotificationModule Meeting = new("meeting", "Meetings", "video");
    public static readonly NotificationModule Calendar = new("calendar", "Calendar", "calendar-days");
    public static readonly NotificationModule News = new("news", "News & Memos", "newspaper");
    public static readonly NotificationModule Memo = new("memo", "Required-reading memos", "megaphone");
    public static readonly NotificationModule Project = new("project", "Projects", "kanban");
    public static readonly NotificationModule Notes = new("notes", "Notes", "notebook-pen");
    public static readonly NotificationModule Files = new("files", "Files", "folder");
    public static readonly NotificationModule Tickets = new("tickets", "Tickets", "life-buoy");
    public static readonly NotificationModule Admin = new("admin", "Administration", "shield-check");
    public static readonly NotificationModule System = new("system", "System", "bell");

    public static NotificationModule For(ShellNotificationItem item) => For(item.Type, item.Title, item.TargetUrl);

    public static NotificationModule For(string? type, string? title, string? targetUrl)
    {
        var e = (type ?? "").ToLowerInvariant();
        var t = (title ?? "").ToLowerInvariant();
        var path = (targetUrl ?? "").Split('?', '#')[0].ToLowerInvariant();

        if (e.Contains("mention") || t.Contains("menyebut") || t.Contains("mentioned"))
            return path.StartsWith("/notes") ? Notes : Mention;
        if (path.StartsWith("/notes")) return Notes;
        if (path.StartsWith("/tickets") || e.StartsWith("ticket")) return Tickets;
        if (path.StartsWith("/calls") || path.StartsWith("/meeting")) return Meeting;
        if (path.StartsWith("/calendar")) return Calendar;
        if (path.StartsWith("/files")) return Files;
        if (path.StartsWith("/admin")) return Admin;

        if (e.StartsWith("chat")) return Chat;
        return e switch
        {
            "mail" => Mail,
            "project" => Project,
            "news" when t.Contains("required reading") || t.Contains("wajib baca") || t.Contains("memo") => Memo,
            "news" => News,
            _ when path.StartsWith("/chat") => Chat,
            _ when path.StartsWith("/mail") => Mail,
            _ when path.StartsWith("/projects") => Project,
            _ when path.StartsWith("/news") => News,
            _ when t.Contains("meeting") || t.Contains("rapat") => Meeting,
            _ => System,
        };
    }
}
