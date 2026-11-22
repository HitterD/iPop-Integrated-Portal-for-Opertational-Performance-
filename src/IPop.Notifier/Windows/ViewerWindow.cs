using System.Text.Json.Nodes;
using IPop.Notifier.Core;

namespace IPop.Notifier.Windows;

/// File viewer (1.5.0, mockup docs/mockups/notifier/priority.html screen 5): one dark window for every file of a
/// message, ← → between them. Opened by the bridge message "preview {files:[{name,path,size}], index}" from any page;
/// a second request reuses the open viewer. Page protocol (Ui/viewer.js):
///   app → page: files {files, index}; preparing {id} (an Office file is being converted on iPop);
///               loaded {id, kind: pdf|image|video|audio|office|none, url, error}
///   page → app: load {id, path, name}; plus download / reveal / openFile from <see cref="BridgedWindow"/>.
public sealed class ViewerWindow : BridgedWindow
{
    public static readonly Size ViewerSize = new(1180, 780);
    private JsonNode files;
    private int index;

    public ViewerWindow(IPopClient api, JsonNode files, int index) : base("viewer.html", WindowSizes.Get("viewer", ViewerSize), api)
    {
        this.files = Clean(files);
        this.index = index;
        Text = "iPop Notifier — Preview";
        SizeKey = "viewer";
        Maximizable = true;
        MinLogicalSize = new Size(720, 480);
        StartPosition = FormStartPosition.CenterScreen;
        var area = Screen.FromPoint(Cursor.Position).WorkingArea;
        var scale = DeviceDpi / 96.0;
        var size = WindowSizes.Get("viewer", ViewerSize);
        ClientSize = new Size(Math.Min((int)(size.Width * scale), area.Width - 24), Math.Min((int)(size.Height * scale), area.Height - 24));
    }

    /// Another Preview click while the viewer is open: same window, new files.
    public void Show(JsonNode next, int at)
    {
        files = Clean(next);
        index = at;
        Send(Payload());
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
    }

    /// Only names, allow-listed paths and sizes reach the page; at most 50 files.
    private static JsonArray Clean(JsonNode list)
    {
        var arr = new JsonArray();
        if (list is not JsonArray a) return arr;
        foreach (var f in a.Take(50))
        {
            var path = (f?["path"]?.GetValue<string>() ?? "").TrimStart('/');
            if (!ApiBridge.IsDownload(path)) continue;
            long size = 0;
            try { size = f?["size"]?.GetValue<long>() ?? 0; } catch { }
            arr.Add(new JsonObject
            {
                ["name"] = Downloads.CleanName(f?["name"]?.GetValue<string>()),
                ["path"] = path,
                ["size"] = size,
                ["from"] = f?["from"]?.GetValue<string>() is { Length: < 120 } from ? from : null,
            });
        }
        return arr;
    }

    private object Payload() => new { type = "files", files, index = Math.Clamp(index, 0, Math.Max(0, ((JsonArray)files).Count - 1)) };

    protected override void OnReady() => Send(Payload());

    protected override void HandleMessage(string type, JsonNode? msg)
    {
        if (type == "load" && msg is not null) { _ = LoadAsync(msg); return; }
        base.HandleMessage(type, msg);
    }

    private async Task LoadAsync(JsonNode msg)
    {
        var id = msg["id"]?.ToString() ?? "";
        var path = msg["path"]?.GetValue<string>() ?? "";
        var name = msg["name"]?.GetValue<string>() ?? "";
        PreviewCache.Result r;
        try { r = await PreviewCache.GetAsync(Api, path, name, () => { if (!IsDisposed) BeginInvoke(() => Send(new { type = "preparing", id })); }); }
        catch (Exception ex) { Log.Warn("viewer", ex.GetType().Name + " " + ex.Message); r = new("none", null, "Couldn't load the file."); }
        if (!IsDisposed) BeginInvoke(() => Send(new { type = "loaded", id, kind = r.Kind, url = r.Url, error = r.Error }));
    }
}
