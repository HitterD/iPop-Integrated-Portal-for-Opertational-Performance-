using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using IPop.Application.Calls;
using Microsoft.Extensions.Configuration;

namespace IPop.Infrastructure.Calls;

public class LiveKitTokenService(IConfiguration config) : ILiveKitTokenService
{
    private string? ApiKey => config["Calls:LiveKit:ApiKey"] ?? config["LiveKit:ApiKey"] ?? "devkey";
    private string? ApiSecret => config["Calls:LiveKit:ApiSecret"] ?? config["LiveKit:ApiSecret"] ?? "secret_key_livekit_ipop_32bytes_long!";
    public string? ServerUrl => config["Calls:LiveKit:Url"] ?? config["LiveKit:Url"] ?? "wss://livekit.sja.local";
    public bool IsConfigured => !string.IsNullOrWhiteSpace(ApiKey) && !string.IsNullOrWhiteSpace(ApiSecret);

    public string GenerateToken(string roomName, string identity, string name, TimeSpan? ttl = null)
    {
        var key = ApiKey;
        var secret = ApiSecret;
        if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(secret))
        {
            key = "devkey";
            secret = "secret_key_livekit_ipop_32bytes_long!";
        }

        var now = DateTimeOffset.UtcNow;
        var expires = now.Add(ttl ?? TimeSpan.FromHours(6));

        var header = new Dictionary<string, object>
        {
            ["alg"] = "HS256",
            ["typ"] = "JWT"
        };

        var videoGrants = new Dictionary<string, object>
        {
            ["room"] = roomName,
            ["roomJoin"] = true,
            ["canPublish"] = true,
            ["canSubscribe"] = true,
            ["canPublishData"] = true
        };

        var payload = new Dictionary<string, object>
        {
            ["iss"] = key,
            ["sub"] = identity,
            ["name"] = name,
            ["nbf"] = now.ToUnixTimeSeconds(),
            ["exp"] = expires.ToUnixTimeSeconds(),
            ["video"] = videoGrants
        };

        var headerJson = JsonSerializer.Serialize(header);
        var payloadJson = JsonSerializer.Serialize(payload);

        var headerBase64 = Base64UrlEncode(Encoding.UTF8.GetBytes(headerJson));
        var payloadBase64 = Base64UrlEncode(Encoding.UTF8.GetBytes(payloadJson));

        var stringToSign = $"{headerBase64}.{payloadBase64}";
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var signatureBytes = hmac.ComputeHash(Encoding.UTF8.GetBytes(stringToSign));
        var signatureBase64 = Base64UrlEncode(signatureBytes);

        return $"{stringToSign}.{signatureBase64}";
    }

    private static string Base64UrlEncode(byte[] input)
    {
        var base64 = Convert.ToBase64String(input);
        return base64.TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
