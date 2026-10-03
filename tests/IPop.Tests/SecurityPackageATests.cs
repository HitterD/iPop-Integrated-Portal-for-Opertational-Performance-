using System.IdentityModel.Tokens.Jwt;
using IPop.Application.Auth;
using IPop.Application.Chat;
using IPop.Infrastructure.Auth;
using IPop.Infrastructure.Chat;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace IPop.Tests;

/// Paket A: server-only message types, session-bound access tokens, server-side forced password change.
public class SecurityPackageATests
{
    private static readonly Argon2PasswordHasher Hasher = new();
    private static readonly JwtTokenService Tokens = new(new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { ["Jwt:Key"] = new string('k', 64) }).Build());
    private static AuthWorkflow Wf(TestDb t) => new(t.Verify(), Hasher, Tokens);

    private static Guid? FamilyOf(string accessToken)
        => Guid.TryParse(new JwtSecurityTokenHandler().ReadJwtToken(accessToken).Claims
            .FirstOrDefault(c => c.Type == AccessTokenGuard.SessionClaim)?.Value, out var f) ? f : null;

    [Theory]
    [InlineData("emergency")]
    [InlineData("system")]
    [InlineData("event")]
    public async Task Member_CannotSendServerOnlyMessageTypes(string type)
    {
        using var t = new TestDb();
        var a = t.AddUser("S1"); var b = t.AddUser("S2");
        var svc = new ChatService(t.Verify());
        var dm = await svc.CreateDirectRoomAsync(a.Id, b.Id, default);

        var res = await new ChatService(t.Verify()).SendMessageAsync(a.Id, new SendMessageRequest(dm.Id, "fake alarm", Guid.NewGuid(), null, type), default);

        Assert.False(res.Success);
        Assert.Equal("invalid_type", res.Error);
        await using var v = t.Verify();
        Assert.False(await v.ChatMessages.AnyAsync(m => m.RoomId == dm.Id));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("text")]
    [InlineData("sticker")]
    [InlineData("poll")]
    public async Task Member_CanStillSendTheTypesTheAppUses(string? type)
    {
        using var t = new TestDb();
        var a = t.AddUser("S3"); var b = t.AddUser("S4");
        var dm = await new ChatService(t.Verify()).CreateDirectRoomAsync(a.Id, b.Id, default);
        var res = await new ChatService(t.Verify()).SendMessageAsync(a.Id, new SendMessageRequest(dm.Id, "{}", Guid.NewGuid(), null, type), default);
        Assert.True(res.Success);
    }

    [Fact]
    public async Task AccessToken_SurvivesRefreshRotation_ButDiesWhenItsSessionIsRevoked()
    {
        using var t = new TestDb();
        var u = t.AddUser("20140312", mustChange: false, passwordHash: Hasher.Hash("Secure123"));
        var login = (await Wf(t).LoginAsync(new LoginRequest("20140312", "Secure123", "dev"), null, default)).Item1!;
        var family = FamilyOf(login.AccessToken);
        Assert.NotNull(family);

        // A routine refresh rotates the row but keeps the family, so the old access token stays usable.
        var refreshed = (await Wf(t).RefreshAsync(new RefreshRequest(login.RefreshToken), null, default)).Item1!;
        Assert.Equal(family, FamilyOf(refreshed.AccessToken));
        Assert.Equal(AccessCheck.Ok, await AccessTokenGuard.CheckAsync(t.Verify(), u.Id, family, DateTime.UtcNow, default));

        // "Sign out this device" (admin session revoke) → the access token stops working at once.
        await using (var db = t.Verify())
            await db.AuthSessions.Where(s => s.FamilyId == family && s.RevokedAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedAt, DateTime.UtcNow));
        Assert.Equal(AccessCheck.Rejected, await AccessTokenGuard.CheckAsync(t.Verify(), u.Id, family, DateTime.UtcNow, default));
    }

    [Fact]
    public async Task AccessToken_IsRejected_WhenAccountLockedOrDeactivated()
    {
        using var t = new TestDb();
        var u = t.AddUser("20140313", mustChange: false, passwordHash: Hasher.Hash("Secure123"));
        var login = (await Wf(t).LoginAsync(new LoginRequest("20140313", "Secure123", "dev"), null, default)).Item1!;
        var family = FamilyOf(login.AccessToken);

        await using (var db = t.Verify())
            await db.Users.Where(x => x.Id == u.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.LockedUntil, DateTime.UtcNow.AddYears(1)));
        Assert.Equal(AccessCheck.Rejected, await AccessTokenGuard.CheckAsync(t.Verify(), u.Id, family, DateTime.UtcNow, default));

        await using (var db = t.Verify())
            await db.Users.Where(x => x.Id == u.Id).ExecuteUpdateAsync(s => s
                .SetProperty(x => x.LockedUntil, (DateTime?)null).SetProperty(x => x.IsActive, false));
        Assert.Equal(AccessCheck.Rejected, await AccessTokenGuard.CheckAsync(t.Verify(), u.Id, family, DateTime.UtcNow, default));
    }

    [Fact]
    public async Task PasswordChange_KillsEveryEarlierAccessToken()
    {
        using var t = new TestDb();
        var u = t.AddUser("20140314");   // default password, must change
        var login = (await Wf(t).LoginAsync(new LoginRequest("20140314", "1234", "dev"), null, default)).Item1!;
        var family = FamilyOf(login.AccessToken);
        Assert.Equal(AccessCheck.MustChangePassword, await AccessTokenGuard.CheckAsync(t.Verify(), u.Id, family, DateTime.UtcNow, default));

        var (_, status) = await Wf(t).ChangePasswordAsync("20140314", new ChangePasswordRequest("MySecure9", "MySecure9", null), null, default);
        Assert.Equal(200, status);
        Assert.Equal(AccessCheck.Rejected, await AccessTokenGuard.CheckAsync(t.Verify(), u.Id, family, DateTime.UtcNow, default));
    }

    [Fact]
    public async Task LegacyTokenWithoutFamily_StillChecksAccountState()
    {
        using var t = new TestDb();
        var u = t.AddUser("20140315", mustChange: false);
        Assert.Equal(AccessCheck.Ok, await AccessTokenGuard.CheckAsync(t.Verify(), u.Id, null, DateTime.UtcNow, default));
        Assert.Equal(AccessCheck.Rejected, await AccessTokenGuard.CheckAsync(t.Verify(), Guid.NewGuid(), null, DateTime.UtcNow, default));
    }

    [Theory]
    [InlineData("/api/auth/change-password", true)]
    [InlineData("/api/auth/logout", true)]
    [InlineData("/api/auth/refresh", true)]
    [InlineData("/api/chat/rooms", false)]
    [InlineData("/api/authx", false)]
    [InlineData("/hubs/chat", false)]
    public void OnlyAuthEndpoints_AreOpenWhilePasswordChangeIsPending(string path, bool allowed)
        => Assert.Equal(allowed, AccessTokenGuard.AllowedDuringPasswordChange(path));
}

public class ChatConnectionsTests
{
    [Fact]
    public void TracksEachConnectionOfAUser_UntilItDisconnects()
    {
        var user = Guid.NewGuid();
        IPop.Api.Chat.ChatConnections.Add(user, "c1");
        IPop.Api.Chat.ChatConnections.Add(user, "c2");
        Assert.Equal(["c1", "c2"], IPop.Api.Chat.ChatConnections.Of(user).Order());

        IPop.Api.Chat.ChatConnections.Remove(user, "c1");
        Assert.Equal(["c2"], IPop.Api.Chat.ChatConnections.Of(user));
        IPop.Api.Chat.ChatConnections.Remove(user, "c2");
        Assert.Empty(IPop.Api.Chat.ChatConnections.Of(user));
    }
}
