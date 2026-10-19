namespace IPop.Infrastructure.Tickets;

/// Links into iDesk that iPop shows to users (Open/Reply/Manage in iDesk) must use the public address:
/// iDesk builds them from the first FRONTEND_URL entry, which on prod is the LAN address
/// (http://192.168.2.136:3080), where the user's iDesk session does not exist. Any absolute link that
/// is not on the public host keeps its path and query but moves to <see cref="IdeskOptions.PublicUrl"/>.
/// With "Sign in with iPop" on, the link goes through iDesk's /v1/auth/oidc/start so it opens signed in.
public static class IdeskLinks
{
    public const string SignInPath = "/v1/auth/oidc/start";

    public static string? Public(string? url, string publicBase, bool signIn = false)
    {
        if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(publicBase)) return url;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps)) return url;
        if (!Uri.TryCreate(publicBase.TrimEnd('/') + "/", UriKind.Absolute, out var pub)) return url;
        var authority = pub.GetLeftPart(UriPartial.Authority);
        var onPublicHost = string.Equals(u.Host, pub.Host, StringComparison.OrdinalIgnoreCase) && u.Scheme == pub.Scheme && u.Port == pub.Port;
        var target = u.PathAndQuery + u.Fragment;
        // Stored links may already be wrapped (parsed once on the webhook, again on read).
        if (!signIn || IsSignInLink(u.AbsolutePath)) return onPublicHost ? url : authority + target;
        return $"{authority}{SignInPath}?returnTo={Uri.EscapeDataString(target)}";
    }

    private static bool IsSignInLink(string path) =>
        path.Equals(SignInPath, StringComparison.OrdinalIgnoreCase) || path.Equals("/api" + SignInPath, StringComparison.OrdinalIgnoreCase);
}
