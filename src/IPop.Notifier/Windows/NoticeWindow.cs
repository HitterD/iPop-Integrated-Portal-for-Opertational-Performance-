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

    /// Set for a notification from while the PC was off: "2 of 9 pending".
    public (int Index, int Total)? Pending { get; }

    public NoticeWindow(Notice notice, string style, IPopClient api, (int Index, int Total)? pending = null)
        : base("popup.html", style == "stack" ? new Size(440, 220) : WindowSizes.Get("popup", new Size(720, 560)), api, noActivate: true, showInTaskbar: style != "stack")
    {
        Notice = notice;
        Pending = pending;
        Style = style;
        WantedHeight = ClientSize.Height;
        Text = notice.Title.Length > 80 ? notice.Title[..80] + "…" : notice.Title;
        if (style == "stack") TopMost = true;
        else SizeKey = "popup";
    }

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
                var h = msg?["height"]?.GetValue<int>() ?? WantedHeight;
                var max = Style == "stack" ? 560 : 820;
                WantedHeight = Math.Clamp(LogicalToDevice(h), LogicalToDevice(120), LogicalToDevice(max));
                HeightChanged?.Invoke(this);
                break;
            case "done": // acted on (sent, joined, acknowledged…) or "Mark read": closes after its little animation
                Done?.Invoke(this, msg?["action"]?.GetValue<string>() ?? "done");
                Close();
                break;
            case "focus":
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
