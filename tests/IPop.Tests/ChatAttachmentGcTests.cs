using IPop.Core.Entities;
using IPop.Infrastructure.Chat;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IPop.Tests;

/// The GC removes files nobody can reach (never-sent uploads after 24 h, deleted messages' files after 30 days),
/// deleting the file before the row, and leaves everything else alone.
public class ChatAttachmentGcTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ipop-gc-" + Guid.NewGuid().ToString("N"));

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private ChatAttachment Local(TestDb t, Guid? messageId, string name, DateTime created)
    {
        var rel = $"chat-files/room/{name}";
        Directory.CreateDirectory(Path.Combine(_root, "chat-files", "room"));
        File.WriteAllText(Path.Combine(_root, rel), "x");
        var a = new ChatAttachment
        {
            Id = Guid.NewGuid(), MessageId = messageId, FileName = name, FileSize = 1, MimeType = "text/plain",
            SynologyServer = "local", SynologyPath = rel, CreatedAt = created,
        };
        t.Db.ChatAttachments.Add(a);
        return a;
    }

    [Fact]
    public async Task Removes_unsent_uploads_and_old_deleted_messages_files_with_their_files()
    {
        using var t = new TestDb();
        var u = t.AddUser("GC1");
        var room = t.AddRoom("group", "R");
        t.AddMember(room.Id, u.Id);
        var now = DateTime.UtcNow;
        var live = t.AddMessage(room.Id, u.Id, "live");
        var oldDeleted = t.AddMessage(room.Id, u.Id, "old");
        oldDeleted.IsDeleted = true; oldDeleted.DeletedAt = now.AddDays(-31);
        var newDeleted = t.AddMessage(room.Id, u.Id, "new");
        newDeleted.IsDeleted = true; newDeleted.DeletedAt = now.AddDays(-5);
        var aLive = Local(t, live.Id, "live.txt", now.AddDays(-90));
        var aOld = Local(t, oldDeleted.Id, "old.txt", now.AddDays(-40));
        var aNew = Local(t, newDeleted.Id, "new.txt", now.AddDays(-6));
        var aOrphan = Local(t, null, "orphan.txt", now.AddHours(-30));
        var aFresh = Local(t, null, "fresh.txt", now.AddHours(-2));
        await t.Db.SaveChangesAsync();

        var removed = await ChatAttachmentGcWorker.RunOnceAsync(t.Verify(), null, _root, now, NullLogger.Instance, default);

        Assert.Equal(2, removed);
        var left = await t.Verify().ChatAttachments.Select(a => a.FileName).OrderBy(x => x).ToListAsync();
        Assert.Equal(["fresh.txt", "live.txt", "new.txt"], left);
        Assert.False(File.Exists(Path.Combine(_root, aOld.SynologyPath)));
        Assert.False(File.Exists(Path.Combine(_root, aOrphan.SynologyPath)));
        Assert.True(File.Exists(Path.Combine(_root, aLive.SynologyPath)));
        Assert.True(File.Exists(Path.Combine(_root, aNew.SynologyPath)));
    }

    [Fact]
    public async Task Deleted_source_keeps_the_forwarded_file_and_thumbnail_until_the_last_reference_expires()
    {
        using var t = new TestDb();
        var user = t.AddUser("GC_FORWARD");
        var room = t.AddRoom("group", "Forwarded files");
        t.AddMember(room.Id, user.Id);
        var now = DateTime.UtcNow;
        var source = t.AddMessage(room.Id, user.Id, "original");
        source.IsDeleted = true;
        source.DeletedAt = now.AddDays(-31);
        var forwarded = t.AddMessage(room.Id, user.Id, "forward");
        var originalAttachment = Local(t, source.Id, "shared.jpg", now.AddDays(-40));
        var forwardedAttachment = Local(t, forwarded.Id, "shared.jpg", now.AddDays(-35));
        var thumb = "chat-files/room/shared.thumb.jpg";
        File.WriteAllText(Path.Combine(_root, thumb), "thumbnail");
        originalAttachment.ThumbnailPath = thumb;
        forwardedAttachment.ThumbnailPath = thumb;
        await t.Db.SaveChangesAsync();

        Assert.Equal(1, await ChatAttachmentGcWorker.RunOnceAsync(t.Verify(), null, _root, now, NullLogger.Instance, default));

        Assert.True(File.Exists(Path.Combine(_root, forwardedAttachment.SynologyPath)), "A live forward still references the original file.");
        Assert.True(File.Exists(Path.Combine(_root, thumb)), "A live forward still references the thumbnail.");
        using (var remaining = t.Verify())
        {
            Assert.False(await remaining.ChatAttachments.AnyAsync(a => a.Id == originalAttachment.Id));
            Assert.True(await remaining.ChatAttachments.AnyAsync(a => a.Id == forwardedAttachment.Id));
            var lastMessage = await remaining.ChatMessages.SingleAsync(m => m.Id == forwarded.Id);
            lastMessage.IsDeleted = true;
            lastMessage.DeletedAt = now.AddDays(-31);
            await remaining.SaveChangesAsync();
        }

        Assert.Equal(1, await ChatAttachmentGcWorker.RunOnceAsync(t.Verify(), null, _root, now, NullLogger.Instance, default));
        Assert.False(File.Exists(Path.Combine(_root, forwardedAttachment.SynologyPath)));
        Assert.False(File.Exists(Path.Combine(_root, thumb)));
    }

    [Fact]
    public async Task Local_cleanup_never_deletes_a_sibling_directory_with_the_same_prefix()
    {
        using var t = new TestDb();
        var sibling = _root + "-outside";
        Directory.CreateDirectory(sibling);
        var outsideFile = Path.Combine(sibling, "must-stay.txt");
        File.WriteAllText(outsideFile, "not an application attachment");
        try
        {
            t.Db.ChatAttachments.Add(new ChatAttachment
            {
                Id = Guid.NewGuid(), FileName = "must-stay.txt", FileSize = 1, MimeType = "text/plain",
                SynologyServer = "local", SynologyPath = outsideFile, CreatedAt = DateTime.UtcNow.AddDays(-3),
            });
            await t.Db.SaveChangesAsync();

            await ChatAttachmentGcWorker.RunOnceAsync(t.Verify(), null, _root, DateTime.UtcNow, NullLogger.Instance, default);

            Assert.True(File.Exists(outsideFile), "A directory prefix is not a directory boundary.");
        }
        finally { Directory.Delete(sibling, true); }
    }

    [Fact]
    public async Task Nas_files_without_a_service_account_are_kept_for_a_later_pass()
    {
        using var t = new TestDb();
        t.Db.ChatAttachments.Add(new ChatAttachment
        {
            Id = Guid.NewGuid(), MessageId = null, FileName = "nas.txt", FileSize = 1, MimeType = "text/plain",
            SynologyServer = "ict", SynologyPath = "/home/chat/room/nas.txt", CreatedAt = DateTime.UtcNow.AddDays(-3),
        });
        await t.Db.SaveChangesAsync();

        Assert.Equal(0, await ChatAttachmentGcWorker.RunOnceAsync(t.Verify(), null, _root, DateTime.UtcNow, NullLogger.Instance, default));
        Assert.Equal(1, await t.Verify().ChatAttachments.CountAsync());
    }
}
