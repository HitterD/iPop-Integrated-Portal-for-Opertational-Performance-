namespace IPop.Core.Entities;

public class User
{
    public Guid Id { get; set; }
    public required string Nik { get; set; }         // unique — HRIS employee number
    public required string Name { get; set; }
    public required string Email { get; set; }

    /// Null until the user changes away from the default password ("1234").
    /// Null + MustChangePassword is the whole default-password mechanism (spec §3).
    public string? PasswordHash { get; set; }
    public bool MustChangePassword { get; set; } = true;

    public string? Position { get; set; }
    public Guid? DepartmentId { get; set; }
    public Guid? SiteId { get; set; }            // admin-assigned; HRIS never touches it
    public string? ManagerNik { get; set; }
    public string? Extension { get; set; }
    public string? Location { get; set; }
    public string? PhotoPath { get; set; }

    public bool IsActive { get; set; } = true;
    public int FailedLoginCount { get; set; }
    public DateTime? LockedUntil { get; set; }

    /// Set by an admin edit; HRIS sync skips profile overwrites on this row so a manual
    /// correction is not reverted by the next sync. Activation still follows the feed.
    public bool IsManuallyEdited { get; set; }

    // Presence stores raw facts only; the effective status is computed on read.
    public DateTime? PresenceLastSeenAt { get; set; }
    /// Null = automatic; "busy" = user-set override. Other values are rejected at the API boundary.
    public string? PresenceManualStatus { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public Department? Department { get; set; }
    public Site? Site { get; set; }
    public ICollection<AuthSession> Sessions { get; set; } = [];
    public ICollection<UserRole> UserRoles { get; set; } = [];
}
