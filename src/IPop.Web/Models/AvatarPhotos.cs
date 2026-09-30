namespace IPop.Web.Models;

/// Who has a profile photo, as NIK → photo URL. Loaded once by the shell from /api/photos and
/// reloaded on directoryChanged, so no list DTO has to carry photo fields. One signed-in user
/// per browser tab, so a static cache is safe here.
public static class AvatarPhotos
{
    private static Dictionary<string, string> _urls = [];
    private static Dictionary<string, string> _large = [];

    public static event Action? Changed;

    public static void Set(Uri apiBase, IReadOnlyDictionary<string, string>? photoIds)
    {
        _urls = photoIds?.ToDictionary(kv => kv.Key, kv => new Uri(apiBase, $"/api/photos/{kv.Value}").ToString()) ?? [];
        _large = photoIds?.ToDictionary(kv => kv.Key, kv => new Uri(apiBase, $"/api/photos/{kv.Value}/large").ToString()) ?? [];
        Changed?.Invoke();
    }

    public static void Clear() { _urls = []; _large = []; Changed?.Invoke(); }

    public static string? Url(string? nik) => nik is not null && _urls.TryGetValue(nik, out var u) ? u : null;

    /// Up-to-1024px copy for the photo viewer (the API falls back to the 256px avatar).
    public static string? LargeUrl(string? nik) => nik is not null && _large.TryGetValue(nik, out var u) ? u : null;

    /// Inline style for an initials avatar: the palette colour, plus the photo on top when there
    /// is one. The initials stay in the DOM (screen readers, fallback) but turn transparent.
    public static string Style(string? nik)
    {
        var color = AvatarPalette.For(nik);
        return Url(nik) is { } url
            ? $"background:{color} url('{url}') center/cover no-repeat;color:transparent"
            : $"background:{color}";
    }

    public static string Initials(string? name)
    {
        var letters = string.Concat((name ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(w => char.IsLetterOrDigit(w[0])).Select(w => w[0]).Take(2)).ToUpperInvariant();
        return letters.Length > 0 ? letters : "?";
    }
}
