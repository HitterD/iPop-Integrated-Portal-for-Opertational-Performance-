using IPop.Api.Endpoints;
using IPop.Core.Entities;
using Xunit;

namespace IPop.Tests;

/// Read-only Gateway admin queries (ipop-10 viewer/dashboard backend):
/// ListClientsCore / ListRequestsCore / ListClientRequestsCore — the exact handler bodies
/// of GET /api/notifications/v1/clients, /requests, /clients/{id}/requests.
/// Same seam as DirectoryReadEndpointsTests: SQLite in-memory via TestDb.
public class NotificationGatewayReadEndpointsTests
{
    private static NotificationGatewayClient AddClient(TestDb t, string clientId = "hr",
        string? allowedIps = null, string? allowedEventTypes = null, int rateLimit = 0, int maxRecipients = 1000)
        => t.AddNotificationClient(clientId, "h:secret123", allowedIps, allowedEventTypes, rateLimit, maxRecipients, true);

    private static NotificationRequest AddRequest(TestDb t, NotificationGatewayClient c,
        string eventType, string priority, string status, string idem)
    {
        var r = new NotificationRequest
        {
            Id = Guid.NewGuid(),
            ClientId = c.Id,
            IdempotencyKey = idem,
            Priority = priority,
            EventType = eventType,
            Title = $"{eventType} {idem}",
            Body = "body",
            Status = status,
            AcceptedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        t.Db.NotificationRequests.Add(r);
        t.Db.SaveChanges();
        return r;
    }

    // ── GET /clients ─────────────────────────────────────────────────────────

    [Fact]
    public async Task ListClients_ReturnsRows_WithoutSecretHash()
    {
        using var t = new TestDb();
        var c1 = AddClient(t, "hr", allowedIps: "10.0.0.1", allowedEventTypes: "payroll.ready");
        var c2 = AddClient(t, "crm");
        AddRequest(t, c1, "payroll.ready", "normal", "delivered", "k1");
        AddRequest(t, c1, "payroll.ready", "high", "queued", "k2");

        var rows = await NotificationGatewayEndpoints.ListClientsCore(t.Verify(), default);

        Assert.Equal(2, rows.Count);
        var hr = rows.Single(x => x.ClientId == "hr");
        Assert.Equal("10.0.0.1", hr.AllowedIps);
        Assert.Equal("payroll.ready", hr.AllowedEventTypes);
        Assert.Equal(2, hr.RequestCount); // requests fan-out counted per client
    }

    // ── GET /requests ────────────────────────────────────────────────────────

    [Fact]
    public async Task ListRequests_FiltersStatus_AndCountsByStatus()
    {
        using var t = new TestDb();
        var c = AddClient(t);
        var r1 = AddRequest(t, c, "payroll.ready", "normal", "delivered", "k1");
        _ = AddRequest(t, c, "sale.new", "high", "failed", "k2");
        _ = AddRequest(t, c, "hr.update", "low", "queued", "k3");

        var res = await NotificationGatewayEndpoints.ListRequestsCore(t.Verify(), "failed", null, null, 1, 20, default);

        Assert.Equal(1, res.Total);
        var failed = Assert.Single(res.Items);
        Assert.Equal("sale.new", failed.EventType);
        Assert.Equal("high", failed.Priority);
        Assert.Equal("failed", failed.Status);
        Assert.NotEqual(r1.Id, failed.Id); // the unfiltered "delivered" row is out

        // byStatus is a full roll-up regardless of filter
        var by = res.ByStatus.ToDictionary(x => x.Status, x => x.Count);
        Assert.Equal(3, by.Values.Sum());
        Assert.True(by.ContainsKey("delivered") && by.ContainsKey("failed") && by.ContainsKey("queued"));
    }

    [Fact]
    public async Task ListRequests_FilterPriorityAndClientId()
    {
        using var t = new TestDb();
        var hr = AddClient(t, "hr");
        var crm = AddClient(t, "crm");
        AddRequest(t, hr, "payroll.ready", "high", "delivered", "k1");
        AddRequest(t, crm, "sale.new", "high", "delivered", "k2");
        AddRequest(t, crm, "sale.new", "low", "delivered", "k3");

        var res = await NotificationGatewayEndpoints.ListRequestsCore(t.Verify(), null, "high", "crm", 1, 20, default);

        Assert.Equal(1, res.Total);
        Assert.Equal("sale.new", Assert.Single(res.Items).EventType);
    }

    // ── GET /clients/{id}/requests ───────────────────────────────────────────

    [Fact]
    public async Task ListClientRequests_ScopesToClient_Paginated()
    {
        using var t = new TestDb();
        var hr = AddClient(t, "hr");
        var crm = AddClient(t, "crm");
        for (var i = 0; i < 5; i++) AddRequest(t, hr, "payroll.ready", "normal", "delivered", $"k{i}");
        AddRequest(t, crm, "sale.new", "normal", "delivered", "zzz");

        var res = await NotificationGatewayEndpoints.ListClientRequestsCore(t.Verify(), hr.Id, 1, 2, default);

        Assert.Equal(5, res.Total);
        Assert.Equal(2, res.Items.Count);
        Assert.All(res.Items, r => Assert.Equal("payroll.ready", r.EventType));
    }
}
