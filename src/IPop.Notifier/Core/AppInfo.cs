using System.Reflection;

namespace IPop.Notifier.Core;

public static class AppInfo
{
    /// The production iPop (API and Web on one origin). The sign-in window lets ICT point a test PC elsewhere.
    public const string DefaultServer = "https://ipop.santos.co.id/";
    /// Taskbar identity shared by every notification window, so Windows groups them under one iPop icon (mockup v2).
    /// Velopack stamps its Start menu shortcut with "velopack.{packId}"; using the same id makes toasts show
    /// "iPop Notifier" with the shortcut's icon.
    public const string AppUserModelId = "velopack.iPopNotifier";
    public const string UrlScheme = "ipop-notifier";
    /// iPop Files' existing "Open in File Explorer" link (ipop://open?server=…&amp;path=…), now answered by this app.
    public const string FilesScheme = "ipop";

    public static string Version { get; } =
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "1.0.0";

    public static string BaseDir => AppContext.BaseDirectory;
    public static string UiDir => Path.Combine(BaseDir, "Ui");
    public static string AssetsDir => Path.Combine(BaseDir, "Assets");
}
