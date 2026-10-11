using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using IPop.Application.Calls;
using Microsoft.Extensions.Configuration;

namespace IPop.Infrastructure.Calls;

public class LiveKitTokenService(IConfiguration config) : ILiveKitTokenService
{
    private string? ApiKey => config["Calls:LiveKit:ApiKey"] ?? config["LiveKit:ApiKey"];
    private string? ApiSecret => config["Calls:LiveKit:ApiSecret"] ?? config["LiveKit:ApiSecret"];
    public string? ServerUrl => NullIfBlank(config["Calls:LiveKit:Url"] ?? config["LiveKit:Url"]);
    public string? ServerApiUrl => NullIfBlank(config["Calls:LiveKit:ServerUrl"]) ?? ServerUrl;
    public bool IsConfigured => !string.IsNullOrWhiteSpace(ApiKey) && !string.IsNullOrWhiteSpace(ApiSecret) && ServerUrl is not null;

    public string GenerateToken(string roomName, string identity, string name, TimeSpan? ttl = null,
        IReadOnlyList<string>? sources = null, bool canUpdateOwnMetadata = false)
    {
        var key = ApiKey;
        var secret = ApiSecret;
        // Fail closed: never sign with a guessable secret; callers fall back to P2P.
        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(secret))
            throw new InvalidOperationException("LiveKit ApiKey/ApiSecret are not configured.");

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

        if (sources is not null) videoGrants["canPublishSources"] = sources;
        if (canUpdateOwnMetadata) videoGrants["canUpdateOwnMetadata"] = true;

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

    public string GenerateAdminToken(string? room, TimeSpan? ttl = null, bool record = false)
    {
        var key = ApiKey;
        var secret = ApiSecret;
        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(secret))
            throw new InvalidOperationException("LiveKit ApiKey/ApiSecret are not configured.");
        var now = DateTimeOffset.UtcNow;
        var grants = new Dictionary<string, object> { ["roomList"] = true };
        if (room is not null) { grants["roomAdmin"] = true; grants["roomCreate"] = true; grants["room"] = room; }
        if (record) grants["roomRecord"] = true;
        var payload = new Dictionary<string, object>
        {
            ["iss"] = key, ["sub"] = "ipop-server", ["nbf"] = now.ToUnixTimeSeconds(),
            ["exp"] = now.Add(ttl ?? TimeSpan.FromMinutes(1)).ToUnixTimeSeconds(), ["video"] = grants,
        };
        var head = Base64UrlEncode(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new Dictionary<string, object> { ["alg"] = "HS256", ["typ"] = "JWT" })));
        var body = Base64UrlEncode(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload)));
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        return $"{head}.{body}.{Base64UrlEncode(hmac.ComputeHash(Encoding.UTF8.GetBytes($"{head}.{body}")))}";
    }

    private static string? NullIfBlank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    private static string Base64UrlEncode(byte[] input)
    {
        var base64 = Convert.ToBase64String(input);
        return base64.TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
