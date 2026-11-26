namespace IPop.Notifier.Core;

/// "Open iPop when Windows starts" (per PC, default on): after a Windows sign-in the Notifier opens iPop once —
/// the installed iPop app when there is one, else the browser. Never a second copy: an iPop window or tab that is
/// already open wins. The decision is pure (tested in IPop.Tests); the Windows parts are in IPopAppLauncher.Windows.cs.
public static partial class IPopAppLauncher
{
    public enum Outcome { Skipped, App, Browser }

    /// How long after start before looking (explorer, network and the browser's own startup apps settle first).
    public static readonly TimeSpan Delay = TimeSpan.FromSeconds(15);
    /// The page iPop opens on.
    public const string StartPage = "/dashboard";

    /// What the decision needs from Windows. AlreadyOpen: an iPop app window or browser tab is open. AppCommand: the
    /// installed app's launch command (IPopLinks.AppCommand) or null. StartApp / OpenBrowser: true when started.
    public sealed record Env(Func<bool> AlreadyOpen, Func<string?> AppCommand, Func<string, string, bool> StartApp, Func<string, bool> OpenBrowser);

    /// Skip when iPop is already open; else the app (web+ipop://open?to=/dashboard); else the browser hand-off page
    /// (https://ipop.santos.co.id/open.html?to=/dashboard), which also covers an app that failed to start.
    public static Outcome Decide(Env env, Uri web)
    {
        if (env.AlreadyOpen()) return Outcome.Skipped;
        if (env.AppCommand() is { } command && IPopLinks.ParseCommand(command, IPopLinks.AppUrl(StartPage)) is { } launch
            && env.StartApp(launch.File, launch.Args))
            return Outcome.App;
        return env.OpenBrowser(IPopLinks.HandoffUrl(web, StartPage)) ? Outcome.Browser : Outcome.Skipped;
    }

    /// The log word for an outcome ("open-at-login: app|browser|skipped").
    public static string LogWord(Outcome o) => o switch { Outcome.App => "app", Outcome.Browser => "browser", _ => "skipped" };
}
