using System.Text.Json.Nodes;
using IPop.Notifier.Core;

namespace IPop.Notifier.Remote;

/// Files during remote help on the user's PC, streamed both ways (BIGIS #8 v3):
///   from ICT: the host page reads the LiveKit stream in batches → <see cref="IncomingFiles"/> appends them to a
///             ".part" in Downloads → renamed when complete. Over the limit, cancelled or cut off → deleted.
///   to ICT:   "Send a file to ICT" (or a copied file) → <see cref="OutgoingFiles"/> token → the page streams it.
/// The limit is the company policy's (Settings › Remote, default 2 GB); only with the user's "File transfer".
public sealed partial class RemoteHost
{
    private IncomingFiles incoming = new(() => IPop.Notifier.Core.Downloads.Folder, 2048L * 1024 * 1024);
    private long maxFileBytes = 2048L * 1024 * 1024;
    private readonly Dictionary<string, (string Name, long Size, string Dir, string Topic)> xfers = [];

    private void ApplyLimits(JsonNode join)
    {
        maxFileBytes = join["maxFileBytes"]?.GetValue<long>() is > 0 and var m ? m : maxFileBytes;
        incoming.MaxBytes = maxFileBytes;
        var a = join["reconnectAttempts"]?.GetValue<int>() ?? 6;
        var s = join["reconnectSeconds"]?.GetValue<int>() ?? 60;
        rejoin = new RemoteRejoin(a, s);
    }

    /// The page announces a stream from ICT. Clipboard files go to a temp folder, others to Downloads.
    private void XferBegin(JsonNode m)
    {
        var id = m["id"]?.GetValue<string>() ?? "";
        var topic = m["topic"]?.GetValue<string>() ?? "file";
        var name = m["name"]?.GetValue<string>() ?? "file";
        var size = m["size"]?.GetValue<long>() ?? 0;
        if (sessionId is null || !perms.Accepts(topic, mode) || (topic == "clip-file" && !clipSync))
        { Refuse(topic); page?.Send(new { type = "xferCancel", id, reason = "notallowed" }); return; }
        var e = incoming.Begin(id, name, size);
        if (e is null)
        {
            var why = IncomingFiles.Reason(incoming.LastRefusal);
            page?.Send(new { type = "xferCancel", id, reason = why });
            help?.Send(new { type = "xfer", id, name, size, dir = "up", state = why == "big" ? "big" : "fail", p = 0.0, limit = maxFileBytes, reason = why });
            return;
        }
        xfers[id] = (e.Name, size, "down", topic);
        help?.Send(new { type = "xfer", id, name = e.Name, size, dir = "up", state = "run", p = 0.0 });
    }

    private void XferChunk(JsonNode m)
    {
        var id = m["id"]?.GetValue<string>() ?? "";
        byte[] data;
        try { data = Convert.FromBase64String(m["data"]?.GetValue<string>() ?? ""); }
        catch (FormatException) { incoming.Abort(id); page?.Send(new { type = "xferCancel", id, reason = "bad" }); return; }
        if (!incoming.Append(id, data)) { page?.Send(new { type = "xferCancel", id, reason = "big" }); XferShow(id, "big", 0); return; }
        if (xfers.TryGetValue(id, out var x) && x.Size > 0)
            help?.Send(new { type = "xfer", id, name = x.Name, size = x.Size, dir = "up", state = "run", p = (m["n"]?.GetValue<long>() ?? 0) / (double)x.Size });
    }

    private void XferEnd(JsonNode m)
    {
        var id = m["id"]?.GetValue<string>() ?? "";
        var ok = m["ok"]?.GetValue<bool>() == true;
        var reason = m["reason"]?.GetValue<string>() ?? "";
        string? path;
        try { path = incoming.End(id, ok); }
        catch (Exception ex) { Log.Warn("remote", "saving a file failed: " + ex.GetType().Name); path = null; }
        if (!xfers.Remove(id, out var x)) return;
        if (path is null) { XferShow(id, reason is "cancel" ? "cancel" : reason is "big" ? "big" : "fail", 0, x); return; }
        if (x.Topic == "clip-file") { IPop.Notifier.Core.Downloads.MarkSaved(path, api.Server); Clip.SetFileFromPath(path); return; }
        IPop.Notifier.Core.Downloads.MarkSaved(path, api.Server);
        files.Add(Path.GetFileName(path));
        help?.Send(new { type = "xfer", id, name = Path.GetFileName(path), size = x.Size, dir = "up", state = "done", p = 1.0 });
        balloon("File from ICT", $"{Path.GetFileName(path)} was saved to Downloads.", ToolTipIcon.Info);
    }

    private void XferShow(string id, string state, double p, (string Name, long Size, string Dir, string Topic)? x = null)
    {
        var v = x ?? (xfers.TryGetValue(id, out var y) ? y : default);
        help?.Send(new { type = "xfer", id, name = v.Name, size = v.Size, dir = "up", state, p, limit = maxFileBytes });
    }

    /// "Send a file to ICT" in the help window: pick, check size, stream from disk through the page.
    internal void SendFileToIct()
    {
        if (sessionId is null || !perms.Files || page is null) return;
        using var dlg = new OpenFileDialog { Title = "Send a file to ICT", CheckFileExists = true, Multiselect = false };
        if (dlg.ShowDialog() != DialogResult.OK) return;
        SendPath(dlg.FileName, "file");
    }

    private void SendPath(string path, string topic)
    {
        var info = new FileInfo(path);
        var id = "u" + Guid.NewGuid().ToString("N")[..12];
        if (info.Length > maxFileBytes)
        {
            help?.Send(new { type = "xfer", id, name = info.Name, size = info.Length, dir = "down", state = "big", p = 0.0, limit = maxFileBytes });
            return;
        }
        var token = OutgoingFiles.Add(path);
        if (token is null) return;
        xfers[id] = (info.Name, info.Length, "up", topic);
        help?.Send(new { type = "xfer", id, name = info.Name, size = info.Length, dir = "down", state = "run", p = 0.0 });
        page!.Send(new { type = "sendUrl", id, topic, name = info.Name, size = info.Length, url = OutgoingFiles.Url(token), token });
    }

    /// Progress / result of a file going to ICT, from the page.
    private void XferOut(JsonNode m)
    {
        var id = m["id"]?.GetValue<string>() ?? "";
        if (!xfers.TryGetValue(id, out var x)) return;
        var state = m["state"]?.GetValue<string>() ?? "run";
        help?.Send(new { type = "xfer", id, name = x.Name, size = x.Size, dir = "down", state, p = m["p"]?.GetValue<double>() ?? 0 });
        if (state is "done" or "cancel" or "fail")
        {
            xfers.Remove(id);
            if (m["token"]?.GetValue<string>() is { } t) OutgoingFiles.Forget(t);
            if (state == "done") files.Add(x.Name);
        }
    }

    /// The user pressed Cancel or Retry on a row in the help window.
    internal void XferAction(string id, string action)
    {
        if (action == "cancel") page?.Send(new { type = "xferCancel", id, reason = "cancel" });
        if (action == "cancel" && xfers.TryGetValue(id, out var x) && x.Dir == "down") { incoming.Abort(id); xfers.Remove(id); XferShow(id, "cancel", 0, x); }
    }

    private void CloseFiles()
    {
        incoming.AbortAll();
        xfers.Clear();
        OutgoingFiles.Clear();
    }
}
