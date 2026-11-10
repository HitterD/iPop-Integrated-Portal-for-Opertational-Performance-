namespace IPop.Application.Calls;

public interface ILiveKitTokenService
{
    /// sources limits what the holder may publish (LiveKit source names: "microphone", "camera", "screen_share",
    /// "screen_share_audio"); null = anything. canUpdateOwnMetadata lets the client set its own attributes.
    string GenerateToken(string roomName, string identity, string name, TimeSpan? ttl = null,
        IReadOnlyList<string>? sources = null, bool canUpdateOwnMetadata = false);
    /// Browser-facing LiveKit WebSocket URL. Never the in-cluster service name: a browser cannot resolve it.
    string? PublicUrl { get; }
    /// Kept for callers that need "the LiveKit URL in use"; may be the in-cluster address.
    string? ServerUrl { get; }
    /// Server-to-server URL override, falling back to the browser URL.
    string? ServerApiUrl { get; }
    /// Short-lived server token for RoomService/Egress (roomList always; roomAdmin+roomCreate for one room; roomRecord when record).
    string GenerateAdminToken(string? room, TimeSpan? ttl = null, bool record = false);
    bool IsConfigured { get; }
}
