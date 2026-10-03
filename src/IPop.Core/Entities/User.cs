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

    /// Chart position title, written on org chart Publish (null = not in the chart).
    public string? Position { get; set; }
    /// What HRIS said at insert time. Kept because Publish overwrites Position/ManagerNik.
    public string? HrisPosition { get; set; }
    public string? HrisManagerNik { get; set; }
    public Guid? DepartmentId { get; set; }
    public Guid? SiteId { get; set; }            // admin-assigned; HRIS never touches it
    public Guid? DivisionId { get; set; }        // admin-assigned; syncs Site/Department from the Division's parents
    /// First holder of the nearest filled parent position in the org chart, written on Publish.
    public string? ManagerNik { get; set; }
    public string? Extension { get; set; }
    public string? Location { get; set; }
    /// Id of the current UserPhoto row, or null for the initials avatar.
    public string? PhotoPath { get; set; }

    public bool IsActive { get; set; } = true;
    public int FailedLoginCount { get; set; }
    public DateTime? LockedUntil { get; set; }

    /// Zona waktu pengguna (mis. "SE Asia Standard Time" untuk WIB). Null = gunakan default server.
    public string? TimeZoneId { get; set; }

    /// Set by an admin edit, for the admin list's "edited" filter. HRIS sync never overwrites
    /// existing rows either way.
    public bool IsManuallyEdited { get; set; }

    // Presence stores raw facts only; the effective status is computed on read.
    public DateTime? PresenceLastSeenAt { get; set; }
    /// The real time of the last heartbeat, for "last seen". PresenceLastSeenAt is aged on
    /// disconnect/sign-out to flip the status to off, so it is not a faithful clock.
    public DateTime? PresenceLastBeatAt { get; set; }
    /// Null = automatic; "busy" = user-set override. Other values are rejected at the API boundary.
    public string? PresenceManualStatus { get; set; }
    /// When a timed Busy ends (UTC); null with Busy set = until the user turns it off.
    public DateTime? PresenceBusyUntilUtc { get; set; }
    /// Reported by the client with each heartbeat: computer idle ≥ 10 min or screen locked.
    public bool PresenceIsIdle { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public Department? Department { get; set; }
    public Site? Site { get; set; }
    public Division? Division { get; set; }
    public ICollection<AuthSession> Sessions { get; set; } = [];
    public ICollection<UserRole> UserRoles { get; set; } = [];
}
