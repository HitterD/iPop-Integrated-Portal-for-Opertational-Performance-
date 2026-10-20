using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using PushSubscription = IPop.Core.Entities.PushSubscription;

namespace IPop.Infrastructure.Notifications.Push;

/// Firebase Cloud Messaging for the native Android app (HTTP v1 API, free tier, no billing).
/// Server side: a service-account JSON (Fcm:ServiceAccountPath) signs a short-lived OAuth token.
/// App side: the public client values (ApplicationId, ApiKey, SenderId) are served by
/// GET /api/push/native/config, so the APK carries no google-services.json.
public sealed class FcmOptions
{
    public string? ProjectId { get; set; }
    /// Path to the service-account JSON downloaded from Firebase (Project settings → Service accounts). Never commit it.
    public string? ServiceAccountPath { get; set; }
    /// Android app registration (Project settings → General → Your apps). Public values.
    public string? ApplicationId { get; set; }
    public string? ApiKey { get; set; }
    public string? SenderId { get; set; }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ProjectId) && !string.IsNullOrWhiteSpace(ServiceAccountPath)
        && File.Exists(ServiceAccountPath) && !string.IsNullOrWhiteSpace(ApplicationId) && !string.IsNullOrWhiteSpace(ApiKey)
        && !string.IsNullOrWhiteSpace(SenderId);

    public static FcmOptions Bind(IConfiguration cfg) => cfg.GetSection("Fcm").Get<FcmOptions>() ?? new FcmOptions();
}

/// Native-app devices live in PushSubscriptions so the outbox, retries, diagnostics and the device
/// list work unchanged: Endpoint = "fcm-native:" + FCM registration token, keys unused.
public static class NativePush
{
    public const string EndpointPrefix = "fcm-native:";
    public const string Label = "iPop app · Android";
    public static bool IsNative(string? endpoint) => endpoint?.StartsWith(EndpointPrefix, StringComparison.Ordinal) == true;
    public static string EndpointOf(string token) => EndpointPrefix + token;
    public static string TokenOf(string endpoint) => endpoint[EndpointPrefix.Length..];

    /// Notification channel on the phone (Android lets the user set a sound per channel).
    public static string ChannelOf(PushPayload p)
    {
        if (p.Kind is "call" or "close" && p.Tag.StartsWith("call:")) return "calls";
        if (p.Kind == "chat") return "chat";
        var url = p.Url ?? "";
        return url.StartsWith("/mail") ? "mail"
            : url.StartsWith("/calendar") || url.StartsWith("/calls") || url.StartsWith("/meet") ? "meetings"
            : url.StartsWith("/tickets") || url.StartsWith("/idesk") ? "tickets"
            : url.StartsWith("/news") ? "news"
            : url.StartsWith("/chat") ? "chat"
            : "system";
    }

    /// FCM data map (all strings). Data-only, so the app always builds the notification itself.
    public static Dictionary<string, string> DataOf(PushPayload p)
    {
        var d = new Dictionary<string, string>
        {
            ["kind"] = p.Kind, ["title"] = p.Title, ["body"] = p.Body, ["tag"] = p.Tag, ["channel"] = ChannelOf(p),
        };
        if (p.Url is { } url) d["url"] = url;
        if (p.CallId is { } callId) d["callId"] = callId.ToString();
        if (p.ActionToken is { } token) d["actionToken"] = token;
        if (p.ApiBase is { } apiBase) d["apiBase"] = apiBase;
        if (p.DeliveryId is { } deliveryId) d["deliveryId"] = deliveryId;
        if (p.RequireInteraction) d["urgent"] = "1";
        if (p.Sender is { Length: > 0 } sender) d["sender"] = sender;
        if (PushDispatcher.ChatRoomOf(p.Url) is { } roomId) d["roomId"] = roomId.ToString();
        return d;
    }
}

/// Sends to native-app devices. Split out so tests can record sends without Google.
public interface INativePushTransport
{
    bool IsConfigured { get; }
    Task<PushSendOutcome> SendAsync(PushSubscription sub, PushPayload payload, TimeSpan ttl, bool urgent, CancellationToken ct);
}

public sealed class DisabledNativePushTransport : INativePushTransport
{
    public bool IsConfigured => false;
    public Task<PushSendOutcome> SendAsync(PushSubscription sub, PushPayload payload, TimeSpan ttl, bool urgent, CancellationToken ct) =>
        Task.FromResult(new PushSendOutcome(PushSendResult.Rejected, "native-disabled"));
}

public sealed class FcmTransport : INativePushTransport, IDisposable
{
    public bool IsConfigured => true;
    private readonly FcmOptions options;
    private readonly ILogger log;
    private readonly HttpClient http;
    private readonly SemaphoreSlim tokenLock = new(1, 1);
    private string? accessToken;
    private DateTime accessTokenExpires;
    private readonly ServiceAccount account;

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private sealed record ServiceAccount(
        [property: JsonPropertyName("client_email")] string ClientEmail,
        [property: JsonPropertyName("private_key")] string PrivateKey,
        [property: JsonPropertyName("token_uri")] string? TokenUri);

    public FcmTransport(FcmOptions options, ILogger<FcmTransport> log) : this(options, (ILogger)log, null) { }

    internal FcmTransport(FcmOptions options, ILogger log, HttpMessageHandler? handler)
    {
        this.options = options;
        this.log = log;
        http = handler is null ? new HttpClient() : new HttpClient(handler);
        http.Timeout = TimeSpan.FromSeconds(15);
        account = JsonSerializer.Deserialize<ServiceAccount>(File.ReadAllText(options.ServiceAccountPath!))
            ?? throw new InvalidOperationException("Fcm:ServiceAccountPath is not a service-account JSON.");
    }

    public async Task<PushSendOutcome> SendAsync(PushSubscription sub, PushPayload payload, TimeSpan ttl, bool urgent, CancellationToken ct)
    {
        var body = new
        {
            message = new
            {
                token = NativePush.TokenOf(sub.Endpoint),
                data = NativePush.DataOf(payload),
                android = new
                {
                    // High priority wakes the phone out of Doze, which a call and a chat need right away.
                    priority = urgent || payload.Kind is "chat" or "call" or "close" ? "HIGH" : "NORMAL",
                    ttl = $"{Math.Max(0, (int)ttl.TotalSeconds)}s",
                    collapse_key = payload.Kind == "chat" ? payload.Tag : null,
                },
            },
        };
        try
        {
            var token = await AccessTokenAsync(ct);
            using var req = new HttpRequestMessage(HttpMethod.Post, $"https://fcm.googleapis.com/v1/projects/{options.ProjectId}/messages:send")
            {
                Content = JsonContent.Create(body, options: Json),
            };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var res = await http.SendAsync(req, ct);
            if (res.IsSuccessStatusCode) return PushSendOutcome.Accepted;
            var text = await res.Content.ReadAsStringAsync(ct);
            var outcome = OutcomeOf((int)res.StatusCode, text, WebPushTransport.RetryAfterOf(res.Headers.RetryAfter));
            if (outcome.Result == PushSendResult.AuthError) { accessToken = null; }
            if (outcome.Result != PushSendResult.Gone)
                log.LogWarning("FCM returned {Status} ({Code}).", (int)res.StatusCode, outcome.Code);
            return outcome;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            log.LogWarning("FCM send timed out.");
            return new(PushSendResult.Transient, "timeout");
        }
        catch (HttpRequestException ex)
        {
            log.LogWarning("FCM send failed: network ({Error}).", ex.HttpRequestError);
            return new(PushSendResult.Transient, "network");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning("FCM send failed: {Error}.", ex.GetType().Name);
            return new(PushSendResult.Rejected, "send-error");
        }
    }

    /// FCM v1 errors: 404 UNREGISTERED and 400 for a malformed/foreign token mean the device is gone.
    internal static PushSendOutcome OutcomeOf(int status, string body, TimeSpan? retryAfter)
    {
        var unregistered = body.Contains("UNREGISTERED", StringComparison.Ordinal)
            || (status == 400 && body.Contains("registration token", StringComparison.OrdinalIgnoreCase))
            || body.Contains("SENDER_ID_MISMATCH", StringComparison.Ordinal);
        if (unregistered) return new(PushSendResult.Gone, "gone");
        return status switch
        {
            401 or 403 => new(PushSendResult.AuthError, $"auth-{status}"),
            429 => new(PushSendResult.Transient, "rate-limited", retryAfter),
            >= 500 => new(PushSendResult.Transient, "server-error", retryAfter),
            _ => new(PushSendResult.Rejected, "rejected-4xx"),
        };
    }

    private async Task<string> AccessTokenAsync(CancellationToken ct)
    {
        if (accessToken is { } t && DateTime.UtcNow < accessTokenExpires) return t;
        await tokenLock.WaitAsync(ct);
        try
        {
            if (accessToken is { } t2 && DateTime.UtcNow < accessTokenExpires) return t2;
            var tokenUri = account.TokenUri ?? "https://oauth2.googleapis.com/token";
            var assertion = SignedJwt(account.ClientEmail, account.PrivateKey, tokenUri, DateTimeOffset.UtcNow);
            using var res = await http.PostAsync(tokenUri, new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:jwt-bearer",
                ["assertion"] = assertion,
            }), ct);
            if (!res.IsSuccessStatusCode)
            {
                log.LogError("FCM OAuth token request failed with {Status}.", (int)res.StatusCode);
                throw new HttpRequestException("FCM OAuth token request failed.", null, res.StatusCode);
            }
            var json = await res.Content.ReadFromJsonAsync<JsonElement>(ct);
            accessToken = json.GetProperty("access_token").GetString();
            var expiresIn = json.TryGetProperty("expires_in", out var e) ? e.GetInt32() : 3600;
            accessTokenExpires = DateTime.UtcNow.AddSeconds(Math.Max(60, expiresIn - 300));
            return accessToken!;
        }
        finally { tokenLock.Release(); }
    }

    /// RS256 JWT for Google's OAuth "jwt-bearer" grant, scoped to FCM only.
    internal static string SignedJwt(string clientEmail, string privateKeyPem, string audience, DateTimeOffset now)
    {
        static string B64(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var header = B64(JsonSerializer.SerializeToUtf8Bytes(new { alg = "RS256", typ = "JWT" }));
        var claims = B64(JsonSerializer.SerializeToUtf8Bytes(new
        {
            iss = clientEmail,
            scope = "https://www.googleapis.com/auth/firebase.messaging",
            aud = audience,
            iat = now.ToUnixTimeSeconds(),
            exp = now.AddMinutes(55).ToUnixTimeSeconds(),
        }));
        using var rsa = RSA.Create();
        rsa.ImportFromPem(privateKeyPem);
        var sig = rsa.SignData(Encoding.ASCII.GetBytes($"{header}.{claims}"), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return $"{header}.{claims}.{B64(sig)}";
    }

    public void Dispose()
    {
        http.Dispose();
        tokenLock.Dispose();
    }
}
