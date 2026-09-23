namespace IPop.Web.Models;

/// Shared avatar color pool — stable per key (NIK), used by directory cards,
/// chart member chips, and the member picker so the same person keeps one color.
/// Single source of truth: Chat's room/user colours and Directory/Admin all index
/// this same array, so a person is the same colour in every module and every session.
public static class AvatarPalette
{
    // ipop-33b: canonical palette (Chat.razor's set survives as the definition —
    // it had drifted from the copy that was here).
    public static readonly string[] Colors = ["#2e7d5b", "#7d5ba6", "#8a6d3b", "#b3543f", "#4f6d8f", "#5b7d2e"];

    /// ipop-33: value-derived deterministic hash for strings — string.GetHashCode()
    /// is seeded per process in .NET Core+, so colours would reshuffle on every load.
    public static int StableStringHash(string? s)
    {
        if (string.IsNullOrEmpty(s)) return 0;
        unchecked
        {
            var h = 17;
            foreach (var ch in s) h = h * 31 + ch;
            return h;
        }
    }

    /// int.MinValue-safe modulo (Math.Abs(int.MinValue) throws).
    public static string ForHash(int hash)
        => Colors[(hash & 0x7FFFFFFF) % Colors.Length];

    public static string For(string? key)
    {
        if (string.IsNullOrEmpty(key)) return Colors[0];
        return ForHash(StableStringHash(key));
    }
}
