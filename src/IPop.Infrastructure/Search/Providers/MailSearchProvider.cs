using IPop.Application.Rbac;
using IPop.Application.Search;
using IPop.Infrastructure.Mail;
using IPop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IPop.Infrastructure.Search.Providers;

/// Mail: the user's own inbox, sent and drafts (never trash/archive), subject then body text.
public sealed class MailSearchProvider(IPopDbContext db, IPermissionEvaluator? perms = null) : ISearchModuleProvider
{
    public SearchModuleInfo Info { get; } = new(SearchModules.Mail, "Mail", "mail", 60);

    public Task<bool> CanSearchAsync(Guid userId, CancellationToken ct) => SearchPaging.AllowedAsync(perms, userId, "mail.read", ct);

    private sealed record Row(Guid MailId, string Subject, string Body, string Sender, DateTime At, string Folder);

    public async Task<SearchPage> SearchAsync(SearchQuery q, CancellationToken ct)
    {
        var s = q.Lower;
        var scope = db.MailRecipients.AsNoTracking()
            .Where(r => r.UserId == q.UserId && (r.Folder == "inbox" || r.Folder == "sent" || r.Folder == "draft") && r.Mail.CancelledAt == null);
        IQueryable<Row> Project(IQueryable<Core.Entities.MailRecipient> x) =>
            x.OrderByDescending(r => r.Mail.SentAt ?? r.Mail.CreatedAt)
             .Select(r => new Row(r.MailId, r.Mail.Subject, r.Mail.Body, r.Mail.Sender.Name, r.Mail.SentAt ?? r.Mail.CreatedAt, r.Folder));
        var tiers = new[]
        {
            Project(scope.Where(r => r.Mail.Subject.ToLower() == s)),
            Project(scope.Where(r => r.Mail.Subject.ToLower() != s && r.Mail.Subject.ToLower().Contains(s))),
            Project(scope.Where(r => !r.Mail.Subject.ToLower().Contains(s) && r.Mail.Body.ToLower().Contains(s))),
        };
        var (rows, total) = await SearchPaging.PageAsync(tiers, q.Skip, q.PageSize, ct);
        var hits = rows.Select(r =>
        {
            var match = SearchMatch.Classify(r.Subject, s);
            var text = MailHtml.ToPlainText(r.Body);
            var draft = r.Folder == "draft";
            return new SearchHit(r.MailId.ToString(), Info.Key, string.IsNullOrWhiteSpace(r.Subject) ? "(no subject)" : r.Subject,
                draft ? "Draft" : r.Folder == "sent" ? $"Sent · {r.At:dd MMM}" : $"From {r.Sender} · {r.At:dd MMM}",
                match == SearchMatch.Body ? SearchText.Snippet(text, s) : null,
                $"/mail?mailId={r.MailId}", "mail", r.At, match, Draft: draft);
        }).ToList();
        return SearchPage.Ready(Info.Key, hits, total, q.Page, q.PageSize);
    }
}
