namespace IPop.Application.Notifier;

/// Which servers the iPop Notifier trusts for what (review 2026-11-26, H2). Shared by the app (linked into
/// IPop.Notifier) and the tests.
///   - Sign-in ("Server…" in the sign-in window): https only in a release build; http only in Debug/Trial builds.
///   - Updates: always the fixed production server (or the Trial build's own feed), never the server someone signed
///     in to, so a typed-in or look-alike server can't hand out an "update".
public static class NotifierServerRules
{
    /// The typed address as "scheme://host[:port]/path/", or null when it isn't one the build accepts.
    public static string? AcceptServer(string? typed, bool allowHttp)
    {
        var s = typed?.Trim();
        if (string.IsNullOrEmpty(s) || !Uri.TryCreate(s, UriKind.Absolute, out var u)) return null;
        if (u.Scheme != Uri.UriSchemeHttps && !(allowHttp && u.Scheme == Uri.UriSchemeHttp)) return null;
        if (!string.IsNullOrEmpty(u.UserInfo) || u.Host.Length == 0) return null;
        return u.GetLeftPart(UriPartial.Path).TrimEnd('/') + "/";
    }

    /// Where update packages come from: the Trial feed when the build has one, else {fixed server}/download/notifier/.
    public static string UpdateFiles(string fixedServer, string? trialFeed) =>
        trialFeed ?? new Uri(new Uri(fixedServer), "download/notifier/").ToString();

    /// The signed-in server may pick the release channel (pilot/all, "mandatory") only when it is the fixed server
    /// itself (same scheme, host and port), or in a Trial build.
    public static bool ChannelFromSignedInServer(Uri signedIn, string fixedServer, bool trial)
    {
        if (trial) return true;
        var f = new Uri(fixedServer);
        return signedIn.Scheme == f.Scheme && string.Equals(signedIn.Host, f.Host, StringComparison.OrdinalIgnoreCase) && signedIn.Port == f.Port;
    }
}
