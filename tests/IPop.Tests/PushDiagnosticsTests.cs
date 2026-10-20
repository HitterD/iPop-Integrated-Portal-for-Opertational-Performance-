using System.Text.Json;
using IPop.Core.Entities;
using IPop.Infrastructure.Notifications.Push;
using IPop.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IPop.Tests;

/// Delivery diagnostics: own devices only, sanitized (no endpoint/keys/content), admin aggregate counts only.
public class PushDiagnosticsTests
{
    private sealed class ScriptedTransport(Func<PushSubscription, PushSendOutcome> outcome) : IWebPushTransport
    {
        public bool IsConfigured => true;
        public Task<PushSendOutcome> SendAsync(PushSubscription sub, string json, TimeSpan ttl, bool urgent, string? topic, CancellationToken ct) =>
            Task.FromResult(outcome(sub));
    }

    private static PushSubscription Subscribe(IPopDbContext db, Guid userId, string endpoint)
    {
        var s = new PushSubscription
        {
            Id = Guid.NewGuid(), UserId = userId, Endpoint = endpoint, P256dh = "SECRETP256", Auth = "SECRETAUTH",
            Label = "Chrome · Windows", CreatedAt = DateTime.UtcNow, LastSeenAt = DateTime.UtcNow,
        };
        db.PushSubscriptions.Add(s);
        db.SaveChanges();
        return s;
    }

    /// Two users; A has a healthy and an auth-failing browser; B has one browser.
    private static async Task<(Guid A, Guid B, PushSubscription Ok, PushSubscription Bad)> Seed(Func<IPopDbContext> ctx, Guid a, Guid b)
    {
        using var db = ctx();
        var ok = Subscribe(db, a, $"https://fcm.googleapis.com/fcm/send/{Guid.NewGuid():N}");
        var bad = Subscribe(db, a, $"https://wns2-sg2p.notify.windows.com/w/?token={Guid.NewGuid():N}");
        Subscribe(db, b, $"https://updates.push.services.mozilla.com/wpush/v2/{Guid.NewGuid():N}");
        await new PushQueue().EnqueueAsync(db, "test", null, default, [a]);
        await new PushQueue().EnqueueAsync(db, "test", null, default, [b]);
        var tr = new ScriptedTransport(s => s.Id == bad.Id ? PushSendOutcome.FromStatus(401) : PushSendOutcome.Accepted);
        using var wdb = ctx();
        await new PushDispatcher(wdb, tr, new EphemeralDataProtectionProvider(), NullLogger<PushDispatcher>.Instance)
            .ProcessAsync(DateTime.UtcNow.AddSeconds(1), default);
        return (a, b, ok, bad);
    }

    private static void AssertSanitized(object dto, params PushSubscription[] subs)
    {
        var json = JsonSerializer.Serialize(dto);
        foreach (var s in subs)
        {
            Assert.DoesNotContain(s.Endpoint, json);
            Assert.DoesNotContain(new Uri(s.Endpoint).AbsolutePath, json);
        }
        Assert.DoesNotContain("SECRET", json);
        Assert.DoesNotContain("Notifications are on", json); // no toast content
    }

    [Fact]
    public async Task User_SeesOnlyOwnDevices_WithHealth_AndNoSecrets()
    {
        using var t = new TestDb();
        var a = t.AddUser("A"); var b = t.AddUser("B");
        var (_, _, ok, bad) = await Seed(t.Verify, a.Id, b.Id);

        var dto = await PushDiagnostics.ForUserAsync(t.Verify(), a.Id, ok.Id, true, DateTime.UtcNow, default);

        Assert.Equal(2, dto.Devices.Count);
        var okDev = dto.Devices.Single(d => d.Id == ok.Id);
        Assert.True(okDev.IsCurrent);
        Assert.Equal("ok", okDev.Health);
        Assert.Equal("fcm", okDev.Provider);
        Assert.NotNull(okDev.LastAcceptedAt);
        var badDev = dto.Devices.Single(d => d.Id == bad.Id);
        Assert.Equal("auth-error", badDev.Health);
        Assert.Equal("auth-401", badDev.LastFailureCode);
        Assert.Equal("wns", badDev.Provider);
        Assert.Equal(2, dto.Recent.Count);
        Assert.All(dto.Recent, r => Assert.Contains(r.DeviceId, new[] { ok.Id, bad.Id }));
        AssertSanitized(dto, ok, bad);
    }

    [Fact]
    public async Task Admin_GetsCountsOnly()
    {
        using var t = new TestDb();
        var a = t.AddUser("A"); var b = t.AddUser("B");
        var (_, _, ok, bad) = await Seed(t.Verify, a.Id, b.Id);

        var dto = await PushDiagnostics.AggregateAsync(t.Verify(), true, DateTime.UtcNow, default);

        Assert.Equal(3, dto.Subscriptions);
        Assert.Equal(1, dto.ByProvider["fcm"]);
        Assert.Equal(1, dto.ByProvider["wns"]);
        Assert.Equal(1, dto.ByProvider["mozilla"]);
        Assert.Equal(2, dto.Last24h[PushDispatcher.Status.Accepted]);
        Assert.Equal(1, dto.Last24h[PushDispatcher.Status.AuthError]);
        Assert.Equal(1, dto.FailureCodes["auth-401"]);
        Assert.Equal(0, dto.PendingDeliveries);
        var json = JsonSerializer.Serialize(dto);
        Assert.DoesNotContain(a.Id.ToString(), json);
        Assert.DoesNotContain("User A", json);
        AssertSanitized(dto, ok, bad);
    }

    /// Runs the outbox + diagnostics queries on real PostgreSQL. Opt-in: set IPOP_PUSH_PG_TEST to a
    /// connection string of a THROWAWAY database that already has the migrations applied.
    [Fact]
    public async Task Postgres_OutboxAndDiagnostics_Translate()
    {
        var cs = Environment.GetEnvironmentVariable("IPOP_PUSH_PG_TEST");
        if (string.IsNullOrWhiteSpace(cs)) return;
        Assert.DoesNotContain("Database=ipop;", cs + ";"); // never the dev/prod database
        var options = new DbContextOptionsBuilder<IPopDbContext>().UseNpgsql(cs).Options;
        IPopDbContext Ctx() => new(options);

        Guid a, b;
        using (var db = Ctx())
        {
            var ua = NewUser(); var ub = NewUser();
            db.Users.AddRange(ua, ub);
            await db.SaveChangesAsync();
            (a, b) = (ua.Id, ub.Id);
        }
        try
        {
            var (_, _, ok, _) = await Seed(Ctx, a, b);
            var mine = await PushDiagnostics.ForUserAsync(Ctx(), a, ok.Id, true, DateTime.UtcNow, default);
            Assert.Equal(2, mine.Devices.Count);
            var agg = await PushDiagnostics.AggregateAsync(Ctx(), true, DateTime.UtcNow, default);
            Assert.True(agg.Subscriptions >= 3);
            using var db = Ctx();
            var d = new PushDispatcher(db, new ScriptedTransport(_ => PushSendOutcome.Accepted), new EphemeralDataProtectionProvider(),
                NullLogger<PushDispatcher>.Instance);
            await d.PurgeAsync(DateTime.UtcNow, default);
        }
        finally
        {
            using var db = Ctx();
            await db.PushDeliveries.Where(x => x.UserId == a || x.UserId == b).ExecuteDeleteAsync();
            await db.PushJobs.Where(j => !j.Deliveries.Any() && j.Kind == "test").ExecuteDeleteAsync();
            await db.Users.Where(u => u.Id == a || u.Id == b).ExecuteDeleteAsync(); // subscriptions cascade
        }

        static User NewUser() => new()
        {
            Id = Guid.NewGuid(), Nik = $"pt{Guid.NewGuid():N}"[..12], Name = "Push Test", Email = $"{Guid.NewGuid():N}@t.co",
            IsActive = true, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
    }
}
