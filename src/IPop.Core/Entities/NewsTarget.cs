namespace IPop.Core.Entities;

/// Targeting row for a NewsPost. One post may target multiple departments, roles,
/// individual users, or "all". Each row records one target dimension. An empty
/// Targets collection means "all" (back-compat with un-targeted posts).
public class NewsTarget
{
    public Guid Id { get; set; }
    public Guid NewsPostId { get; set; }

    /// "all" | "department" | "role" | "user"
    public required string Kind { get; set; }

    /// For kind=department -> Department code; for kind=role -> Role name; for kind=user -> NIK; for "all" -> null.
    public string? Value { get; set; }

    public NewsPost NewsPost { get; set; } = default!;
}
