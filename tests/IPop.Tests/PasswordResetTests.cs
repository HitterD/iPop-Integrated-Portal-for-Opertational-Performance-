using System.Text.RegularExpressions;
using IPop.Application.Auth;
using IPop.Application.Email;
using IPop.Core.Entities;
using IPop.Infrastructure.Auth;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace IPop.Tests;

public class PasswordResetTests
{
    private sealed class CapturingQueue : IEmailQueue
    {
        public List<EmailMessage> Sent { get; } = [];
        public Task EnqueueAsync(EmailMessage msg, CancellationToken ct = default) { Sent.Add(msg); return Task.CompletedTask; }
    }

    private static readonly Argon2PasswordHasher Hasher = new();
    private const string Base = "https://ipop.test/reset-password";

    private static string TokenFrom(EmailMessage m) =>
        Uri.UnescapeDataString(Regex.Match(m.TextBody!, @"token=([^\s]+)").Groups[1].Value);

    [Fact]
    public async Task Request_by_nik_or_email_queues_link_and_reset_changes_password_once()
    {
        using var t = new TestDb();
        var u = t.AddUser("20140312");
        var q = new CapturingQueue();

        await new PasswordResetService(t.Verify(), Hasher, q).RequestAsync(new(u.Email.ToUpperInvariant()), Base, null, default);
        var msg = Assert.Single(q.Sent);
        Assert.Equal(u.Email, msg.To[0]);
        Assert.Contains(Base + "?token=", msg.HtmlBody);

        var token = TokenFrom(msg);
        var ok = await new PasswordResetService(t.Verify(), Hasher, q).ResetAsync(new(token, "NewPass123", "NewPass123"), null, default);
        Assert.Equal(200, ok.Status);

        using var db = t.Verify();
        var user = await db.Users.SingleAsync(x => x.Id == u.Id);
        Assert.True(Hasher.Verify("NewPass123", user.PasswordHash!));
        Assert.False(user.MustChangePassword);

        var reuse = await new PasswordResetService(t.Verify(), Hasher, q).ResetAsync(new(token, "Other1234", "Other1234"), null, default);
        Assert.Equal(400, reuse.Status);
        Assert.Equal("invalid_token", reuse.Err!.Code);
    }

    [Fact]
    public async Task Unknown_or_inactive_account_sends_nothing()
    {
        using var t = new TestDb();
        t.AddUser("OFF", active: false);
        var q = new CapturingQueue();
        var svc = new PasswordResetService(t.Verify(), Hasher, q);

        await svc.RequestAsync(new("nobody"), Base, null, default);
        await svc.RequestAsync(new("OFF"), Base, null, default);

        Assert.Empty(q.Sent);
    }

    [Fact]
    public async Task New_request_supersedes_old_token_and_hourly_cap_applies()
    {
        using var t = new TestDb();
        t.AddUser("A1");
        var q = new CapturingQueue();

        for (var i = 0; i < PasswordResetService.MaxPerHour + 2; i++)
            await new PasswordResetService(t.Verify(), Hasher, q).RequestAsync(new("A1"), Base, null, default);
        Assert.Equal(PasswordResetService.MaxPerHour, q.Sent.Count);

        var first = await new PasswordResetService(t.Verify(), Hasher, q).ResetAsync(new(TokenFrom(q.Sent[0]), "NewPass123", "NewPass123"), null, default);
        Assert.Equal(400, first.Status);
        var last = await new PasswordResetService(t.Verify(), Hasher, q).ResetAsync(new(TokenFrom(q.Sent[^1]), "NewPass123", "NewPass123"), null, default);
        Assert.Equal(200, last.Status);
    }

    [Fact]
    public async Task Reset_enforces_policy_and_revokes_sessions()
    {
        using var t = new TestDb();
        var u = t.AddUser("B1");
        t.Db.AuthSessions.Add(new AuthSession { UserId = u.Id, RefreshTokenHash = "h", CreatedAt = DateTime.UtcNow, ExpiresAt = DateTime.UtcNow.AddDays(1) });
        t.Db.SaveChanges();
        var q = new CapturingQueue();
        await new PasswordResetService(t.Verify(), Hasher, q).RequestAsync(new("B1"), Base, null, default);
        var token = TokenFrom(q.Sent[0]);

        var weak = await new PasswordResetService(t.Verify(), Hasher, q).ResetAsync(new(token, "short", "short"), null, default);
        Assert.Equal("policy", weak.Err!.Code);

        var ok = await new PasswordResetService(t.Verify(), Hasher, q).ResetAsync(new(token, "NewPass123", "NewPass123"), null, default);
        Assert.Equal(200, ok.Status);
        using var db = t.Verify();
        Assert.All(await db.AuthSessions.Where(s => s.UserId == u.Id).ToListAsync(), s => Assert.NotNull(s.RevokedAt));
    }
}
