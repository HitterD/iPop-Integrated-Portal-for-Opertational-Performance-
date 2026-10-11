using Microsoft.AspNetCore.Http;

namespace IPop.Api;

/// Where the API accepts the JWT as ?access_token= instead of the Authorization header. Only places a browser
/// cannot set headers: the SignalR websocket handshake (/hubs) and a meeting recording file played by
/// &lt;video src&gt; or saved by &lt;a download&gt; (GET /api/meeting-rooms/recordings/{id}/file). Nothing else, so a
/// token never ends up in the URL (and the proxy log) of an ordinary API call.
public static class QueryTokenRule
{
    private static readonly PathString Hubs = new("/hubs");
    private static readonly PathString Recordings = new("/api/meeting-rooms/recordings");

    public static bool Allows(PathString path)
    {
        if (path.StartsWithSegments(Hubs)) return true;
        // Exactly /api/meeting-rooms/recordings/{guid}/file: no empty segment (//), no trailing slash, nothing after.
        if (!path.StartsWithSegments(Recordings, StringComparison.OrdinalIgnoreCase, out var rest)) return false;
        var parts = (rest.Value ?? "").Split('/');
        return parts.Length == 3 && parts[0].Length == 0
            && Guid.TryParseExact(parts[1], "D", out _)
            && parts[2].Equals("file", StringComparison.OrdinalIgnoreCase);
    }
}
