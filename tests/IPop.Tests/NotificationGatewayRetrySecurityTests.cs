using IPop.Api.Endpoints;
using IPop.Application.Notifications;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace IPop.Tests;

public sealed class NotificationGatewayRetrySecurityTests
{
    [Fact]
    public async Task Retry_authentication_requires_both_client_id_and_secret_and_forwards_ip()
    {
        var service = new RecordingNotificationService();
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("10.0.0.8");
        context.Request.Headers["X-Client-Id"] = "billing";
        context.Request.Headers["X-Client-Secret"] = "secret";

        var client = await NotificationGatewayEndpoints.AuthenticateRetryClientAsync(context, service, default);

        Assert.NotNull(client);
        Assert.Equal("billing", service.ClientId);
        Assert.Equal("secret", service.Secret);
        Assert.Equal("10.0.0.8", service.RemoteIp);
    }

    [Fact]
    public async Task Retry_authentication_rejects_missing_secret_before_calling_service()
    {
        var service = new RecordingNotificationService();
        var context = new DefaultHttpContext();
        context.Request.Headers["X-Client-Id"] = "billing";

        var client = await NotificationGatewayEndpoints.AuthenticateRetryClientAsync(context, service, default);

        Assert.Null(client);
        Assert.Null(service.ClientId);
    }

    private sealed class RecordingNotificationService : INotificationGatewayService
    {
        public string? ClientId { get; private set; }
        public string? Secret { get; private set; }
        public string? RemoteIp { get; private set; }

        public Task<NotificationGatewayClientInfo?> AuthenticateClientAsync(string clientId, string secret, string? remoteIp, CancellationToken ct)
        {
            ClientId = clientId;
            Secret = secret;
            RemoteIp = remoteIp;
            return Task.FromResult<NotificationGatewayClientInfo?>(new(Guid.NewGuid(), clientId, true, 0, 100, [], -1));
        }

        public Task<NotificationEnqueueResult> EnqueueAsync(Guid clientId, NotificationEnqueueRequest req, CancellationToken ct) => throw new NotSupportedException();
        public Task<NotificationEnqueueResult> PublishInternalAsync(string clientSlug, string eventType, string title, string message, NotificationTarget target, string priority = "normal", string? targetUrl = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<NotificationDeliveryState?> MarkReadAsync(Guid requestId, Guid userId, CancellationToken ct) => throw new NotSupportedException();
        public Task<int> MarkAllReadAsync(Guid userId, CancellationToken ct) => throw new NotSupportedException();
        public Task<NotificationDeliveryState?> MarkAcknowledgedAsync(Guid requestId, Guid userId, CancellationToken ct) => throw new NotSupportedException();
        public Task<NotificationDeliveryState?> RetryDeliveryAsync(Guid requestId, Guid userId, CancellationToken ct) => throw new NotSupportedException();
    }
}
