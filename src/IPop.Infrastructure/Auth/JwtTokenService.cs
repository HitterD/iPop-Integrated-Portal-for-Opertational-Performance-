using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using IPop.Application.Auth;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;

namespace IPop.Infrastructure.Auth;

/// Access token 15 min; refresh token random 64 bytes, only its SHA-256 hash is stored (spec §3.7).
public class JwtTokenService(IConfiguration config) : ITokenService
{
    public string CreateAccessToken(Guid userId, string nik, IEnumerable<string> roles, out DateTime expiresAt, Guid? sessionFamily = null)
    {
        expiresAt = DateTime.UtcNow.AddMinutes(15);
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(
            config["Jwt:Key"] ?? throw new InvalidOperationException("Jwt:Key is required")));
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, userId.ToString()),
            new("nik", nik),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
        };
        if (sessionFamily is { } family) claims.Add(new(AccessTokenGuard.SessionClaim, family.ToString()));
        claims.AddRange(roles.Select(r => new Claim(ClaimTypes.Role, r)));
        var token = new JwtSecurityToken(
            issuer: config["Jwt:Issuer"] ?? "ipop",
            audience: config["Jwt:Audience"] ?? "ipop",
            claims: claims,
            expires: expiresAt,
            signingCredentials: new SigningCredentials(key, SecurityAlgorithms.HmacSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public string CreateRefreshToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(64));

    public string HashRefreshToken(string raw) =>
        Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));
}
