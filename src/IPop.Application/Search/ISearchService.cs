namespace IPop.Application.Search;

/// Legacy flat row for GET /api/search (kept for compatibility).
public record SearchResultItem(
    string Group,
    string Title,
    string? Subtitle,
    string Url,
    string Icon,
    string? AvatarColor = null,
    string? Initials = null,
    /// People only: their NIK (calendar link) and "Free now" / "In a meeting until 11:00".
    string? Nik = null,
    string? Status = null);

/// Module keys of the per-module search (GET /api/search/modules/{module}).
public static class SearchModules
{
    public const string Directory = "directory";
    public const string Projects = "projects";
    public const string Tasks = "tasks";
    public const string Notes = "notes";
    public const string Chat = "chat";
    public const string Mail = "mail";
    public const string News = "news";
    public const string Calendar = "calendar";
    public const string Tickets = "tickets";
    public const string Files = "files";
    public const string Calls = "calls";
    public const string Notifications = "notifications";
    public const string Navigation = "navigation";

    public const int MinQueryLength = 2;
    public const int DefaultPageSize = 10;
    public const int MaxPageSize = 50;
}

/// How a hit matched, best first: exact title, title contains, body contains.
public static class SearchMatch
{
    public const string TitleExact = "title-exact";
    public const string Title = "title";
    public const string Body = "body";

    public static int Rank(string match) => match switch { TitleExact => 0, Title => 1, _ => 2 };

    public static string Classify(string? title, string lowerQuery)
    {
        var t = (title ?? "").Trim();
        if (t.Equals(lowerQuery, StringComparison.OrdinalIgnoreCase)) return TitleExact;
        return t.Contains(lowerQuery, StringComparison.OrdinalIgnoreCase) ? Title : Body;
    }
}

/// Per-module envelope status. An error is reported as such, never as an empty result.
public static class SearchStatus
{
    public const string Ready = "ready";
    public const string Error = "error";
    /// The source can't be searched for this account right now (e.g. no NAS credential); Message says why.
    public const string Unavailable = "unavailable";
    public const string Forbidden = "forbidden";
}

/// One hit. Title/Snippet are plain text: the client encodes them before highlighting.
public record SearchHit(
    string Id,
    string Module,
    string Title,
    string? Subtitle,
    string? Snippet,
    string Url,
    string Icon,
    DateTime? UpdatedAt,
    string Match,
    bool Draft = false,
    string? AvatarColor = null,
    string? Initials = null,
    /// People only: NIK for the calendar/invite actions, and the free/busy hint.
    string? Nik = null,
    string? Status = null);

public record SearchPage(
    string Module,
    string Status,
    IReadOnlyList<SearchHit> Items,
    int Total,
    int Page,
    int PageSize,
    bool HasMore,
    /// Honest caveat for a ready page (e.g. "long pages are only searched in their first N characters"), or the error.
    string? Message = null,
    /// A ready page that could not cover every source in time (slow NAS); the client can say so.
    bool Partial = false,
    /// For Unavailable: where the user can fix it (e.g. /files to connect).
    string? ActionUrl = null)
{
    public int? NextPage => HasMore ? Page + 1 : null;

    public static SearchPage Ready(string module, IReadOnlyList<SearchHit> items, int total, int page, int pageSize, string? message = null, bool partial = false)
        => new(module, SearchStatus.Ready, items, total, page, pageSize, page * pageSize < total, message, partial);
    public static SearchPage Empty(string module, int page, int pageSize) => Ready(module, [], 0, page, pageSize);
    public static SearchPage Error(string module, string message, int page, int pageSize)
        => new(module, SearchStatus.Error, [], 0, page, pageSize, false, message);
    public static SearchPage Unavailable(string module, string message, int page, int pageSize, string? actionUrl = null)
        => new(module, SearchStatus.Unavailable, [], 0, page, pageSize, false, message, false, actionUrl);
    public static SearchPage Forbidden(string module, int page, int pageSize)
        => new(module, SearchStatus.Forbidden, [], 0, page, pageSize, false, "You don't have access to this module.");
}

public record SearchModuleInfo(string Key, string Label, string Icon, int Order);

public record SearchQuery(Guid UserId, string Text, int Page, int PageSize)
{
    /// Trimmed, lower-cased query for case-insensitive matching.
    public string Lower { get; } = Text.Trim().ToLowerInvariant();
    public int Skip => (Page - 1) * PageSize;
}

/// One module's search. Access is checked inside the provider (membership, sharing, folder scope);
/// CanSearchAsync is the coarse module gate (RBAC) used for the catalog.
public interface ISearchModuleProvider
{
    SearchModuleInfo Info { get; }
    Task<bool> CanSearchAsync(Guid userId, CancellationToken ct);
    Task<SearchPage> SearchAsync(SearchQuery query, CancellationToken ct);
}

public interface ISearchService
{
    /// Legacy one-shot search (GET /api/search).
    Task<IReadOnlyList<SearchResultItem>> SearchAsync(Guid userId, string query, CancellationToken ct = default);

    /// Modules this user may search, in display order.
    Task<IReadOnlyList<SearchModuleInfo>> ModulesAsync(Guid userId, CancellationToken ct = default);

    /// One module, one page. Null = unknown module. Provider failures come back as Status "error".
    Task<SearchPage?> SearchModuleAsync(Guid userId, string module, string query, int page, int pageSize, CancellationToken ct = default);
}

/// Snippet/paging helpers shared by providers and the client.
public static class SearchText
{
    public static (int Page, int PageSize) Clamp(int page, int pageSize)
        => (Math.Max(1, page), Math.Clamp(pageSize <= 0 ? SearchModules.DefaultPageSize : pageSize, 1, SearchModules.MaxPageSize));

    /// Collapses whitespace so snippets stay on one line.
    public static string Flatten(string? s)
        => string.IsNullOrEmpty(s) ? "" : string.Join(' ', s.Split((char[])[' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries));

    /// A window of <paramref name="text"/> around the first match (or its start when nothing matches).
    public static string? Snippet(string? text, string lowerQuery, int radius = 60)
    {
        var flat = Flatten(text);
        if (flat.Length == 0) return null;
        var i = lowerQuery.Length == 0 ? -1 : flat.IndexOf(lowerQuery, StringComparison.OrdinalIgnoreCase);
        var max = radius * 2 + lowerQuery.Length;
        if (i < 0) return flat.Length <= max ? flat : flat[..max] + "…";
        var start = Math.Max(0, i - radius);
        var len = Math.Min(flat.Length - start, max);
        return (start > 0 ? "…" : "") + flat.Substring(start, len) + (start + len < flat.Length ? "…" : "");
    }
}
