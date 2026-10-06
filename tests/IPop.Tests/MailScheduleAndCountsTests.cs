using IPop.Application.Mail;
using IPop.Core.Entities;
using IPop.Infrastructure.Badges;
using IPop.Infrastructure.Mail;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace IPop.Tests;

/// Scheduled mail behaves like an immediate send, Cancel / Send now / the scheduler can't both win,
/// and the Mail badge and the inbox "unread" agree about muted threads.
public class MailScheduleAndCountsTests
{
    private static ComposeMailRequest Scheduled(string to) =>
        new("Plan", "<p>x</p>", [to], [], [], ScheduledAt: DateTime.UtcNow.AddHours(1));

    private static async Task MakeDueAsync(TestDb t, Guid mailId) =>
        await t.Db.Mails.Where(m => m.Id == mailId).ExecuteUpdateAsync(u => u.SetProperty(m => m.ScheduledAt, DateTime.UtcNow.AddMinutes(-1)));

    [Fact]
    public async Task A_scheduled_mail_runs_the_recipients_rules_and_out_of_office_when_it_goes_out()
    {
        using var t = new TestDb();
        var sender = t.AddUser("SC_S"); var r = t.AddUser("SC_R");
        var label = new MailLabel { Id = Guid.NewGuid(), UserId = r.Id, Name = "Boss", Color = "#000", CreatedAt = DateTime.UtcNow };
        t.Db.MailLabels.Add(label);
        t.Db.MailRules.Add(new MailRule { Id = Guid.NewGuid(), UserId = r.Id, Name = "boss", SenderContains = sender.Nik, LabelId = label.Id, IsEnabled = true, CreatedAt = DateTime.UtcNow });
        t.Db.MailAutoReplySettings.Add(new MailAutoReplySetting { Id = Guid.NewGuid(), UserId = r.Id, IsEnabled = true, Message = "Away", UpdatedAt = DateTime.UtcNow });
        t.Db.SaveChanges();
        var sent = await new MailService(t.Verify()).SendAsync(sender.Id, Scheduled(r.Nik), default);
        await MakeDueAsync(t, sent.MailId!.Value);

        Assert.Equal(1, await new MailService(t.Verify()).DeliverDueScheduledMailsAsync(default));

        await using var v = t.Verify();
        Assert.True(await v.MailLabelAssignments.AnyAsync(a => a.LabelId == label.Id && a.MailId == sent.MailId));
        Assert.True(await v.Mails.AnyAsync(m => m.Signature == "auto-reply" && m.SenderId == r.Id));
    }

    [Fact]
    public async Task Cancel_after_the_scheduler_sent_it_says_already_sent_and_changes_nothing()
    {
        using var t = new TestDb();
        var sender = t.AddUser("RC_S"); var r = t.AddUser("RC_R");
        var sent = await new MailService(t.Verify()).SendAsync(sender.Id, Scheduled(r.Nik), default);
        var stale = new MailService(t.Verify());              // the Cancel request, already loaded
        await MakeDueAsync(t, sent.MailId!.Value);
        await new MailService(t.Verify()).DeliverDueScheduledMailsAsync(default);

        var cancel = await stale.CancelScheduleAsync(sender.Id, sent.MailId.Value, default);

        Assert.False(cancel.Success);
        await using var v = t.Verify();
        Assert.Equal("sent", (await v.Mails.SingleAsync(m => m.Id == sent.MailId)).Status);
        Assert.True(await v.MailRecipients.AnyAsync(x => x.MailId == sent.MailId && x.UserId == r.Id && x.Folder == "inbox"));
    }

    [Fact]
    public async Task The_scheduler_skips_a_mail_that_Send_now_already_sent()
    {
        using var t = new TestDb();
        var sender = t.AddUser("SN_S"); var r = t.AddUser("SN_R");
        var sent = await new MailService(t.Verify()).SendAsync(sender.Id, Scheduled(r.Nik), default);
        await MakeDueAsync(t, sent.MailId!.Value);
        Assert.True((await new MailService(t.Verify()).SendScheduledImmediatelyAsync(sender.Id, sent.MailId.Value, default)).Success);

        Assert.Equal(0, await new MailService(t.Verify()).DeliverDueScheduledMailsAsync(default));
        await using var v = t.Verify();
        Assert.Equal(1, await v.MailNotifyJobs.CountAsync(j => j.MailId == sent.MailId));
    }

    [Fact]
    public async Task Muted_threads_count_as_read_in_the_badge_and_the_inbox_alike()
    {
        using var t = new TestDb();
        var sender = t.AddUser("MU_S"); var r = t.AddUser("MU_R");
        var svc = new MailService(t.Verify());
        var a = await svc.SendAsync(sender.Id, new ComposeMailRequest("One", "<p>x</p>", [r.Nik], [], []), default);
        await new MailService(t.Verify()).SendAsync(sender.Id, new ComposeMailRequest("Two", "<p>x</p>", [r.Nik], [], []), default);
        await t.Db.MailRecipients.Where(x => x.MailId == a.MailId && x.UserId == r.Id).ExecuteUpdateAsync(u => u.SetProperty(x => x.IsMuted, true));

        var counts = await new MailService(t.Verify()).GetFolderCountsAsync(r.Id, default);
        var badge = await new MailBadgeProvider(t.Verify()).CountAsync(r.Id, default);

        Assert.Equal(1, badge);
        Assert.Equal(badge, counts.InboxUnread);
    }
}
