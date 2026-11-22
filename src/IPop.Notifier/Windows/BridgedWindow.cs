using System.Text.Json.Nodes;
using IPop.Notifier.Core;

namespace IPop.Notifier.Windows;

/// An HTML window that may call iPop through <see cref="ApiBridge"/> (allow-listed routes only).
public class BridgedWindow(string page, Size size, IPopClient api, bool noActivate = false, bool showInTaskbar = true)
    : HtmlWindow(page, size, noActivate, showInTaskbar)
{
    protected IPopClient Api { get; } = api;

    /// "preview {files:[{name,path,size}], index}" from any page: the app opens (or reuses) the one viewer window.
    public static event Action<JsonNode, int>? PreviewRequested;

    protected override void HandleMessage(string type, JsonNode? msg)
    {
        switch (type)
        {
            case "api" when msg is not null:
                _ = Reply(ApiBridge.CallAsync(Api, msg));
                break;
            case "image" when msg is not null:
                _ = Reply(ApiBridge.ImageAsync(Api, msg));
                break;
            case "openIPop":
                _ = Integration.OpenTargetAsync(Api, msg?["path"]?.GetValue<string>());
                break;
            case "explorer":
                var unc = msg?["unc"]?.GetValue<string>() ?? "";
                if (!Integration.OpenInExplorer(unc)) Send(new { type = "toast", text = "Only company NAS folders can be opened." });
                break;
            // 1.5.0 files: download {id, path, name} → downloadProgress {id, p} … downloadDone {id, file, name} |
            // downloadFailed {id, message}; reveal {file} (Show in folder); openFile {file} (Open).
            case "download" when msg is not null:
                _ = DownloadAsync(msg);
                break;
            case "reveal":
                if (!Downloads.Reveal(msg?["file"]?.GetValue<string>())) Send(new { type = "toast", text = "That file isn't in Downloads any more." });
                break;
            case "openFile":
                if (Downloads.Open(msg?["file"]?.GetValue<string>()) is { } why) Send(new { type = "toast", text = why });
                break;
            case "preview" when msg is not null:
                if (msg["files"] is JsonArray { Count: > 0 } files)
                {
                    var at = msg["index"] is JsonValue v && v.TryGetValue<int>(out var i) ? i : 0;
                    PreviewRequested?.Invoke(files.DeepClone(), at);
                }
                break;
            case "copy":
                try { Clipboard.SetText(msg?["text"]?.GetValue<string>() ?? ""); } catch { }
                break;
            default:
                base.HandleMessage(type, msg);
                break;
        }
    }

    private async Task DownloadAsync(JsonNode msg)
    {
        var id = msg["id"]?.ToString() ?? Guid.NewGuid().ToString("N");
        var path = msg["path"]?.GetValue<string>() ?? "";
        var name = msg["name"]?.GetValue<string>();
        var last = DateTime.MinValue;
        void Progress(int p)
        {
            // At most ~8 updates a second (plus 100 %), always on the UI thread.
            if (p < 100 && DateTime.UtcNow - last < TimeSpan.FromMilliseconds(120)) return;
            last = DateTime.UtcNow;
            if (!IsDisposed) BeginInvoke(() => Send(new { type = "downloadProgress", id, p }));
        }
        object result;
        try
        {
            var file = await Downloads.SaveAsync(Api, path, name, Progress);
            result = new { type = "downloadDone", id, file, name = Path.GetFileName(file) };
        }
        catch (DownloadException ex) { result = new { type = "downloadFailed", id, message = ex.Message }; }
        catch (Exception ex)
        {
            Log.Warn("download", ex.GetType().Name + " " + ex.Message);
            result = new { type = "downloadFailed", id, message = "Couldn't save the file in Downloads." };
        }
        if (!IsDisposed) BeginInvoke(() => Send(result));
    }

    private async Task Reply(Task<object> work)
    {
        var result = await work;
        if (!IsDisposed) BeginInvoke(() => Send(result));
    }
}
