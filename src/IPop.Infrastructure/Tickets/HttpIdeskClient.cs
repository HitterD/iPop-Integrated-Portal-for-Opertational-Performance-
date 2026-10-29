using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using IPop.Application.Tickets;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.Retry;
using Polly.Timeout;

namespace IPop.Infrastructure.Tickets;

/// Connection settings for the iDesk "ipop" integration. A secret under 32 chars counts as
/// missing, so a half-configured server fails closed rather than signing with a weak key.
public sealed class IdeskOptions
{
    public const int MinSecretLength = 32;
    public string BaseUrl { get; init; } = "https://idesk.santos.co.id/api/v1/integrations/ipop/";
    public string ClientId { get; init; } = "ipop";
    public string ClientSecret { get; init; } = "";
    /// Where users open iDesk; links iDesk sends with another host (its LAN address) are moved here.
    public string PublicUrl { get; init; } = DefaultPublicUrl;
    public const string DefaultPublicUrl = "https://idesk.santos.co.id";
    /// The address the parsers below use (static so webhook parsing needs no instance); set from config at startup.
    public static string CurrentPublicUrl { get; set; } = DefaultPublicUrl;
    /// IDesk:SignInWithIpop — iDesk links open through iDesk's "Sign in with iPop" (needs the OIDC client on both sides).
    public static bool CurrentSignInWithIpop { get; set; }
    public bool IsConfigured => ClientSecret.Length >= MinSecretLength;

    public static IdeskOptions Bind(IConfiguration config)
    {
        var baseUrl = (config["IDesk:BaseUrl"] ?? "").Trim();
        var publicUrl = (config["IDesk:PublicUrl"] ?? "").Trim();
        CurrentPublicUrl = string.IsNullOrEmpty(publicUrl) ? DefaultPublicUrl : publicUrl.TrimEnd('/');
        CurrentSignInWithIpop = config.GetValue<bool>("IDesk:SignInWithIpop");
        return new IdeskOptions
        {
            PublicUrl = CurrentPublicUrl,
            BaseUrl = string.IsNullOrEmpty(baseUrl) ? "https://idesk.santos.co.id/api/v1/integrations/ipop/" : baseUrl.TrimEnd('/') + "/",
            ClientId = string.IsNullOrWhiteSpace(config["IDesk:ClientId"]) ? "ipop" : config["IDesk:ClientId"]!.Trim(),
            ClientSecret = (config["IDesk:ClientSecret"] ?? "").Trim(),
        };
    }
}

/// HMAC scheme shared by outgoing calls and the incoming webhook:
/// lowercase hex HMAC-SHA256(secret, "{timestampMs}.{rawBody}").
public static class IdeskSignature
{
    public const string ClientIdHeader = "x-ipop-client-id";
    public const string TimestampHeader = "x-ipop-timestamp";
    public const string SignatureHeader = "x-ipop-signature";
    public static readonly TimeSpan MaxSkew = TimeSpan.FromMinutes(5);

    public static string Sign(string secret, string timestamp, string body)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes($"{timestamp}.{body}"))).ToLowerInvariant();
    }

    /// Timestamp within ±5 min and a constant-time signature match.
    public static bool Verify(string secret, string? timestamp, string? signature, string body, DateTimeOffset now)
    {
        if (string.IsNullOrEmpty(timestamp) || string.IsNullOrEmpty(signature)) return false;
        if (!long.TryParse(timestamp, NumberStyles.None, CultureInfo.InvariantCulture, out var ms)) return false;
        if (Math.Abs(now.ToUnixTimeMilliseconds() - ms) > MaxSkew.TotalMilliseconds) return false;
        var expected = Encoding.UTF8.GetBytes(Sign(secret, timestamp, body));
        return CryptographicOperations.FixedTimeEquals(expected, Encoding.UTF8.GetBytes(signature.Trim().ToLowerInvariant()));
    }
}

/// Adapter for the iDesk integration API, modelled on HttpSjaGateway: each body is serialised once
/// and those exact bytes are signed and sent. Every call returns an <see cref="IdeskResult{T}"/>;
/// transport trouble maps to Unreachable so the outbox can try again with the same externalRef.
public sealed class HttpIdeskClient : IIdeskClient
{
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(20);

    private readonly HttpClient _http;
    private readonly IdeskOptions _options;
    private readonly ILogger<HttpIdeskClient> _logger;
    private readonly ResiliencePipeline<HttpResponseMessage> _reads;
    private readonly ResiliencePipeline<HttpResponseMessage> _writes;

    public HttpIdeskClient(HttpClient http, IdeskOptions options, ILogger<HttpIdeskClient> logger)
    {
        _http = http;
        _options = options;
        _logger = logger;
        _http.Timeout = Timeout.InfiniteTimeSpan;   // Polly owns the per-attempt timeout

        // GETs retry once on transport errors and timeouts. POST /tickets is idempotent too, but a
        // large upload that timed out once is better left to the outbox than resent while the
        // user waits, so it only retries a connection that failed outright.
        // Only 502/504 (a proxy that lost the backend for a moment) are worth a second try: iDesk's
        // own 503 means "integration not configured" and comes back the same, just a second later.
        _reads = Pipeline(1, new PredicateBuilder<HttpResponseMessage>()
            .Handle<HttpRequestException>().Handle<TimeoutRejectedException>()
            .HandleResult(r => r.StatusCode is HttpStatusCode.BadGateway or HttpStatusCode.GatewayTimeout));
        _writes = Pipeline(1, new PredicateBuilder<HttpResponseMessage>().Handle<HttpRequestException>());
    }

    private static ResiliencePipeline<HttpResponseMessage> Pipeline(int retries, PredicateBuilder<HttpResponseMessage> handle) =>
        new ResiliencePipelineBuilder<HttpResponseMessage>()
            .AddRetry(new RetryStrategyOptions<HttpResponseMessage>
            {
                MaxRetryAttempts = retries,
                Delay = TimeSpan.FromMilliseconds(300),
                ShouldHandle = handle,
            })
            .AddTimeout(CallTimeout)
            .Build();

    public bool IsConfigured => _options.IsConfigured;

    public async Task<IdeskResult<IReadOnlyList<TicketModuleDto>>> GetModulesAsync(CancellationToken ct = default)
    {
        var (outcome, json, msg) = await SendAsync(HttpMethod.Get, "modules", null, ct);
        if (outcome != IdeskOutcome.Ok) return new(outcome, null, msg);
        try
        {
            using var doc = JsonDocument.Parse(json!);
            var list = doc.RootElement.EnumerateArray().Select(m => new TicketModuleDto(
                Str(m, "slug") ?? "",
                Str(m, "name") ?? Str(m, "slug") ?? "",
                Str(m, "description"),
                Strings(m, "categories"),
                Str(m, "defaultCategory"),
                Strings(m, "priorities"),
                m.TryGetProperty("supportsHardwareInstallation", out var h) && h.ValueKind == JsonValueKind.True))
                .Where(m => m.Slug.Length > 0).ToList();
            return new(IdeskOutcome.Ok, list);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            _logger.LogError(ex, "iDesk /modules answer could not be parsed");
            return new(IdeskOutcome.Unreachable, null, "Unexpected answer from iDesk.");
        }
    }

    public async Task<IdeskResult<IdeskTicket>> CreateTicketAsync(string jsonBody, CancellationToken ct = default)
    {
        var (outcome, json, msg) = await SendAsync(HttpMethod.Post, "tickets", jsonBody, ct);
        return outcome == IdeskOutcome.Ok ? ParseOne(json!) : new(outcome, null, msg);
    }

    public async Task<IdeskResult<IdeskTicket>> GetTicketAsync(string ticketNumber, CancellationToken ct = default)
    {
        var (outcome, json, msg) = await SendAsync(HttpMethod.Get, "tickets/" + Uri.EscapeDataString(ticketNumber), null, ct);
        return outcome == IdeskOutcome.Ok ? ParseOne(json!) : new(outcome, null, msg);
    }

    public async Task<IdeskResult<IReadOnlyList<IdeskTicket>>> ListByNikAsync(string nik, string state, CancellationToken ct = default)
    {
        var path = $"tickets?nik={Uri.EscapeDataString(nik)}&state={Uri.EscapeDataString(state)}";
        var (outcome, json, msg) = await SendAsync(HttpMethod.Get, path, null, ct);
        if (outcome != IdeskOutcome.Ok) return new(outcome, null, msg);
        try
        {
            using var doc = JsonDocument.Parse(json!);
            // Accept a bare array or a { data|items|tickets: [...] } envelope.
            var arr = doc.RootElement;
            if (arr.ValueKind == JsonValueKind.Object)
                foreach (var key in new[] { "data", "items", "tickets" })
                    if (arr.TryGetProperty(key, out var inner) && inner.ValueKind == JsonValueKind.Array) { arr = inner; break; }
            if (arr.ValueKind != JsonValueKind.Array) throw new JsonException("not an array");
            return new(IdeskOutcome.Ok, arr.EnumerateArray().Select(ParseTicket).OfType<IdeskTicket>().ToList());
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            _logger.LogError(ex, "iDesk ticket list could not be parsed");
            return new(IdeskOutcome.Unreachable, null, "Unexpected answer from iDesk.");
        }
    }

    public async Task<IdeskResult<IdeskMessages>> GetMessagesAsync(string ticketNumber, CancellationToken ct = default)
    {
        var (outcome, json, msg) = await SendAsync(HttpMethod.Get, "tickets/" + Uri.EscapeDataString(ticketNumber) + "/messages", null, ct);
        if (outcome != IdeskOutcome.Ok) return new(outcome, null, msg);
        try
        {
            using var doc = JsonDocument.Parse(json!);
            var root = doc.RootElement;
            var list = root.TryGetProperty("messages", out var arr) && arr.ValueKind == JsonValueKind.Array
                ? arr.EnumerateArray().Select(ParseMessage).OfType<IdeskMessage>().ToList()
                : [];
            return new(IdeskOutcome.Ok, new IdeskMessages(PublicLink(Str(root, "url")), list));
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            _logger.LogError(ex, "iDesk ticket messages could not be parsed");
            return new(IdeskOutcome.Unreachable, null, "Unexpected answer from iDesk.");
        }
    }

    public async Task<IdeskResult<bool>> ReplyAsync(string ticketRef, string nik, string content, CancellationToken ct = default)
    {
        var body = JsonSerializer.Serialize(new { nik, content });
        var (outcome, _, msg) = await SendAsync(HttpMethod.Post, "tickets/" + Uri.EscapeDataString(ticketRef) + "/messages", body, ct);
        return new(outcome, outcome == IdeskOutcome.Ok, msg);
    }

    public async Task<IdeskResult<IReadOnlyList<IdeskBooking>>> ListZoomBookingsAsync(string nik, DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        var path = $"zoom-bookings?nik={Uri.EscapeDataString(nik)}&from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}";
        var (outcome, json, msg) = await SendAsync(HttpMethod.Get, path, null, ct);
        if (outcome != IdeskOutcome.Ok) return new(outcome, null, msg);
        try
        {
            using var doc = JsonDocument.Parse(json!);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) throw new JsonException("not an array");
            return new(IdeskOutcome.Ok, doc.RootElement.EnumerateArray().Select(ParseBooking).OfType<IdeskBooking>().ToList());
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            _logger.LogError(ex, "iDesk zoom bookings could not be parsed");
            return new(IdeskOutcome.Unreachable, null, "Unexpected answer from iDesk.");
        }
    }

    private async Task<(IdeskOutcome, string?, string?)> SendAsync(HttpMethod method, string path, string? body, CancellationToken ct)
    {
        if (!_options.IsConfigured)
        {
            _logger.LogError("IDesk:ClientSecret is missing or shorter than {Min} chars - iDesk call refused (fail closed)", IdeskOptions.MinSecretLength);
            return (IdeskOutcome.NotConfigured, null, "iDesk is not configured on this server.");
        }

        var endpoint = new Uri(new Uri(_options.BaseUrl), path);
        var pipeline = method == HttpMethod.Get ? _reads : _writes;
        HttpStatusCode status;
        string json;
        try
        {
            using var resp = await pipeline.ExecuteAsync(async attemptCt =>
            {
                // Fresh timestamp per attempt; the signed bytes are the bytes on the wire.
                var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
                var req = new HttpRequestMessage(method, endpoint);
                if (body is not null) req.Content = new StringContent(body, Encoding.UTF8, "application/json");
                req.Headers.Add(IdeskSignature.ClientIdHeader, _options.ClientId);
                req.Headers.Add(IdeskSignature.TimestampHeader, timestamp);
                req.Headers.Add(IdeskSignature.SignatureHeader, IdeskSignature.Sign(_options.ClientSecret, timestamp, body ?? ""));
                return await _http.SendAsync(req, attemptCt);
            }, ct);
            status = resp.StatusCode;
            json = await resp.Content.ReadAsStringAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            // No NIKs or bodies in the log: only the path without its query and the failure kind.
            _logger.LogWarning(ex, "iDesk {Method} {Path} failed", method, endpoint.AbsolutePath);
            return (IdeskOutcome.Unreachable, null, "iDesk could not be reached.");
        }

        var code = (int)status;
        if (code is >= 200 and < 300) return (IdeskOutcome.Ok, json, null);
        var message = ExtractMessage(json);
        _logger.LogWarning("iDesk {Method} {Path} answered {Status}", method, endpoint.AbsolutePath, code);
        return code switch
        {
            404 => (IdeskOutcome.NotFound, null, message),
            422 => (IdeskOutcome.NotRegistered, null, message),
            // 401/403 = clock or secret mismatch, 408/429/5xx = transient: none is the user's fault.
            401 or 403 or 408 or 429 or >= 500 => (IdeskOutcome.Unreachable, null, message ?? "iDesk could not be reached."),
            _ => (IdeskOutcome.Rejected, null, message ?? "iDesk rejected the ticket."),
        };
    }

    private IdeskResult<IdeskTicket> ParseOne(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var t = ParseTicket(doc.RootElement);
            if (t is not null) return new(IdeskOutcome.Ok, t);
        }
        catch (JsonException) { }
        _logger.LogError("iDesk ticket answer could not be parsed");
        return new(IdeskOutcome.Unreachable, null, "Unexpected answer from iDesk.");
    }

    /// Section 4 shape of the iDesk contract; also used for webhook payloads.
    public static IdeskTicket? ParseTicket(JsonElement t)
    {
        if (t.ValueKind != JsonValueKind.Object) return null;
        var id = Str(t, "ticketId");
        if (string.IsNullOrEmpty(id)) return null;
        var requester = t.TryGetProperty("requester", out var r) && r.ValueKind == JsonValueKind.Object ? r : default;
        var assigneeEl = t.TryGetProperty("assignee", out var a) && a.ValueKind == JsonValueKind.Object ? a : default;
        var assignee = assigneeEl.ValueKind == JsonValueKind.Object ? Str(assigneeEl, "name") : null;
        var created = Date(t, "createdAt") ?? DateTime.UtcNow;
        return new IdeskTicket(
            id,
            Str(t, "ticketNumber"),
            Str(t, "externalRef"),
            Str(t, "origin") ?? (Str(t, "externalRef") is null ? "IDESK" : "IPOP"),
            requester.ValueKind == JsonValueKind.Object ? Str(requester, "nik") : null,
            requester.ValueKind == JsonValueKind.Object ? Str(requester, "name") : null,
            Str(t, "status") ?? TicketStatus.Todo,
            Str(t, "priority") ?? "MEDIUM",
            Str(t, "module") ?? "it-support",
            Str(t, "category"),
            Str(t, "title") ?? "",
            assignee,
            Date(t, "slaTarget"),
            created,
            Date(t, "updatedAt") ?? created,
            Date(t, "resolvedAt"),
            Date(t, "closedAt"),
            PublicLink(Str(t, "url")),
            ParseVisit(t),
            assigneeEl.ValueKind == JsonValueKind.Object ? Str(assigneeEl, "nik") : null,
            Date(t, "resolutionDeadlineAt"));
    }

    private static IdeskVisit? ParseVisit(JsonElement t)
    {
        if (!t.TryGetProperty("visit", out var v) || v.ValueKind != JsonValueKind.Object) return null;
        return Date(v, "start") is { } start && Date(v, "end") is { } end && end > start
            ? new IdeskVisit(start, end, Str(v, "hardwareType")) : null;
    }

    private static IdeskMessage? ParseMessage(JsonElement m)
    {
        if (m.ValueKind != JsonValueKind.Object || Str(m, "id") is not { Length: > 0 } id) return null;
        var sender = m.TryGetProperty("sender", out var s) && s.ValueKind == JsonValueKind.Object ? Str(s, "name") : null;
        var files = m.TryGetProperty("attachments", out var a) && a.ValueKind == JsonValueKind.Array
            ? a.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.Object ? Str(x, "name") : null).OfType<string>().ToList()
            : [];
        return new IdeskMessage(id, sender, m.TryGetProperty("fromRequester", out var f) && f.ValueKind == JsonValueKind.True,
            Str(m, "content") ?? "", files, Date(m, "createdAt") ?? DateTime.UtcNow);
    }

    /// Section 7.1 shape; also the webhook "booking" payload. A cancelled event carries only the ids.
    public static IdeskBooking? ParseBooking(JsonElement b)
    {
        if (b.ValueKind != JsonValueKind.Object || Str(b, "bookingId") is not { Length: > 0 } id) return null;
        if (Date(b, "start") is not { } start || Date(b, "end") is not { } end) return null;
        var zoom = b.TryGetProperty("zoom", out var z) && z.ValueKind == JsonValueKind.Object ? z : default;
        var account = b.TryGetProperty("account", out var acc) && acc.ValueKind == JsonValueKind.Object ? Str(acc, "name") : null;
        var people = b.TryGetProperty("attendees", out var at) && at.ValueKind == JsonValueKind.Array
            ? at.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Object)
                .Select(x => new IdeskBookingAttendee(Str(x, "nik"), Str(x, "name") ?? "", Str(x, "role") ?? "PARTICIPANT")).ToList()
            : [];
        return new IdeskBooking(id, Str(b, "seriesId"), Str(b, "title") ?? "Zoom meeting", Str(b, "description"), start, end, account,
            zoom.ValueKind == JsonValueKind.Object ? Str(zoom, "joinUrl") : null,
            zoom.ValueKind == JsonValueKind.Object ? Str(zoom, "meetingId") : null,
            zoom.ValueKind == JsonValueKind.Object ? Str(zoom, "passcode") : null,
            people, PublicLink(Str(b, "url")));
    }

    /// iDesk links as users must open them (public host, see <see cref="IdeskLinks"/>).
    public static string? PublicLink(string? url) => IdeskLinks.Public(url, IdeskOptions.CurrentPublicUrl, IdeskOptions.CurrentSignInWithIpop);

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static IReadOnlyList<string> Strings(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList()
            : [];

    private static DateTime? Date(JsonElement e, string name) =>
        Str(e, name) is { } s && DateTime.TryParse(s, CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d)
            ? DateTime.SpecifyKind(d, DateTimeKind.Utc) : null;

    /// NestJS puts errors in "message", either a string or an array of strings.
    private static string? ExtractMessage(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("message", out var msg)) return null;
            return msg.ValueKind switch
            {
                JsonValueKind.String => msg.GetString(),
                JsonValueKind.Array => string.Join(", ", msg.EnumerateArray().Select(e => e.ToString()).Where(s => !string.IsNullOrWhiteSpace(s))),
                _ => null,
            };
        }
        catch (JsonException) { return null; }
    }
}
