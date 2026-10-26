using System.Text.Json;
using System.Text.Json.Nodes;

namespace IPop.Api.Notifier;

/// The iPop Notifier release feed, one folder (Notifier:ReleasesPath, the same one the Web host serves at
/// /download/notifier/). publish-notifier.ps1 writes:
///   releases.win.json        Velopack feed for everyone (also what Notifier 1.2.x reads directly)
///   releases.win.pilot.json  the same feed plus the release being tried by the pilot group (ICT/Admin)
///   release.json             { "all": { "version", "mandatory" }, "pilot": { "version", "mandatory" } | null }
/// Notifier 1.3+ asks the API which feed applies to the signed-in person, so a pilot release never reaches
/// other PCs until it is released to all.
public sealed class NotifierReleases(IConfiguration cfg)
{
    public const string DefaultPath = "C:/ipop-ops/notifier/releases";
    public const string AllFeed = "releases.win.json";
    public const string PilotFeed = "releases.win.pilot.json";
    public const string Manifest = "release.json";

    public sealed record Release(string Version, bool Mandatory, DateTime? PublishedAt);
    public sealed record State(Release? All, Release? Pilot);

    public string Folder => cfg["Notifier:ReleasesPath"] is { Length: > 0 } p ? p : DefaultPath;

    public State Read()
    {
        try
        {
            var path = Path.Combine(Folder, Manifest);
            if (!File.Exists(path)) return new State(FromFeed(AllFeed), null);
            var n = JsonNode.Parse(File.ReadAllText(path));
            return new State(Parse(n?["all"]) ?? FromFeed(AllFeed), Parse(n?["pilot"]));
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return new State(null, null);
        }
    }

    /// The manifest's modified time: the change watcher tells running apps to check when it moves.
    public DateTime? Stamp()
    {
        try
        {
            var path = Path.Combine(Folder, Manifest);
            return File.Exists(path) ? File.GetLastWriteTimeUtc(path) : null;
        }
        catch { return null; }
    }

    /// Velopack feed JSON for this person, with the target release's "mandatory" flag next to it.
    public (string? FeedJson, Release? Target) FeedFor(bool pilot)
    {
        var state = Read();
        var usePilot = pilot && state.Pilot is not null && File.Exists(Path.Combine(Folder, PilotFeed));
        var file = usePilot ? PilotFeed : AllFeed;
        try
        {
            var path = Path.Combine(Folder, file);
            return (File.Exists(path) ? File.ReadAllText(path) : null, usePilot ? state.Pilot : state.All);
        }
        catch (IOException) { return (null, null); }
    }

    private static Release? Parse(JsonNode? n)
    {
        var v = n?["version"]?.GetValue<string>();
        if (!IsVersion(v)) return null;
        DateTime? at = n?["publishedAt"]?.GetValue<string>() is { } s && DateTime.TryParse(s, null, System.Globalization.DateTimeStyles.RoundtripKind, out var d) ? d : null;
        return new Release(v!, n?["mandatory"]?.GetValue<bool>() == true, at);
    }

    /// Before the first release.json: the newest version in the public feed.
    private Release? FromFeed(string file)
    {
        try
        {
            var path = Path.Combine(Folder, file);
            if (!File.Exists(path)) return null;
            var assets = JsonNode.Parse(File.ReadAllText(path))?["Assets"]?.AsArray();
            var newest = assets?.Select(a => a?["Version"]?.GetValue<string>()).Where(IsVersion)
                .OrderByDescending(v => System.Version.Parse(v!)).FirstOrDefault();
            return newest is null ? null : new Release(newest, false, null);
        }
        catch (Exception e) when (e is IOException or JsonException or InvalidOperationException) { return null; }
    }

    public static bool IsVersion(string? v) => v is { Length: > 0 and <= 20 } && System.Version.TryParse(v, out var p) && p.Build >= 0 && v.Count(c => c == '.') == 2;
}
