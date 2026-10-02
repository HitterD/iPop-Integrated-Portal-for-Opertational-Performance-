using IPop.Application.News;
using IPop.Core.Entities;
using IPop.Infrastructure.News;
using Xunit;

namespace IPop.Tests;

public class NewsComposerAttachmentPolicyTests
{
    [Fact]
    public async Task Private_inline_media_cannot_be_read_through_the_shared_endpoint_by_another_manager()
    {
        using var t = new TestDb();
        var author = t.AddHr("AUTHOR");
        var other = t.AddHr("OTHER");
        var file = new Attachment
        {
            Id = Guid.NewGuid(), Area = "news", FileName = "image.png", ContentType = "image/png",
            Sha256 = "hash", StoragePath = "local:image.png", UploaderId = author.Id, CreatedAt = DateTime.UtcNow
        };
        var draft = new NewsComposerDraft
        {
            Id = Guid.NewGuid(), OwnerId = author.Id, State = "active", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        };
        var media = new NewsComposerMedia
        {
            Id = Guid.NewGuid(), OwnerId = author.Id, UploadDraftId = draft.Id, Kind = "image",
            AttachmentId = file.Id, FileName = file.FileName, ContentType = file.ContentType, Sha256 = file.Sha256,
            CreatedAt = DateTime.UtcNow
        };
        t.Db.Attachments.Add(file);
        t.Db.NewsComposerDrafts.Add(draft);
        t.Db.NewsComposerMedia.Add(media);
        t.Db.SaveChanges();
        using var db = t.Verify();
        var policy = new NewsAttachmentPolicy(db, new NewsService(db));

        Assert.True(await policy.CanReadAsync(file.Id, author.Id, default));
        Assert.False(await policy.CanReadAsync(file.Id, other.Id, default));
    }

    [Fact]
    public async Task Inline_media_follows_current_post_audience_and_denies_deactivated_users()
    {
        using var t = new TestDb();
        var author = t.AddHr("AUTHOR");
        var reader = t.AddUser("READER");
        var outsider = t.AddUser("OUTSIDE");
        using var db = t.Verify();
        var service = new NewsService(db);
        var result = await service.CreateAsync(author.Id, new CreateNewsRequest("news", "Private", "Body",
            Mode: "publish", Targets: [new NewsTargetInput("user", reader.Nik)]), default);
        var file = new Attachment
        {
            Id = Guid.NewGuid(), Area = "news", FileName = "image.png", ContentType = "image/png",
            Sha256 = "hash", StoragePath = "local:image.png", UploaderId = author.Id, CreatedAt = DateTime.UtcNow
        };
        var media = new NewsComposerMedia
        {
            Id = Guid.NewGuid(), OwnerId = author.Id, Kind = "image", AttachmentId = file.Id,
            FileName = file.FileName, ContentType = file.ContentType, Sha256 = file.Sha256, CreatedAt = DateTime.UtcNow
        };
        db.Attachments.Add(file);
        db.NewsComposerMedia.Add(media);
        db.NewsPostMedia.Add(new NewsPostMedia { NewsPostId = result.News!.Id, MediaId = media.Id, Role = "inline" });
        await db.SaveChangesAsync();
        var policy = new NewsAttachmentPolicy(db, service);

        Assert.True(await policy.CanReadAsync(file.Id, reader.Id, default));
        Assert.False(await policy.CanReadAsync(file.Id, outsider.Id, default));
        var user = await db.Users.FindAsync(reader.Id);
        user!.IsActive = false;
        await db.SaveChangesAsync();
        Assert.False(await policy.CanReadAsync(file.Id, reader.Id, default));
    }
}
