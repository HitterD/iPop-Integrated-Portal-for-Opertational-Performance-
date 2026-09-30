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

    /// Call history grouped by date on the UI. Includes both sides (outgoing + incoming).
    Task<CallHistoryPage> GetHistoryAsync(Guid userId, int page, int pageSize, CancellationToken ct);

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

    /// The user's last connection dropped: end their active calls and cancel rings they started.
    Task<IReadOnlyList<CallRoomDto>> EndForUserAsync(Guid userId, CancellationToken ct);
}
