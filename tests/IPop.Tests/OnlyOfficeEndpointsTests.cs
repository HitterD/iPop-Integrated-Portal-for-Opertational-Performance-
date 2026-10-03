using System.Net;
using System.Text;
using System.Text.Json;
using IPop.Core.Entities;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace IPop.Tests;

public class OnlyOfficeEndpointsTests
{
    [Theory]
    [InlineData(".docx", "word")]
    [InlineData(".doc", "word")]
    [InlineData(".rtf", "word")]
    [InlineData(".xlsx", "cell")]
    [InlineData(".xls", "cell")]
    [InlineData(".csv", "cell")]
    [InlineData(".pptx", "slide")]
    [InlineData(".ppt", "slide")]
    public void DocumentType_MapsCorrectly(string ext, string expectedType)
    {
        var method = typeof(IPop.Api.Endpoints.OnlyOfficeEndpoints)
            .GetMethod("GetDocumentType", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);

        var result = method.Invoke(null, [ext]) as string;
        Assert.Equal(expectedType, result);
    }

    private static readonly System.Reflection.MethodInfo Create = typeof(IPop.Api.Endpoints.OnlyOfficeEndpoints)
        .GetMethod("CreateFileToken", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
    private static readonly System.Reflection.MethodInfo Validate = typeof(IPop.Api.Endpoints.OnlyOfficeEndpoints)
        .GetMethod("ValidateFileToken", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
    private static readonly System.Reflection.MethodInfo Resolve = typeof(IPop.Api.Endpoints.OnlyOfficeEndpoints)
        .GetMethod("ResolveEditedFileUrl", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;

    private static byte[] Key(string secret) => System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(secret));
    private static long In(TimeSpan t) => DateTimeOffset.UtcNow.Add(t).ToUnixTimeSeconds();

    [Fact]
    public void FileToken_RoundTrips_AndValidates()
    {
        var userId = Guid.NewGuid();
        var path = "/home/chat/doc1.docx";
        var exp = In(TimeSpan.FromHours(1));
        var token = (string)Create.Invoke(null, [Key("server-secret"), userId, "spj", path, exp])!;

        Assert.True((bool)Validate.Invoke(null, [Key("server-secret"), userId, "spj", path, (long?)exp, token])!);
        Assert.False((bool)Validate.Invoke(null, [Key("server-secret"), userId, "other-server", path, (long?)exp, token])!);
        Assert.False((bool)Validate.Invoke(null, [Key("server-secret"), userId, "spj", "/home/other.docx", (long?)exp, token])!);
    }

    [Fact]
    public void FileToken_CannotBeForgedWithoutTheServerSecret()
    {
        var userId = Guid.NewGuid();
        var exp = In(TimeSpan.FromHours(1));
        // Anyone can compute a token with a key they choose; it must not pass on a server with a different secret.
        var forged = (string)Create.Invoke(null, [Key("attacker-guess"), userId, "spj", "/home/a.docx", exp])!;
        Assert.False((bool)Validate.Invoke(null, [Key("server-secret"), userId, "spj", "/home/a.docx", (long?)exp, forged])!);
    }

    [Fact]
    public void FileToken_ExpiresAndCannotBeExtended()
    {
        var userId = Guid.NewGuid();
        var past = In(TimeSpan.FromMinutes(-1));
        var token = (string)Create.Invoke(null, [Key("s"), userId, "spj", "/a.docx", past])!;
        Assert.False((bool)Validate.Invoke(null, [Key("s"), userId, "spj", "/a.docx", (long?)past, token])!);
        // Moving exp forward breaks the signature.
        Assert.False((bool)Validate.Invoke(null, [Key("s"), userId, "spj", "/a.docx", (long?)In(TimeSpan.FromDays(1)), token])!);
        Assert.False((bool)Validate.Invoke(null, [Key("s"), userId, "spj", "/a.docx", (long?)null, token])!);
    }

    [Theory]
    [InlineData("https://ipop.santos.co.id/onlyoffice/cache/files/data/k1/output.docx/output.docx?md5=a&expires=1", "http://127.0.0.1:8088/cache/files/data/k1/output.docx/output.docx?md5=a&expires=1")]
    [InlineData("http://localhost:8088/cache/files/data/k2/output.xlsx/output.xlsx?md5=b", "http://127.0.0.1:8088/cache/files/data/k2/output.xlsx/output.xlsx?md5=b")]
    [InlineData("http://169.254.169.254/latest/meta-data/", null)]
    [InlineData("http://10.0.0.5/secret/cache.txt", null)]
    [InlineData("file:///c:/windows/win.ini", null)]
    [InlineData("not a url", null)]
    public void CallbackUrl_IsReRootedOnTheDocumentServer(string given, string? expected)
    {
        var got = (Uri?)Resolve.Invoke(null, ["http://127.0.0.1:8088", given]);
        Assert.Equal(expected, got?.ToString());
    }

    [Fact]
    public void DocumentKey_IsConsistent_ForSameDocument()
    {
        var keyMethod = typeof(IPop.Api.Endpoints.OnlyOfficeEndpoints)
            .GetMethod("GenerateDocumentKey", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(keyMethod);

        var key1 = keyMethod.Invoke(null, ["spj", "/home/chat/room1/agenda.docx"]) as string;
        var key2 = keyMethod.Invoke(null, ["SPJ", "/home/chat/room1/agenda.docx"]) as string;

        Assert.NotNull(key1);
        Assert.NotNull(key2);
        Assert.Equal(key1, key2); // case-insensitive server mapping
    }

    [Fact]
    public void DocumentKey_ChangesAfterSessionInvalidation()
    {
        var keyMethod = typeof(IPop.Api.Endpoints.OnlyOfficeEndpoints)
            .GetMethod("GenerateDocumentKey", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        var invalidateMethod = typeof(IPop.Api.Endpoints.OnlyOfficeEndpoints)
            .GetMethod("InvalidateDocumentSession", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

        Assert.NotNull(keyMethod);
        Assert.NotNull(invalidateMethod);

        var keyBefore = keyMethod.Invoke(null, ["spj", "/home/chat/doc_versioned.docx"]) as string;
        invalidateMethod.Invoke(null, ["spj", "/home/chat/doc_versioned.docx"]);
        var keyAfter = keyMethod.Invoke(null, ["spj", "/home/chat/doc_versioned.docx"]) as string;

        Assert.NotNull(keyBefore);
        Assert.NotNull(keyAfter);
        Assert.NotEqual(keyBefore, keyAfter);
    }
}
