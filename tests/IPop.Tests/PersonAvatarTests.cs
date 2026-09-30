using Bunit;
using IPop.Web.Components;
using IPop.Web.Models;
using IPop.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IPop.Tests;

// AvatarPhotos is a static cache, so these tests must not run in parallel with other users of it.
[Collection("AvatarPhotos")]
public class PersonAvatarTests : IDisposable
{
    private static readonly Uri Api = new("http://api.test/");

    public void Dispose() => AvatarPhotos.Clear();

    [Fact]
    public void Without_a_photo_it_shows_initials_on_a_rounded_square()
    {
        using var ctx = Context();
        var cut = ctx.Render<PersonAvatar>(p => p.Add(a => a.Nik, "100").Add(a => a.Name, "Jane Kurniawan").Add(a => a.Size, 72));

        var box = cut.Find(".pav");
        Assert.Equal("JK", box.TextContent.Trim());
        var style = box.GetAttribute("style")!;
        Assert.Contains("width:72px", style);
        Assert.Contains($"border-radius:{PersonAvatar.Radius(72)}px", style);
        Assert.DoesNotContain("url(", style);
    }

    [Fact]
    public void Photo_changes_re_render_every_avatar()
    {
        using var ctx = Context();
        var cut = ctx.Render<PersonAvatar>(p => p.Add(a => a.Nik, "100").Add(a => a.Name, "Jane"));

        AvatarPhotos.Set(Api, new Dictionary<string, string> { ["100"] = "abc" });

        cut.WaitForAssertion(() => Assert.Contains("http://api.test/api/photos/abc", cut.Find(".pav").GetAttribute("style")));
    }

    [Fact]
    public void Presence_dot_renders_only_with_a_status()
    {
        using var ctx = Context();
        Assert.Empty(ctx.Render<PersonAvatar>(p => p.Add(a => a.Name, "A")).FindAll(".pav-dot"));
        Assert.Contains("busy", ctx.Render<PersonAvatar>(p => p.Add(a => a.Name, "A").Add(a => a.Status, "busy")).Find(".pav-dot").ClassName);
    }

    [Fact]
    public void Viewable_avatar_opens_the_large_photo_and_does_nothing_without_one()
    {
        using var ctx = Context();
        var viewer = ctx.Services.GetRequiredService<PhotoViewerState>();

        var noPhoto = ctx.Render<PersonAvatar>(p => p.Add(a => a.Nik, "100").Add(a => a.Name, "Jane").Add(a => a.Viewable, true));
        Assert.Empty(noPhoto.FindAll("button"));
        Assert.False(viewer.Open("100", "Jane"));

        AvatarPhotos.Set(Api, new Dictionary<string, string> { ["100"] = "abc" });
        var cut = ctx.Render<PersonAvatar>(p => p.Add(a => a.Nik, "100").Add(a => a.Name, "Jane").Add(a => a.Viewable, true));
        cut.Find("button.pav-view").Click();

        Assert.True(viewer.IsOpen);
        Assert.Equal("http://api.test/api/photos/abc/large", viewer.Url);
    }

    [Fact]
    public void Viewer_shows_the_photo_and_escape_closes_it()
    {
        using var ctx = Context();
        AvatarPhotos.Set(Api, new Dictionary<string, string> { ["100"] = "abc" });
        var host = ctx.Render<PhotoViewer>();
        var viewer = ctx.Services.GetRequiredService<PhotoViewerState>();
        var overlays = ctx.Services.GetRequiredService<OverlayStack>();

        viewer.Open("100", "Jane");
        host.WaitForAssertion(() => Assert.Equal("http://api.test/api/photos/abc/large", host.Find(".photo-viewer img").GetAttribute("src")));
        Assert.True(overlays.Any);

        overlays.CloseTopAsync().GetAwaiter().GetResult();
        host.WaitForAssertion(() => Assert.Empty(host.FindAll(".photo-viewer")));
        Assert.False(overlays.Any);
    }

    [Theory]
    [InlineData("Jane Kurniawan", "JK")]
    [InlineData("  budi ", "B")]
    [InlineData("(Admin) Tono", "T")]
    [InlineData("", "?")]
    [InlineData(null, "?")]
    public void Initials(string? name, string expected) => Assert.Equal(expected, AvatarPhotos.Initials(name));

    private static BunitContext Context()
    {
        var ctx = new BunitContext();
        ctx.Services.AddScoped<PhotoViewerState>();
        ctx.Services.AddScoped<OverlayStack>();
        return ctx;
    }
}

[CollectionDefinition("AvatarPhotos", DisableParallelization = true)]
public class AvatarPhotosCollection;
