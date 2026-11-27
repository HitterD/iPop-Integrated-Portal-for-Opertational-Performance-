using System.Text.Json.Nodes;

namespace IPop.Notifier.Remote;

/// Hidden window hosting the LiveKit page on the user's PC. It shares one whole monitor through getDisplayMedia with
/// no picker: its WebView2 environment auto-selects <paramref name="source"/> ("Screen 2"…; hotfix 1.8.2), so picking
/// another monitor means a new HostPage in that monitor's environment.
internal sealed class HostPage : IPop.Notifier.Windows.HtmlWindow
{
    private readonly RemoteHost host;
    private readonly string source;
    public event Action? Ready;
    protected override string? CaptureSource => source;

    public HostPage(RemoteHost host, string source) : base("remote-host.html", new Size(320, 200), noActivate: true, showInTaskbar: false)
    {
        this.host = host;
        this.source = source;
        Opacity = 0;
        StartPosition = FormStartPosition.Manual;
        Location = new Point(-32000, -32000);
        Show();
    }

    protected override void OnReady() => Ready?.Invoke();

    protected override void HandleMessage(string type, JsonNode? msg)
    {
        if (type is "ready" or "drag" or "minimize" or "close" or "openUrl") { base.HandleMessage(type, msg); return; }
        host.FromPage(this, type, msg);
    }
}
