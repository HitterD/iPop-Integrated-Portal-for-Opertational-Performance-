using System.IdentityModel.Tokens.Jwt;
using IPop.Infrastructure.Auth;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace IPop.Tests;

public class JwtTokenServiceTests
{
    private const string Key = "dev-only-signing-key-that-is-long-enough-for-hmac-sha256!!";

    private readonly JwtTokenService _s = new(new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Jwt:Key"] = Key, ["Jwt:Issuer"] = "ipop", ["Jwt:Audience"] = "ipop",
        }).Build());

    [Fact]
    public void AccessToken_CarriesNikClaim_AndExpiresIn15Min()
    {
        var userId = Guid.NewGuid();
        var token = _s.CreateAccessToken(userId, "20140312", ["Admin"], out var exp);

        Assert.True(exp > DateTime.UtcNow.AddMinutes(14) && exp <= DateTime.UtcNow.AddMinutes(15));

        // Validate the signature/expiry, then read claims from the validated token.
        // The handler maps JWT "sub" onto ClaimTypes.NameIdentifier.
        var handler = new JwtSecurityTokenHandler();
        handler.ValidateToken(token, new TokenValidationParameters
        {
            ValidIssuer = "ipop",
            ValidAudience = "ipop",
            IssuerSigningKey = new SymmetricSecurityKey(System.Text.Encoding.UTF8.GetBytes(Key)),
            ValidateLifetime = true,
            ClockSkew = TimeSpan.Zero,
        }, out var validated);

        var jwt = (JwtSecurityToken)validated;
        Assert.Equal(userId.ToString(), jwt.Claims.First(c => c.Type == JwtRegisteredClaimNames.Sub).Value);
        Assert.Equal("20140312", jwt.Claims.First(c => c.Type == "nik").Value);
        // Role claims ride as ClaimTypes.Role so RequireRole("Admin") / [Authorize(Roles=...)] resolve them.
        // JwtSecurityToken expands ClaimTypes.Role to the full WS URI.
        Assert.Contains(jwt.Claims, c => c.Type == System.Security.Claims.ClaimTypes.Role && c.Value == "Admin");
    }

    [Fact]
    public void RefreshToken_HashIsSha256_AndDiffersFromRaw()
    {
        var raw = _s.CreateRefreshToken();
        var hash = _s.HashRefreshToken(raw);
        Assert.NotEqual(raw, hash);
        Assert.Equal(_s.HashRefreshToken(raw), hash);            // deterministic — lookups depend on it
        Assert.NotEqual(_s.CreateRefreshToken(), _s.CreateRefreshToken());
    }
}
