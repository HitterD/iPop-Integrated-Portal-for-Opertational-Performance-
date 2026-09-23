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

    [Fact]
    public void FileToken_RoundTrips_AndValidates()
    {
        var userId = Guid.NewGuid();
        var server = "spj";
        var path = "/home/chat/doc1.docx";

        var createMethod = typeof(IPop.Api.Endpoints.OnlyOfficeEndpoints)
            .GetMethod("CreateFileToken", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        var validateMethod = typeof(IPop.Api.Endpoints.OnlyOfficeEndpoints)
            .GetMethod("ValidateFileToken", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

        Assert.NotNull(createMethod);
        Assert.NotNull(validateMethod);

        var token = createMethod.Invoke(null, [userId, server, path]) as string;
        Assert.NotNull(token);
        Assert.Equal(16, token.Length);

        var valid = (bool)validateMethod.Invoke(null, [userId, server, path, token])!;
        Assert.True(valid);

        var invalid = (bool)validateMethod.Invoke(null, [userId, "other-server", path, token])!;
        Assert.False(invalid);
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
