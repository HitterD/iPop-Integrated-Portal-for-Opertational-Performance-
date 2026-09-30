using IPop.Core.Entities;
using IPop.Infrastructure.Notifications;
using Xunit;

namespace IPop.Tests;

/// Notifications created before deep links existed point at a bare module page. The backfill
/// rewrites them to the specific item only when that item is identifiable beyond doubt.
public class NotificationLinkBackfillTests
{
    private static NotificationRequest Req(NotificationGatewayClient c, string type, string title, string body, string url, DateTime at) => new()
    {
        Id = Guid.NewGuid(), ClientId = c.Id, IdempotencyKey = Guid.NewGuid().ToString("N"), Priority = "bulk",
        EventType = type, Title = title, Body = body, TargetUrl = url, Status = "delivered",
        AcceptedAt = at, CreatedAt = at, UpdatedAt = at,
    };

    private static void Deliver(TestDb t, NotificationRequest r, Guid userId)
        => t.Db.NotificationDeliveries.Add(new NotificationDelivery
        { Id = Guid.NewGuid(), RequestId = r.Id, UserId = userId, Status = "delivered", CreatedAt = r.CreatedAt, UpdatedAt = r.CreatedAt });

    private static Mail NewMail(Guid senderId, Guid recipientId, string subject, DateTime sentAt)
    {
        var m = new Mail { Id = Guid.NewGuid(), SenderId = senderId, Subject = subject, Body = "b", Status = "sent", CreatedAt = sentAt, SentAt = sentAt };
        m.Recipients.Add(new MailRecipient { Id = Guid.NewGuid(), MailId = m.Id, UserId = recipientId, RecipientType = "to", Folder = "inbox" });
        return m;
    }

    [Fact]
    public async Task Mail_notification_links_to_the_one_matching_message()
    {
        using var t = new TestDb();
        var sender = t.AddUser("S"); var me = t.AddUser("M");
        var c = t.AddNotificationClient("mail", "h:x");
        var at = DateTime.UtcNow.AddDays(-1);
        var mail = NewMail(sender.Id, me.Id, "Subject Meeting", at.AddSeconds(-2));
        t.Db.Mails.Add(mail);
        var n = Req(c, "mail.new", $"New email from {sender.Name}", "Subject Meeting — Body Meeting", "/mail", at);
        t.Db.NotificationRequests.Add(n); Deliver(t, n, me.Id);
        t.Db.SaveChanges();

        var changed = await NotificationLinkBackfill.RunAsync(t.Verify(), default);

        Assert.Equal(1, changed);
        Assert.Equal($"/mail?mailId={mail.Id}", t.Verify().NotificationRequests.Single().TargetUrl);
    }

    [Fact]
    public async Task Mail_with_two_equally_good_matches_is_left_alone()
    {
        using var t = new TestDb();
        var sender = t.AddUser("S"); var me = t.AddUser("M");
        var c = t.AddNotificationClient("mail", "h:x");
        var at = DateTime.UtcNow.AddDays(-1);
        t.Db.Mails.AddRange(NewMail(sender.Id, me.Id, "Same", at), NewMail(sender.Id, me.Id, "Same", at.AddSeconds(30)));
        var n = Req(c, "mail.new", $"Email baru dari {sender.Name}", "Same — x", "/mail", at);
        t.Db.NotificationRequests.Add(n); Deliver(t, n, me.Id);
        t.Db.SaveChanges();

        var changed = await NotificationLinkBackfill.RunAsync(t.Verify(), default);

        Assert.Equal(0, changed);
        Assert.Equal("/mail", t.Verify().NotificationRequests.Single().TargetUrl); // a general page beats a wrong item
    }

    [Fact]
    public async Task Mail_sent_to_someone_else_is_not_matched()
    {
        using var t = new TestDb();
        var sender = t.AddUser("S"); var me = t.AddUser("M"); var other = t.AddUser("O");
        var c = t.AddNotificationClient("mail", "h:x");
        var at = DateTime.UtcNow.AddDays(-1);
        t.Db.Mails.Add(NewMail(sender.Id, other.Id, "Private", at));
        var n = Req(c, "mail.new", $"Email baru dari {sender.Name}", "Private — x", "/mail", at);
        t.Db.NotificationRequests.Add(n); Deliver(t, n, me.Id);
        t.Db.SaveChanges();

        Assert.Equal(0, await NotificationLinkBackfill.RunAsync(t.Verify(), default));
    }

    [Fact]
    public async Task News_notification_strips_the_tag_and_links_to_the_post()
    {
        using var t = new TestDb();
        var author = t.AddUser("A");
        var c = t.AddNotificationClient("news", "h:x");
        var at = DateTime.UtcNow.AddDays(-2);
        var post = new NewsPost { Id = Guid.NewGuid(), Type = "memo", Title = "Aturan Baru", Body = "b", Status = "published", AuthorId = author.Id, PublishedAt = at.AddSeconds(-1) };
        t.Db.NewsPosts.Add(post);
        var n = Req(c, "memo.mandatory", "[Wajib Baca] Aturan Baru", "b", "/news", at);
        t.Db.NotificationRequests.Add(n); Deliver(t, n, author.Id);
        t.Db.SaveChanges();

        Assert.Equal(1, await NotificationLinkBackfill.RunAsync(t.Verify(), default));
        Assert.Equal($"/news/{post.Id}", t.Verify().NotificationRequests.Single().TargetUrl);
    }

    [Fact]
    public async Task Already_specific_and_unknown_links_are_untouched_and_a_second_run_is_a_noop()
    {
        using var t = new TestDb();
        var u = t.AddUser("U");
        var c = t.AddNotificationClient("x", "h:x");
        var at = DateTime.UtcNow.AddDays(-1);
        var specific = Req(c, "mail.new", "Email baru dari Z", "s — b", "/mail?mailId=" + Guid.NewGuid(), at);
        var unknown = Req(c, "files.transfer", "Done", "b", "/files", at);
        var noMatch = Req(c, "mail.new", "Email baru dari Nobody", "ghost — b", "/mail", at);
        t.Db.NotificationRequests.AddRange(specific, unknown, noMatch);
        foreach (var r in new[] { specific, unknown, noMatch }) Deliver(t, r, u.Id);
        t.Db.SaveChanges();

        Assert.Equal(0, await NotificationLinkBackfill.RunAsync(t.Verify(), default));
        Assert.Equal(0, await NotificationLinkBackfill.RunAsync(t.Verify(), default));
        var urls = t.Verify().NotificationRequests.Select(r => r.TargetUrl).ToList();
        Assert.Contains(specific.TargetUrl, urls);
        Assert.Contains("/files", urls);
        Assert.Contains("/mail", urls);
    }
}
