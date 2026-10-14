namespace IPop.Tests;

/// A booking start that is always valid under the room limits (same office day, not past, within 60 days):
/// tomorrow 09:00 Asia/Jakarta, as UTC. `UtcNow + 2 h` crossed midnight when tests ran after 22:00 WIB.
internal static class SafeBookingTime
{
    public static DateTime Start => DateTime.SpecifyKind(DateTime.UtcNow.AddHours(7).Date.AddDays(1).AddHours(2), DateTimeKind.Utc);
}
