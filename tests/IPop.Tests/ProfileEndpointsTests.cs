using IPop.Api.Endpoints;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace IPop.Tests;

public class ProfileEndpointsTests
{
    // Header-only fakes: the server never decodes pixels, so a valid header is enough.
    private static byte[] Png(int w, int h, int size = 64)
    {
        var b = new byte[size];
        new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13 }.CopyTo(b, 0);
        "IHDR"u8.ToArray().CopyTo(b, 12);
        BE32(b, 16, w); BE32(b, 20, h);
        return b;
    }

    private static byte[] Jpeg(int w, int h)
    {
        var b = new byte[64];
        new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x04, 0, 0 }.CopyTo(b, 0);   // APP0, length 4
        new byte[] { 0xFF, 0xC0, 0x00, 0x11, 0x08 }.CopyTo(b, 8);               // SOF0
        b[13] = (byte)(h >> 8); b[14] = (byte)h; b[15] = (byte)(w >> 8); b[16] = (byte)w;
        return b;
    }

    private static byte[] WebPx(int w, int h)
    {
        var b = new byte[64];
        "RIFF"u8.ToArray().CopyTo(b, 0); "WEBP"u8.ToArray().CopyTo(b, 8); "VP8X"u8.ToArray().CopyTo(b, 12);
        b[24] = (byte)(w - 1); b[25] = (byte)((w - 1) >> 8);
        b[27] = (byte)(h - 1); b[28] = (byte)((h - 1) >> 8);
        return b;
    }

    private static void BE32(byte[] b, int at, int v)
    {
        b[at] = (byte)(v >> 24); b[at + 1] = (byte)(v >> 16); b[at + 2] = (byte)(v >> 8); b[at + 3] = (byte)v;
    }

    [Theory]
    [InlineData("png")]
    [InlineData("jpeg")]
    [InlineData("webp")]
    public void ImageSniff_ReadsTypeAndSize(string kind)
    {
        var bytes = kind switch { "png" => Png(256, 200), "jpeg" => Jpeg(256, 200), _ => WebPx(256, 200) };
        var type = ImageSniff.Detect(bytes);
        Assert.Equal($"image/{kind}", type);
        Assert.Equal((256, 200), ImageSniff.Dimensions(bytes, type!));
    }

    [Fact]
    public void ImageSniff_RejectsNonImages()
    {
        Assert.Null(ImageSniff.Detect("<svg xmlns='http://www.w3.org/2000/svg'></svg>"u8));
        Assert.Null(ImageSniff.Detect("GIF89a............."u8));
    }

    [Fact]
    public async Task ChangeEmail_Valid_SavesAndAudits()
    {
        using var t = new TestDb();
        t.AddUser("100");

        var (status, _) = await ProfileEndpoints.ChangeEmailAsync(t.Db, "100", "  new.mail@santos.co.id ", default);

        Assert.Equal(200, status);
        await using var v = t.Verify();
        Assert.Equal("new.mail@santos.co.id", (await v.Users.SingleAsync()).Email);
        var log = await v.AuditLogs.SingleAsync();
        Assert.Equal("profile.email.change", log.Action);
        Assert.Contains("100@t.co", log.DetailJson);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-an-email")]
    [InlineData("a@b")]
    public async Task ChangeEmail_Invalid_Returns400(string email)
    {
        using var t = new TestDb();
        t.AddUser("100");
        var (status, error) = await ProfileEndpoints.ChangeEmailAsync(t.Db, "100", email, default);
        Assert.Equal(400, status);
        Assert.Equal("invalid_email", error!.Code);
    }

    [Fact]
    public async Task ChangeEmail_TakenCaseInsensitive_Returns409()
    {
        using var t = new TestDb();
        t.AddUser("100");
        t.AddUser("200");
        var (status, error) = await ProfileEndpoints.ChangeEmailAsync(t.Db, "100", "200@T.CO", default);
        Assert.Equal(409, status);
        Assert.Equal("email_taken", error!.Code);
    }

    [Fact]
    public async Task ChangeEmail_Unchanged_IsNoOp()
    {
        using var t = new TestDb();
        t.AddUser("100");
        var (status, _) = await ProfileEndpoints.ChangeEmailAsync(t.Db, "100", "100@t.co", default);
        Assert.Equal(200, status);
        Assert.Equal(0, await t.Verify().AuditLogs.CountAsync());
    }

    [Fact]
    public async Task ChangeEmail_UnknownCaller_Returns401()
    {
        using var t = new TestDb();
        var (status, _) = await ProfileEndpoints.ChangeEmailAsync(t.Db, "nobody", "x@y.co", default);
        Assert.Equal(401, status);
    }

    [Fact]
    public async Task SetPhoto_ReplacesPreviousPhoto()
    {
        using var t = new TestDb();
        t.AddUser("100");

        var (s1, _, first) = await ProfileEndpoints.SetPhotoAsync(t.Verify(), "100", Png(256, 256), default);
        var (s2, _, second) = await ProfileEndpoints.SetPhotoAsync(t.Verify(), "100", WebPx(256, 256), default);

        Assert.Equal(200, s1); Assert.Equal(200, s2);
        Assert.NotEqual(first, second);
        await using var v = t.Verify();
        var photo = await v.UserPhotos.SingleAsync();
        Assert.Equal(second, photo.Id);
        Assert.Equal("image/webp", photo.ContentType);
        Assert.Equal(second.ToString(), (await v.Users.SingleAsync()).PhotoPath);
    }

    [Fact]
    public async Task SetPhoto_RejectsWrongType()
    {
        using var t = new TestDb();
        t.AddUser("100");
        var (status, error, _) = await ProfileEndpoints.SetPhotoAsync(t.Db, "100", "<svg onload=alert(1)>........................"u8.ToArray(), default);
        Assert.Equal(400, status);
        Assert.Equal("photo_type", error!.Code);
    }

    [Fact]
    public async Task SetPhoto_RejectsOversizedFile()
    {
        using var t = new TestDb();
        t.AddUser("100");
        var (status, error, _) = await ProfileEndpoints.SetPhotoAsync(t.Db, "100", Png(256, 256, ProfileEndpoints.MaxPhotoBytes + 1), default);
        Assert.Equal(400, status);
        Assert.Equal("photo_size", error!.Code);
    }

    [Theory]
    [InlineData(16, 16)]
    [InlineData(4000, 4000)]
    public async Task SetPhoto_RejectsBadDimensions(int w, int h)
    {
        using var t = new TestDb();
        t.AddUser("100");
        var (status, error, _) = await ProfileEndpoints.SetPhotoAsync(t.Db, "100", Png(w, h), default);
        Assert.Equal(400, status);
        Assert.Equal("photo_dimensions", error!.Code);
    }

    [Fact]
    public async Task RemovePhoto_ClearsRowAndPointer()
    {
        using var t = new TestDb();
        t.AddUser("100");
        await ProfileEndpoints.SetPhotoAsync(t.Verify(), "100", Png(256, 256), default);

        Assert.True(await ProfileEndpoints.RemovePhotoAsync(t.Verify(), "100", default));

        await using var v = t.Verify();
        Assert.Equal(0, await v.UserPhotos.CountAsync());
        Assert.Null((await v.Users.SingleAsync()).PhotoPath);
        Assert.Contains(await v.AuditLogs.ToListAsync(), a => a.Action == "profile.photo.remove");
    }
}
