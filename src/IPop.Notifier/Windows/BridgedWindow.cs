using System.Text.Json.Nodes;
using IPop.Notifier.Core;

namespace IPop.Notifier.Windows;

/// An HTML window that may call iPop through <see cref="ApiBridge"/> (allow-listed routes only).
public class BridgedWindow(string page, Size size, IPopClient api, bool noActivate = false, bool showInTaskbar = true)
    : HtmlWindow(page, size, noActivate, showInTaskbar)
{
    protected IPopClient Api { get; } = api;

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
            case "copy":
                try { Clipboard.SetText(msg?["text"]?.GetValue<string>() ?? ""); } catch { }
                break;
            default:
                base.HandleMessage(type, msg);
                break;
        }
    }

    private async Task Reply(Task<object> work)
    {
        var result = await work;
        if (!IsDisposed) BeginInvoke(() => Send(result));
    }
}
