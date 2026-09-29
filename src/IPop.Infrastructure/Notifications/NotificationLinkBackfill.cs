using IPop.Core.Entities;
using IPop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace IPop.Infrastructure.Notifications;

/// <summary>
/// Notifications created before deep links existed point at a module page ("/mail",
/// "/news", "/calendar") rather than the item they announce, so clicking one lands
/// "outside" with no hint which notification it was. This walks those rows once and, when
/// the item can be identified UNAMBIGUOUSLY, rewrites the link to the specific item.
/// Idempotent: only rows whose link is still the bare module page are considered, and
/// anything ambiguous or unmatched is left alone (a general page beats a wrong item).
/// </summary>
public class NotificationLinkBackfill(IServiceScopeFactory scopes, ILogger<NotificationLinkBackfill> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            // Let the API finish migrating/seeding before touching data.
            await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
            await using var scope = scopes.CreateAsyncScope();
            var fixedCount = await RunAsync(scope.ServiceProvider.GetRequiredService<IPopDbContext>(), stoppingToken);
            if (fixedCount > 0) logger.LogInformation("Notification link backfill: {Count} notification(s) now deep-link to their item.", fixedCount);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Notification link backfill failed; old notifications keep their module-page links.");
        }
    }

    private static readonly string[] BareLinks = ["/mail", "/news", "/calendar"];
    private const string MailPrefix = "Email baru dari ";

    /// Returns how many requests were rewritten.
    public static async Task<int> RunAsync(IPopDbContext db, CancellationToken ct)
    {
        var requests = await db.NotificationRequests
            .Where(r => r.TargetUrl != null && BareLinks.Contains(r.TargetUrl))
            .OrderBy(r => r.CreatedAt).Take(2000)
            .ToListAsync(ct);
        if (requests.Count == 0) return 0;

        var changed = 0;
        foreach (var r in requests)
        {
            var url = r.EventType switch
            {
                "mail.new" => await MailLinkAsync(db, r, ct),
                "news.published" or "news.condolence" or "memo.published" or "memo.mandatory" or "memo.reminder"
                    => await NewsLinkAsync(db, r, ct),
                "meeting.reminder" => await MeetingLinkAsync(db, r, ct),
                _ => null,
            };
            if (url is null || url == r.TargetUrl) continue;
            r.TargetUrl = url;
            changed++;
        }
        if (changed > 0) await db.SaveChangesAsync(ct);
        return changed;
    }

    /// "Email baru dari {sender}" + body "{subject} — {preview}", delivered to one person:
    /// the mail that sender sent that person with that subject, close to the notification time.
    private static async Task<string?> MailLinkAsync(IPopDbContext db, NotificationRequest r, CancellationToken ct)
    {
        if (!r.Title.StartsWith(MailPrefix, StringComparison.Ordinal)) return null;
        var senderName = r.Title[MailPrefix.Length..].Trim();
        var subject = r.Body.Split(" — ", 2)[0].Trim();
        var recipients = await db.NotificationDeliveries.AsNoTracking()
            .Where(d => d.RequestId == r.Id).Select(d => d.UserId).ToListAsync(ct);
        if (recipients.Count == 0 || subject.Length == 0) return null;

        var from = r.CreatedAt.AddMinutes(-10);
        var to = r.CreatedAt.AddMinutes(10);
        var hits = await db.Mails.AsNoTracking()
            .Where(m => m.Subject == subject && m.Sender.Name == senderName
                        && m.SentAt != null && m.SentAt >= from && m.SentAt <= to
                        && m.Recipients.Any(x => recipients.Contains(x.UserId) && x.Folder != "sent"))
            .Select(m => m.Id).Take(2).ToListAsync(ct);
        return hits.Count == 1 ? $"/mail?mailId={hits[0]}" : null;
    }

    /// Titles are "[Prefix] {post title}" (or "[Pengingat Wajib Baca] …"); strip the leading tag.
    private static async Task<string?> NewsLinkAsync(IPopDbContext db, NotificationRequest r, CancellationToken ct)
    {
        var title = r.Title;
        if (title.StartsWith('['))
        {
            var close = title.IndexOf(']');
            if (close > 0) title = title[(close + 1)..];
        }
        title = title.Trim();
        if (title.Length == 0) return null;
        var hits = await db.NewsPosts.AsNoTracking()
            .Where(p => p.Title == title && p.PublishedAt != null
                        && p.PublishedAt >= r.CreatedAt.AddHours(-1) && p.PublishedAt <= r.CreatedAt.AddHours(1))
            .Select(p => p.Id).Take(2).ToListAsync(ct);
        return hits.Count == 1 ? $"/news/{hits[0]}" : null;
    }

    /// "Meeting Reminder: {title}" -> the meeting reminded to this person around the notification time.
    private static async Task<string?> MeetingLinkAsync(IPopDbContext db, NotificationRequest r, CancellationToken ct)
    {
        // Two generations of titles exist: "Meeting Reminder: X" and the older Indonesian "Pengingat Rapat: X".
        var prefix = new[] { "Meeting Reminder: ", "Pengingat Rapat: " }.FirstOrDefault(x => r.Title.StartsWith(x, StringComparison.Ordinal));
        if (prefix is null) return null;
        var title = r.Title[prefix.Length..].Trim();
        var userIds = await db.NotificationDeliveries.AsNoTracking().Where(d => d.RequestId == r.Id).Select(d => d.UserId).ToListAsync(ct);
        var hits = await db.MeetingReminders.AsNoTracking()
            .Where(x => userIds.Contains(x.UserId) && x.Meeting.Title == title
                        && x.SentAt != null && x.SentAt >= r.CreatedAt.AddMinutes(-15) && x.SentAt <= r.CreatedAt.AddMinutes(15))
            .Select(x => x.MeetingId).Distinct().Take(2).ToListAsync(ct);
        return hits.Count == 1 ? $"/calls?tab=scheduled&meeting={hits[0]}" : null;
    }
}
