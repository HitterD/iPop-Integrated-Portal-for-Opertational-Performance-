using IPop.Application.Auth;
using IPop.Core.Entities;
using IPop.Infrastructure.Auth;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace IPop.Tests;

public class AdminSessionTests
{
    private static ITokenService CreateTokens()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Jwt:Key"] = "SuperSecretKeyForTestingPurposesOnly1234567890!",
                ["Jwt:Issuer"] = "ipop",
                ["Jwt:Audience"] = "ipop"
            })
            .Build();
        return new JwtTokenService(config);
    }

    [Fact]
    public async Task RevokeOthers_RevokesOtherSessions_KeepsCurrentSession()
    {
        using var t = new TestDb();
        var tokens = CreateTokens();
        var now = DateTime.UtcNow;

        var user = t.AddUser("20140312", active: true);
        var otherUser = t.AddUser("20080115", active: true);

        var currentRefresh = tokens.CreateRefreshToken();
        var currentHash = tokens.HashRefreshToken(currentRefresh);

        var otherRefresh1 = tokens.CreateRefreshToken();
        var otherRefresh2 = tokens.CreateRefreshToken();

        var sessCurrent = new AuthSession
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            RefreshTokenHash = currentHash,
            CreatedAt = now,
            ExpiresAt = now.AddDays(7)
        };
        var sessOther1 = new AuthSession
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            RefreshTokenHash = tokens.HashRefreshToken(otherRefresh1),
            CreatedAt = now,
            ExpiresAt = now.AddDays(7)
        };
        var sessOther2 = new AuthSession
        {
            Id = Guid.NewGuid(),
            UserId = otherUser.Id,
            RefreshTokenHash = tokens.HashRefreshToken(otherRefresh2),
            CreatedAt = now,
            ExpiresAt = now.AddDays(7)
        };

        t.Db.AuthSessions.AddRange(sessCurrent, sessOther1, sessOther2);
        await t.Db.SaveChangesAsync();

        // Revoke others keeping currentHash
        var query = t.Db.AuthSessions.Where(s => s.RevokedAt == null && s.ExpiresAt > now);
        query = query.Where(s => s.RefreshTokenHash != currentHash);
        var count = await query.ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedAt, now));

        Assert.Equal(2, count);

        await using var v = t.Verify();
        var cur = await v.AuthSessions.SingleAsync(s => s.Id == sessCurrent.Id);
        Assert.Null(cur.RevokedAt);

        var o1 = await v.AuthSessions.SingleAsync(s => s.Id == sessOther1.Id);
        Assert.NotNull(o1.RevokedAt);

        var o2 = await v.AuthSessions.SingleAsync(s => s.Id == sessOther2.Id);
        Assert.NotNull(o2.RevokedAt);
    }
}
