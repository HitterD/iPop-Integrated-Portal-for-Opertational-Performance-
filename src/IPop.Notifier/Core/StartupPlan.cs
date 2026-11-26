namespace IPop.Notifier.Core;

/// What the Notifier does when it starts. Whether to sign in depends only on the saved session: a restart after an
/// update (Velopack passes --updated), a start from the Start menu and the Windows autostart (--background) all keep
/// the session, so none of them shows the sign-in window. Opening iPop at login is the autostart's own, separate step.
public static class StartupPlan
{
    /// StartSession: connect with the saved session. ShowSignIn: the sign-in window (signed out only).
    /// BrowserSignIn: also open the browser sign-in at once (first run after install). OpenIPopAtLogin: the
    /// "Open iPop when Windows starts" step (IPopAppLauncher.RunAtLoginAsync), started by Windows only.
    public sealed record Plan(bool StartSession, bool ShowSignIn, bool BrowserSignIn, bool OpenIPopAtLogin);

    public static Plan Decide(IReadOnlyCollection<string> args, bool signedIn) => new(
        StartSession: signedIn,
        ShowSignIn: !signedIn,
        BrowserSignIn: !signedIn && args.Contains("--first-run"),
        OpenIPopAtLogin: args.Contains("--background"));
}
