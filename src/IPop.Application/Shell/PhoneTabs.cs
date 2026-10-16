namespace IPop.Application.Shell;

/// One module in the app navigation (sidebar, phone tab bar, More sheet, Edit tabs).
/// <paramref name="Color"/> is the tile colour class used on phones (mockup v1 frames g/h).
public sealed record ShellModule(string Label, string Icon, string Href, string Color,
    string? BadgeKey = null, bool AdminOnly = false, string? Tip = null);

/// The phone tab bar: four pinned modules + More (mockup v1 frames g, h). The same catalogue
/// validates PUT /api/me/ui-prefs/tabs on the server and renders the bar on the client.
public static class PhoneTabs
{
    public const int Count = 4;

    /// Every module a user can reach, in More-grid order.
    public static readonly IReadOnlyList<ShellModule> All =
    [
        new("Chat", "message-circle", "/chat", "c-chat", BadgeKey: "chat"),
        new("Mail", "mail", "/mail", "c-mail", BadgeKey: "mail"),
        new("Calls & Meetings", "phone", "/calls", "c-calls", BadgeKey: "calls"),
        new("Calendar", "calendar", "/calendar", "c-cal"),
        new("Dashboard", "layout-grid", "/dashboard", "c-dash"),
        new("Directory", "users", "/directory", "c-dir"),
        new("Notes", "notebook-pen", "/notes", "c-notes"),
        new("Projects", "kanban", "/projects", "c-proj"),
        new("Tickets", "life-buoy", "/tickets", "c-tix", BadgeKey: "tickets"),
        new("News & Memos", "newspaper", "/news", "c-news", BadgeKey: "news"),
        new("Notifications", "bell", "/notifications", "c-notif", BadgeKey: "notif"),
        new("Files", "folder", "/files", "c-files"),
        new("Admin settings", "sliders-horizontal", "/admin", "c-admin", AdminOnly: true),
    ];

    public static readonly IReadOnlyList<string> Default = ["/chat", "/mail", "/calls", "/calendar"];

    /// Short label under a tab icon ("Calls & Meetings" does not fit 1/5 of a phone).
    public static string TabLabel(ShellModule m) => m.Href switch
    {
        "/calls" => "Calls",
        "/news" => "News",
        "/admin" => "Admin",
        _ => m.Label,
    };

    public static ShellModule? Find(string href) => All.FirstOrDefault(m => m.Href == href);

    /// Exactly <see cref="Count"/> distinct, known hrefs; /admin only for admins.
    public static bool IsValid(IReadOnlyList<string>? hrefs, bool isAdmin)
        => hrefs is { Count: Count }
           && hrefs.Distinct(StringComparer.Ordinal).Count() == Count
           && hrefs.All(h => Find(h) is { } m && (!m.AdminOnly || isAdmin));

    /// What the bar shows: the saved list when it is still valid for this user, else the default.
    public static IReadOnlyList<string> Resolve(IReadOnlyList<string>? saved, bool isAdmin)
        => IsValid(saved, isAdmin) ? saved! : Default;

    /// The More grid: every module this user may open that is not pinned.
    public static IReadOnlyList<ShellModule> More(IReadOnlyList<string> pinned, bool isAdmin)
        => All.Where(m => !pinned.Contains(m.Href) && (!m.AdminOnly || isAdmin)).ToList();
}
