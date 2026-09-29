namespace IPop.Web.Components.Files;

/// <summary>
/// What a file is, for icons, colours and the type filter. Colours live in css/v6/files.css as
/// <c>.fk-{kind}</c> so the Finder palette (muted, one hue per family) is themeable in one place
/// instead of inline hex per row.
/// </summary>
public static class FileKinds
{
    public static string Ext(string name) => System.IO.Path.GetExtension(name).ToLowerInvariant();

    public static string Kind(string name, bool isDir = false) => isDir ? "folder" : Ext(name) switch
    {
        ".pdf" => "pdf",
        ".xls" or ".xlsx" or ".csv" => "sheet",
        ".doc" or ".docx" or ".rtf" or ".odt" => "doc",
        ".ppt" or ".pptx" => "slide",
        ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".bmp" or ".svg" or ".heic" => "image",
        ".zip" or ".rar" or ".7z" or ".tar" or ".gz" => "archive",
        ".mp4" or ".mov" or ".avi" or ".mkv" => "video",
        ".mp3" or ".wav" or ".ogg" or ".m4a" => "audio",
        ".dwg" or ".dxf" or ".step" or ".stp" => "cad",
        ".txt" or ".log" or ".json" or ".xml" or ".md" => "text",
        _ => "other",
    };

    public static string Icon(string name, bool isDir = false) => Kind(name, isDir) switch
    {
        "folder" => "folder",
        "pdf" => "file-text",
        "sheet" => "file-spreadsheet",
        "doc" => "file-text",
        "slide" => "presentation",
        "image" => "image",
        "archive" => "file-archive",
        "video" => "file-video",
        "audio" => "file-audio",
        "cad" => "drafting-compass",
        "text" => "file-code",
        _ => "file",
    };

    /// <summary>Finder's "Kind" column: words, not extensions.</summary>
    public static string Label(string name, bool isDir = false) => Kind(name, isDir) switch
    {
        "folder" => "Folder",
        "pdf" => "PDF document",
        "sheet" => Ext(name) == ".csv" ? "CSV" : "Excel workbook",
        "doc" => "Word document",
        "slide" => "Presentation",
        "image" => $"{Ext(name).TrimStart('.').ToUpperInvariant()} image",
        "archive" => "Archive",
        "video" => "Video",
        "audio" => "Audio",
        "cad" => "CAD drawing",
        "text" => "Text",
        _ => string.IsNullOrEmpty(Ext(name)) ? "Document" : $"{Ext(name).TrimStart('.').ToUpperInvariant()} file",
    };

    /// <summary>Type-filter buckets offered in the toolbar.</summary>
    public static bool Matches(string name, bool isDir, string filter) => filter switch
    {
        "" => true,
        "folder" => isDir,
        "other" => !isDir && Kind(name) is "other" or "text" or "cad" or "video" or "audio",
        _ => !isDir && Kind(name) == filter,
    };

    public static string Size(long b) => b switch
    {
        < 1024 => $"{b} B",
        < 1024 * 1024 => $"{b / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{b / 1024.0 / 1024:0.#} MB",
        _ => $"{b / 1024.0 / 1024 / 1024:0.##} GB",
    };

    /// <summary>Finder dates: "Today 14:05", "Yesterday 09:12", "12 Sep 2026".</summary>
    public static string When(long unix)
    {
        if (unix == 0) return "—";
        var t = DateTimeOffset.FromUnixTimeSeconds(unix).ToLocalTime();
        var today = DateTime.Today;
        if (t.Date == today) return $"Today {t:HH:mm}";
        if (t.Date == today.AddDays(-1)) return $"Yesterday {t:HH:mm}";
        return t.Year == today.Year ? t.ToString("d MMM, HH:mm") : t.ToString("d MMM yyyy");
    }
}
