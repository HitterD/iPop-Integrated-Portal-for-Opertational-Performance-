namespace IPop.Web.Models;

/// Shared avatar color pool — stable per key (NIK), used by directory cards,
/// chart member chips, and the member picker so the same person keeps one color.
public static class AvatarPalette
{
    public static readonly string[] Colors = ["#2e7d5b", "#4f6d8f", "#8a6d3b", "#7d5ba6", "#b3543f", "#3f3f3a"];
    public static string For(string key) => Colors[(int)((uint)key.GetHashCode() % Colors.Length)];
}
