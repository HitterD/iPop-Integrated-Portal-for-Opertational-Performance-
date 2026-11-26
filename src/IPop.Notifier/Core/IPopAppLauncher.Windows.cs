using System.Diagnostics;

namespace IPop.Notifier.Core;

/// The Windows side of <see cref="IPopAppLauncher"/>: wait, check the connection, look for an open iPop, start it.
public static partial class IPopAppLauncher
{
    /// How long to wait for the connection after <see cref="Delay"/> before giving up (offline at login = skipped).
    private static readonly TimeSpan OnlineWait = TimeSpan.FromMinutes(2);

    /// Runs once per Windows start (the --background autostart). <paramref name="enabled"/> and
    /// <paramref name="online"/> are read when the time comes, so a setting changed in the meantime counts.
    public static async Task RunAtLoginAsync(Func<bool> enabled, Func<bool> signedIn, Func<bool> online, Func<Uri> web, CancellationToken ct)
    {
        try
        {
            await Task.Delay(Delay, ct);
            for (var waited = TimeSpan.Zero; !online() && waited < OnlineWait; waited += TimeSpan.FromSeconds(5))
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
            if (!enabled() || !signedIn()) { Log.Info("open-at-login", "skipped (off or signed out)"); return; }
            if (!online()) { Log.Info("open-at-login", "skipped (offline)"); return; }
            var outcome = Decide(WindowsEnv, web());
            Log.Info("open-at-login", LogWord(outcome));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log.Warn("open-at-login", "failed: " + ex.GetType().Name + " " + ex.Message); }
    }

    private static readonly Env WindowsEnv = new(
        AlreadyOpen: BrowserTabs.AnyIPopOpen,
        AppCommand: IPopLinks.AppCommand,
        StartApp: (file, args) =>
        {
            try { Process.Start(new ProcessStartInfo(file, args) { UseShellExecute = false }); return true; }
            catch (Exception ex) { Log.Warn("open-at-login", "iPop app launch failed: " + ex.Message); return false; }
        },
        OpenBrowser: url =>
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); return true; }
            catch { return false; }
        });
}
