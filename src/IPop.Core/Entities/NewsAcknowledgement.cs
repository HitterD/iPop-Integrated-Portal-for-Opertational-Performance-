namespace IPop.Core.Entities;

/// Mandatory read acknowledgement. One row per (news, user) once the reader has acknowledged.
public class NewsAcknowledgement
{
    public Guid NewsPostId { get; set; }
    public Guid UserId { get; set; }
    public DateTime AcknowledgedAt { get; set; }

    public NewsPost NewsPost { get; set; } = default!;
    public User User { get; set; } = default!;
}
