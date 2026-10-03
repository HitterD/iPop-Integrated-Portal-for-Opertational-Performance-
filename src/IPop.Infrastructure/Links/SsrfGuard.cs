using System.Net;
using System.Net.Sockets;

namespace IPop.Infrastructure.Links;

/// What the link-preview fetcher may talk to: http/https on ports 80/443, to public unicast
/// addresses only. Everything private, loopback, link-local (incl. cloud metadata 169.254.169.254),
/// CGNAT, multicast, reserved and documentation ranges is refused — IPv6 too, including v4 hidden
/// inside v6 (mapped, compatible, 6to4, NAT64).
public static class SsrfGuard
{
    private static readonly IPNetwork[] BlockedV4 =
    [
        IPNetwork.Parse("0.0.0.0/8"),
        IPNetwork.Parse("10.0.0.0/8"),
        IPNetwork.Parse("100.64.0.0/10"),
        IPNetwork.Parse("127.0.0.0/8"),
        IPNetwork.Parse("169.254.0.0/16"),
        IPNetwork.Parse("172.16.0.0/12"),
        IPNetwork.Parse("192.0.0.0/24"),
        IPNetwork.Parse("192.0.2.0/24"),
        IPNetwork.Parse("192.88.99.0/24"),
        IPNetwork.Parse("192.168.0.0/16"),
        IPNetwork.Parse("198.18.0.0/15"),
        IPNetwork.Parse("198.51.100.0/24"),
        IPNetwork.Parse("203.0.113.0/24"),
        IPNetwork.Parse("224.0.0.0/4"),
        IPNetwork.Parse("240.0.0.0/4"),
    ];

    private static readonly IPNetwork[] BlockedV6 =
    [
        IPNetwork.Parse("::/128"),
        IPNetwork.Parse("::1/128"),
        IPNetwork.Parse("100::/64"),       // discard-only
        IPNetwork.Parse("2001::/32"),      // Teredo
        IPNetwork.Parse("2001:db8::/32"),  // documentation
        IPNetwork.Parse("fc00::/7"),       // unique local
        IPNetwork.Parse("fe80::/10"),      // link-local
        IPNetwork.Parse("fec0::/10"),      // site-local (deprecated)
        IPNetwork.Parse("ff00::/8"),       // multicast
    ];

    public static bool IsPublic(IPAddress ip)
    {
        if (ip.AddressFamily == AddressFamily.InterNetwork)
            return !BlockedV4.Any(n => n.Contains(ip));
        if (ip.AddressFamily != AddressFamily.InterNetworkV6) return false;

        if (ip.IsIPv4MappedToIPv6) return IsPublic(ip.MapToIPv4());
        var b = ip.GetAddressBytes();
        // IPv4-compatible ::a.b.c.d (deprecated) and NAT64 64:ff9b::a.b.c.d carry an IPv4 tail.
        if (b.Take(12).All(x => x == 0)) return false;
        if (b[0] == 0x00 && b[1] == 0x64 && b[2] == 0xff && b[3] == 0x9b && b.Skip(4).Take(8).All(x => x == 0))
            return IsPublic(new IPAddress(b[12..16]));
        // 6to4 2002:a.b.c.d::/48 — judge by the embedded IPv4.
        if (b[0] == 0x20 && b[1] == 0x02) return IsPublic(new IPAddress(b[2..6]));
        if (BlockedV6.Any(n => n.Contains(ip))) return false;
        // Only global unicast (2000::/3) is reachable on the public internet.
        return (b[0] & 0xE0) == 0x20;
    }

    /// Scheme http/https, port 80/443, no userinfo, a non-empty host.
    public static bool IsAllowedUri(Uri uri) =>
        uri.IsAbsoluteUri
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
        && uri.Port is 80 or 443
        && string.IsNullOrEmpty(uri.UserInfo)
        && !string.IsNullOrEmpty(uri.IdnHost);
}
