using IPop.Application.Auth;
using IPop.Core.Entities;
using IPop.Infrastructure.Auth;
using IPop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace IPop.Tests;

public class AuthWorkflowTests
{
    private static readonly Argon2PasswordHasher Hasher = new();
    private static readonly JwtTokenService Tokens = new(new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { ["Jwt:Key"] = new string('k', 64) }).Build());

    // One workflow per call, each on a fresh context — mirrors the scoped
    // context per request in production, so ExecuteUpdateAsync writes are
    // always read back fresh rather than from a stale change tracker.
    private static AuthWorkflow NewWorkflow(IPopDbContext db) => new(db, Hasher, Tokens);
    private static AuthWorkflow NewWorkflow(TestDb t) => new(t.Verify(), Hasher, Tokens);
    private static LoginRequest LoginReq(string pw) => new("20140312", pw, "test-device");

    [Fact]
    public async Task Login_NullHash_OnlyDefaultPasswordWorks()
    {
        using var t = new TestDb();
        t.AddUser("20140312");                       // PasswordHash = null

        var bad = await NewWorkflow(t).LoginAsync(LoginReq("nope"), null, default);
        Assert.Equal(401, bad.Item3);

        var good = await NewWorkflow(t).LoginAsync(LoginReq("1234"), null, default);
        Assert.Equal(200, good.Item3);
        Assert.True(good.Item1!.MustChangePassword);
    }

    [Fact]
    public async Task Login_FiveFailures_LocksFor15Min_AndCounterResetsOnSuccess()
    {
        using var t = new TestDb();
        t.AddUser("20140312");

        for (var i = 0; i < 4; i++)
        {
            var r = await NewWorkflow(t).LoginAsync(LoginReq("bad"), null, default);
            Assert.Equal(401, r.Item3);
        }
        var fifth = await NewWorkflow(t).LoginAsync(LoginReq("bad"), null, default);
        Assert.Equal(423, fifth.Item3);

        // Now locked: even the correct password is refused.
        var locked = await NewWorkflow(t).LoginAsync(LoginReq("1234"), null, default);
        Assert.Equal(423, locked.Item3);

        // Lift the lock manually (direct write on its own context); success must zero the counter.
        await using (var v0 = t.Verify())
        {
            var u = await v0.Users.SingleAsync(x => x.Nik == "20140312");
            u.LockedUntil = DateTime.UtcNow.AddSeconds(-1);
            u.FailedLoginCount = 3;
            await v0.SaveChangesAsync();
        }
        var ok = await NewWorkflow(t).LoginAsync(LoginReq("1234"), null, default);
        Assert.Equal(200, ok.Item3);
        await using var v1 = t.Verify();
        Assert.Equal(0, (await v1.Users.SingleAsync(x => x.Nik == "20140312")).FailedLoginCount);
    }

    [Fact]
    public async Task Login_DeactivatedUser_NeverAuthenticates()
    {
        using var t = new TestDb();
        t.AddUser("20140312", active: false);
        var r = await NewWorkflow(t).LoginAsync(LoginReq("1234"), null, default);
        Assert.Equal(401, r.Item3);
    }

    [Fact]
    public async Task ChangePassword_KillsEverySession_Globally()
    {
        using var t = new TestDb();
        t.AddUser("20140312");

        // Two independent sessions (two devices).
        var s1 = (await NewWorkflow(t).LoginAsync(LoginReq("1234"), "ip-1", default)).Item1!;
        var s2 = (await NewWorkflow(t).LoginAsync(LoginReq("1234"), "ip-2", default)).Item1!;

        var (err, status) = await NewWorkflow(t).ChangePasswordAsync("20140312",
            new ChangePasswordRequest("MySecure9", "MySecure9"), "ip-1", default);
        Assert.Equal(200, status);

        // Both refresh tokens must now be dead.
        Assert.Equal(401, (await NewWorkflow(t).RefreshAsync(new RefreshRequest(s1.RefreshToken), null, default)).Item3);
        Assert.Equal(401, (await NewWorkflow(t).RefreshAsync(new RefreshRequest(s2.RefreshToken), null, default)).Item3);

        // Old password no longer works; new one does, and the flag is cleared.
        Assert.Equal(401, (await NewWorkflow(t).LoginAsync(LoginReq("1234"), null, default)).Item3);
        var relogin = await NewWorkflow(t).LoginAsync(LoginReq("MySecure9"), null, default);
        Assert.Equal(200, relogin.Item3);
        Assert.False(relogin.Item1!.MustChangePassword);
    }

    [Theory]
    [InlineData("short1A", "short1A", "newPassword")]            // too short
    [InlineData("alllowercase1", "alllowercase1", "newPassword")]// no uppercase
    [InlineData("NOUPPERdigit", "NOUPPERdigit", "newPassword")]  // no digit
    [InlineData("1234", "1234", "newPassword")]                  // equals default
    [InlineData("Valid123", "Different1", "confirmPassword")]    // mismatch
    public async Task ChangePassword_PolicyViolations_Rejected(string pw, string confirm, string field)
    {
        using var t = new TestDb();
        t.AddUser("20140312");
        var (err, status) = await NewWorkflow(t).ChangePasswordAsync("20140312",
            new ChangePasswordRequest(pw, confirm), null, default);
        Assert.Equal(400, status);
        Assert.True(err!.Fields!.ContainsKey(field));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("WrongPass9")]
    public async Task ChangePassword_Voluntary_RequiresCurrentPassword(string? current)
    {
        using var t = new TestDb();
        t.AddUser("20140312", passwordHash: Hasher.Hash("OldPass9"), mustChange: false);
        var (err, status) = await NewWorkflow(t).ChangePasswordAsync("20140312",
            new ChangePasswordRequest("MySecure9", "MySecure9", current), null, default);
        Assert.Equal(400, status);
        Assert.True(err!.Fields!.ContainsKey("currentPassword"));
        Assert.Equal(200, (await NewWorkflow(t).LoginAsync(LoginReq("OldPass9"), null, default)).Item3);
    }

    [Fact]
    public async Task ChangePassword_Voluntary_WithCurrentPassword_Succeeds()
    {
        using var t = new TestDb();
        t.AddUser("20140312", passwordHash: Hasher.Hash("OldPass9"), mustChange: false);
        var (_, status) = await NewWorkflow(t).ChangePasswordAsync("20140312",
            new ChangePasswordRequest("MySecure9", "MySecure9", "OldPass9"), null, default);
        Assert.Equal(200, status);
        Assert.Equal(200, (await NewWorkflow(t).LoginAsync(LoginReq("MySecure9"), null, default)).Item3);
    }

    [Fact]
    public async Task Refresh_Rotates_OldTokenDiesImmediately()
    {
        using var t = new TestDb();
        t.AddUser("20140312");
        var login = (await NewWorkflow(t).LoginAsync(LoginReq("1234"), null, default)).Item1!;

        var rotated = await NewWorkflow(t).RefreshAsync(new RefreshRequest(login.RefreshToken), null, default);
        Assert.Equal(200, rotated.Item3);

        // Reusing the rotated-out token must fail.
        var reuse = await NewWorkflow(t).RefreshAsync(new RefreshRequest(login.RefreshToken), null, default);
        Assert.Equal(401, reuse.Item3);

        // The fresh token still works.
        var again = await NewWorkflow(t).RefreshAsync(new RefreshRequest(rotated.Item1!.RefreshToken), null, default);
        Assert.Equal(200, again.Item3);
    }

    [Fact]
    public async Task Refresh_DeactivatedUser_RevokesAndRejects()
    {
        using var t = new TestDb();
        var u = t.AddUser("20140312");
        var login = (await NewWorkflow(t).LoginAsync(LoginReq("1234"), null, default)).Item1!;

        await using (var v0 = t.Verify())
        {
            (await v0.Users.SingleAsync(x => x.Nik == "20140312")).IsActive = false;
            await v0.SaveChangesAsync();
        }

        Assert.Equal(401, (await NewWorkflow(t).RefreshAsync(new RefreshRequest(login.RefreshToken), null, default)).Item3);
        await using var v2 = t.Verify();
        var session = await v2.AuthSessions.SingleAsync(s => s.UserId == u.Id);
        Assert.NotNull(session.RevokedAt);
    }
}
