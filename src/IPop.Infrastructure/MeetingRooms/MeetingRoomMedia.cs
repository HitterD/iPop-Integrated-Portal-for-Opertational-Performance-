using System.Text.RegularExpressions;
using IPop.Application.Hall;
using Microsoft.Extensions.Configuration;

namespace IPop.Infrastructure.MeetingRooms;

/// The ONE place that names LiveKit media rooms for the five hall meeting rooms, plus the environment switches of the
/// meeting-room workers (final review findings 1 and 4).
/// <list type="bullet">
/// <item>Every booking gets its own media room <c>{MediaNamespace}-{key}-{meetingId:N}</c>: tokens, the room list,
/// metadata, Egress, the chat relay and both workers address only that name. A token minted for one booking can never
/// enter the next booking's media, and an Egress composite of booking A can never capture booking B.</item>
/// <item><c>MeetingRooms:MediaNamespace</c> (default "hall", production): staging/QA sharing a LiveKit server MUST set
/// its own value (e.g. "qa-hall") so it never lists, removes, rewrites or records production rooms. The legacy v1 /
/// sja-hall 3D rooms "hall-{key}" never match (no meeting id), so v2 code leaves them alone.</item>
/// <item><c>MeetingRooms:BookingWorkerEnabled</c> (default true): false turns off the booking worker's destructive
/// pass (participant removal, metadata rewrite, recording stop, chat cleanup).</item>
/// </list>
public sealed class MeetingRoomMedia
{
    public const string DefaultNamespace = "hall";
    private static readonly Regex Valid = new("^[a-z0-9](?:[a-z0-9-]{0,30}[a-z0-9])?$", RegexOptions.CultureInvariant);

    /// Production-compatible defaults (namespace "hall", booking worker on).
    public static readonly MeetingRoomMedia Default = new();

    public MeetingRoomMedia(IConfiguration? config = null)
    {
        var ns = config?["MeetingRooms:MediaNamespace"];
        Namespace = string.IsNullOrWhiteSpace(ns) ? DefaultNamespace : ns.Trim();
        if (!Valid.IsMatch(Namespace))
            throw new InvalidOperationException(
                "MeetingRooms:MediaNamespace must be 1-32 lowercase letters, digits or '-' (no leading/trailing '-').");
        var worker = config?["MeetingRooms:BookingWorkerEnabled"];
        BookingWorkerEnabled = string.IsNullOrWhiteSpace(worker) || !bool.TryParse(worker, out var on) || on;
        if (!string.IsNullOrWhiteSpace(worker) && !bool.TryParse(worker, out _))
            throw new InvalidOperationException("MeetingRooms:BookingWorkerEnabled must be true or false.");
    }

    /// Deployment identity is explicit: production currently runs ASP.NET Development, so that environment is not a boundary.
    public static void ValidateDeployment(IConfiguration config)
    {
        var mode = config["MeetingRooms:DeploymentMode"] ?? "Isolated";
        if (mode is not ("Production" or "Isolated"))
            throw new InvalidOperationException("MeetingRooms:DeploymentMode must be Production or Isolated.");
        var media = new MeetingRoomMedia(config);
        if (mode == "Production") return;
        var ns = config["MeetingRooms:MediaNamespace"];
        var nas = config["MeetingRooms:NasFolder"];
        var spool = CanonicalLocalRoot(config["MeetingRooms:SpoolDir"]);
        var cache = CanonicalLocalRoot(config["MeetingRooms:CacheDir"]);
        // The Windows host's production roots; on Linux they cannot exist, so there is nothing to overlap (null).
        var prodSpool = CanonicalLocalRoot(@"F:\ipop-data\recordings-spool");
        var prodCache = CanonicalLocalRoot(@"F:\ipop-data\recordings-cache");
        if (string.IsNullOrWhiteSpace(ns) || media.Namespace == DefaultNamespace
            || string.IsNullOrWhiteSpace(config["MeetingRooms:BookingWorkerEnabled"])
            || RecordingPaths.NasFile(nas, nas?.TrimEnd('/') + "/boundary-check.mp4") is null
            || (nas is not null && Overlaps(nas.TrimEnd('/'), "/sja/SJA SPJ/ICT/recordings"))
            || spool is null || cache is null || Overlaps(spool, cache)
            || Overlaps(spool, prodSpool) || Overlaps(spool, prodCache)
            || Overlaps(cache, prodSpool) || Overlaps(cache, prodCache))
            throw new InvalidOperationException("Isolated meeting rooms require a non-hall namespace, explicit BookingWorkerEnabled, and distinct explicit NAS, SpoolDir and CacheDir roots outside production roots.");
    }

    private static bool Overlaps(string a, string? b) => b is not null && (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)
        || a.StartsWith(b + "/", StringComparison.OrdinalIgnoreCase) || b.StartsWith(a + "/", StringComparison.OrdinalIgnoreCase));

    private static string? CanonicalLocalRoot(string? root)
    {
        if (string.IsNullOrWhiteSpace(root)) return null;
        try
        {
            var normalized = Path.GetFullPath(root).Replace('\\', '/').TrimEnd('/');
            // Absolute, not a drive/filesystem root; no aliasing through dot segments or junctions.
            var typed = root.Replace('\\', '/').TrimEnd('/');
            if (!Path.IsPathFullyQualified(root) || normalized.Length <= 3 || typed.Split('/').Any(s => s is "." or "..")
                || RecordingPaths.LocalFile(root, "boundary-check.mp4") is null) return null;
            return normalized;
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or IOException) { return null; }
    }

    public string Namespace { get; }
    public bool BookingWorkerEnabled { get; }

    /// The LiveKit media room of booking <paramref name="meetingId"/> in hall room <paramref name="key"/>.
    public string Room(string key, Guid meetingId)
    {
        if (!HallBooking.Rooms.Any(r => r.Key == key)) throw new ArgumentException($"Unknown hall room '{key}'.", nameof(key));
        if (meetingId == Guid.Empty) throw new ArgumentException("A media room belongs to a booking.", nameof(meetingId));
        return $"{Namespace}-{key}-{meetingId:N}";
    }

    /// The hall room and booking of a media room of THIS namespace; null for anything else (other environments, the
    /// legacy "hall-{key}" rooms, group calls, garbage).
    public (string Key, Guid MeetingId)? Parse(string? liveKitRoom)
    {
        if (string.IsNullOrEmpty(liveKitRoom)) return null;
        foreach (var (key, _) in HallBooking.Rooms)
        {
            var prefix = $"{Namespace}-{key}-";
            if (liveKitRoom.StartsWith(prefix, StringComparison.Ordinal)
                && Guid.TryParseExact(liveKitRoom[prefix.Length..], "N", out var id) && id != Guid.Empty
                && liveKitRoom[prefix.Length..] == id.ToString("N"))
                return (key, id);
        }
        return null;
    }
}
