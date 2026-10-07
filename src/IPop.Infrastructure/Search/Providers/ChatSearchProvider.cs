using IPop.Application.Chat;
using IPop.Application.Rbac;
using IPop.Application.Search;

namespace IPop.Infrastructure.Search.Providers;

/// Chat: rooms by name/member (page 1 only) and messages across every room the user is an active member of.
/// Membership, deleted messages and thread replies follow IChatService exactly; a message opens its room
/// at that message (/chat/{room}?jump={message}).
public sealed class ChatSearchProvider(IChatService chat, IPermissionEvaluator? perms = null) : ISearchModuleProvider
{
    public SearchModuleInfo Info { get; } = new(SearchModules.Chat, "Chat", "message-circle", 50);
    private const int RoomsOnFirstPage = 5;

    public Task<bool> CanSearchAsync(Guid userId, CancellationToken ct) => SearchPaging.AllowedAsync(perms, userId, "chat.read", ct);

    public async Task<SearchPage> SearchAsync(SearchQuery q, CancellationToken ct)
    {
        var hits = new List<SearchHit>();
        var total = 0;
        if (q.Page == 1)
        {
            var rooms = await chat.SearchRoomsAsync(q.UserId, q.Text, ct);
            foreach (var r in rooms.Items.Take(RoomsOnFirstPage))
            {
                var name = r.Type == "dm" ? r.RecipientName ?? "Direct message" : r.Name ?? "Group";
                hits.Add(new SearchHit($"room:{r.Id}", Info.Key, name, r.Type == "dm" ? "Direct message" : "Group",
                    null, $"/chat/{r.Id}", r.Type == "dm" ? "user" : "users", r.LastMessageAt, SearchMatch.Classify(name, q.Lower)));
            }
            total += Math.Min(rooms.Items.Count, RoomsOnFirstPage);
        }
        var messages = await chat.SearchAllMessagesAsync(q.UserId, q.Text, q.Page, q.PageSize, ct);
        foreach (var m in messages.Items)
            hits.Add(new SearchHit($"msg:{m.Id}", Info.Key, m.SenderName, "Message", SearchText.Snippet(Clean(m.Content), q.Lower),
                $"/chat/{m.RoomId}?jump={m.Id}", "message-circle", m.CreatedAt, SearchMatch.Body));
        total += messages.TotalCount;
        var hasMore = q.Page * q.PageSize < messages.TotalCount;
        return new SearchPage(Info.Key, SearchStatus.Ready, hits, total, q.Page, q.PageSize, hasMore);
    }

    private static string Clean(string content) => content.StartsWith("[forwarded]", StringComparison.OrdinalIgnoreCase) ? content["[forwarded]".Length..] : content;
}
