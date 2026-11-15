using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.Processing;

namespace IPop.Api.Notes;

/// Validates and shrinks an uploaded cover photo. The format is decided by the file's CONTENT (never its name or the
/// browser's content type): only JPEG, PNG and WebP are accepted, at most 10 MB, and anything wider than 2000 px is
/// scaled down so a phone photo doesn't make every page load 8 MB.
public static class NoteCoverImage
{
    public const long MaxBytes = 10L * 1024 * 1024;
    public const int MaxWidth = 2000;

    public sealed record Result(byte[] Bytes, string ContentType, string Extension, int Width, int Height);

    /// Returns null and sets <paramref name="error"/> ("empty" | "too_large" | "not_an_image" | "unsupported_format").
    public static Result? Prepare(Stream input, long length, out string? error)
    {
        error = null;
        if (length <= 0) { error = "empty"; return null; }
        if (length > MaxBytes) { error = "too_large"; return null; }
        try
        {
            var format = Image.DetectFormat(input);
            if (format is not (JpegFormat or PngFormat or WebpFormat)) { error = format is null ? "not_an_image" : "unsupported_format"; return null; }
            input.Position = 0;
            using var image = Image.Load(input);
            image.Mutate(x => x.AutoOrient());
            if (image.Width > MaxWidth)
                image.Mutate(x => x.Resize(new ResizeOptions { Size = new Size(MaxWidth, 0), Mode = ResizeMode.Max }));
            using var ms = new MemoryStream();
            // PNG keeps its transparency; everything else is re-encoded as JPEG (small, and drops EXIF/GPS from phone photos).
            if (format is PngFormat) { image.SaveAsPng(ms); return new Result(ms.ToArray(), "image/png", ".png", image.Width, image.Height); }
            image.SaveAsJpeg(ms, new JpegEncoder { Quality = 85 });
            return new Result(ms.ToArray(), "image/jpeg", ".jpg", image.Width, image.Height);
        }
        catch (Exception e) when (e is UnknownImageFormatException or InvalidImageContentException or NotSupportedException)
        {
            error = "not_an_image";
            return null;
        }
    }
}
