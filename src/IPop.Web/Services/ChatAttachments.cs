using IPop.Application.Chat;
using Microsoft.JSInterop;

namespace IPop.Web.Services;

/// What the UI should *do* with an attachment, derived from MIME + filename.
public enum AttachmentKind
{
    Image,
    Video,
    Audio,
    Pdf,
    Sheet,      // xlsx / csv — RadzenSpreadsheet
    LegacySheet,// xls — SheetJS
    Doc,        // docx — docx-preview
    LegacyDoc,  // doc / rtf / odt — no in-browser renderer
    Slides,     // ppt / pptx — no in-browser renderer
    Text,       // txt / md / json / xml / log / code
    Archive,
    Other
}

/// Fetches bearer-protected chat attachments into object URLs and caches them.
///
/// The attachment endpoints sit behind `RequirePermission("chat.read")`, so a plain
/// `&lt;img src="/api/chat/attachments/..."&gt;` can never authenticate. Everything therefore
/// goes through `ipop.authBlob`, which fetches with an Authorization header and hands
/// back a `blob:` URL. This service owns those URLs and keeps them across room switches
/// (going back to a room shows its images at once), evicting the least recently used
/// ones once they add up to more than <see cref="BudgetBytes"/>.
public class ChatAttachments(AuthState auth, HttpClient http, IJSRuntime js)
{
    private readonly Dictionary<string, BlobRef> _cache = [];
    // In-flight fetches are shared, not skipped: two bubbles showing the same attachment (or a
    // bubble plus the drawer's media grid) must both await the one request. Returning null to the
    // second caller made it render a permanent broken-image state for a file that loaded fine.
    private readonly Dictionary<string, Task<string?>> _inFlight = [];

    private sealed record BlobRef(string Url, string ContentType, long Size);

    /// Upper bound for the bytes held in object URLs before the oldest are revoked.
    public const long BudgetBytes = 150L * 1024 * 1024;
    private readonly LinkedList<string> _lru = new();   // most recently used first
    private long _bytes;

    private void Touch(string key)
    {
        _lru.Remove(key);
        _lru.AddFirst(key);
    }

    private void Drop(string key)
    {
        if (!_cache.Remove(key, out var r)) return;
        _lru.Remove(key);
        _bytes -= r.Size;
        _ = js.InvokeVoidAsync("ipop.revokeBlob", r.Url);
    }

    /// Revokes least-recently-used blobs until the cache fits the budget. An <img> that already
    /// decoded a revoked URL keeps showing it; only a fresh mount refetches.
    private void Trim()
    {
        while (_bytes > BudgetBytes && _lru.Count > 1)
            Drop(_lru.Last!.Value);
    }

    // ── Cache ────────────────────────────────────────────────────────────────

    private static string Key(Guid id, bool thumb) => (thumb ? "t:" : "f:") + id;

    /// Cached object URL, or null when it has not been fetched yet.
    public string? UrlFor(Guid id, bool thumb)
    {
        var key = Key(id, thumb);
        if (!_cache.TryGetValue(key, out var r)) return null;
        Touch(key);
        return r.Url;
    }

    /// True while a fetch for this attachment is running — lets the UI show a skeleton.
    public bool IsLoading(Guid id, bool thumb) => _inFlight.ContainsKey(Key(id, thumb));

    /// The blob's real content type as reported by the server response.
    public string? ContentTypeFor(Guid id, bool thumb) =>
        _cache.TryGetValue(Key(id, thumb), out var r) ? r.ContentType : null;

    /// Fetches (or returns the cached) object URL for an attachment.
    /// `thumb: true` hits /thumbnail, which also triggers the server-side thumbnail
    /// backfill for older rows; a 404 there is expected for non-image files.
    public Task<string?> GetAsync(Guid id, bool thumb)
    {
        var key = Key(id, thumb);
        if (_cache.TryGetValue(key, out var hit)) { Touch(key); return Task.FromResult<string?>(hit.Url); }
        if (_inFlight.TryGetValue(key, out var pending)) return pending;

        var task = FetchAsync(key, id, thumb);
        _inFlight[key] = task;
        return task;
    }

    /// Clears the cached failure for an attachment so the next GetAsync refetches it.
    /// Backs the "click to retry" affordance on a broken image.
    public void Forget(Guid id)
    {
        foreach (var thumb in (ReadOnlySpan<bool>)[true, false])
        {
            var key = Key(id, thumb);
            Drop(key);
            _inFlight.Remove(key);
        }
    }

    private async Task<string?> FetchAsync(string key, Guid id, bool thumb)
    {
        try
        {
            var path = thumb
                ? $"/api/chat/attachments/{id}/thumbnail"
                : $"/api/chat/attachments/{id}/download?inline=true";
            var url = new Uri(http.BaseAddress!, path).ToString();

            var res = await js.InvokeAsync<BlobResult?>("ipop.authBlob2", url, auth.AccessToken);

            // This path bypasses AuthState.Send, so it gets no automatic refresh-on-401. Without
            // this retry an expired access token turned every image in the room into a permanent
            // broken tile, while document downloads (which go through Send) kept working.
            if (res?.Url is null && res?.Status is 401 && await auth.TryRestoreAsync())
                res = await js.InvokeAsync<BlobResult?>("ipop.authBlob2", url, auth.AccessToken);

            if (res?.Url is null) return null;
            Drop(key); // a retry racing an earlier fetch must not leak the first URL
            _cache[key] = new BlobRef(res.Url, res.Type ?? "", Math.Max(res.Size, 0));
            _bytes += Math.Max(res.Size, 0);
            Touch(key);
            Trim();
            return res.Url;
        }
        catch
        {
            return null;
        }
        finally
        {
            _inFlight.Remove(key);
        }
    }

    /// Downloads the raw bytes (used by viewers that need to parse the file, e.g. XLSX).
    public async Task<byte[]?> GetBytesAsync(Guid id)
    {
        try
        {
            var res = await auth.SendAsync(HttpMethod.Get, $"/api/chat/attachments/{id}/download");
            if (!res.IsSuccessStatusCode) return null;
            return await res.Content.ReadAsByteArrayAsync();
        }
        catch { return null; }
    }

    /// Reads the attachment as UTF-8 text (txt / md / csv / json previews).
    public async Task<string?> GetTextAsync(Guid id, int maxChars = 200_000)
    {
        var bytes = await GetBytesAsync(id);
        if (bytes is null) return null;
        var text = System.Text.Encoding.UTF8.GetString(bytes);
        return text.Length > maxChars ? text[..maxChars] + "\n\n… truncated …" : text;
    }

    /// Saves the attachment to the user's downloads with its original filename.
    public async Task DownloadAsync(AttachmentDto att)
    {
        var url = new Uri(http.BaseAddress!, $"/api/chat/attachments/{att.Id}/download").ToString();
        await js.InvokeVoidAsync("ipop.authDownload", url, auth.AccessToken, att.FileName);
    }

    /// Revokes every cached object URL (sign-out); room switches rely on the LRU budget instead.
    public void ReleaseAll()
    {
        foreach (var r in _cache.Values)
            _ = js.InvokeVoidAsync("ipop.revokeBlob", r.Url);
        _cache.Clear();
        _lru.Clear();
        _bytes = 0;
        _inFlight.Clear();
    }

    private sealed class BlobResult
    {
        public string? Url { get; set; }
        public string? Type { get; set; }
        public long Size { get; set; }
        /// HTTP status of the fetch; 0 when the request never reached the server.
        public int Status { get; set; }
    }

    // ── Classification ───────────────────────────────────────────────────────

    /// Effective MIME for an attachment. Stored MimeType wins when it is meaningful,
    /// otherwise the extension decides — older rows were saved with MimeType = "".
    public static string Mime(AttachmentDto att) => Mime(att.MimeType, att.FileName);

    public static string Mime(string? stored, string fileName)
    {
        var m = stored?.Trim();
        if (!string.IsNullOrEmpty(m))
        {
            var semi = m.IndexOf(';');
            if (semi >= 0) m = m[..semi].Trim();
        }
        if (!string.IsNullOrEmpty(m) && m.Contains('/') &&
            !m.Equals("application/octet-stream", StringComparison.OrdinalIgnoreCase))
            return m;

        return Ext(fileName) switch
        {
            ".jpg" or ".jpeg" or ".jpe" => "image/jpeg",
            ".png" => "image/png",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".bmp" => "image/bmp",
            ".tif" or ".tiff" => "image/tiff",
            ".svg" => "image/svg+xml",
            ".ico" => "image/x-icon",
            ".heic" => "image/heic",
            ".heif" => "image/heif",
            ".avif" => "image/avif",
            ".pdf" => "application/pdf",
            ".doc" or ".dot" => "application/msword",
            ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            ".rtf" => "application/rtf",
            ".odt" => "application/vnd.oasis.opendocument.text",
            ".xls" or ".xlsm" => "application/vnd.ms-excel",
            ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            ".ods" => "application/vnd.oasis.opendocument.spreadsheet",
            ".ppt" => "application/vnd.ms-powerpoint",
            ".pptx" => "application/vnd.openxmlformats-officedocument.presentationml.presentation",
            ".odp" => "application/vnd.oasis.opendocument.presentation",
            ".csv" => "text/csv",
            ".tsv" => "text/tab-separated-values",
            ".txt" or ".log" => "text/plain",
            ".md" => "text/markdown",
            ".json" => "application/json",
            ".xml" => "application/xml",
            ".yml" or ".yaml" => "text/yaml",
            ".html" or ".htm" => "text/html",
            ".css" => "text/css",
            ".mp3" => "audio/mpeg",
            ".wav" => "audio/wav",
            ".ogg" or ".opus" => "audio/ogg",
            ".m4a" or ".aac" => "audio/mp4",
            ".flac" => "audio/flac",
            ".mp4" or ".m4v" => "video/mp4",
            ".webm" => "video/webm",
            ".mov" => "video/quicktime",
            ".avi" => "video/x-msvideo",
            ".mkv" => "video/x-matroska",
            ".zip" => "application/zip",
            ".rar" => "application/vnd.rar",
            ".7z" => "application/x-7z-compressed",
            ".tar" => "application/x-tar",
            ".gz" => "application/gzip",
            _ => "application/octet-stream"
        };
    }

    public static string Ext(string fileName)
    {
        var dot = fileName.LastIndexOf('.');
        return dot < 0 ? "" : fileName[dot..].ToLowerInvariant();
    }

    public static AttachmentKind Kind(AttachmentDto att)
    {
        var mime = Mime(att);
        var ext = Ext(att.FileName);

        if (ext is ".xlsx" or ".csv") return AttachmentKind.Sheet;
        if (ext is ".xls" or ".xlsm") return AttachmentKind.LegacySheet;
        if (ext is ".docx") return AttachmentKind.Doc;
        if (ext is ".doc" or ".rtf" or ".odt") return AttachmentKind.LegacyDoc;
        if (ext is ".ppt" or ".pptx" or ".odp") return AttachmentKind.Slides;
        if (ext is ".ods") return AttachmentKind.LegacySheet;

        if (mime == "application/pdf") return AttachmentKind.Pdf;
        if (mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase)) return AttachmentKind.Image;
        if (mime.StartsWith("video/", StringComparison.OrdinalIgnoreCase)) return AttachmentKind.Video;
        if (mime.StartsWith("audio/", StringComparison.OrdinalIgnoreCase)) return AttachmentKind.Audio;
        if (mime.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
            || mime is "application/json" or "application/xml") return AttachmentKind.Text;
        if (mime is "application/zip" or "application/vnd.rar" or "application/x-7z-compressed"
            or "application/x-tar" or "application/gzip") return AttachmentKind.Archive;

        return AttachmentKind.Other;
    }

    /// True when the attachment renders as a picture directly in the message bubble.
    public static bool IsImage(AttachmentDto att) => Kind(att) == AttachmentKind.Image;

    /// True when the viewer can show the file's contents rather than only offering a download.
    public static bool CanPreview(AttachmentDto att) => Kind(att) is
        AttachmentKind.Image or AttachmentKind.Video or AttachmentKind.Audio or
        AttachmentKind.Pdf or AttachmentKind.Sheet or AttachmentKind.LegacySheet or
        AttachmentKind.Doc or AttachmentKind.Text;

    // ── Presentation ─────────────────────────────────────────────────────────

    /// Lucide icon name for the file card.
    public static string Icon(AttachmentDto att) => Kind(att) switch
    {
        AttachmentKind.Image => "image",
        AttachmentKind.Video => "video",
        AttachmentKind.Audio => "music",
        AttachmentKind.Pdf => "file-text",
        AttachmentKind.Sheet or AttachmentKind.LegacySheet => "table",
        AttachmentKind.Doc or AttachmentKind.LegacyDoc => "file-text",
        AttachmentKind.Slides => "presentation",
        AttachmentKind.Text => "file-code",
        AttachmentKind.Archive => "archive",
        _ => "file"
    };

    /// Short badge label, e.g. "PDF", "XLSX".
    public static string TypeLabel(AttachmentDto att)
    {
        var ext = Ext(att.FileName).TrimStart('.');
        if (!string.IsNullOrEmpty(ext)) return ext.ToUpperInvariant();
        return Kind(att) switch
        {
            AttachmentKind.Image => "IMAGE",
            AttachmentKind.Video => "VIDEO",
            AttachmentKind.Audio => "AUDIO",
            _ => "FILE"
        };
    }

    /// Accent colour for the icon tile, keyed to the file family so scanning a
    /// conversation for "the spreadsheet" is fast.
    public static string Accent(AttachmentDto att) => Kind(att) switch
    {
        AttachmentKind.Pdf => "#c0392b",
        AttachmentKind.Sheet or AttachmentKind.LegacySheet => "#1b7a3d",
        AttachmentKind.Doc or AttachmentKind.LegacyDoc => "#1a56c4",
        AttachmentKind.Slides => "#d35400",
        AttachmentKind.Image => "#7b3fa0",
        AttachmentKind.Video => "#0e7490",
        AttachmentKind.Audio => "#b7791f",
        AttachmentKind.Archive => "#5b6770",
        AttachmentKind.Text => "#374151",
        _ => "#5b6770"
    };

    public static string FormatSize(long bytes) => bytes switch
    {
        < 0 => "—",
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024.0:0.#} MB",
        _ => $"{bytes / 1024.0 / 1024.0 / 1024.0:0.##} GB"
    };
}
