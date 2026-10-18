namespace IPop.Core.Entities;

/// One remote-help session from the iPop Notifier (Admin/ICT → a user's PC). The video and input run peer to peer
/// through LiveKit; this row is the consent record and the log (kept a year): who, which PC, when, how long, files.
public class RemoteSession
{
    public Guid Id { get; set; }
    public Guid HelperId { get; set; }
    public Guid TargetUserId { get; set; }
    public Guid DeviceId { get; set; }
    /// LiveKit room: a second ICT joining a running session (view only) shares the first one's room.
    public required string Room { get; set; }
    /// "control" | "view"
    public string Mode { get; set; } = "control";
    /// "asking" | "declined" | "active" | "ended" | "expired"
    public string Status { get; set; } = "asking";
    /// The PC is marked unattended by Admin: no prompt was shown.
    public bool Unattended { get; set; }
    public DateTime RequestedAt { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? EndedAt { get; set; }
    /// "helper" | "user" | "timeout" | "error"
    public string? EndedBy { get; set; }
    /// File names sent either way, newline separated (names only, no content).
    public string? Files { get; set; }

    public User Helper { get; set; } = default!;
    public User TargetUser { get; set; } = default!;
    public NotifierDevice Device { get; set; } = default!;
}

/// A picture or video an ICT/Admin keeps to show on the user's screen during remote help ("My media").
public class RemoteMedia
{
    public Guid Id { get; set; }
    public Guid OwnerId { get; set; }
    public Guid AttachmentId { get; set; }
    public required string Name { get; set; }
    public required string ContentType { get; set; }
    public long Size { get; set; }
    public DateTime CreatedAt { get; set; }

    public User Owner { get; set; } = default!;
}

/// Admin switched remote help off for one ICT/Admin person (the Devices list itself stays visible).
public class RemoteHelperBlock
{
    public Guid UserId { get; set; }
    public Guid SetById { get; set; }
    public DateTime CreatedAt { get; set; }

    public User User { get; set; } = default!;
}
