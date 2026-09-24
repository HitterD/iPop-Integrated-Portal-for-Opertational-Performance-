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
    public async Task CallService_IncludesLiveKitToken_WhenCallIsActive()
    {
        using var t = new TestDb();
        var caller = t.AddUser("CALLER_1");
        var callee = t.AddUser("CALLEE_1");

        var lk = CreateService();
        var svc = new CallService(t.Verify(), lk);

        var created = await svc.CreateCallAsync(caller.Id, new CreateCallRequest(callee.Nik), default);
        Assert.True(created.Success);
        Assert.NotNull(created.Room);
        Assert.NotNull(created.Room.LiveKitToken);
        Assert.Equal("wss://livekit.test.local", created.Room.LiveKitUrl);

        // Check token contents
        var parts = created.Room.LiveKitToken.Split('.');
        Assert.Equal(3, parts.Length);
        var payloadJson = Encoding.UTF8.GetString(Base64UrlDecode(parts[1]));
        Assert.Contains(created.Room.Id.ToString(), payloadJson);
    }

    [Fact]
    public async Task CallService_OmitsLiveKit_WhenNotConfigured()
    {
        using var t = new TestDb();
        var caller = t.AddUser("CALLER_2");
        var callee = t.AddUser("CALLEE_2");

        var lk = CreateService(apiKey: "", apiSecret: "");
        Assert.False(lk.IsConfigured);
        Assert.Throws<InvalidOperationException>(() => lk.GenerateToken("r", "u", "n"));

        var created = await new CallService(t.Verify(), lk).CreateCallAsync(caller.Id, new CreateCallRequest(callee.Nik), default);
        Assert.True(created.Success);
        Assert.Null(created.Room!.LiveKitToken);
        Assert.Null(created.Room.LiveKitUrl);
    }

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
