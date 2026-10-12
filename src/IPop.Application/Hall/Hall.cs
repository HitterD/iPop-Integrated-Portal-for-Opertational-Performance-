namespace IPop.Application.Hall;

public static class HallLimits
{
    public const int RoomCapacity = 20;
}

/// Meeting rooms are booked through ordinary meetings whose Location is "SJA Hall · {Room name}". One helper
/// writes and reads that form so the meeting forms, the rooms tab and the conflict check agree.
public static class HallBooking
{
    public const string Prefix = "SJA Hall · ";
    public static readonly (string Key, string Name)[] Rooms =
        [("robusta", "Robusta"), ("arabika", "Arabika"), ("kalosi", "Kalosi"), ("kapal-api", "Kapal Api"), ("toraja", "Toraja")];

    public static string Location(string roomName) => Prefix + roomName;

    /// The hall room key for a meeting location, or null when it is not a hall room. Case and spacing are forgiven.
    public static string? RoomKey(string? location)
    {
        if (string.IsNullOrWhiteSpace(location)) return null;
        var l = string.Join(' ', location.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (!l.StartsWith("SJA Hall", StringComparison.OrdinalIgnoreCase)) return null;
        var name = l["SJA Hall".Length..].TrimStart(' ', '·', '-', ':', '•').Trim();
        return Rooms.FirstOrDefault(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase)).Key;
    }

    /// Canonical location string for a hall room location typed in any of the forgiven forms; other text unchanged.
    public static string? Normalize(string? location) =>
        RoomKey(location) is { } key ? Location(Rooms.First(r => r.Key == key).Name) : location;
}

/// The booking holding a hall room right now. Allowed = the asking user is the organizer or an attendee.
public record HallRoomBooking(Guid MeetingId, string Title, DateTime EndAt, bool Allowed);

/// The next booking of a room (for the room card). Allowed = the asking user is the organizer or a non-declined attendee.
public record HallNextBooking(Guid MeetingId, string Title, DateTime StartAt, DateTime EndAt, string OrganizerName, bool Allowed);

/// The 6-digit password of a hall-room booking (meeting rooms v2 §1). Random per meeting; compared in constant time.
public static class RoomPasswords
{
    public const int Length = 6;

    public static string New() => System.Security.Cryptography.RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");

    /// "123 456": how the password is shown in the invite, the reminder and Calendar (Matches ignores the space).
    public static string Format(string password) =>
        password is { Length: Length } ? $"{password[..3]} {password[3..]}" : password;

    /// True when <paramref name="typed"/> (spaces ignored, as the invite shows "123 456") equals <paramref name="stored"/>.
    public static bool Matches(string? stored, string? typed)
    {
        if (stored is not { Length: Length } || typed is null) return false;
        var clean = new string(typed.Where(c => !char.IsWhiteSpace(c)).ToArray());
        if (clean.Length != Length || !clean.All(char.IsAsciiDigit)) return false;
        return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.ASCII.GetBytes(stored), System.Text.Encoding.ASCII.GetBytes(clean));
    }
}
