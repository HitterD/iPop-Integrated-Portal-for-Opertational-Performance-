using IPop.Application.News;
using IPop.Infrastructure.News;
using Xunit;

namespace IPop.Tests;

public class NewsComposerAcknowledgementTests
{
    [Fact]
    public async Task Rich_posts_require_the_version_the_reader_saw()
    {
        using var t = new TestDb();
        var hr = t.AddHr("HR");
        var reader = t.AddUser("READER");
        using var db = t.Verify();
        var service = new NewsService(db);
        var created = await service.CreateAsync(hr.Id, new CreateNewsRequest("memo", "Read me", "Body",
            RequiresAcknowledgement: true, Mode: "publish"), default);
        var id = created.News!.Id;
        var post = await db.NewsPosts.FindAsync(id);
        post!.BodyFormat = "html";
        post.ContentVersion = 3;
        await db.SaveChangesAsync();

        Assert.Equal("content_version_required", (await service.AcknowledgeAsync(reader.Id, id, default)).Error);
        Assert.Equal("post_content_conflict", (await service.AcknowledgeAsync(reader.Id, id, default, 2)).Error);
        Assert.Empty(db.NewsAcknowledgements.Where(a => a.NewsPostId == id));
        Assert.True((await service.AcknowledgeAsync(reader.Id, id, default, 3)).Success);
        Assert.Single(db.NewsAcknowledgements.Where(a => a.NewsPostId == id));
    }
}
