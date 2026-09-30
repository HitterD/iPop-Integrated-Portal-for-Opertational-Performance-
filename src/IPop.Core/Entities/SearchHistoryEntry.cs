namespace IPop.Core.Entities;

/// <summary>A query explicitly submitted to global search by one account.</summary>
public class SearchHistoryEntry
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public required string Query { get; set; }
    public required string NormalizedQuery { get; set; }
    public DateTime LastSearchedAt { get; set; }
    public User User { get; set; } = default!;
}
