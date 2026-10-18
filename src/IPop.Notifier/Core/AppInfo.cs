using System.Reflection;

namespace IPop.Notifier.Core;

public static class AppInfo
{
    /// The production iPop (API and Web on one origin). The sign-in window lets ICT point a test PC elsewhere.
    public const string DefaultServer = "https://ipop.santos.co.id/";
    /// Taskbar identity shared by every notification window, so Windows groups them under one iPop icon (mockup v2).
    public const string AppUserModelId = "SantosJayaAbadi.iPopNotifier";
    public const string UrlScheme = "ipop-notifier";

    public static string Version { get; } =
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "1.0.0";

    public static string BaseDir => AppContext.BaseDirectory;
    public static string UiDir => Path.Combine(BaseDir, "Ui");
    public static string AssetsDir => Path.Combine(BaseDir, "Assets");
}
