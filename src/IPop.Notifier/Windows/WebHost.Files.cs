using IPop.Notifier.Remote;
using Microsoft.Web.WebView2.Core;

namespace IPop.Notifier.Windows;

/// http://files.notifier.ipop/{token}: the files a person picked to send during remote help, streamed from disk
/// (OutgoingFiles). Unknown tokens get 404; nothing else on the disk is reachable.
public static partial class WebHost
{
    private static void ServeOutgoingFiles(CoreWebView2 core)
    {
        core.AddWebResourceRequestedFilter($"http://{OutgoingFiles.Host}/*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += (_, e) =>
        {
            if (!Uri.TryCreate(e.Request.Uri, UriKind.Absolute, out var u) || u.Host != OutgoingFiles.Host) return;
            var env = core.Environment;
            if (OutgoingFiles.Resolve(u.AbsolutePath) is not { } path)
            {
                e.Response = env.CreateWebResourceResponse(null, 404, "Not Found", "");
                return;
            }
            try
            {
                var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 81920, FileOptions.SequentialScan);
                e.Response = env.CreateWebResourceResponse(fs, 200, "OK", $"Content-Type: application/octet-stream\r\nContent-Length: {fs.Length}");
            }
            catch { e.Response = env.CreateWebResourceResponse(null, 404, "Not Found", ""); }
        };
    }
}
