using IPop.Application.Notes;

namespace IPop.Web.Services;

/// What the Notes and Whiteboards list pages show: the same list the sidebar already loads, filtered by "Mine / Shared with me /
/// Favorites", searched by title and first lines, sorted, and cut into a first batch that grows with "Show more".
/// Pure on purpose (no UI, no clock) so the rules can be tested.
public static class NoteListView
{
    public enum Filter { All, Mine, Shared, Favorites }
    public enum Sort { Recent, Name, Created }

    public static Filter ParseFilter(string? s) => s switch { "mine" => Filter.Mine, "shared" => Filter.Shared, "fav" => Filter.Favorites, _ => Filter.All };
    public static Sort ParseSort(string? s) => s switch { "name" => Sort.Name, "created" => Sort.Created, _ => Sort.Recent };

    /// kind: "all" (Notes list, pages and whiteboards together), "page" or "board" (the Pages / Whiteboards filters). The per-person tutorial page stays out of both lists
    /// (it has its own row in the sidebar).
    public static List<NoteListItemDto> Build(IEnumerable<NoteListItemDto> pages, string kind, Filter filter, string? query, Sort sort)
    {
        var q = (query ?? "").Trim();
        // kind: "page" = text pages, "board" = whiteboards, anything else ("all") = both: whiteboards are Notes pages like the others.
        IEnumerable<NoteListItemDto> rows = pages.Where(p => (kind == "board" ? p.Kind == "board" : kind == "page" ? p.Kind != "board" : true) && !p.IsGuide);
        rows = filter switch
        {
            Filter.Mine => rows.Where(p => p.IsMine),
            Filter.Shared => rows.Where(p => !p.IsMine),
            Filter.Favorites => rows.Where(p => p.IsFavorite),
            _ => rows,
        };
        if (q.Length > 0)
            rows = rows.Where(p => Contains(p.Title, q) || Contains(p.Excerpt, q) || Contains(p.OwnerName, q));
        return (sort switch
        {
            Sort.Name => rows.OrderBy(p => string.IsNullOrWhiteSpace(p.Title) ? "Untitled" : p.Title, StringComparer.CurrentCultureIgnoreCase).ThenByDescending(p => p.UpdatedAt),
            Sort.Created => rows.OrderByDescending(p => p.CreatedAt ?? p.UpdatedAt),
            _ => rows.OrderByDescending(p => p.UpdatedAt),
        }).ToList();
    }

    private static bool Contains(string? text, string q) => text is not null && text.Contains(q, StringComparison.CurrentCultureIgnoreCase);

    /// "Today", "Yesterday", "This week", "Earlier": the group heading above a card, from the edit time, for the Recent sort.
    public static string Group(DateTime updatedUtc, DateTime nowUtc)
    {
        var d = nowUtc.Date - DateTime.SpecifyKind(updatedUtc, DateTimeKind.Utc).Date;
        if (d.TotalDays <= 0) return "Today";
        if (d.TotalDays == 1) return "Yesterday";
        if (d.TotalDays < 7) return "This week";
        if (d.TotalDays < 31) return "This month";
        return "Earlier";
    }

    /// Two lines of text for a card: the first lines of the page, trimmed to what fits (the card clips the rest).
    public static string Snippet(string? excerpt, int max = 140)
    {
        var t = (excerpt ?? "").Trim();
        return t.Length <= max ? t : t[..(max - 1)].TrimEnd() + "…";
    }
}
