using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.Processing;

namespace IPop.Api.Notes;

/// The small picture of a whiteboard shown on the Whiteboards list. The editor draws it in the browser and uploads it; the server
/// re-encodes it (content check, 640 px wide at most, JPEG) so what lands on the NAS is always a small, metadata-free picture.
public static class NoteBoardThumbnail
{
    public const long MaxBytes = 4L * 1024 * 1024;
    public const int MaxWidth = 640;

    public sealed record Result(byte[] Bytes, string ContentType, string Extension);

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
            if (image.Width > MaxWidth)
                image.Mutate(x => x.Resize(new ResizeOptions { Size = new Size(MaxWidth, 0), Mode = ResizeMode.Max }));
            using var ms = new MemoryStream();
            image.SaveAsJpeg(ms, new JpegEncoder { Quality = 80 });
            return new Result(ms.ToArray(), "image/jpeg", ".jpg");
        }
        catch (Exception e) when (e is UnknownImageFormatException or InvalidImageContentException or NotSupportedException)
        {
            error = "not_an_image";
            return null;
        }
    }
}
