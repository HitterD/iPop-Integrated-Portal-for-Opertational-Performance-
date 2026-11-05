using IPop.Api.Endpoints;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace IPop.Tests;

/// Chat upload thumbnails: decoded at the target size, and pictures above the megapixel cap are refused from the header
/// alone (a "decompression bomb" never gets its pixels allocated).
public class ChatUploadThumbnailTests
{
    private static MemoryStream Png(int w, int h)
    {
        using var img = new Image<Rgba32>(w, h, new Rgba32(30, 120, 80));
        var ms = new MemoryStream();
        img.SaveAsPng(ms);
        ms.Position = 0;
        return ms;
    }

    [Fact]
    public void Large_photo_gets_a_bounded_thumbnail()
    {
        using var src = Png(3000, 2000);
        Assert.Equal(ChatEndpoints.ThumbResult.Ok, ChatEndpoints.TryCreateThumbnail(src, 640, out var thumb));
        using var t = Image.Load(thumb!);
        Assert.True(t.Width <= 640 && t.Height <= 640);
        Assert.Equal(640, Math.Max(t.Width, t.Height));
    }

    [Fact]
    public void Small_picture_keeps_its_size()
    {
        using var src = Png(300, 200);
        Assert.Equal(ChatEndpoints.ThumbResult.Ok, ChatEndpoints.TryCreateThumbnail(src, 640, out var thumb));
        using var t = Image.Load(thumb!);
        Assert.Equal((300, 200), (t.Width, t.Height));
    }

    [Fact]
    public void Picture_above_the_megapixel_cap_is_refused_without_decoding()
    {
        // 7000 x 6000 = 42 MP (> 40). A flat PNG of that size is small on disk; decoding it would be ~168 MB.
        using var src = Png(7000, 6000);
        Assert.Equal(ChatEndpoints.ThumbResult.TooManyPixels, ChatEndpoints.TryCreateThumbnail(src, 640, out var thumb));
        Assert.Null(thumb);
    }

    [Fact]
    public void Not_a_picture_is_a_soft_failure()
    {
        using var src = new MemoryStream("not an image"u8.ToArray());
        Assert.Equal(ChatEndpoints.ThumbResult.Failed, ChatEndpoints.TryCreateThumbnail(src, 640, out var thumb));
        Assert.Null(thumb);
    }
}
