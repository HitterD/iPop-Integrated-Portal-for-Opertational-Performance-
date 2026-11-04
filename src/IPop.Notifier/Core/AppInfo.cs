using System.Reflection;

namespace IPop.Notifier.Core;

public static class AppInfo
{
    /// The production iPop (API and Web on one origin). The sign-in window lets ICT point a test PC elsewhere.
#if TRIAL
    /// Trial build (release.ps1 -Trial): the QA copy of iPop on the build PC, never production.
    /// CI builds the QA flavour for the cluster's staging instead (-p:NotifierServer=…, -p:NotifierUpdateFeed=none):
    /// that server and its own /download/notifier/ feed.
    public static readonly string DefaultServer = Meta("NotifierServer") ?? "http://10.10.6.13:6391/";
    public const string Flavour = "QA";
    /// Trial installers and updates come from the build PC, not the iPop server.
    public static readonly string? UpdateFeed = Meta("NotifierUpdateFeed") is { } feed ? (feed == "none" ? null : feed) : "http://10.10.6.13:6393/";
#else
    public const string DefaultServer = "https://ipop.santos.co.id/";
    public const string Flavour = "";
    public const string? UpdateFeed = null;
#endif

    private static string? Meta(string key) =>
        Assembly.GetExecutingAssembly().GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == key)?.Value is { Length: > 0 } v ? v : null;
    /// Taskbar identity shared by every notification window, so Windows groups them under one iPop icon (mockup v2).
    /// Velopack stamps its Start menu shortcut with "velopack.{packId}"; using the same id makes toasts show
    /// "iPop Notifier" with the shortcut's icon.
#if TRIAL
    public const string AppUserModelId = "velopack.iPopNotifierQA";
#else
    public const string AppUserModelId = "velopack.iPopNotifier";
#endif
    public static string UrlScheme => Flavour.Length > 0
#if DEBUG
        || Environment.GetEnvironmentVariable("IPOP_NOTIFIER_QA_DIR") is { Length: > 0 }
#endif
        ? "ipop-notifier-qa" : "ipop-notifier";
    /// iPop Files' existing "Open in File Explorer" link (ipop://open?server=…&amp;path=…), now answered by this app.
    public const string FilesScheme = "ipop";

    public static string Version { get; } =
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "1.0.0";

    public static string BaseDir => AppContext.BaseDirectory;
    public static string UiDir => Path.Combine(BaseDir, "Ui");
    public static string AssetsDir => Path.Combine(BaseDir, "Assets");
}
