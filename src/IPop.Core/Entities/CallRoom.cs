namespace IPop.Core.Entities;

/// A call session (voice/video, stub wave). Tracks the room through the
/// signaling lifecycle: created when an endpoint invites a participant;
/// the actual media transport (WebRTC) is out of scope this round.
public class CallRoom
{
    public Guid Id { get; set; }

    /// Initiator (caller) — the user who started the call.
    public Guid CreatorId { get; set; }
    public User Creator { get; set; } = default!;

    /// Identifies what is being called: "user" (1:1 P2P), "group" (chat-group call over LiveKit)
    /// or "adhoc" (a 1:1 call upgraded to LiveKit when a person was added; never downgraded).
    public required string Kind { get; set; }

    /// Group calls: the chat group being called (Kind == "group"); null for 1:1 calls.
    /// A group has at most one ringing/active call at a time.
    public Guid? ChatRoomId { get; set; }
    public ChatRoom? ChatRoom { get; set; }

    /// Signaling phase: "ringing" | "active" | "ended" | "declined" | "missed".
    public required string Status { get; set; }

    /// Short-lived signaling token per room (stub — a real gateway issues tokens). Optional at create.
    public string? Token { get; set; }

    public DateTime StartedAt { get; set; }
    public DateTime? EstablishedAt { get; set; }
    public DateTime? EndedAt { get; set; }

    /// Network quality indicator (stub: user-set, "good"|"fair"|"poor").
    public string? Quality { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public ICollection<CallParticipant> Participants { get; set; } = [];
}

/// A member of a call with per-participant signaling state.
public class CallParticipant
{
    public Guid Id { get; set; }
    public Guid CallId { get; set; }
    public CallRoom Call { get; set; } = default!;
    public Guid UserId { get; set; }
    public User User { get; set; } = default!;

    /// "invited" | "accepted" | "declined" | "left" | "muted" (muted = accepted + muted flag).
    public required string Status { get; set; }
    public bool IsMuted { get; set; }
    public bool IsScreenSharing { get; set; }

    /// Join time; while "invited" it is the time the ring started (per-invitee ring timeout).
    public DateTime JoinedAt { get; set; }
    public DateTime? LeftAt { get; set; }

    /// Who added this person to a running call ("Add person"); null for the original invitees.
    /// Soft reference (no FK): only used to word the ring ("Beny invited you…").
    public Guid? InvitedById { get; set; }
}

/// Completed call record for the call log (grouped by date on the UI).
public class CallHistory
{
    public Guid Id { get; set; }
    public Guid CallId { get; set; }
    public CallRoom Call { get; set; } = default!;
    public Guid PerformerId { get; set; }
    public User Performer { get; set; } = default!;

    /// Direction from Performer's view: "outgoing" | "incoming".
    public required string Direction { get; set; }

    /// "completed" | "missed" | "declined" | "canceled" | "no-answer" — how the call ended for the performer.
    public required string Result { get; set; }

    /// Seconds the call was live (from EstablishedAt to EndedAt) — stub computed at end.
    public int DurationSeconds { get; set; }

    public DateTime StartedAt { get; set; }
    public DateTime EndedAt { get; set; }

    /// When the performer saw a missed call on /calls (clears the sidebar badge). Null = unseen.
    public DateTime? SeenAt { get; set; }
}
