using IPop.Web.Components;
using IPop.Web.Components.Phone.Notifications;
using IPop.Web.Layout;
using IPop.Web.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace IPop.Web.Pages;

public partial class Notifications
{
    [CascadingParameter] public MainLayout Layout { get; set; } = default!;

    private const int PageSize = 50;
    private List<NotificationItemDto> items = [];
    private HashSet<Guid> selectedIds = [];
    private bool isLoading = true;
    private int unreadCount = 0;
    private int snoozedCount = 0;
    private Dictionary<string, int> unreadByCategory = [];
    private int loadedPage = 1;
    private bool hasMore;
    private bool loadingMore;
    private string categoryFilter = "all";
    private bool selecting;
    private bool snoozeMenuOpen;
    private (double X, double Y)? snoozeMenuAt;
    private ContextMenu? snoozeMenu;
    private string searchQuery = "";
    private bool showPrefs;
    private bool quietHoursEnabled;
    private string quietStart = "22:00", quietEnd = "07:00";
    private string? quietDays;
    private bool allowImportant = true;
    private HashSet<string> mutedCategories = new(StringComparer.OrdinalIgnoreCase);
    private NotificationItemDto? selectedItem;
    private CancellationTokenSource? searchCts;
    private bool mDetail;
    private DotNetObjectReference<Notifications>? mRef;

    // ── Wave 2 phone list (NF1) / preferences (NF2): same data and actions as the desktop panes ──
    private static readonly string[] PhoneChipIds = ["all", "unread", "mention", "chat", "mail", "tickets", "task", "meeting", "news", "system", "snoozed"];

    private IReadOnlyList<IPop.Web.Components.Phone.SegmentItem> PhoneChips => PhoneChipIds
        .Select(id => CategoryList.First(c => c.Id == id))
        .Select(c => new IPop.Web.Components.Phone.SegmentItem(c.Id, c.Id == "news" ? "News" : c.Label, c.Id == "unread" ? CountCategory("unread") : null))
        .ToList();

    private IReadOnlyList<PhoneNotifRow> PhoneRows => GroupedNotifications
        .SelectMany(g => g.Select(n => new PhoneNotifRow(n.Id, n.Category, IconOf(n), RowTitle(n), NotificationPreview.Of(n.Body, n.TargetUrl), FormatTime(n.At),
            g.Key is "Today" ? "Today" : g.Key is "Yesterday" ? "Yesterday" : "Earlier", n.IsRead,
            IsSnoozed(n) ? $"Until {SnoozeLabel(n.SnoozedUntil!.Value)}" : null)))
        .ToList();

    private IReadOnlyList<PhoneNotifCategory> PhoneCategories => NotificationCategories.All
        .Select(c => new PhoneNotifCategory(c.Id, c.Label, c.Icon, !mutedCategories.Contains(c.Id)))
        .ToList();

    private NotificationItemDto? Find(Guid id) => items.FirstOrDefault(x => x.Id == id);
    private Task PhoneSearch(string q) { searchQuery = q; return OnSearchChanged(); }
    private void PhoneOpen(Guid id) { if (Find(id) is { } n) SelectNotification(n); }
    private Task PhoneToggleRead(Guid id) => Find(id) is { } n ? ToggleReadStatus(n) : Task.CompletedTask;
    private Task PhoneDelete(Guid id) => Find(id) is { } n ? DeleteItem(n) : Task.CompletedTask;

    /// Swipe → Snooze: the same options as the detail bar's menu, as a bottom sheet.
    private void PhoneSnoozeAsk(Guid id)
    {
        if (Find(id) is not { } n) return;
        selectedItem = n;
        snoozeMenuOpen = true;
    }

    // ── Categories: shared with NotificationSettingsDialog via NotificationCategories ──
    private record CategoryDef(string Id, string Label, string Icon);
    private record CategoryGroup(string? Label, IReadOnlyList<CategoryDef> Items);
    private static readonly CategoryDef[] CategoryList =
    [
        new("all", "All", "inbox"),
        new("unread", "Unread", "circle-dot"),
        .. NotificationCategories.All.Select(c => new CategoryDef(c.Id, c.Label, c.Icon)),
        new("snoozed", "Snoozed", "alarm-clock"),
    ];
    private static CategoryDef Cat(string id) => CategoryList.First(c => c.Id == id);
    private static readonly CategoryGroup[] CategoryGroups =
    [
        new(null, [Cat("all"), Cat("unread"), Cat("mention")]),
        new("Apps", [Cat("chat"), Cat("mail"), Cat("tickets"), Cat("task"), Cat("meeting"), Cat("news"), Cat("system")]),
        new("Later", [Cat("snoozed")]),
    ];

    private string CurrentCategoryTitle => CategoryList.FirstOrDefault(c => c.Id == categoryFilter)?.Label ?? "Notifications";
    private static string CategoryName(string id) => id switch
    {
        "mention" => "Mentions", "tickets" => "iDesk", "meeting" => "Meetings", "news" => "Memos & News",
        _ => CategoryList.FirstOrDefault(c => c.Id == id)?.Label ?? "System",
    };

    private string ListSummary
    {
        get
        {
            var list = FilteredList;
            if (list.Count == 0) return "Nothing here";
            var unread = categoryFilter is "all" or "unread" ? unreadCount : CountCategory(categoryFilter);
            var total = hasMore ? "Newest first" : list.Count == 1 ? "1 notification" : $"{list.Count} notifications";
            return unread > 0 && categoryFilter is not ("unread" or "snoozed") ? $"{total} · {unread} unread" : total;
        }
    }

    private string EmptyHint => categoryFilter switch
    {
        "unread" => "You’ve read everything.",
        "snoozed" => "Snoozed notifications wait here until their time comes.",
        "all" => "New messages, tickets and meetings show up here and in the iPop Notifier.",
        _ => $"Nothing from {CurrentCategoryTitle} right now."
    };

    private string DndLabel => quietHoursEnabled ? $"On · {quietStart} – {quietEnd}" : "Off";

    private static bool IsSnoozed(NotificationItemDto n) => n.SnoozedUntil is { } s && s > DateTime.UtcNow;

    private static string SnoozeLabel(DateTime utc)
    {
        var local = utc.Kind == DateTimeKind.Local ? utc : DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime();
        if (local.Date == DateTime.Today) return $"today {local:HH:mm}";
        if (local.Date == DateTime.Today.AddDays(1)) return $"tomorrow {local:HH:mm}";
        return local.ToString("ddd d MMM, HH:mm");
    }

    // ---- What each row / the detail shows ----
    private static string IconOf(NotificationItemDto n)
    {
        var e = n.EventType.ToLowerInvariant();
        var u = n.TargetUrl ?? "";
        return n.Category switch
        {
            "mention" => "at-sign",
            "chat" => e == "chat.emergency" ? "siren" : IsGroupChat(n) ? "users" : "message-circle",
            "mail" => "mail",
            "tickets" => e is "ticket.resolved" or "ticket.closed" || n.Body.Contains("Confirmed as fixed") || n.Body.Contains("closed.") ? "circle-check-big" : "life-buoy",
            "task" => "kanban",
            "meeting" => e == "call.missed" ? "phone-missed" : e.StartsWith("calendar.") ? "calendar-days" : "calendar-clock",
            "news" => e.StartsWith("memo.") ? "megaphone" : "newspaper",
            _ => e.StartsWith("system.epop") ? "archive" : u.StartsWith("/notes") ? "notebook-pen" : u.StartsWith("/files") ? "folder" : "shield-check",
        };
    }

    private static bool ShowAvatar(NotificationItemDto n) =>
        !string.IsNullOrEmpty(n.ActorName) && n.Category is "chat" or "mention" or "mail" or "meeting";

    /// Chat rows are titled "Name · 3 new messages": the list shows the name and a "3 new" chip instead.
    private static string RowTitle(NotificationItemDto n) =>
        n.EventType == "chat.message" ? StripCount(n.Title) : n.Title;

    private static string StripCount(string title)
    {
        var i = title.LastIndexOf(" · ", StringComparison.Ordinal);
        return i > 0 && title.EndsWith(" new messages", StringComparison.Ordinal) ? title[..i] : title;
    }

    private static int NewCount(NotificationItemDto n)
    {
        if (n.EventType != "chat.message") return 0;
        var i = n.Title.LastIndexOf(" · ", StringComparison.Ordinal);
        if (i < 0 || !n.Title.EndsWith(" new messages", StringComparison.Ordinal)) return 1;
        return int.TryParse(n.Title[(i + 3)..^" new messages".Length], out var c) ? c : 1;
    }

    private static string SourceLine(NotificationItemDto n)
    {
        var e = n.EventType.ToLowerInvariant();
        return n.Category switch
        {
            "chat" => e == "chat.emergency" ? "Chat · Emergency" : IsGroupChat(n) ? "Chat · Group" : "Chat · Direct message",
            "mention" => n.TargetUrl?.StartsWith("/notes") == true ? "Mention · Notes" : "Mention · Chat",
            "tickets" => TicketNumber(n) is { } num ? $"iDesk · {num}" : "iDesk",
            "meeting" => e == "call.missed" ? "Calls" : e.StartsWith("calendar.") ? "Calendar" : "Meetings",
            "task" => "Tasks",
            "news" => e.StartsWith("memo.") ? "Memo" : "News",
            "mail" => "Mail",
            _ => n.TargetUrl?.StartsWith("/notes") == true ? "Notes" : "iPop",
        };
    }

    /// A group row's body is "Sender: text" and its title is the group, not the sender.
    private static bool IsGroupChat(NotificationItemDto n) =>
        !string.IsNullOrEmpty(n.ActorName) && n.Body.StartsWith(n.ActorName + ": ", StringComparison.Ordinal) && RowTitle(n) != n.ActorName;

    private static string? TicketNumber(NotificationItemDto n)
    {
        var last = n.Body.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault();
        return last is not null && last.StartsWith('#') ? last : null;
    }

    /// iDesk bodies are "Person · Role · Unit / what happened / #number" (one per line): the person goes in the header, the number in the
    /// source line, so the text shows only what happened.
    private static string[] TicketLines(NotificationItemDto n) =>
        n.Body.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    private static string BodyText(NotificationItemDto n)
    {
        if (n.Category != "tickets") return n.Body;
        var lines = TicketLines(n).Where(l => !l.StartsWith('#')).ToList();
        return lines.Count > 1 ? string.Join("\n", lines.Skip(1)) : n.Body;
    }

    private static string WhoLine(NotificationItemDto n) => n.Category switch
    {
        "chat" => RowTitle(n),
        "tickets" => TicketLines(n) is { Length: > 1 } tl && !tl[0].StartsWith('#') ? tl[0] : "iDesk",
        "system" => "iPop",
        _ => !string.IsNullOrEmpty(n.ActorName) ? n.ActorName! : CategoryName(n.Category),
    };

    private static string DetailTitle(NotificationItemDto n)
    {
        if (n.EventType == "chat.message")
        {
            var c = NewCount(n);
            return n.IsRead ? "Latest messages" : c > 1 ? $"{c} new messages" : "New message";
        }
        return n.Title;
    }

    private static string WhenLong(DateTime local)
    {
        if (local.Date == DateTime.Today) return $"Today at {local:HH:mm}";
        if (local.Date == DateTime.Today.AddDays(-1)) return $"Yesterday at {local:HH:mm}";
        return local.ToString("dddd d MMMM 'at' HH:mm");
    }

    private static string MuteLabel(string kind, bool muted) => (muted ? "Unmute " : "Mute ") + CategoryName(kind);

    private IEnumerable<IGrouping<string, NotificationItemDto>> GroupedNotifications =>
        FilteredList.GroupBy(n =>
        {
            var local = n.At.Date;
            var today = DateTime.Today;
            if (local == today) return "Today";
            if (local == today.AddDays(-1)) return "Yesterday";
            if (local >= today.AddDays(-7)) return "Earlier this week";
            return "Older";
        });

    // Avatar tile: initials on a colour picked from the name (same name = same colour).
    private static readonly string[] AvatarColors = ["#B45B8C", "#3A86C8", "#6F8290", "#C99A1E", "#A3447F", "#2E9E5B", "#5562B8", "#D9703C"];
    private static string AvatarColor(string? name)
    {
        var h = 0;
        foreach (var ch in name ?? "") h = unchecked(h * 31 + ch);
        return AvatarColors[(h & 0x7fffffff) % AvatarColors.Length];
    }
    private static string Initials(string? name)
    {
        var parts = (name ?? "?").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length switch { 0 => "?", 1 => parts[0][..1].ToUpperInvariant(), _ => (parts[0][..1] + parts[1][..1]).ToUpperInvariant() };
    }

    private static string FormatTime(DateTime dt)
    {
        var now = DateTime.Now;
        var diff = now - dt;
        if (diff.TotalMinutes < 1) return "Just now";
        if (diff.TotalMinutes < 60) return $"{(int)diff.TotalMinutes}m";
        if (dt.Date == DateTime.Today) return dt.ToString("HH:mm");
        if (dt.Date == DateTime.Today.AddDays(-1)) return "Yesterday";
        if (diff.TotalDays < 7) return dt.ToString("ddd");
        return dt.ToString("dd MMM");
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        await JS.InvokeVoidAsync("ipop.icons");
        // The menu only exists after the render that set snoozeMenuOpen; place it now.
        if (snoozeMenuAt is { } at && snoozeMenu is not null)
        {
            snoozeMenuAt = null;
            // Keyboard activation has no pointer position: drop the menu under the detail toolbar.
            if (at.Y <= 14) at = (await JS.InvokeAsync<double>("ipop.viewportWidth") - 280, 170);
            await snoozeMenu.OpenAsync(Math.Max(8, at.X), at.Y);
        }
    }

    public void Dispose()
    {
        Notifs.UnreadChanged -= OnLiveUnreadChanged;
        if (mDetail) _ = JS.InvokeVoidAsync("ipop.mobile.clearDetail").AsTask().ContinueWith(_ => { });
        mRef?.Dispose();
        searchCts?.Cancel();
        searchCts?.Dispose();
    }
}
