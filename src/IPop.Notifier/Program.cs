using IPop.Notifier.Core;
using IPop.Notifier.Windows;
using Velopack;

namespace IPop.Notifier;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        // Velopack first: install/update/uninstall hooks run and exit here (per-user install, no admin).
        VelopackApp.Build()
            .OnAfterInstallFastCallback(_ => { Integration.RegisterUrlScheme(); Integration.SetStartWithWindows(true); })
            .OnBeforeUninstallFastCallback(_ =>
            {
                Integration.UnregisterUrlScheme();
                Integration.SetStartWithWindows(false);
                // Signed-out on uninstall: the encrypted session, settings cache and WebView2 cache go too.
                try { Directory.Delete(SessionStore.Folder, recursive: true); } catch { }
            })
            .Run();

        // One app per Windows user: a second start (autostart + click, or an ipop-notifier:// link) hands its
        // arguments to the running one and exits.
        using var single = new SingleInstance("iPopNotifier-" + Environment.UserName);
        if (!single.IsFirst)
        {
            single.SendToFirst(args.Length > 0 ? string.Join(' ', args) : "show");
            return;
        }

        Native.SetCurrentProcessExplicitAppUserModelID(AppInfo.AppUserModelId);
        ApplicationConfiguration.Initialize();
        System.Windows.Forms.Application.SetColorMode(SystemColorMode.System);

        var app = new NotifierApp(args);
        single.Received += line => app.Post(() => app.HandleCommand(line));
        single.Listen();
        System.Windows.Forms.Application.Run(app);
    }
}
