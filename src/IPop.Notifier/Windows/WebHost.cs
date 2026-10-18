using System.Text.Json;
using System.Text.Json.Nodes;
using IPop.Notifier.Core;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace IPop.Notifier.Windows;

/// One WebView2 environment for every window of the app (shared browser process = light, even with 10+ windows).
/// UI files are served from the app folder at https://notifier.ipop/ — never from the network.
public static class WebHost
{
    public const string Origin = "https://notifier.ipop/";
    public const string HttpOrigin = "http://notifier.ipop/";
    public static string MediaCache => Path.Combine(SessionStore.Folder, "media-cache");
    private static Task<CoreWebView2Environment>? env;

    public static Task<CoreWebView2Environment> EnvironmentAsync() => env ??= CoreWebView2Environment.CreateAsync(null,
        Path.Combine(SessionStore.Folder, "WebView2"),
        new CoreWebView2EnvironmentOptions("--disable-features=msSmartScreenProtection --renderer-process-limit=3 --auto-select-desktop-capture-source=\"Entire screen\" --unsafely-treat-insecure-origin-as-secure=http://notifier.ipop,http://media.notifier.ipop" + QaDebugPort()));

    private static string QaDebugPort()
    {
#if DEBUG
        // QA only (Debug builds): lets Playwright attach over CDP to drive the windows.
        if (int.TryParse(Environment.GetEnvironmentVariable("IPOP_NOTIFIER_QA_CDP"), out var port) && port is > 1024 and < 65536)
            return $" --remote-debugging-port={port}";
#endif
        return "";
    }

    public static bool RuntimeMissing()
    {
        try { return string.IsNullOrEmpty(CoreWebView2Environment.GetAvailableBrowserVersionString()); }
        catch { return true; }
    }

    public static async Task InitAsync(WebView2 view, string page, Action<string, JsonNode?> onMessage)
    {
        view.DefaultBackgroundColor = Color.Transparent;
        await view.EnsureCoreWebView2Async(await EnvironmentAsync());
        var core = view.CoreWebView2;
        core.SetVirtualHostNameToFolderMapping("notifier.ipop", AppInfo.BaseDir, CoreWebView2HostResourceAccessKind.DenyCors);
        // Remote-help media (pictures/videos from the helper), cached in the data folder; read-only to pages.
        Directory.CreateDirectory(MediaCache);
        core.SetVirtualHostNameToFolderMapping("media.notifier.ipop", MediaCache, CoreWebView2HostResourceAccessKind.DenyCors);
        var s = core.Settings;
        s.AreDevToolsEnabled = false;
#if DEBUG
        if (Environment.GetEnvironmentVariable("IPOP_NOTIFIER_QA_CDP") is { Length: > 0 }) s.AreDevToolsEnabled = true;
#endif
        s.AreDefaultContextMenusEnabled = false;
        s.IsStatusBarEnabled = false;
        s.IsZoomControlEnabled = false;
        s.IsPinchZoomEnabled = false;
        s.AreBrowserAcceleratorKeysEnabled = false;
        s.IsPasswordAutosaveEnabled = false;
        s.IsGeneralAutofillEnabled = false;
        // Links never navigate the window: anything that isn't our own page opens in the browser.
        core.NavigationStarting += (_, e) =>
        {
            if (e.Uri.StartsWith(Origin, StringComparison.OrdinalIgnoreCase) || e.Uri.StartsWith(HttpOrigin, StringComparison.OrdinalIgnoreCase)) return;
            e.Cancel = true;
            Integration.OpenUrl(e.Uri);
        };
        core.NewWindowRequested += (_, e) => { e.Handled = true; Integration.OpenUrl(e.Uri); };
        // Remote help: the hidden host page shares the screen without Chrome's picker (the user already said Allow
        // in our own prompt); every other page is refused.
        core.PermissionRequested += (_, e) => e.State = page == "remote-host.html" ? CoreWebView2PermissionState.Allow : CoreWebView2PermissionState.Deny;
        core.ScreenCaptureStarting += (_, e) => { e.Handled = true; e.Cancel = page != "remote-host.html"; };
        core.WebMessageReceived += (_, e) =>
        {
            JsonNode? msg;
            try { msg = JsonNode.Parse(e.WebMessageAsJson); } catch { return; }
            var type = msg?["type"]?.GetValue<string>();
            if (type is not null) onMessage(type, msg);
        };
        // Remote pages talk to LiveKit: a plain ws:// LiveKit on the LAN is only allowed from an http page (mixed content),
        // so they load over http on the same virtual host, which the environment flags as a secure context (screen
        // capture needs one). Only our own virtual hosts get that treatment; nothing from the network.
        core.Navigate((page.StartsWith("remote-") ? HttpOrigin : Origin) + "Ui/" + page);
    }

    public static void Post(WebView2 view, object message)
    {
        try { view.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(message, IPopClient.Json)); } catch { }
    }
}
