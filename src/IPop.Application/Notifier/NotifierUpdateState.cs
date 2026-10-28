using System.Text.Json;

namespace IPop.Application.Notifier;

/// Per-PC, per-version update timing. Stored separately from account/session state so restarting or
/// signing out cannot reset Later or the downloaded version's 24-hour age.
public sealed class NotifierUpdateState
{
    private readonly string path;
    private Snapshot current;
    public sealed record Snapshot(string Version, DateTime ReadyAt, DateTime? AskedAt);

    public string? Version => current.Version;
    public DateTime ReadyAt => current.ReadyAt;
    public DateTime? AskedAt => current.AskedAt;

    public NotifierUpdateState(string path)
    {
        this.path = path;
        try
        {
            current = JsonSerializer.Deserialize<Snapshot>(File.ReadAllText(path))
                ?? new("", default, null);
            if (string.IsNullOrWhiteSpace(current.Version) || current.ReadyAt == default)
                current = new("", default, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            current = new("", default, null);
        }
    }

    public void MarkReady(string version, DateTime now)
    {
        if (current.Version == version) return;
        Save(new(version, now, null));
    }

    public void MarkAsked(DateTime now) => Save(current with { AskedAt = now });

    private void Save(Snapshot next)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(next));
        File.Move(temp, path, overwrite: true);
        current = next;
    }
}
