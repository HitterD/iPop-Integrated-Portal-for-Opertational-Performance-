using System.Text.Json.Nodes;
using IPop.Notifier.Core;
using IPop.Notifier.Windows;

namespace IPop.Notifier.Remote;

/// The Remote window (1.8.0, mockup ipop-remote-v2 prop-remote): connect by 9-digit ID / PC name, address book.
public sealed class RemoteBookWindow : BridgedWindow
{
    public static readonly Size BookSize = new(1100, 720);
    /// (device id, typed target, join view-only): one of the first two is set.
    public event Action<Guid?, string?, bool>? ConnectRequested;
    private readonly string? me;

    public RemoteBookWindow(IPopClient api, string? displayName) : base("remote-book.html", BookSize, api)
    {
        me = displayName;
        Text = "iPop Notifier — Remote";
        FitStandardSize(BookSize);
        Maximizable = true;
        StartPosition = FormStartPosition.CenterScreen;
    }

    protected override async void OnReady()
    {
        var info = await Api.GetJsonAsync("api/notifier/me");
        Send(new { type = "who", name = me, isAdmin = info?["isAdmin"]?.GetValue<bool>() == true });
    }

    public void Toast(string text) => Send(new { type = "toast", text });
    /// A connect the company policy stopped (Remote off, blocked, excluded, too many…): the red banner here.
    public void Banner(object b) { Send(new { type = "banner", b }); Activate(); }
    /// Someone else is helping that PC: "Join view-only" or Cancel.
    public void Busy(object b) { Send(new { type = "busy", b }); Activate(); }

    protected override void HandleMessage(string type, JsonNode? msg)
    {
        if (type != "connect") { base.HandleMessage(type, msg); return; }
        var join = msg?["joinViewOnly"]?.GetValue<bool>() == true;
        if (Guid.TryParse(msg?["deviceId"]?.GetValue<string>(), out var id)) ConnectRequested?.Invoke(id, null, join);
        else if (msg?["target"]?.GetValue<string>() is { Length: > 0 and <= 80 } t) ConnectRequested?.Invoke(null, t, join);
    }
}

/// "Enter access password" for an unattended PC (1.8.0, mockup prop-password), 440×460. The password is handed to the
/// app for this one connect; iPop checks it. Wrong → the same window shows "Wrong password" with the tries left.
public sealed class RemotePasswordWindow : IPop.Notifier.Windows.HtmlWindow
{
    public static readonly Size DialogSize = new(440, 460);
    public event Action<string, bool>? Submitted;
    public event Action? AskUser;

    public RemotePasswordWindow() : base("remote-password.html", DialogSize)
    {
        Text = "iPop Notifier — Remote access password";
        FitStandardSize(DialogSize);
        StartPosition = FormStartPosition.CenterScreen;
        TopMost = true;
    }

    public void Present(string? machine, string? remoteId, string? helper, int? triesLeft, bool wrong, int lockMinutes = 15) =>
        Send(new { type = "show", machine, remoteId, helper, triesLeft, wrong, lockMinutes });

    protected override void HandleMessage(string type, JsonNode? msg)
    {
        switch (type)
        {
            case "password": Submitted?.Invoke(msg?["password"]?.GetValue<string>() ?? "", msg?["remember"]?.GetValue<bool>() != false); break;
            case "askUser": AskUser?.Invoke(); break;
            default: base.HandleMessage(type, msg); break;
        }
    }
}
