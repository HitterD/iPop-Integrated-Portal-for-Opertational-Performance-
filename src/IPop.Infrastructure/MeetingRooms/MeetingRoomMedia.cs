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
