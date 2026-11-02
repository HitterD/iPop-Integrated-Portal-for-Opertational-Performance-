using System.Net;

namespace IPop.Api.Notifier;

/// The PC's own address, not the reverse proxy's: in production every request reaches the API from NPM
/// (192.168.2.3), so "same address as the browser" would match the whole company. Forwarded headers are believed
/// only when the request comes straight from a trusted proxy (Notifier:TrustedProxies, default NPM + this host).
/// Entries are addresses or CIDR ranges (in the cluster the ingress pods live in the pod network, e.g. 10.42.0.0/16).
public static class ClientAddress
{
    private static readonly string[] DefaultProxies = ["192.168.2.3", "127.0.0.1", "::1"];

    public static string? Of(HttpContext http, IConfiguration cfg)
    {
        var peer = http.Connection.RemoteIpAddress;
        if (peer is null) return null;
        if (peer.IsIPv4MappedToIPv6) peer = peer.MapToIPv4();
        var proxies = cfg.GetSection("Notifier:TrustedProxies").Get<string[]>() is { Length: > 0 } p ? p : DefaultProxies;
        if (!proxies.Any(x => IsProxy(x, peer))) return peer.ToString();

        // NPM sets X-Real-IP to the address it saw; X-Forwarded-For's last hop is the same thing.
        var real = http.Request.Headers["X-Real-IP"].ToString().Trim();
        if (!IPAddress.TryParse(real, out var ip))
        {
            var xff = http.Request.Headers["X-Forwarded-For"].ToString().Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (xff.Length == 0 || !IPAddress.TryParse(xff[^1], out ip)) return peer.ToString();
        }
        return (ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip).ToString();
    }

    private static bool IsProxy(string entry, IPAddress peer) =>
        entry.Contains('/')
            ? System.Net.IPNetwork.TryParse(entry, out var net) && net.Contains(peer)
            : IPAddress.TryParse(entry, out var a) && a.Equals(peer);
}
