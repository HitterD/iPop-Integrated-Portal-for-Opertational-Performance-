namespace IPop.Application.Calls;

/// Signaling/stub call service. The transport is mocked this wave — this service owns
/// the state machine (invite → ringing → active/declined/missed → ended) and the
/// per-participant flags (mute, screen share) as "stub" data, recorded in the history.
public interface ICallService
{
    /// Create a call + invite the peer. Returns the ringing room (or error).
    Task<CallResultDto> CreateCallAsync(Guid callerId, CreateCallRequest req, CancellationToken ct);

    /// Accept / decline / leave / end — the hub and REST call this same state machine.
    /// Action: "accept" | "decline" | "leave" | "end".
    Task<CallResultDto> SignalAsync(Guid userId, CallSignalRequest req, CancellationToken ct);

    /// Live room state (for the active call bar / participant grid).
    Task<CallRoomDto?> GetRoomAsync(Guid callId, Guid userId, CancellationToken ct);

    /// The user's ringing call (incoming ringtone) — shows the incoming card.
    Task<CallRoomDto?> GetRingingCallAsync(Guid userId, CancellationToken ct);

    /// The live call the user is in (answered, or their own outgoing ring), so a reloaded or
    /// reconnected tab can take it up again. Null when none.
    Task<CallRoomDto?> GetActiveCallAsync(Guid userId, CancellationToken ct);

    /// Call history grouped by date on the UI. Includes both sides (outgoing + incoming).
    Task<CallHistoryPage> GetHistoryAsync(Guid userId, int page, int pageSize, CancellationToken ct);

    /// Marks the user's unseen missed calls as seen (all, or just one call). Returns the new badge count.
    Task<int> MarkHistorySeenAsync(Guid userId, Guid? callId, CancellationToken ct);

    /// Rings started before cutoff become "missed" (no-answer for the caller).
    /// Returns the closed rooms so the caller can broadcast their final state.
    Task<IReadOnlyList<CallRoomDto>> ExpireRingingAsync(DateTime cutoff, CancellationToken ct);

    /// Group call for a chat group: creates it (ringing every other member) or, when the group
    /// already has a ringing/active call, joins the caller into it. Caller must be a member.
    Task<CallResultDto> StartGroupCallAsync(Guid callerId, Guid chatRoomId, CancellationToken ct);

    /// Join a group call (accept the ring, or late join from the chat banner). Marks the
    /// participant accepted; a current group member who was not invited is added.
    Task<CallResultDto> JoinGroupCallAsync(Guid userId, Guid callId, CancellationToken ct);

    /// The chat group's ringing/active call, or null. Only group members may see it.
    Task<CallResultDto> GetActiveGroupCallAsync(Guid userId, Guid chatRoomId, CancellationToken ct);

    /// "Add person": an accepted participant invites a directory user into a running call (or re-rings
    /// someone who left/declined/missed). A 1:1 call is converted to an ad-hoc LiveKit call
    /// (Kind "adhoc", never back to P2P); the participants then fetch LiveKit tokens via join.
    Task<CallResultDto> InviteAsync(Guid inviterId, Guid callId, string nik, CancellationToken ct);

    /// The user's last connection dropped: end their active calls and cancel rings they started.
    /// unlessSeenAfter: skip calls whose media tab beat since then (alive on another API replica).
    Task<IReadOnlyList<CallRoomDto>> EndForUserAsync(Guid userId, CancellationToken ct, DateTime? unlessSeenAfter = null);

    /// The tab holding my media in this call is alive (every ~20 s), with its measured link quality.
    /// False when I am not (or no longer) in a live call with that id.
    Task<bool> HeartbeatAsync(Guid userId, Guid callId, string? quality, CancellationToken ct);

    /// Takes out of live calls everyone whose media tab stopped beating before cutoff; a 1:1 call
    /// ends, a group call ends when nobody is left. Returns the changed rooms for broadcasting.
    Task<IReadOnlyList<CallRoomDto>> ReapStaleAsync(DateTime cutoff, CancellationToken ct);
}
