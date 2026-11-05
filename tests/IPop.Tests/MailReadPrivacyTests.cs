using IPop.Application.Mail;
using IPop.Infrastructure.Mail;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace IPop.Tests;

/// Other people only ever see the strict "seen" state of a recipient (SeenAt), never their private unread flag
/// (ReadAt: Mark all read, bulk read).
public class MailReadPrivacyTests
{
    [Fact]
    public async Task Mark_all_read_is_not_shown_to_other_recipients_or_the_sender_only_seen_is()
    {
        using var t = new TestDb();
        var sender = t.AddUser("MRP_S"); var b = t.AddUser("MRP_B"); var c = t.AddUser("MRP_C");
        var sent = await new MailService(t.Verify()).SendAsync(sender.Id,
            new ComposeMailRequest("Budget", "<p>numbers</p>", [b.Nik, c.Nik], [], []), default);
        Assert.True(sent.Success);
        using (var db = t.Verify())
        {
            // B used "Mark all read" without opening it.
            var row = await db.MailRecipients.SingleAsync(r => r.UserId == b.Id && r.RecipientType == "to");
            row.ReadAt = DateTime.UtcNow; row.IsRead = true; row.SeenAt = null;
            await db.SaveChangesAsync();
        }
        var mailId = (await t.Verify().MailRecipients.FirstAsync(r => r.UserId == b.Id)).MailId;

        var asC = await new MailService(t.Verify()).GetAsync(c.Id, mailId, default);
        Assert.Null(Assert.Single(asC!.DeliveryState, d => d.UserId == b.Id).ReadAt);

        var asSender = await new MailService(t.Verify()).GetDeliveryStateAsync(sender.Id, mailId, default);
        Assert.Null(Assert.Single(asSender, d => d.UserId == b.Id).ReadAt);

        using (var db = t.Verify())
        {
            var row = await db.MailRecipients.SingleAsync(r => r.UserId == b.Id && r.RecipientType == "to");
            row.SeenAt = DateTime.UtcNow;   // B actually opened it
            await db.SaveChangesAsync();
        }
        asC = await new MailService(t.Verify()).GetAsync(c.Id, mailId, default);
        Assert.NotNull(Assert.Single(asC!.DeliveryState, d => d.UserId == b.Id).ReadAt);
    }
}
