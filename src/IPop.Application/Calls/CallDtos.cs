namespace IPop.Application.Calls;

/// Create a call (invite). Kind is always "user" in the stub wave.
/// Peer is addressed by NIK (same identity the directory uses) — the service resolves it.
public record CreateCallRequest(string PeerNik, string? Quality = null);

/// Signaling actions between UI + hub.
public record CallSignalRequest(Guid CallId, string Action, bool? IsMuted = null, bool? IsScreenSharing = null);

/// WebRTC peer-to-peer signaling packet (offer, answer, candidate, rejoin) passed over SignalR.
/// From identifies the sending tab, stamped by the server so a callee can stick to one peer:
/// the client's per-tab id (Tab, kept across a refresh) or else its hub connection id.
/// "rejoin" = the sender reloaded or reconnected mid-call and needs a fresh peer connection.
public record WebRtcSignalDto(Guid CallId, string Type, string Payload, string? From = null, string? Tab = null);

/// A participant in a call (with name resolved for the UI).
/// JoinedAt: when they joined — or, while "invited", when their ring started (per-invitee timeout).
/// InvitedById: who added them to a running call ("Add person"); null for the original invitees.
public record CallParticipantDto(
    Guid UserId, string Nik, string Name, string Status,
    bool IsMuted, bool IsScreenSharing, DateTime? LeftAt,
    DateTime? JoinedAt = null, Guid? InvitedById = null);

/// "Add person": invite a directory user (by NIK) into a running call.
public record InviteToCallRequest(string Nik);

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
    string? LiveKitToken = null,
    // Group calls (Kind == "group"): the chat group, its name and avatar for the ring card/banner.
    Guid? ChatRoomId = null,
    string? Title = null,
    string? AvatarColor = null,
    string? AvatarUrl = null,
    // LiveKit room name ("call-{id:N}") when a token was minted.
    string? LiveKitRoom = null);

/// One history row (grouped by date on the UI).
public record CallHistoryDto(
    Guid CallId,
    string Direction,
    string Result,
    int DurationSeconds,
    DateTime StartedAt,
    DateTime EndedAt,
    string PeerName,
    string? PeerNik = null,
    // Group calls: the chat group (PeerName is then the group name and PeerNik is null).
    Guid? ChatRoomId = null,
    // False only for a missed incoming call the user hasn't seen yet (bold + dot on /calls).
    bool Seen = true);

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
    public const string GroupNotFound = "group_not_found";
    public const string NotGroupMember = "not_group_member";
    public const string MediaUnavailable = "media_unavailable";
    public const string AlreadyInCall = "already_in_call";
}

public static class CallKind
{
    /// 1:1 peer-to-peer WebRTC call.
    public const string User = "user";
    /// Chat-group call over LiveKit (ChatRoomId set).
    public const string Group = "group";
    /// Ad-hoc multi-party call over LiveKit, no chat group: a 1:1 call someone added a person to.
    public const string AdHoc = "adhoc";

    /// Media runs through the LiveKit SFU (group and ad-hoc calls), with group-call semantics:
    /// per-member decline/leave, the call ends when the last connected participant leaves.
    public static bool IsLiveKit(string? kind) => kind is Group or AdHoc;
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
