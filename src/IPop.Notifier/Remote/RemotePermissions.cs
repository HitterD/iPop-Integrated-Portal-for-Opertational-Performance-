namespace IPop.Notifier.Remote;

/// What the user allowed in the prompt (BIGIS #8): mouse and keyboard, clipboard, file transfer. The user's PC holds
/// this and refuses everything else coming from the helper — the helper's buttons are only a hint, this is the gate.
/// Pure (no WinForms) so the tests compile it in directly.
public sealed class RemotePermissions
{
    public bool Control { get; }
    public bool Clipboard { get; }
    public bool Files { get; }

    public RemotePermissions(bool control, bool clipboard, bool files) { Control = control; Clipboard = clipboard; Files = files; }

    /// "control,clipboard,files" (any order, any case); null or empty = nothing allowed.
    public static RemotePermissions Parse(string? list)
    {
        var set = (list ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => s.ToLowerInvariant()).ToHashSet();
        return new RemotePermissions(set.Contains("control"), set.Contains("clipboard"), set.Contains("files"));
    }

    public static RemotePermissions All => new(true, true, true);

    public string ToList() => string.Join(',', new[] { Control ? "control" : null, Clipboard ? "clipboard" : null, Files ? "files" : null }.OfType<string>());

    /// May data on this LiveKit topic reach the PC? "in" = mouse/keyboard (also only in control mode), "clip" and
    /// "clip-file"/"clip-data" = clipboard, "file" = file transfer. Control messages ("ctl"), chat and media always pass.
    public bool Accepts(string? topic, string mode) => topic switch
    {
        "in" => Control && mode == "control",
        "clip" or "clip-file" or "clip-data" => Clipboard,
        "file" => Files,
        _ => true,
    };

    /// Helper control messages that switch something on: "mode: control" needs Control, "clipSync on" needs Clipboard.
    public string ClampMode(string? asked) => asked == "control" && Control ? "control" : "view";
    public bool ClampClipSync(bool asked) => asked && Clipboard;
}
