using IPop.Core.Entities;
using IPop.Infrastructure.Mail;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IPop.Tests;

/// Never-sent mail uploads go after 7 days with their file - unless another row (a forward) still shows that file.
public class MailAttachmentGcTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ipop-mailgc-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private MailAttachment Local(TestDb t, Guid? mailId, string file, DateTime created)
    {
        var rel = $"mail-files/u/{file}";
        Directory.CreateDirectory(Path.Combine(_root, "mail-files", "u"));
        if (!File.Exists(Path.Combine(_root, rel))) File.WriteAllText(Path.Combine(_root, rel), "x");
        var a = new MailAttachment
        {
            Id = Guid.NewGuid(), MailId = mailId, FileName = file, FileSize = 1, MimeType = "text/plain",
            SynologyServer = "local", SynologyPath = rel, CreatedAt = created,
        };
        t.Db.MailAttachments.Add(a);
        return a;
    }

    [Fact]
    public async Task Old_unsent_uploads_go_with_their_file_but_a_file_a_forward_still_uses_stays()
    {
        using var t = new TestDb();
        var now = DateTime.UtcNow;
        var sender = t.AddUser("MG1");
        var mail = new Mail { Id = Guid.NewGuid(), SenderId = sender.Id, Subject = "s", Body = "b", Status = "sent", CreatedAt = now, UpdatedAt = now };
        t.Db.Mails.Add(mail);
        var old = Local(t, null, "old.pdf", now.AddDays(-8));
        var fresh = Local(t, null, "fresh.pdf", now.AddDays(-1));
        var orphanShared = Local(t, null, "shared.pdf", now.AddDays(-9));
        Local(t, mail.Id, "shared.pdf", now.AddDays(-9));      // a sent mail (forward) still points at shared.pdf
        await t.Db.SaveChangesAsync();

        var n = await MailAttachmentGcWorker.RunOnceAsync(t.Verify(), null, _root, now, NullLogger.Instance, default);

        Assert.Equal(2, n);
        var left = await t.Verify().MailAttachments.Select(a => a.Id).ToListAsync();
        Assert.DoesNotContain(old.Id, left); Assert.DoesNotContain(orphanShared.Id, left); Assert.Contains(fresh.Id, left);
        Assert.False(File.Exists(Path.Combine(_root, old.SynologyPath)));
        Assert.True(File.Exists(Path.Combine(_root, orphanShared.SynologyPath)));   // still shown by the sent mail
    }
}
