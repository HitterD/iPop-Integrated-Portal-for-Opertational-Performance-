namespace IPop.Core.Entities;

/// Immutable audit trail for a NewsPost lifecycle (draft/pending/publish/reject/pin/edit/target...).
/// Append-only — never mutated after creation.
public class NewsAudit
{
    public Guid Id { get; set; }
    public Guid NewsPostId { get; set; }
    public Guid ActorId { get; set; }
    public required string Action { get; set; }    // "created" | "submitted" | "approved" | "rejected" | "published" | "unpublished" | "edited" | "pinned" | "unpinned" | "acknowledged" ...
    public string? Detail { get; set; }
    public DateTime CreatedAt { get; set; }

    public NewsPost NewsPost { get; set; } = default!;
    public User Actor { get; set; } = default!;
}
