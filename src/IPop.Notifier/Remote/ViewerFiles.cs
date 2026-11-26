using System.Text.Json.Nodes;
using IPop.Notifier.Core;

namespace IPop.Notifier.Remote;

/// The ICT viewer's side of streamed files (BIGIS #8 v3): batches from the page are appended to a ".part" file in
/// the ICT's save folder (Settings › Remote, default Downloads\iPop Remote) and renamed when complete; copied files on
/// the ICT PC go out by token (OutgoingFiles), never read whole. One instance per viewer window.
internal sealed class ViewerFiles(Action<object> send, Func<Uri> server)
{
    private IncomingFiles incoming = new(DefaultFolder, 2048L * 1024 * 1024);
    private readonly Dictionary<string, string> topics = [];

    public static string DefaultFolder() => Path.Combine(Core.Downloads.Folder, "iPop Remote");

    public void Configure(string? saveDir, long maxBytes)
    {
        var dir = string.IsNullOrWhiteSpace(saveDir) ? DefaultFolder() : Environment.ExpandEnvironmentVariables(saveDir);
        incoming = new IncomingFiles(() => dir, maxBytes > 0 ? maxBytes : 2048L * 1024 * 1024);
    }

    public bool Handle(string type, JsonNode? m)
    {
        var id = m?["id"]?.GetValue<string>() ?? "";
        switch (type)
        {
            case "xferBegin":
                var e = incoming.Begin(id, m?["name"]?.GetValue<string>(), m?["size"]?.GetValue<long>() ?? 0);
                if (e is null) { send(new { type = "xferSaved", id, path = (string?)null, reason = "big" }); return true; }
                topics[id] = m?["topic"]?.GetValue<string>() ?? "file";
                return true;
            case "xferChunk":
                byte[] data;
                try { data = Convert.FromBase64String(m?["data"]?.GetValue<string>() ?? ""); }
                catch (FormatException) { incoming.Abort(id); send(new { type = "xferSaved", id, path = (string?)null, reason = "bad" }); return true; }
                if (!incoming.Append(id, data)) { send(new { type = "xferSaved", id, path = (string?)null, reason = "big" }); return true; }
                send(new { type = "xferProgress", id, n = m?["n"]?.GetValue<long>() ?? 0 });
                return true;
            case "xferEnd":
                string? path = null;
                try { path = incoming.End(id, m?["ok"]?.GetValue<bool>() == true); }
                catch (Exception ex) { Log.Warn("viewer", "saving a file failed: " + ex.GetType().Name); }
                topics.Remove(id, out var topic);
                if (path is not null && topic == "clip-file") { Clip.SetFileFromPath(path); send(new { type = "toast", text = Path.GetFileName(path) + " copied — paste it here" }); return true; }
                if (path is not null) Core.Downloads.MarkSaved(path, server());
                send(new { type = "xferSaved", id, path, reason = m?["reason"]?.GetValue<string>() });
                return true;
            case "xferRefused":
                return true;
        }
        return false;
    }

    public void Close() { incoming.AbortAll(); OutgoingFiles.Clear(); }
}
