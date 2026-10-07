using IPop.Application.Calls;
using IPop.Application.Rbac;
using IPop.Application.Search;
using IPop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IPop.Infrastructure.Search.Providers;

/// Calls: the user's own call log, by the other participants' names or the group's name. No transcripts exist.
public sealed class CallsSearchProvider(IPopDbContext db, IPermissionEvaluator? perms = null) : ISearchModuleProvider
{
    public SearchModuleInfo Info { get; } = new(SearchModules.Calls, "Calls", "phone", 110);

    public Task<bool> CanSearchAsync(Guid userId, CancellationToken ct) => SearchPaging.AllowedAsync(perms, userId, "calls.use", ct);

    private sealed record Row(Guid CallId, string Direction, string Result, int Duration, DateTime StartedAt, string Kind, string? GroupName);

    public async Task<SearchPage> SearchAsync(SearchQuery q, CancellationToken ct)
    {
        var s = q.Lower;
        var me = q.UserId;
        var scope = db.CallHistories.AsNoTracking().Where(h => h.PerformerId == me
            && (h.Call.Participants.Any(p => p.UserId != me && p.User.Name.ToLower().Contains(s))
                || (h.Call.ChatRoom != null && h.Call.ChatRoom.Name != null && h.Call.ChatRoom.Name.ToLower().Contains(s))));
        var tiers = new[]
        {
            scope.OrderByDescending(h => h.StartedAt).Select(h => new Row(h.CallId, h.Direction, h.Result, h.DurationSeconds, h.StartedAt, h.Call.Kind,
                h.Call.ChatRoom != null ? h.Call.ChatRoom.Name : null)),
        };
        var (rows, total) = await SearchPaging.PageAsync(tiers, q.Skip, q.PageSize, ct);
        var callIds = rows.Select(r => r.CallId).ToList();
        var peers = callIds.Count == 0 ? [] : await db.CallParticipants.AsNoTracking()
            .Where(p => callIds.Contains(p.CallId) && p.UserId != me).OrderBy(p => p.JoinedAt)
            .Select(p => new { p.CallId, p.User.Name }).ToListAsync(ct);
        var names = peers.GroupBy(p => p.CallId).ToDictionary(g => g.Key, g => g.Select(x => x.Name).ToList());
        var hits = rows.Select(r =>
        {
            var who = r.GroupName is { Length: > 0 } ? r.GroupName
                : names.TryGetValue(r.CallId, out var n) ? (n.Count <= 2 ? string.Join(" & ", n) : $"{n[0]}, {n[1]} +{n.Count - 2}") : "Unknown";
            var what = r.Result == "completed" ? $"{(r.Direction == "incoming" ? "Incoming" : "Outgoing")} · {TimeSpan.FromSeconds(r.Duration):m\\:ss}" : Cap(r.Result);
            return new SearchHit(r.CallId.ToString(), Info.Key, who, $"{what} · {r.StartedAt.ToLocalTime():dd MMM HH:mm}", null,
                "/calls?tab=recents", r.Result == "missed" ? "phone-missed" : "phone", r.StartedAt, SearchMatch.Classify(who, s));
        }).ToList();
        return SearchPage.Ready(Info.Key, hits, total, q.Page, q.PageSize);
    }

    private static string Cap(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..].Replace('-', ' ');
}

/// Notifications: what was delivered to this user (title, then body). Follows the request's own deep link.
public sealed class NotificationsSearchProvider(IPopDbContext db) : ISearchModuleProvider
{
    public SearchModuleInfo Info { get; } = new(SearchModules.Notifications, "Notifications", "bell", 120);

    public Task<bool> CanSearchAsync(Guid userId, CancellationToken ct) => Task.FromResult(true);

    private sealed record Row(Guid RequestId, string Title, string Body, string EventType, string? TargetUrl, DateTime At, bool Read);

    public async Task<SearchPage> SearchAsync(SearchQuery q, CancellationToken ct)
    {
        var s = q.Lower;
        var scope = db.NotificationDeliveries.AsNoTracking().Where(d => d.UserId == q.UserId);
        IQueryable<Row> Project(IQueryable<Core.Entities.NotificationDelivery> x) =>
            x.OrderByDescending(d => d.DeliveredAt ?? d.UpdatedAt).Select(d => new Row(d.RequestId, d.Request.Title, d.Request.Body, d.Request.EventType,
                d.Request.TargetUrl, d.DeliveredAt ?? d.UpdatedAt, d.Status == "read" || d.Status == "acknowledged"));
        var tiers = new[]
        {
            Project(scope.Where(d => d.Request.Title.ToLower() == s)),
            Project(scope.Where(d => d.Request.Title.ToLower() != s && d.Request.Title.ToLower().Contains(s))),
            Project(scope.Where(d => !d.Request.Title.ToLower().Contains(s) && d.Request.Body.ToLower().Contains(s))),
        };
        var (rows, total) = await SearchPaging.PageAsync(tiers, q.Skip, q.PageSize, ct);
        var hits = rows.Select(r =>
        {
            var match = SearchMatch.Classify(r.Title, s);
            return new SearchHit(r.RequestId.ToString(), Info.Key, r.Title, $"{(r.Read ? "Read" : "Unread")} · {r.At.ToLocalTime():dd MMM HH:mm}",
                match == SearchMatch.Body ? SearchText.Snippet(r.Body, s) : null,
                string.IsNullOrWhiteSpace(r.TargetUrl) || !r.TargetUrl.StartsWith('/') ? "/notifications" : r.TargetUrl, "bell", r.At, match);
        }).ToList();
        return SearchPage.Ready(Info.Key, hits, total, q.Page, q.PageSize);
    }
}

/// Navigation shortcuts to module pages; Administration only for admins. Never searches admin data.
public sealed class NavigationSearchProvider(IPopDbContext db) : ISearchModuleProvider
{
    public SearchModuleInfo Info { get; } = new(SearchModules.Navigation, "Go to", "compass", 5);

    public Task<bool> CanSearchAsync(Guid userId, CancellationToken ct) => Task.FromResult(true);

    private sealed record Item(string Label, string Icon, string Url, string Keywords, bool AdminOnly = false);

    private static readonly Item[] Items =
    [
        new("Dashboard", "layout-grid", "/dashboard", "home overview"),
        new("Directory", "users", "/directory", "people employees org chart"),
        new("Projects", "kanban", "/projects", "tasks boards"),
        new("Notes", "notebook-pen", "/notes", "pages wiki"),
        new("Tickets", "life-buoy", "/tickets", "idesk ict support helpdesk"),
        new("New ticket", "life-buoy", "/tickets/new", "idesk ict support helpdesk"),
        new("Chat", "message-circle", "/chat", "messages rooms groups"),
        new("Mail", "mail", "/mail", "email inbox"),
        new("News & Memos", "newspaper", "/news", "announcements memo"),
        new("Calls", "phone", "/calls", "video voice"),
        new("Files", "folder", "/files", "nas synology documents"),
        new("Calendar", "calendar", "/calendar", "meetings events agenda"),
        new("Notifications", "bell", "/notifications", "alerts inbox"),
        new("Profile", "circle-user", "/profile", "me account photo"),
        new("Settings", "settings", "/settings", "preferences theme"),
        new("Administration", "sliders-horizontal", "/admin", "admin users roles rbac audit sessions", AdminOnly: true),
    ];

    public async Task<SearchPage> SearchAsync(SearchQuery q, CancellationToken ct)
    {
        var s = q.Lower;
        var isAdmin = await SearchPaging.IsAdminAsync(db, q.UserId, ct);
        var ranked = Items.Where(i => !i.AdminOnly || isAdmin)
            .Where(i => i.Label.Contains(s, StringComparison.OrdinalIgnoreCase) || i.Keywords.Contains(s, StringComparison.OrdinalIgnoreCase))
            .OrderBy(i => SearchMatch.Rank(SearchMatch.Classify(i.Label, s)));
        var (items, total) = SearchPaging.Page(ranked, q.Skip, q.PageSize);
        var hits = items.Select(i => new SearchHit(i.Url, Info.Key, i.Label, "Open page", null, i.Url, i.Icon, null, SearchMatch.Classify(i.Label, s))).ToList();
        return SearchPage.Ready(Info.Key, hits, total, q.Page, q.PageSize);
    }
}
