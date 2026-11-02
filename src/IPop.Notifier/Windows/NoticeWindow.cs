using System.Text.Json.Nodes;
using IPop.Notifier.Core;

namespace IPop.Notifier.Windows;

/// One notification = one window. Style "windows" (mockup v2): a real window with title bar, toolbar and full
/// message, grouped under the iPop taskbar icon. Style "stack" (mockup v1): a glass card stacked at the screen corner,
/// not on the taskbar. The same page (Ui/popup.html) renders both.
public sealed class NoticeWindow : BridgedWindow
{
    public Notice Notice { get; }
    public string Style { get; }
    public event Action<NoticeWindow, string>? Done;
    public event Action<NoticeWindow>? HeightChanged;
    public int WantedHeight { get; private set; }

    /// 1.4.0: every popup window opens at this one size, for every module (docs/mockups/notifier/aligned.html §1).
    /// Not remembered: a resized or maximised popup doesn't change the next one. Also the smallest it can be resized to.
    public static readonly Size StandardSize = new(440, 560);
    /// v1 stacked card: fixed width, height follows the content.
    public static readonly Size CardSize = new(440, 220);

    /// Set for a notification from while the PC was off: "2 of 9 pending".
    public (int Index, int Total)? Pending { get; }

    public NoticeWindow(Notice notice, string style, IPopClient api, (int Index, int Total)? pending = null)
        : base("popup.html", style == "stack" ? CardSize : StandardSize, api, noActivate: true, showInTaskbar: style != "stack")
    {
        Notice = notice;
        Pending = pending;
        Style = style;
        // Sizes are logical (96 dpi); the form doesn't auto-scale, so scale to this screen's DPI here.
        if (style != "stack") ClientSize = new Size(Scale(StandardSize.Width), Scale(StandardSize.Height));
        WantedHeight = ClientSize.Height;
        Text = notice.Title.Length > 80 ? notice.Title[..80] + "…" : notice.Title;
        // Every popup stays in front of all other apps (user 2026-11-02: never behind another window). It still opens
        // without taking the keyboard (ShowWithoutActivation). Maximised "read full" is a normal window again.
        TopMost = true;
        if (style != "stack") Maximizable = true; // fixed 440×560; only Maximize changes the size
    }

    protected override void OnWindowStateChanged() => TopMost = !Maximized;

    private int more;
    /// v1: "+N more waiting" on the newest card (kept until the page asks, so a fresh card shows it too).
    public void SetMore(int count)
    {
        more = count;
        Send(new { type = "more", count });
    }

    protected override void OnReady() => Send(new
    {
        type = "notice",
        style = Style,
        server = Api.Server.ToString(),
        notice = Notice,
        me = Api.Session?.Name,
        nik = Api.Session?.Nik,
        more,
        pending = Pending is { } p ? new { index = p.Index, total = p.Total } : null,
    });

    protected override void HandleMessage(string type, JsonNode? msg)
    {
        switch (type)
        {
            case "size":
                // Window style: one fixed size (only the message area scrolls). Stacked card: height follows the content.
                if (Style != "stack") break;
                var h = msg?["height"]?.GetValue<int>() ?? WantedHeight;
                WantedHeight = Math.Clamp(LogicalToDevice(h), LogicalToDevice(120), LogicalToDevice(560));
                HeightChanged?.Invoke(this);
                break;
            case "done": // acted on (sent, joined, acknowledged…) or "Mark read": closes after its little animation
                Done?.Invoke(this, msg?["action"]?.GetValue<string>() ?? "done");
                Close();
                break;
            case "focus":
                Activate();
                break;
            case "readFull": // "Read full message": the comfortable maximised view shows everything inline
                if (Style == "stack") break;
                if (WindowState != FormWindowState.Maximized) WindowState = FormWindowState.Maximized;
                Activate();
                break;
            case "upload" when msg is not null:
                _ = UploadAsync(msg);
                break;
            default:
                base.HandleMessage(type, msg);
                break;
        }
    }

    /// Mail reply attachment: same upload as iPop's composer (/api/mail/attachments/upload), returns its id.
    private async Task UploadAsync(JsonNode msg)
    {
        var reference = msg["ref"]?.GetValue<double>() ?? 0;
        object result;
        try
        {
            var bytes = Convert.FromBase64String(msg["data"]?.GetValue<string>() ?? "");
            var name = Path.GetFileName(msg["name"]?.GetValue<string>() ?? "file");
            using var form = new MultipartFormDataContent();
            var part = new ByteArrayContent(bytes);
            part.Headers.ContentType = new(msg["mime"]?.GetValue<string>() is { Length: > 0 and < 120 } m && m.Contains('/') ? m : "application/octet-stream");
            form.Add(part, "files", name);
            using var res = await Api.SendContentAsync(HttpMethod.Post, "api/mail/attachments/upload", form);
            var json = JsonNode.Parse(await res.Content.ReadAsStringAsync());
            result = res.IsSuccessStatusCode && json is JsonArray { Count: > 0 } arr
                ? new { type = "uploadResult", @ref = reference, id = arr[0]?["id"]?.GetValue<Guid>(), error = (string?)null }
                : new { type = "uploadResult", @ref = reference, id = (Guid?)null, error = json?["message"]?.GetValue<string>() };
        }
        catch { result = new { type = "uploadResult", @ref = reference, id = (Guid?)null, error = (string?)null }; }
        if (!IsDisposed) BeginInvoke(() => Send(result));
    }

    private int LogicalToDevice(int v) => (int)Math.Round(v * DeviceDpi / 96.0);
}
