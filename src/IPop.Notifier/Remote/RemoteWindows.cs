using System.Text.Json.Nodes;
using IPop.Notifier.Core;
using IPop.Notifier.Windows;

namespace IPop.Notifier.Remote;

/// On the user's PC: first the Allow / Decline prompt, then "ICT is helping you" with the helper's media. Excluded from
/// screen capture, so the helper never sees it in the stream. Minimise from either side folds it to its title bar.
public sealed class HelpWindow : BridgedWindow
{
    private readonly RemoteHost host;
    private readonly bool asking;
    public event Action<bool>? Answered;
    private Size full;

    public HelpWindow(IPopClient api, RemoteHost host, bool asking)
        : base("remote-help.html", asking ? new Size(420, 300) : new Size(900, 600), api, noActivate: true, showInTaskbar: !asking)
    {
        this.host = host;
        this.asking = asking;
        Text = asking ? "iPop Notifier — Remote help request" : "ICT is helping you";
        TopMost = true;
        var area = Screen.PrimaryScreen!.WorkingArea;
        Location = asking
            ? new Point(area.Right - Width - 16, area.Bottom - Height - 16)
            : new Point(area.Left + (area.Width - Width) / 2, area.Top + (area.Height - Height) / 2);
        full = ClientSize;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        // Stays visible to the user, never in what the helper receives.
        SessionFrame.SetWindowDisplayAffinity(Handle, SessionFrame.WDA_EXCLUDEFROMCAPTURE);
    }

    /// Media bytes become a file in the app's temp folder served on the virtual host, so big videos stream from disk.
    public Task<string?> PutMediaAsync(byte[] bytes, string mime)
    {
        try
        {
            var dir = WebHost.MediaCache;
            Directory.CreateDirectory(dir);
            var name = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes))[..16] + (mime.StartsWith("video") ? ".mp4" : ".img");
            var path = Path.Combine(dir, name);
            if (!File.Exists(path)) File.WriteAllBytes(path, bytes);
            return Task.FromResult<string?>("https://media.notifier.ipop/" + name);
        }
        catch { return Task.FromResult<string?>(null); }
    }

    public void SetMinimised(bool min)
    {
        if (IsDisposed || asking) return;
        var area = Screen.PrimaryScreen!.WorkingArea;
        var scale = DeviceDpi / 96.0;
        if (min)
        {
            ClientSize = new Size((int)(340 * scale), (int)(46 * scale));
            Location = new Point(area.Right - Width - 16, area.Bottom - Height - 16);
        }
        else
        {
            ClientSize = full;
            Location = new Point(area.Left + (area.Width - Width) / 2, area.Top + (area.Height - Height) / 2);
        }
        Send(new { type = "min", min });
    }

    protected override void HandleMessage(string type, JsonNode? msg)
    {
        switch (type)
        {
            case "answer": Answered?.Invoke(msg?["allow"]?.GetValue<bool>() == true); break;
            case "mediaMin": SetMinimised(msg?["min"]?.GetValue<bool>() == true); break;
            case "chatSend": host.ChatFromUser(msg?["text"]?.GetValue<string>() ?? ""); break;
            case "endSession": _ = host.EndAsync("user"); break;
            case "close": break; // Esc must not end the session or hide the window
            default: base.HandleMessage(type, msg); break;
        }
    }
}

/// ICT side: one window per session with the user's live screen (mockup remote §1, left).
public sealed class ViewerWindow : BridgedWindow
{
    private readonly object start;
    private ClipWatcher? clip;
    private bool clipOn = true;
    public Guid SessionId { get; }

    public ViewerWindow(IPopClient api, Guid sessionId, object start) : base("remote-viewer.html", WindowSizes.Get("remote", new Size(1360, 860)), api)
    {
        SessionId = sessionId;
        this.start = start;
        SizeKey = "remote";
        Text = "iPop Remote help";
        StartPosition = FormStartPosition.CenterScreen;
        KeyPreview = false;
    }

    protected override void OnReady()
    {
        Send(start);
        clip = new ClipWatcher(OnClipboard);
    }

    public void Answer(string status) => Send(new { type = "answer", status });
    public void Ended(string? by) => Send(new { type = "remoteEnded", endedBy = by });

    private void OnClipboard()
    {
        if (!clipOn || !ContainsFocus) return; // only what the helper copies while working in this window
        var r = Clip.Read();
        if (r is not { } v) return;
        if (v.FileBytes is not null) Send(new { type = "clipFile", name = v.FileName, mime = "application/octet-stream", data = Convert.ToBase64String(v.FileBytes) });
        else if (v.Payload is not null) Send(new { type = "clipboard", kind = v.Payload["kind"]?.GetValue<string>(), text = v.Payload["text"]?.GetValue<string>(), data = v.Payload["data"]?.GetValue<string>() });
    }

    protected override async void HandleMessage(string type, JsonNode? msg)
    {
        switch (type)
        {
            case "maximize": WindowState = WindowState == FormWindowState.Maximized ? FormWindowState.Normal : FormWindowState.Maximized; break;
            case "setClipboard": Clip.Apply(msg!); break;
            case "setClipboardFile": Clip.SetFile(msg?["name"]?.GetValue<string>() ?? "file", Convert.FromBase64String(msg?["data"]?.GetValue<string>() ?? "")); break;
            case "clipSync": clipOn = msg?["on"]?.GetValue<bool>() == true; break;
            case "saveFile": Downloads.Save(msg?["name"]?.GetValue<string>() ?? "file", Convert.FromBase64String(msg?["data"]?.GetValue<string>() ?? "")); break;
            case "uploadMedia":
                var result = await UploadMediaAsync(msg);
                Send(new { type = "mediaUploaded", error = result });
                break;
            default: base.HandleMessage(type, msg); break;
        }
    }

    private async Task<string?> UploadMediaAsync(JsonNode? msg)
    {
        try
        {
            var bytes = Convert.FromBase64String(msg?["data"]?.GetValue<string>() ?? "");
            using var form = new MultipartFormDataContent();
            var part = new ByteArrayContent(bytes);
            part.Headers.ContentType = new(msg?["mime"]?.GetValue<string>() is { Length: > 0 } m ? m : "application/octet-stream");
            form.Add(part, "file", Path.GetFileName(msg?["name"]?.GetValue<string>() ?? "media"));
            using var res = await Api.SendContentAsync(HttpMethod.Post, "api/notifier/remote/media", form);
            if (res.IsSuccessStatusCode) return null;
            return JsonNode.Parse(await res.Content.ReadAsStringAsync())?["message"]?.GetValue<string>() ?? "Upload failed.";
        }
        catch { return "Upload failed."; }
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        clip?.Dispose();
        base.OnFormClosed(e);
    }
}
