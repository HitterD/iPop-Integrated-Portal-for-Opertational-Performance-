using IPop.Application.Notes;
using IPop.Application.Rbac;
using IPop.Application.Search;
using IPop.Infrastructure.Notes;

namespace IPop.Infrastructure.Search.Providers;

/// Notes: every page the user can open (own, shared to them or to a room they are in, and everything beneath).
/// Reuses NoteService's visibility and its stored plain text; the binary Yjs state is never read as text.
public sealed class NotesSearchProvider(INoteService notes, IPermissionEvaluator? perms = null) : ISearchModuleProvider
{
    public SearchModuleInfo Info { get; } = new(SearchModules.Notes, "Notes", "notebook-pen", 40);

    public static readonly string TruncatedMessage =
        "Some older pages may have an incomplete search index. Opening them refreshes the index.";

    public Task<bool> CanSearchAsync(Guid userId, CancellationToken ct) => SearchPaging.AllowedAsync(perms, userId, "notes.read", ct);

    public async Task<SearchPage> SearchAsync(SearchQuery q, CancellationToken ct)
    {
        var page = await notes.SearchPagedAsync(q.UserId, q.Text, q.Page, q.PageSize, ct);
        var hits = page.Items.Select(r => new SearchHit(r.Id.ToString(), Info.Key,
            string.IsNullOrWhiteSpace(r.Title) ? "Untitled" : r.Title,
            string.IsNullOrEmpty(r.Emoji) ? "Note" : $"{r.Emoji} Note",
            r.Snippet, $"/notes/{r.Id}", "notebook-pen", r.UpdatedAt,
            r.TitleExact ? SearchMatch.TitleExact : r.TitleMatch ? SearchMatch.Title : SearchMatch.Body)).ToList();
        return SearchPage.Ready(Info.Key, hits, page.Total, q.Page, q.PageSize, page.Truncated ? TruncatedMessage : null);
    }
}
