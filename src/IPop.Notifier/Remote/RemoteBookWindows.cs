using System.Text.Json.Nodes;
using IPop.Notifier.Core;
using IPop.Notifier.Windows;

namespace IPop.Notifier.Remote;

/// The Remote window (1.8.0, mockup ipop-remote-v2 prop-remote): connect by 9-digit ID / PC name, address book.
public sealed class RemoteBookWindow : BridgedWindow
{
    public static readonly Size BookSize = new(1100, 720);
    /// (device id, typed target): exactly one is set.
    public event Action<Guid?, string?>? ConnectRequested;
    private readonly string? me;

    public RemoteBookWindow(IPopClient api, string? displayName) : base("remote-book.html", BookSize, api)
    {
        me = displayName;
        Text = "iPop Notifier — Remote";
        FitStandardSize(BookSize);
        Maximizable = true;
        StartPosition = FormStartPosition.CenterScreen;
    }

    protected override void OnReady() => Send(new { type = "who", name = me });

    public void Toast(string text) => Send(new { type = "toast", text });

    protected override void HandleMessage(string type, JsonNode? msg)
    {
        if (type != "connect") { base.HandleMessage(type, msg); return; }
        if (Guid.TryParse(msg?["deviceId"]?.GetValue<string>(), out var id)) ConnectRequested?.Invoke(id, null);
        else if (msg?["target"]?.GetValue<string>() is { Length: > 0 and <= 80 } t) ConnectRequested?.Invoke(null, t);
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

    public void Present(string? machine, string? remoteId, string? helper, int? triesLeft, bool wrong) =>
        Send(new { type = "show", machine, remoteId, helper, triesLeft, wrong });

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
