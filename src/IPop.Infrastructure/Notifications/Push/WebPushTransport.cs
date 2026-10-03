using System.Net;
using PushSubscription = IPop.Core.Entities.PushSubscription;
using Lib.Net.Http.WebPush;
using Lib.Net.Http.WebPush.Authentication;
using Microsoft.Extensions.Logging;

namespace IPop.Infrastructure.Notifications.Push;

/// Sent = the push service accepted it (not proof Windows showed it). Gone = 404/410, the
/// subscription is dead. AuthError = 401/403 (VAPID/key problem: surfaced, never retried blindly,
/// never triggers key rotation). Rejected = other 4xx. Transient = 429/5xx/network/timeout.
public enum PushSendResult { Sent, Gone, AuthError, Rejected, Transient }

/// Code is a short sanitized label for diagnostics — never a response body or exception text.
public sealed record PushSendOutcome(PushSendResult Result, string? Code = null, TimeSpan? RetryAfter = null)
{
    public static readonly PushSendOutcome Accepted = new(PushSendResult.Sent);

    public static PushSendOutcome FromStatus(int status, TimeSpan? retryAfter = null) => status switch
    {
        >= 200 and < 300 => Accepted,
        404 or 410 => new(PushSendResult.Gone, "gone"),
        401 => new(PushSendResult.AuthError, "auth-401"),
        403 => new(PushSendResult.AuthError, "auth-403"),
        429 => new(PushSendResult.Transient, "rate-limited", retryAfter),
        >= 500 => new(PushSendResult.Transient, "server-error", retryAfter),
        413 => new(PushSendResult.Rejected, "payload-too-large"),
        _ => new(PushSendResult.Rejected, "rejected-4xx"),
    };
}

/// One encrypted message to one browser. Split out so tests can record sends without a push service.
public interface IWebPushTransport
{
    bool IsConfigured { get; }
    Task<PushSendOutcome> SendAsync(PushSubscription sub, string json, TimeSpan ttl, bool urgent, string? topic, CancellationToken ct);
}

public sealed class WebPushTransport : IWebPushTransport, IDisposable
{
    private readonly PushServiceClient client;
    private readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private readonly ILogger<WebPushTransport> log;

    public WebPushTransport(PushOptions options, ILogger<WebPushTransport> log)
    {
        this.log = log;
        client = new PushServiceClient(http)
        {
            DefaultAuthentication = new VapidAuthentication(options.VapidPublicKey!, options.VapidPrivateKey!) { Subject = options.Subject },
            DefaultAuthenticationScheme = VapidAuthenticationScheme.Vapid,
            AutoRetryAfter = false, // the outbox owns retries (bounded, inside the original TTL)
        };
    }

    public bool IsConfigured => true;

    public async Task<PushSendOutcome> SendAsync(PushSubscription sub, string json, TimeSpan ttl, bool urgent, string? topic, CancellationToken ct)
    {
        var target = new Lib.Net.Http.WebPush.PushSubscription { Endpoint = sub.Endpoint };
        target.SetKey(PushEncryptionKeyName.P256DH, sub.P256dh);
        target.SetKey(PushEncryptionKeyName.Auth, sub.Auth);
        var message = new PushMessage(json)
        {
            TimeToLive = Math.Max(0, (int)ttl.TotalSeconds),
            Urgency = urgent ? PushMessageUrgency.High : PushMessageUrgency.Normal,
            Topic = topic,
        };
        var provider = PushDispatcher.ProviderOf(sub.Endpoint);
        try
        {
            await client.RequestPushMessageDeliveryAsync(target, message, ct);
            return PushSendOutcome.Accepted;
        }
        catch (PushServiceClientException ex)
        {
            var outcome = PushSendOutcome.FromStatus((int)ex.StatusCode, RetryAfterOf(ex.Headers?.RetryAfter));
            if (outcome.Result != PushSendResult.Gone)
                log.LogWarning("Web push to {Provider} returned {Status} ({Code}).", provider, (int)ex.StatusCode, outcome.Code);
            return outcome;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            log.LogWarning("Web push to {Provider} timed out.", provider);
            return new(PushSendResult.Transient, "timeout");
        }
        catch (HttpRequestException ex)
        {
            log.LogWarning("Web push to {Provider} failed: network ({Error}).", provider, ex.HttpRequestError);
            return new(PushSendResult.Transient, "network");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning("Web push to {Provider} failed: {Error}.", provider, ex.GetType().Name);
            return new(PushSendResult.Rejected, "send-error");
        }
    }

    internal static TimeSpan? RetryAfterOf(System.Net.Http.Headers.RetryConditionHeaderValue? h) =>
        h?.Delta is { } d ? d
        : h?.Date is { } at ? (at - DateTimeOffset.UtcNow is var w && w > TimeSpan.Zero ? w : TimeSpan.Zero)
        : null;

    public void Dispose() => http.Dispose();
}

/// Push is not configured (no VAPID keys): nothing is sent.
public sealed class DisabledWebPushTransport : IWebPushTransport
{
    public bool IsConfigured => false;
    public Task<PushSendOutcome> SendAsync(PushSubscription sub, string json, TimeSpan ttl, bool urgent, string? topic, CancellationToken ct) =>
        Task.FromResult(new PushSendOutcome(PushSendResult.Rejected, "push-disabled"));
}
