namespace IPop.Core.Entities;

public class NewsComment
{
    public Guid Id { get; set; }
    public Guid NewsPostId { get; set; }
    public Guid AuthorId { get; set; }
    public required string Body { get; set; }
    public DateTime CreatedAt { get; set; }
    public NewsPost NewsPost { get; set; } = default!;
    public User Author { get; set; } = default!;
}

public class NewsLike
{
    public Guid NewsPostId { get; set; }
    public Guid UserId { get; set; }
    public DateTime CreatedAt { get; set; }
    public NewsPost NewsPost { get; set; } = default!;
    public User User { get; set; } = default!;
}
