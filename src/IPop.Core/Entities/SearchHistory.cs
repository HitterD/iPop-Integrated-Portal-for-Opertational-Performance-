namespace IPop.Core.Entities;

public class SearchHistory
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public required string Query { get; set; }
    public DateTime SearchedAt { get; set; }

    public User User { get; set; } = default!;
}
