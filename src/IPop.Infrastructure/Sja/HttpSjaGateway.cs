using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using IPop.Application.Sja;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.Retry;

namespace IPop.Infrastructure.Sja;

/// Adapter menuju SJA Notification Center (iDesk) untuk pairing perangkat via NIK.
///
/// Kontrak yang diikuti — diverifikasi pada hmac-auth.guard.ts, bukan draf spesifikasi:
///   POST {base}/v1/devices/activation/approve-s2s
///   header: x-sja-app-id, x-sja-timestamp (epoch ms), x-sja-signature
///   signature = HMAC-SHA256(secret, "{timestamp}.{rawBody}") heks huruf kecil
///   body: { "code": "123456", "nik": "00061058" }
///
/// Dua properti kontrak yang membentuk desain ini:
///   • guard menandatangani byte rawBody yang benar-benar diterima, jadi rawBody
///     diserialisasi sekali lalu dipakai untuk signature dan untuk konten HTTP;
///   • jendela timestamp penerima 5 menit, jauh di atas total waktu retry.
public class HttpSjaGateway : ISjaGateway
{
    private const int MinSecretLength = 32;

    private readonly HttpClient _http;
    private readonly ILogger<HttpSjaGateway> _logger;
    private readonly string _baseUrl;
    private readonly string _appId;
    private readonly string _appSecret;
    private readonly ResiliencePipeline _pipeline;

    public HttpSjaGateway(HttpClient http, IConfiguration config, ILogger<HttpSjaGateway> logger)
    {
        _http = http;
        _logger = logger;

        _baseUrl = (config["Sja:BaseUrl"] ?? "http://10.10.6.13:18302").TrimEnd('/');
        _appId = (config["Sja:AppId"] ?? "ipop").Trim();
        _appSecret = (config["Sja:AppSecret"] ?? string.Empty).Trim();

        _pipeline = new ResiliencePipelineBuilder()
            .AddRetry(new RetryStrategyOptions
            {
                MaxRetryAttempts = 2,
                Delay = TimeSpan.FromSeconds(1),
                ShouldHandle = new PredicateBuilder().Handle<HttpRequestException>(),
            })
            .AddTimeout(TimeSpan.FromSeconds(15))
            .Build();
    }

    public async Task<SjaDeviceLinkResult> ApproveDeviceActivationAsync(string code, string nik, CancellationToken ct = default)
    {
        // Fail-closed: tanpa secret yang layak, jangan kirim apa pun ke jaringan.
        if (string.IsNullOrWhiteSpace(_appSecret))
        {
            _logger.LogError("Sja:AppSecret belum diset - pairing perangkat ditolak (fail-closed)");
            return new SjaDeviceLinkResult(false, null, null, null, "SJA integration is not configured on this server.");
        }

        if (_appSecret.Length < MinSecretLength)
        {
            _logger.LogError("Sja:AppSecret terlalu pendek - pairing perangkat ditolak");
            return new SjaDeviceLinkResult(false, null, null, null, "SJA integration is not configured on this server.");
        }

        var rawBody = JsonSerializer.Serialize(new { code = code.Trim(), nik = nik.Trim() });
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(_appSecret));
        var signature = Convert.ToHexString(
            hmac.ComputeHash(Encoding.UTF8.GetBytes($"{timestamp}.{rawBody}"))).ToLowerInvariant();

        var endpoint = $"{_baseUrl}/v1/devices/activation/approve-s2s";

        HttpStatusCode status;
        string json;
        try
        {
            (status, json) = await _pipeline.ExecuteAsync<(HttpStatusCode, string)>(async attemptCt =>
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, endpoint)
                {
                    Content = new StringContent(rawBody, Encoding.UTF8, "application/json"),
                };
                req.Headers.Add("x-sja-app-id", _appId);
                req.Headers.Add("x-sja-timestamp", timestamp);
                req.Headers.Add("x-sja-signature", signature);

                using var resp = await _http.SendAsync(req, attemptCt);
                return (resp.StatusCode, await resp.Content.ReadAsStringAsync(attemptCt));
            }, ct);
        }
        catch (Exception ex)
        {
            // NIK tidak ikut di-log; hanya endpoint dan jenis kegagalannya.
            _logger.LogError(ex, "Gagal menghubungi SJA Notification Center di {Endpoint}", endpoint);
            return new SjaDeviceLinkResult(false, null, null, null, "Notification server could not be reached. Please try again shortly.");
        }

        if (status != HttpStatusCode.OK && status != HttpStatusCode.Created)
        {
            _logger.LogWarning("SJA menolak pairing perangkat, status {Status}", (int)status);
            return new SjaDeviceLinkResult(false, null, null, null,
                ExtractMessage(json) ?? "Activation code is invalid or has expired.");
        }

        return ParseSuccess(json);
    }

    /// Pesan kesalahan NestJS ada di properti "message", bisa berupa string atau array string.
    private static string? ExtractMessage(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("message", out var msg)) return null;

            return msg.ValueKind switch
            {
                JsonValueKind.String => msg.GetString(),
                JsonValueKind.Array => string.Join(", ", msg.EnumerateArray()
                    .Select(e => e.GetString())
                    .Where(s => !string.IsNullOrWhiteSpace(s))),
                _ => null,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private SjaDeviceLinkResult ParseSuccess(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var deviceName = root.TryGetProperty("deviceName", out var d) ? d.GetString() : null;
            var employeeName = root.TryGetProperty("employeeName", out var e) ? e.GetString() : null;

            var capabilities = new List<string>();
            if (root.TryGetProperty("capabilities", out var caps) && caps.ValueKind == JsonValueKind.Array)
            {
                foreach (var c in caps.EnumerateArray())
                {
                    var value = c.GetString();
                    if (!string.IsNullOrWhiteSpace(value)) capabilities.Add(value);
                }
            }

            return new SjaDeviceLinkResult(true, deviceName, employeeName, capabilities);
        }
        catch (JsonException ex)
        {
            // Server sudah menerima pairing; respons tak terbaca bukan alasan menyatakan gagal.
            _logger.LogError(ex, "Respons sukses dari SJA tidak dapat diurai");
            return new SjaDeviceLinkResult(true, null, null, Array.Empty<string>());
        }
    }
}
