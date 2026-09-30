using IPop.Api.Endpoints;
using IPop.Application.Mail;
using IPop.Core.Entities;
using IPop.Infrastructure.Mail;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace IPop.Tests;

/// A composer upload is stored before any mail exists: MailId is null and UploaderId records who
/// may claim it. These pin the insert the upload endpoint does, the link on send/draft, and that
/// nobody but the uploader can reach a pending file.
public sealed class MailAttachmentLinkTests
{
    private static MailAttachment AddPendingUpload(TestDb t, Guid uploaderId, string name = "report.pdf")
    {
        // Same shape the upload endpoint inserts.
        var a = new MailAttachment
        {
            Id = Guid.NewGuid(), MailId = null, UploaderId = uploaderId,
            FileName = name, FileSize = 1234, MimeType = "application/pdf",
            SynologyServer = "local", SynologyPath = $"mail-files/{uploaderId:N}/{Guid.NewGuid():N}_{name}",
            CreatedAt = DateTime.UtcNow,
        };
        t.Db.MailAttachments.Add(a);
        t.Db.SaveChanges(); // failed with a FK violation while MailId was Guid.Empty
        return a;
    }

    [Fact]
    public async Task Pending_upload_is_linked_on_send_and_visible_to_the_recipient()
    {
        using var t = new TestDb();
        var a = t.AddUser("A");
        var b = t.AddUser("B");
        var up = AddPendingUpload(t, a.Id);

        var res = await new MailService(t.Verify()).SendAsync(a.Id,
            new ComposeMailRequest("Report", "<p>see file</p>", [b.Nik], [], [], AttachmentIds: [up.Id]), default);

        Assert.True(res.Success);
        await using var v = t.Verify();
        var row = await v.MailAttachments.SingleAsync(x => x.Id == up.Id);
        Assert.Equal(res.MailId, row.MailId);
        Assert.True((await v.Mails.SingleAsync(m => m.Id == res.MailId)).HasAttachment);

        var detail = await new MailService(t.Verify()).GetAsync(b.Id, res.MailId!.Value, default);
        Assert.Equal("report.pdf", Assert.Single(detail!.Attachments).FileName);
        Assert.False(MailEndpoints.IsPendingUploadOf(row, a.Id)); // linked: no longer an orphan
    }

    [Fact]
    public async Task Someone_elses_pending_upload_cannot_be_linked()
    {
        using var t = new TestDb();
        var a = t.AddUser("A");
        var b = t.AddUser("B");
        var foreign = AddPendingUpload(t, b.Id, "secret.pdf");

        var res = await new MailService(t.Verify()).SendAsync(a.Id,
            new ComposeMailRequest("Hi", "<p>x</p>", [b.Nik], [], [], AttachmentIds: [foreign.Id]), default);

        Assert.True(res.Success);
        await using var v = t.Verify();
        Assert.Null((await v.MailAttachments.SingleAsync(x => x.Id == foreign.Id)).MailId);
        Assert.False((await v.Mails.SingleAsync(m => m.Id == res.MailId)).HasAttachment);
    }

    [Fact]
    public void Pending_upload_belongs_only_to_its_uploader()
    {
        var owner = Guid.NewGuid();
        var other = Guid.NewGuid();
        var pending = new MailAttachment
        {
            MailId = null, UploaderId = owner, FileName = "f", MimeType = "x",
            SynologyServer = "local", SynologyPath = $"mail-files/{owner:N}/f",
        };

        Assert.True(MailEndpoints.IsPendingUploadOf(pending, owner));
        Assert.False(MailEndpoints.IsPendingUploadOf(pending, other));

        // A row without an uploader (only possible before the column existed) is nobody's,
        // even if the path happens to name the caller.
        pending.UploaderId = null;
        Assert.False(MailEndpoints.IsPendingUploadOf(pending, owner));

        pending.UploaderId = owner;
        pending.MailId = Guid.NewGuid();
        Assert.False(MailEndpoints.IsPendingUploadOf(pending, owner));
    }

    [Fact]
    public async Task Autosaved_draft_attachments_move_to_the_sent_mail()
    {
        using var t = new TestDb();
        var a = t.AddUser("A");
        var b = t.AddUser("B");
        var up = AddPendingUpload(t, a.Id);
        var req = new ComposeMailRequest("Draft", "<p>x</p>", [b.Nik], [], [], AttachmentIds: [up.Id]);

        var draft = await new MailService(t.Verify()).SaveDraftAsync(a.Id, req, default);
        await using (var v = t.Verify())
            Assert.Equal(draft.MailId, (await v.MailAttachments.SingleAsync(x => x.Id == up.Id)).MailId);

        var sent = await new MailService(t.Verify()).SendAsync(a.Id, req, default);
        await using (var v = t.Verify())
            Assert.Equal(sent.MailId, (await v.MailAttachments.SingleAsync(x => x.Id == up.Id)).MailId);
    }

    [Fact]
    public async Task Removing_a_file_from_a_draft_returns_it_to_pending()
    {
        using var t = new TestDb();
        var a = t.AddUser("A");
        var keep = AddPendingUpload(t, a.Id, "keep.pdf");
        var drop = AddPendingUpload(t, a.Id, "drop.pdf");

        var draft = await new MailService(t.Verify()).SaveDraftAsync(a.Id,
            new ComposeMailRequest("Draft", "<p>x</p>", [], [], [], AttachmentIds: [keep.Id, drop.Id]), default);
        var upd = await new MailService(t.Verify()).UpdateDraftAsync(a.Id, draft.MailId!.Value,
            new ComposeMailRequest("Draft", "<p>x</p>", [], [], [], AttachmentIds: [keep.Id]), default);

        Assert.True(upd.Success);
        await using var v = t.Verify();
        Assert.Equal(draft.MailId, (await v.MailAttachments.SingleAsync(x => x.Id == keep.Id)).MailId);
        var dropped = await v.MailAttachments.SingleAsync(x => x.Id == drop.Id);
        Assert.Null(dropped.MailId);
        Assert.True(MailEndpoints.IsPendingUploadOf(dropped, a.Id));
    }
}
