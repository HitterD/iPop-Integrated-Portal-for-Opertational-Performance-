namespace IPop.Application.Calls;

/// Create a call (invite). Kind is always "user" in the stub wave.
/// Peer is addressed by NIK (same identity the directory uses) — the service resolves it.
public record CreateCallRequest(string PeerNik, string? Quality = null);

/// Signaling actions between UI + hub.
public record CallSignalRequest(Guid CallId, string Action, bool? IsMuted = null, bool? IsScreenSharing = null);

/// WebRTC peer-to-peer signaling packet (offer, answer, candidate) passed over SignalR.
public record WebRtcSignalDto(Guid CallId, string Type, string Payload);

/// A participant in a call (with name resolved for the UI).
public record CallParticipantDto(
    Guid UserId, string Nik, string Name, string Status,
    bool IsMuted, bool IsScreenSharing, DateTime? LeftAt);

/// Room state returned by invite/accept/decline/leave + hub broadcasts.
public record CallRoomDto(
    Guid Id,
    string Kind,
    string Status,
    string? Token,
    Guid CreatorId,
    DateTime StartedAt,
    DateTime? EstablishedAt,
    DateTime? EndedAt,
    string? Quality,
    IReadOnlyList<CallParticipantDto> Participants,
    string? LiveKitUrl = null,
    string? LiveKitToken = null);

/// One history row (grouped by date on the UI).
public record CallHistoryDto(
    Guid CallId,
    string Direction,
    string Result,
    int DurationSeconds,
    DateTime StartedAt,
    DateTime EndedAt,
    string PeerName,
    string? PeerNik = null);

public record CallHistoryPage(IReadOnlyList<CallHistoryDto> Items, int Total, int Page, int PageSize);

public record CallResultDto(bool Success, string? Error, string? ErrorCode, CallRoomDto? Room);

/// Shared error codes + status constants kept in one place (no free strings).
public static class CallErrors
{
    public const string PeerNotFound = "peer_not_found";
    public const string RoomNotFound = "room_not_found";
    public const string NotParticipant = "not_participant";
    public const string InvalidSignal = "invalid_signal";
    public const string CallAlreadyEnded = "call_already_ended";
}

public static class CallStatus
{
    public const string Ringing = "ringing";
    public const string Active = "active";
    public const string Ended = "ended";
    public const string Declined = "declined";
    public const string Missed = "missed";
}

public static class CallParticipantStatus
{
    public const string Invited = "invited";
    public const string Accepted = "accepted";
    public const string Declined = "declined";
    public const string Left = "left";
}
