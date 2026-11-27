using System.Text.Json;

namespace IPop.Application.Notifier;

/// Per-person remote help preferences (ui-prefs "remote", Settings › Remote; BIGIS #8 v3):
///   viewer (ICT): starting quality, screen size, auto-hide toolbar, shortcuts, chat sound, save folder;
///   user: the ticks they want by default in the Allow prompt (null = follow the company default).
/// Defaults are the mockup README's. Unknown or bad values fall back; strings are length-capped.
public sealed record RemotePrefs
{
    public string Quality { get; init; } = "auto";
    public string Fit { get; init; } = "fit";
    public bool AutoHide { get; init; }
    public bool ChatSound { get; init; } = true;
    /// Null = Downloads\iPop Remote (resolved by the Notifier on the PC).
    public string? SaveDir { get; init; }
    public RemoteKeys Keys { get; init; } = new();
    public RemotePerms? UserDefaults { get; init; }

    public RemotePrefs Normalized() => this with
    {
        Quality = Quality is "smooth" or "quality" ? Quality : "auto",
        Fit = Fit is "actual" or "fill" ? Fit : "fit",
        SaveDir = string.IsNullOrWhiteSpace(SaveDir) ? null : SaveDir.Trim().Length > 260 ? SaveDir.Trim()[..260] : SaveDir.Trim(),
        Keys = (Keys ?? new()).Normalized(),
    };

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public string ToJson() => JsonSerializer.Serialize(Normalized(), Json);
    public static RemotePrefs Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new();
        try { return (JsonSerializer.Deserialize<RemotePrefs>(json, Json) ?? new()).Normalized(); }
        catch (JsonException) { return new(); }
    }
}

public sealed record RemoteKeys
{
    public string Cad { get; init; } = "Ctrl+Alt+End";
    public string Fs { get; init; } = "F11";
    public string Toolbar { get; init; } = "Ctrl+Shift+T";

    public RemoteKeys Normalized() => new()
    {
        Cad = Ok(Cad) ? Cad : "Ctrl+Alt+End", Fs = Ok(Fs) ? Fs : "F11", Toolbar = Ok(Toolbar) ? Toolbar : "Ctrl+Shift+T",
    };

    private static bool Ok(string? k) => !string.IsNullOrWhiteSpace(k) && k.Length <= 30;
}
