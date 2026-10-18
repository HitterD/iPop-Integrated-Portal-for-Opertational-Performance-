namespace IPop.Notifier.Core;

/// Small rolling diagnostic log (%LOCALAPPDATA%\SantosJayaAbadi\iPopNotifier\logs\app.log, 1 MB × 2). Events only —
/// never message text, tokens or passwords — so ICT can read it on a pilot PC when something doesn't work.
public static class Log
{
    private static readonly object Gate = new();
    private const long MaxBytes = 1_000_000;
    private static string Dir => Path.Combine(SessionStore.Folder, "logs");
    public static string FilePath => Path.Combine(Dir, "app.log");

    /// Last 64 KB of the log, for "send diagnostics".
    public static string Tail()
    {
        lock (Gate)
        {
            try
            {
                var text = File.Exists(FilePath) ? File.ReadAllText(FilePath) : "";
                return text.Length > 60_000 ? text[^60_000..] : text;
            }
            catch { return ""; }
        }
    }

    public static async Task UploadAsync(IPopClient api)
    {
        try
        {
            using var content = new StringContent(Tail(), System.Text.Encoding.UTF8, "text/plain");
            using var _ = await api.SendContentAsync(HttpMethod.Post, "api/notifier/devices/log?machine=" + Uri.EscapeDataString(Environment.MachineName), content);
        }
        catch { }
    }

    public static void Info(string area, string text) => Write("INF", area, text);
    public static void Warn(string area, string text) => Write("WRN", area, text);
    public static void Error(string area, string text, Exception? ex = null) =>
        Write("ERR", area, ex is null ? text : $"{text} — {ex.GetType().Name}: {ex.Message}");

    private static void Write(string level, string area, string text)
    {
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(Dir);
                var info = new FileInfo(FilePath);
                if (info.Exists && info.Length > MaxBytes) File.Move(FilePath, FilePath + ".1", overwrite: true);
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {level} [{area}] {text}{Environment.NewLine}");
            }
            catch { }
        }
    }
}
