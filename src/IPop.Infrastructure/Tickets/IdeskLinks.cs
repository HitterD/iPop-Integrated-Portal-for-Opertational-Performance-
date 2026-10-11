namespace IPop.Infrastructure.Tickets;

/// Links into iDesk that iPop shows to users (Open/Reply/Manage in iDesk) must use the public address:
/// iDesk builds them from the first FRONTEND_URL entry, which on prod is the LAN address
/// (http://192.168.2.136:3080), where the user's iDesk session does not exist. Any absolute link that
/// is not on the public host keeps its path and query but moves to <see cref="IdeskOptions.PublicUrl"/>.
public static class IdeskLinks
{
    public static string? Public(string? url, string publicBase)
    {
        if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(publicBase)) return url;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps)) return url;
        if (!Uri.TryCreate(publicBase.TrimEnd('/') + "/", UriKind.Absolute, out var pub)) return url;
        if (string.Equals(u.Host, pub.Host, StringComparison.OrdinalIgnoreCase) && u.Scheme == pub.Scheme) return url;
        return pub.GetLeftPart(UriPartial.Authority) + u.PathAndQuery + u.Fragment;
    }
}
