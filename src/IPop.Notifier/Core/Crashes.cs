namespace IPop.Notifier.Core;

/// Hotfix 1.8.1: nothing may reach the person as the raw ".NET Unhandled exception … Continue / Quit" dialog (1.8.0
/// showed one with 0x8007139F from WebView2). Every unhandled error is written to the log that is uploaded to iPop,
/// with its stack, and the app keeps running; the window that failed shows its own friendly state.
public static class Crashes
{
    /// The last unhandled error, for the tray's "something went wrong" note (null = none since start).
    public static string? Last { get; private set; }
    public static event Action<Exception>? Caught;

    public static void Install()
    {
        System.Windows.Forms.Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        System.Windows.Forms.Application.ThreadException += (_, e) => Report("ui", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => { if (e.ExceptionObject is Exception ex) Report("fatal", ex); };
        TaskScheduler.UnobservedTaskException += (_, e) => { Report("task", e.Exception); e.SetObserved(); };
    }

    public static void Report(string where, Exception ex)
    {
        Last = $"{ex.GetType().Name}: {ex.Message}";
        // The full stack (no message text or tokens are in exception stacks of this app).
        Log.Error("crash", $"unhandled ({where}) {ex}");
        try { Caught?.Invoke(ex); } catch { }
    }
}
