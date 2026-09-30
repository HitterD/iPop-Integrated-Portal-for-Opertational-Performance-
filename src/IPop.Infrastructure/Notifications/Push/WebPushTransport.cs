using System.Net;
using PushSubscription = IPop.Core.Entities.PushSubscription;
using Lib.Net.Http.WebPush;
using Lib.Net.Http.WebPush.Authentication;
using Microsoft.Extensions.Logging;

namespace IPop.Infrastructure.Notifications.Push;

public enum PushSendResult { Sent, Gone, Failed }

/// One encrypted message to one browser. Split out so tests can record sends without a push service.
public interface IWebPushTransport
{
    bool IsConfigured { get; }
    Task<PushSendResult> SendAsync(PushSubscription sub, string json, TimeSpan ttl, bool urgent, string? topic, CancellationToken ct);
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
        };
    }

    public bool IsConfigured => true;

    public async Task<PushSendResult> SendAsync(PushSubscription sub, string json, TimeSpan ttl, bool urgent, string? topic, CancellationToken ct)
    {
        var target = new Lib.Net.Http.WebPush.PushSubscription { Endpoint = sub.Endpoint };
        target.SetKey(PushEncryptionKeyName.P256DH, sub.P256dh);
        target.SetKey(PushEncryptionKeyName.Auth, sub.Auth);
        var message = new PushMessage(json)
        {
            TimeToLive = (int)ttl.TotalSeconds,
            Urgency = urgent ? PushMessageUrgency.High : PushMessageUrgency.Normal,
            Topic = topic,
        };
        try
        {
            await client.RequestPushMessageDeliveryAsync(target, message, ct);
            return PushSendResult.Sent;
        }
        catch (PushServiceClientException ex) when (ex.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
        {
            return PushSendResult.Gone; // browser unsubscribed or the subscription expired
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            log.LogWarning(ex, "Web push to {Host} failed.", new Uri(sub.Endpoint).Host);
            return PushSendResult.Failed;
        }
    }

    public void Dispose() => http.Dispose();
}

/// Push is not configured (no VAPID keys): nothing is sent.
public sealed class DisabledWebPushTransport : IWebPushTransport
{
    public bool IsConfigured => false;
    public Task<PushSendResult> SendAsync(PushSubscription sub, string json, TimeSpan ttl, bool urgent, string? topic, CancellationToken ct) =>
        Task.FromResult(PushSendResult.Failed);
}
