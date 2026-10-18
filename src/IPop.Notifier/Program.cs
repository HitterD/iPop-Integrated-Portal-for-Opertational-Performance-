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
        var instance = "iPopNotifier" + AppInfo.Flavour + "-" + Environment.UserName;
#if DEBUG
        // QA runs two copies side by side (helper + host): each throwaway profile is its own instance.
        if (Environment.GetEnvironmentVariable("IPOP_NOTIFIER_QA_DIR") is { Length: > 0 } qaDir)
            instance += "-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(qaDir)))[..8];
#endif
        using var single = new SingleInstance(instance);
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
