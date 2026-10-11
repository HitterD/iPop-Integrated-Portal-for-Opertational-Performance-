using System.Text;
using System.Text.Json;
using IPop.Application.Calls;
using IPop.Infrastructure.Calls;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace IPop.Tests;

public class LiveKitTokenTests
{
    private static ILiveKitTokenService CreateService(string? apiKey = "test-key", string? apiSecret = "test-secret-123456789012345678901234")
    {
        var dict = new Dictionary<string, string?>
        {
            ["Calls:LiveKit:ApiKey"] = apiKey,
            ["Calls:LiveKit:ApiSecret"] = apiSecret,
            ["Calls:LiveKit:Url"] = "wss://livekit.test.local"
        };
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(dict).Build();
        return new LiveKitTokenService(cfg);
    }

    [Theory]
    [InlineData(null, "wss://livekit.test.local")]
    [InlineData("", "wss://livekit.test.local")]
    [InlineData("   ", "wss://livekit.test.local")]
    [InlineData("http://127.0.0.1:7880", "http://127.0.0.1:7880")]
    public void Server_api_override_does_not_change_the_browser_url(string? serverUrl, string expected)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Calls:LiveKit:Url"] = "wss://livekit.test.local",
            ["Calls:LiveKit:ServerUrl"] = serverUrl,
        }).Build();
        ILiveKitTokenService service = new LiveKitTokenService(config);
        Assert.Equal(expected, service.ServerApiUrl);
        Assert.Equal("wss://livekit.test.local", service.ServerUrl);
    }

    [Fact]
    public void GenerateToken_ProducesValidJwtWithLiveKitVideoGrants()
    {
        var svc = CreateService();
        var token = svc.GenerateToken("room-123", "user-nik-1", "Bagas Arif");

        Assert.NotNull(token);
        var parts = token.Split('.');
        Assert.Equal(3, parts.Length);

        // Decode payload
        var payloadJson = Encoding.UTF8.GetString(Base64UrlDecode(parts[1]));
        using var doc = JsonDocument.Parse(payloadJson);
        var root = doc.RootElement;

        Assert.Equal("test-key", root.GetProperty("iss").GetString());
        Assert.Equal("user-nik-1", root.GetProperty("sub").GetString());
        Assert.Equal("Bagas Arif", root.GetProperty("name").GetString());

        var video = root.GetProperty("video");
        Assert.Equal("room-123", video.GetProperty("room").GetString());
        Assert.True(video.GetProperty("roomJoin").GetBoolean());
        Assert.True(video.GetProperty("canPublish").GetBoolean());
        Assert.True(video.GetProperty("canSubscribe").GetBoolean());
    }

    [Fact]
    public async Task CallService_NeverMintsLiveKitTokens_For1to1Calls()
    {
        // 1:1 calls are peer-to-peer WebRTC; LiveKit is reserved for meetings.
        using var t = new TestDb();
        var caller = t.AddUser("CALLER_1");
        var callee = t.AddUser("CALLEE_1");

        var created = await new CallService(t.Verify()).CreateCallAsync(caller.Id, new CreateCallRequest(callee.Nik), default);
        Assert.True(created.Success);
        Assert.Null(created.Room!.LiveKitToken);
        Assert.Null(created.Room.LiveKitUrl);
    }

    [Fact]
    public void Unconfigured_LiveKit_RefusesToMint()
    {
        var lk = CreateService(apiKey: "", apiSecret: "");
        Assert.False(lk.IsConfigured);
        Assert.Throws<InvalidOperationException>(() => lk.GenerateToken("r", "u", "n"));
    }

    [Fact]
    public void Sources_and_own_metadata_are_granted_only_when_asked()
    {
        var svc = CreateService();
        var plain = Payload(svc.GenerateToken("r", "u", "U")).GetProperty("video");
        Assert.False(plain.TryGetProperty("canPublishSources", out _));
        Assert.False(plain.TryGetProperty("canUpdateOwnMetadata", out _));

        var v = Payload(svc.GenerateToken("r", "u", "U", sources: ["microphone", "screen_share"], canUpdateOwnMetadata: true)).GetProperty("video");
        Assert.Equal(["microphone", "screen_share"], v.GetProperty("canPublishSources").EnumerateArray().Select(x => x.GetString()));
        Assert.True(v.GetProperty("canUpdateOwnMetadata").GetBoolean());
    }

    [Fact]
    public void Admin_token_grants_room_admin_and_optionally_record()
    {
        var svc = CreateService();
        var v = Payload(svc.GenerateAdminToken("hall-kalosi", record: true)).GetProperty("video");
        Assert.True(v.GetProperty("roomAdmin").GetBoolean());
        Assert.True(v.GetProperty("roomCreate").GetBoolean());
        Assert.True(v.GetProperty("roomRecord").GetBoolean());
        Assert.Equal("hall-kalosi", v.GetProperty("room").GetString());
        Assert.False(Payload(svc.GenerateAdminToken(null)).GetProperty("video").TryGetProperty("roomRecord", out _));
    }

    private static JsonElement Payload(string token) =>
        JsonDocument.Parse(Encoding.UTF8.GetString(Base64UrlDecode(token.Split('.')[1]))).RootElement;

    private static byte[] Base64UrlDecode(string input)
    {
        var output = input.Replace('-', '+').Replace('_', '/');
        switch (output.Length % 4)
        {
            case 2: output += "=="; break;
            case 3: output += "="; break;
        }
        return Convert.FromBase64String(output);
    }
}
