namespace IPop.Core.Entities;

/// Per-user, per-module tutorial progress: whether the coach-mark tour was finished or the
/// first-visit invite dismissed. Absent row = never seen, so the invite shows.
public class UserTutorialState
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    /// Module key as used by the web tour registry ("chat", "projects", …).
    public required string ModuleKey { get; set; }
    /// "done" (tour completed or skipped) or "dismissed" (invite closed without a tour).
    public required string Status { get; set; }
    public DateTime UpdatedAt { get; set; }

    public User User { get; set; } = default!;
}
