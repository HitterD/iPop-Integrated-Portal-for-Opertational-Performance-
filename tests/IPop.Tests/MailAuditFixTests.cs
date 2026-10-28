using IPop.Application.Mail;
using IPop.Core.Entities;
using IPop.Infrastructure.Mail;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace IPop.Tests;

/// Regression tests for the 2026-10-28 mail module audit.
public class MailAuditFixTests
{
    private static ComposeMailRequest Mail(string subject, params string[] to) => new(subject, "<p>body</p>", to, [], []);

    [Fact]
    public async Task Recipient_cannot_move_received_mail_into_draft_or_sent()
    {
        using var t = new TestDb();
        var a = t.AddUser("A");
        var b = t.AddUser("B");
        var svc = new MailService(t.Verify());
        var sent = await svc.SendAsync(a.Id, Mail("Original", b.Nik), default);
        var id = sent.MailId!.Value;

        Assert.Equal("invalid_folder", (await svc.MoveToFolderAsync(b.Id, id, MailFolder.Draft, default)).Error);
        var bulk = await new MailService(t.Verify()).BulkMoveAsync(b.Id, [id], MailFolder.Draft, default);
        Assert.Equal(0, bulk.Affected);

        // Even with a "draft" row forced in, UpdateDraft must not rewrite someone else's mail.
        await t.Verify().MailRecipients.Where(r => r.MailId == id && r.UserId == b.Id)
            .ExecuteUpdateAsync(u => u.SetProperty(r => r.Folder, "draft"));
        var hijack = await new MailService(t.Verify()).UpdateDraftAsync(b.Id, id, Mail("Hacked", a.Nik), default);
        Assert.Equal("draft_not_found", hijack.Error);
        Assert.Equal("Original", (await t.Verify().Mails.SingleAsync(m => m.Id == id)).Subject);
    }

    [Fact]
    public async Task Moving_to_inbox_from_archive_sends_the_authors_copy_back_to_sent()
    {
        using var t = new TestDb();
        var a = t.AddUser("A");
        var b = t.AddUser("B");
        var svc = new MailService(t.Verify());
        var id = (await svc.SendAsync(a.Id, Mail("Mine", b.Nik), default)).MailId!.Value;

        Assert.True((await svc.MoveToFolderAsync(a.Id, id, MailFolder.Archive, default)).Success);
        // The list's undo moves back with "inbox": the author's copy belongs in Sent.
        Assert.True((await new MailService(t.Verify()).BulkMoveAsync(a.Id, [id], MailFolder.Inbox, default)).Affected == 1);
        var row = await t.Verify().MailRecipients.SingleAsync(r => r.MailId == id && r.UserId == a.Id);
        Assert.Equal("sent", row.Folder);
    }

    [Fact]
    public async Task Restore_returns_sent_mail_to_sent_and_received_mail_to_inbox()
    {
        using var t = new TestDb();
        var a = t.AddUser("A");
        var b = t.AddUser("B");
        var svc = new MailService(t.Verify());
        var id = (await svc.SendAsync(a.Id, Mail("Restore me", b.Nik), default)).MailId!.Value;
        await svc.DeleteAsync(a.Id, id, default);
        await new MailService(t.Verify()).DeleteAsync(b.Id, id, default);

        await new MailService(t.Verify()).BulkRestoreAsync(a.Id, [id], default);
        await new MailService(t.Verify()).BulkRestoreAsync(b.Id, [id], default);

        var rows = await t.Verify().MailRecipients.Where(r => r.MailId == id).ToListAsync();
        Assert.Equal("sent", rows.Single(r => r.UserId == a.Id).Folder);
        Assert.Equal("inbox", rows.Single(r => r.UserId == b.Id).Folder);
    }

    [Fact]
    public async Task Scheduled_mail_is_invisible_to_recipients_until_delivered()
    {
        using var t = new TestDb();
        var a = t.AddUser("A");
        var b = t.AddUser("B");
        var svc = new MailService(t.Verify());
        var id = (await svc.SendAsync(a.Id, Mail("Later", b.Nik) with { ScheduledAt = DateTime.UtcNow.AddHours(2) }, default)).MailId!.Value;

        Assert.Null(await new MailService(t.Verify()).GetAsync(b.Id, id, default));
        Assert.Equal("mail_not_found", (await new MailService(t.Verify()).ToggleStarAsync(b.Id, id, default)).Error);
    }

    [Fact]
    public async Task Deleting_a_scheduled_mail_stops_its_delivery()
    {
        using var t = new TestDb();
        var a = t.AddUser("A");
        var b = t.AddUser("B");
        var svc = new MailService(t.Verify());
        var id = (await svc.SendAsync(a.Id, Mail("Oops", b.Nik) with { ScheduledAt = DateTime.UtcNow.AddMinutes(-1) }, default)).MailId!.Value;

        Assert.True((await new MailService(t.Verify()).DeleteAsync(a.Id, id, default)).Success);
        Assert.Equal(0, await new MailService(t.Verify()).DeliverDueScheduledMailsAsync(default));
        Assert.Equal(0, (await new MailService(t.Verify()).GetFolderAsync(b.Id, MailFolder.Inbox, 1, 20, null, default)).TotalCount);
    }

    [Fact]
    public async Task Cancelled_schedule_keeps_its_recipients_as_a_draft()
    {
        using var t = new TestDb();
        var a = t.AddUser("A");
        var b = t.AddUser("B");
        var svc = new MailService(t.Verify());
        var id = (await svc.SendAsync(a.Id, Mail("Edit me", b.Nik) with { ScheduledAt = DateTime.UtcNow.AddHours(1) }, default)).MailId!.Value;

        Assert.True((await new MailService(t.Verify()).CancelScheduleAsync(a.Id, id, default)).Success);
        var draft = await new MailService(t.Verify()).GetAsync(a.Id, id, default);
        Assert.NotNull(draft);
        Assert.Contains(draft!.Recipients, r => r.Nik == b.Nik && r.RecipientType == "to");
    }

    [Fact]
    public async Task Reply_cannot_join_a_thread_the_sender_has_no_part_in()
    {
        using var t = new TestDb();
        var a = t.AddUser("A");
        var b = t.AddUser("B");
        var c = t.AddUser("C");
        var svc = new MailService(t.Verify());
        var secret = (await svc.SendAsync(a.Id, Mail("Board only", b.Nik), default)).MailId!.Value;

        // C forges both the parent and the root of A↔B's conversation.
        var forged = await new MailService(t.Verify()).SendAsync(c.Id,
            Mail("Injected", b.Nik) with { InReplyToId = secret, ThreadRootId = secret }, default);
        Assert.True(forged.Success);
        var m = await t.Verify().Mails.SingleAsync(x => x.Id == forged.MailId);
        Assert.Null(m.ThreadRootId);
        Assert.Null(m.InReplyToId);

        // A real participant's reply is still threaded on the server's own terms.
        var reply = await new MailService(t.Verify()).SendAsync(b.Id,
            Mail("Re: Board only", a.Nik) with { InReplyToId = secret, ThreadRootId = Guid.NewGuid() }, default);
        Assert.Equal(secret, (await t.Verify().Mails.SingleAsync(x => x.Id == reply.MailId)).ThreadRootId);
    }

    [Fact]
    public async Task Unknown_importance_is_normalised()
    {
        using var t = new TestDb();
        var a = t.AddUser("A");
        var b = t.AddUser("B");
        var res = await new MailService(t.Verify()).SendAsync(a.Id, Mail("x", b.Nik) with { Importance = "URGENT-ULTRA-MEGA-HIGH" }, default);
        Assert.True(res.Success);
        Assert.Equal("normal", (await t.Verify().Mails.SingleAsync(m => m.Id == res.MailId)).Importance);
    }

    [Fact]
    public async Task Date_filter_uses_the_users_local_day()
    {
        using var t = new TestDb();
        var a = t.AddUser("A");
        var b = t.AddUser("B");
        var svc = new MailService(t.Verify());
        var id = (await svc.SendAsync(a.Id, Mail("Morning", b.Nik), default)).MailId!.Value;
        // 28 Oct 06:00 in Jakarta (UTC+7) = 27 Oct 23:00Z.
        var sentAt = new DateTime(2026, 10, 27, 23, 0, 0, DateTimeKind.Utc);
        await t.Verify().Mails.Where(m => m.Id == id).ExecuteUpdateAsync(u => u.SetProperty(m => m.SentAt, sentAt));

        var day = new DateTime(2026, 10, 28); // Kind=Unspecified, as model binding produces it
        var hit = await new MailService(t.Verify()).GetFolderAsync(b.Id, MailFolder.Inbox, 1, 20,
            new MailSearchRequest(DateFrom: day, DateTo: day), default);
        Assert.Equal(1, hit.TotalCount);

        var prev = new DateTime(2026, 10, 27);
        var miss = await new MailService(t.Verify()).GetFolderAsync(b.Id, MailFolder.Inbox, 1, 20,
            new MailSearchRequest(DateFrom: prev, DateTo: prev), default);
        Assert.Equal(0, miss.TotalCount);
    }

    [Fact]
    public async Task Search_is_case_insensitive()
    {
        using var t = new TestDb();
        var a = t.AddUser("A");
        var b = t.AddUser("B");
        await new MailService(t.Verify()).SendAsync(a.Id, Mail("Rapat Mingguan", b.Nik), default);
        var res = await new MailService(t.Verify()).GetFolderAsync(b.Id, MailFolder.Inbox, 1, 20, new MailSearchRequest(Query: "RAPAT"), default);
        Assert.Equal(1, res.TotalCount);
    }

    [Fact]
    public async Task Out_of_office_reply_is_stored_as_encoded_html_and_covers_the_last_day()
    {
        using var t = new TestDb();
        var a = t.AddUser("A");
        var b = t.AddUser("B");
        // Until = "today" as the dialog posts it: local midnight as UTC, i.e. earlier than now.
        t.Db.MailAutoReplySettings.Add(new MailAutoReplySetting
        {
            Id = Guid.NewGuid(), UserId = b.Id, IsEnabled = true,
            FromDate = DateTime.UtcNow.AddDays(-3), UntilDate = DateTime.UtcNow.AddHours(-20),
            Message = "<img src=x onerror=alert(1)>\nBack Monday", UpdatedAt = DateTime.UtcNow,
        });
        t.Db.SaveChanges();

        await new MailService(t.Verify()).SendAsync(a.Id, Mail("Hi", b.Nik), default);

        var reply = await t.Verify().Mails.SingleAsync(m => m.SenderId == b.Id && m.Signature == "auto-reply");
        Assert.True(reply.IsHtml);
        Assert.DoesNotContain("<img", reply.Body);
        Assert.Contains("&lt;img", reply.Body);
        Assert.Contains("<br>", reply.Body);
    }

    [Fact]
    public void Plain_text_bodies_are_encoded_on_the_client()
    {
        var html = IPop.Web.Services.MailAddressing.PlainToHtml("<script>x</script>");
        Assert.DoesNotContain("<script>", html);
        Assert.Contains("&lt;script&gt;", html);
    }
}
