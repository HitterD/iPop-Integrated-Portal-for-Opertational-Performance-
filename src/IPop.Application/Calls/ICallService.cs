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
}
