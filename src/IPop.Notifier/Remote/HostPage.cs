using System.Text.Json.Nodes;

namespace IPop.Notifier.Remote;

/// Hidden window hosting the LiveKit page on the user's PC (moved out of RemoteHost, 1.8.0). With one monitor it
/// shares it through getDisplayMedia (no picker); with several it captures the whole desktop and crops it to the
/// monitor the helper picked.
internal sealed class HostPage : IPop.Notifier.Windows.HtmlWindow
{
    private readonly RemoteHost host;
    public event Action? Ready;

    public HostPage(RemoteHost host) : base("remote-host.html", new Size(320, 200), noActivate: true, showInTaskbar: false)
    {
        this.host = host;
        Opacity = 0;
        StartPosition = FormStartPosition.Manual;
        Location = new Point(-32000, -32000);
        Show();
    }

    protected override void OnReady() => Ready?.Invoke();

    protected override void HandleMessage(string type, JsonNode? msg)
    {
        if (type is "ready" or "drag" or "minimize" or "close" or "openUrl") { base.HandleMessage(type, msg); return; }
        host.FromPage(type, msg);
    }
}
