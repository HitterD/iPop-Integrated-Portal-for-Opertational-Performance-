using IPop.Api.Endpoints;
using IPop.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace IPop.Tests;

public class NewsDiscussionTests
{
    [Fact]
    public async Task Discussion_ListsCommentsAndCountsLikesPerPost()
    {
        using var t = new TestDb();
        var author = t.AddUser("DISC_AUTHOR");
        var reader = t.AddUser("DISC_READER");
        var post = new NewsPost
        {
            Id = Guid.NewGuid(), Type = "news", Title = "Update", Body = "Details", Status = "published",
            AuthorId = author.Id, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        var other = new NewsPost
        {
            Id = Guid.NewGuid(), Type = "news", Title = "Other", Body = "Other details", Status = "published",
            AuthorId = author.Id, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        t.Db.NewsPosts.AddRange(post, other);
        t.Db.NewsComments.Add(new NewsComment
        {
            Id = Guid.NewGuid(), NewsPostId = post.Id, AuthorId = reader.Id,
            Body = "Thanks for the update", CreatedAt = DateTime.UtcNow,
        });
        t.Db.NewsLikes.AddRange(
            new NewsLike { NewsPostId = post.Id, UserId = reader.Id, CreatedAt = DateTime.UtcNow },
            new NewsLike { NewsPostId = post.Id, UserId = author.Id, CreatedAt = DateTime.UtcNow },
            new NewsLike { NewsPostId = other.Id, UserId = reader.Id, CreatedAt = DateTime.UtcNow });
        await t.Db.SaveChangesAsync();

        await using var db = t.Verify();
        var discussion = await NewsEndpoints.GetDiscussionAsync(db, post.Id, reader.Id, default);
        Assert.Equal(2, discussion.LikeCount);
        Assert.True(discussion.IsLiked);
        var comment = Assert.Single(discussion.Comments);
        Assert.Equal(reader.Name, comment.AuthorName);
        Assert.Equal("Thanks for the update", comment.Body);
        Assert.Equal(1, await db.NewsComments.CountAsync());
    }
}
