using Microsoft.AspNetCore.Http;

namespace IPop.Api;

/// Where the API accepts the JWT as ?access_token= instead of the Authorization header: only the SignalR
/// websocket handshake (/hubs), where a browser cannot set headers. Meeting recording files use a
/// recording-scoped ticket instead (POST /api/meeting-rooms/recordings/{id}/ticket), so a session token never
/// ends up in the URL (and the proxy log) of an ordinary API call or a video.
public static class QueryTokenRule
{
    private static readonly PathString Hubs = new("/hubs");

    public static bool Allows(PathString path) => path.StartsWithSegments(Hubs);
}
