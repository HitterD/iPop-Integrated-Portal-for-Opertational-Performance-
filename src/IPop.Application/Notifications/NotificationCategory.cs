namespace IPop.Application.Notifications;

/// The category a notification is filed under on /notifications (and muted by in Preferences):
/// mention | chat | mail | tickets | task | meeting | news | system. The event type decides first,
/// the target link refines it (older rows and outside clients only carry the link).
/// Shared by the API (inbox filter/counts, muted types) and the web page so both agree.
public static class NotificationCategory
{
    public const string Mention = "mention", Chat = "chat", Mail = "mail", Tickets = "tickets", Task = "task",
        Meeting = "meeting", News = "news", System = "system";

    /// Every category, in the order the page lists them.
    public static readonly IReadOnlyList<string> All = [Mention, Chat, Mail, Tickets, Task, Meeting, News, System];

    public static string Of(string? eventType, string? targetUrl)
    {
        var e = (eventType ?? "").Trim().ToLowerInvariant();
        var path = (targetUrl ?? "").Split('?', '#')[0].ToLowerInvariant();
        // Only a dotted event names its module ("system.epop_import"); a bare "system"/"hrsync" lets the link decide.
        var head = e.Contains('.') ? e.Split('.', 2)[0] : "";

        if (e.Contains("mention")) return Mention;
        switch (head)
        {
            case "chat": return Chat;
            case "call" or "calls" or "meeting" or "calendar": return Meeting;
            case "ticket" or "tickets" or "idesk": return Tickets;
            case "mail": return Mail;
            case "project" or "task": return Task;
            case "news" or "memo": return News;
            case "system" or "approval" or "admin": return System;
        }
        if (head == "" && e is "chat" or "mail" or "news" or "project") return e == "project" ? Task : e;
        if (path.StartsWith("/tickets") || path.StartsWith("/idesk/tickets")) return Tickets;
        if (path.StartsWith("/calls") || path.StartsWith("/calendar") || path.StartsWith("/meeting")) return Meeting;
        if (path.StartsWith("/chat")) return Chat;
        if (path.StartsWith("/mail")) return Mail;
        if (path.StartsWith("/projects")) return Task;
        if (path.StartsWith("/news")) return News;
        return System;
    }

    /// Muted-types matching: a stored value may be a category, the old coarse module
    /// (chat/mail/news/project/system), an exact event type, or the settings dialog's "call".
    public static bool IsMuted(IEnumerable<string> muted, string eventType, string? targetUrl, string coarse)
    {
        var category = Of(eventType, targetUrl);
        foreach (var m in muted)
        {
            if (m.Equals(category, StringComparison.OrdinalIgnoreCase)
                || m.Equals(eventType, StringComparison.OrdinalIgnoreCase)
                || (m.Equals("call", StringComparison.OrdinalIgnoreCase) && category == Meeting))
                return true;
            // The coarse module only mutes its own category: "system" must no longer silence iDesk,
            // meetings and notes all at once, and "chat" must not silence mentions.
            if (m.Equals(coarse, StringComparison.OrdinalIgnoreCase) && CoarseOf(category) == coarse.ToLowerInvariant())
                return true;
        }
        return false;
    }

    private static string CoarseOf(string category) => category switch
    {
        Chat => "chat",
        Mail => "mail",
        News => "news",
        Task => "project",
        System => "system",
        _ => "",
    };
}
