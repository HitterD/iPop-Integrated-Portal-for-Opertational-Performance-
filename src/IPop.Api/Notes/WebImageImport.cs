using IPop.Infrastructure.Links;

namespace IPop.Api.Notes;

/// A picture copied from a web page and pasted into a note: the clipboard only has its address. The server fetches it with the
/// link-preview fetcher (public addresses only, no cookies, size cap, re-validated redirects), so the page keeps a real copy on
/// the NAS instead of a link that breaks or tracks readers.
public static class WebImageImport
{
    /// Same cap as the picture limit for pasted images.
    public const int MaxBytes = 10 * 1024 * 1024;

    public sealed record Result(byte[] Bytes, string ContentType, string FileName);

    internal static bool Accept(string media) =>
        media is "image/png" or "image/jpeg" or "image/gif" or "image/webp" or "image/avif" or "image/bmp";

    public static async Task<Result?> FetchAsync(SafeHttpFetcher fetcher, string? url, CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)) return null;
        var res = await fetcher.FetchAsync(new FetchRequest(uri, Accept, MaxBytes, AllowTruncate: false,
            Accept: "image/avif,image/webp,image/png,image/jpeg,image/gif;q=0.9"), ct);
        if (!res.Ok || res.Body is not { Length: > 0 } body) return null;
        // trust the bytes, not the header: a "picture" that isn't one is refused
        var sniffed = Sniff(body);
        if (sniffed is null) return null;
        return new Result(body, sniffed, FileNameFor(res.FinalUri ?? uri, sniffed));
    }

    internal static string? Sniff(byte[] b)
    {
        if (b.Length >= 8 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47) return "image/png";
        if (b.Length >= 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF) return "image/jpeg";
        if (b.Length >= 6 && b[0] == 'G' && b[1] == 'I' && b[2] == 'F' && b[3] == '8') return "image/gif";
        if (b.Length >= 12 && b[0] == 'R' && b[1] == 'I' && b[2] == 'F' && b[3] == 'F' && b[8] == 'W' && b[9] == 'E' && b[10] == 'B' && b[11] == 'P') return "image/webp";
        if (b.Length >= 12 && b[4] == 'f' && b[5] == 't' && b[6] == 'y' && b[7] == 'p' && b[8] == 'a' && b[9] == 'v' && b[10] == 'i') return "image/avif";
        if (b.Length >= 2 && b[0] == 'B' && b[1] == 'M') return "image/bmp";
        return null;
    }

    internal static string FileNameFor(Uri uri, string contentType)
    {
        var ext = contentType switch
        {
            "image/png" => ".png", "image/jpeg" => ".jpg", "image/gif" => ".gif", "image/webp" => ".webp", "image/avif" => ".avif", _ => ".bmp",
        };
        var last = Uri.UnescapeDataString(uri.Segments.LastOrDefault() ?? "").Trim('/');
        var stem = Path.GetFileNameWithoutExtension(last);
        stem = new string(stem.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_' or ' ').ToArray()).Trim();
        if (stem.Length == 0) stem = "pasted-image";
        if (stem.Length > 60) stem = stem[..60];
        return stem + ext;
    }
}
