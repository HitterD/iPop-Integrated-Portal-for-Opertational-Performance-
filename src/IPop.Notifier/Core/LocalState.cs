using System.Text.Json;

namespace IPop.Notifier.Core;

/// Small local memory (not secret): what was already shown, what was held while busy, and the last settings
/// from the server so the app behaves the same when it starts offline.
public sealed class LocalState
{
    public DateTime? LastShownAt { get; set; }
    public List<Guid> Shown { get; set; } = [];
    /// Held during a meeting/full screen/Focus or while popups were off: the Inbox marks these "missed".
    public List<Guid> Held { get; set; } = [];
    public string? SettingsJson { get; set; }

    /// "Start with Windows" for THIS PC (review M1): null = never chosen here (on, the installer default). Not part of
    /// the roaming settings, so turning it off on one PC never removes it on another, and a server load can't undo
    /// the sign-in window's choice.
    public bool? StartWithWindows { get; set; }
    /// Whether this PC ever had the per-PC switch; a PC upgraded from an older version takes over the last roaming
    /// value once (see SettingsService).
    public bool StartWithWindowsMigrated { get; set; }

    private static string FilePath => Path.Combine(SessionStore.Folder, "state.json");
    private static readonly object Gate = new();

    public static LocalState Load()
    {
        try { return JsonSerializer.Deserialize<LocalState>(File.ReadAllText(FilePath)) ?? new(); }
        catch { return new(); }
    }

    public void Save()
    {
        lock (Gate)
        {
            // Big enough for a long leave's backlog: an id that drops out could pop up again.
            if (Shown.Count > 3000) Shown.RemoveRange(0, Shown.Count - 3000);
            if (Held.Count > 200) Held.RemoveRange(0, Held.Count - 200);
            try
            {
                Directory.CreateDirectory(SessionStore.Folder);
                var tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(this));
                File.Move(tmp, FilePath, overwrite: true);
            }
            catch { }
        }
    }
}
