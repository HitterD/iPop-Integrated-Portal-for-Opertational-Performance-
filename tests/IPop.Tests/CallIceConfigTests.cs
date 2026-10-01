using IPop.Api.Endpoints;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace IPop.Tests;

/// Pentest V-02: /api/calls/config must never hand out a static TURN password.
public class CallIceConfigTests
{
    private static IConfiguration Cfg(Dictionary<string, string?> d) => new ConfigurationBuilder().AddInMemoryCollection(d).Build();

    [Fact]
    public void Static_turn_credentials_are_never_exposed()
    {
        var cfg = Cfg(new()
        {
            ["Calls:IceServers:0:urls:0"] = "stun:10.0.0.1:3478",
            ["Calls:IceServers:0:urls:1"] = "turn:10.0.0.1:3478?transport=udp",
            ["Calls:IceServers:0:username"] = "key",
            ["Calls:IceServers:0:credential"] = "the-livekit-secret",
        });
        var s = Assert.Single(CallEndpoints.BuildIceServers(cfg, "0001", DateTimeOffset.UtcNow));
        Assert.Equal(["stun:10.0.0.1:3478"], s.Urls);
        Assert.Null(s.Username);
        Assert.Null(s.Credential);
    }

    [Fact]
    public void Shared_secret_yields_short_lived_hmac_credentials()
    {
        var cfg = Cfg(new()
        {
            ["Calls:IceServers:0:urls"] = "turn:10.0.0.1:3478",
            ["Calls:IceServers:0:sharedSecret"] = "s3cret",
        });
        var now = DateTimeOffset.FromUnixTimeSeconds(1_000_000);
        var s = Assert.Single(CallEndpoints.BuildIceServers(cfg, "0001", now));
        var expiry = 1_000_000 + (long)CallEndpoints.TurnCredentialLifetime.TotalSeconds;
        Assert.Equal($"{expiry}:0001", s.Username);
        using var h = new System.Security.Cryptography.HMACSHA1("s3cret"u8.ToArray());
        Assert.Equal(Convert.ToBase64String(h.ComputeHash(System.Text.Encoding.UTF8.GetBytes(s.Username!))), s.Credential);
        Assert.DoesNotContain("s3cret", s.Credential);
    }

    [Fact]
    public void Turn_only_server_without_secret_is_dropped_and_stun_fallback_used()
    {
        var cfg = Cfg(new() { ["Calls:IceServers:0:urls"] = "turn:10.0.0.1:3478", ["Calls:IceServers:0:credential"] = "x" });
        var s = Assert.Single(CallEndpoints.BuildIceServers(cfg, "0001", DateTimeOffset.UtcNow));
        Assert.Equal(["stun:stun.l.google.com:19302"], s.Urls);
    }
}
