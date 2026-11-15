using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;

namespace IPop.Api.Auth;

/// The signed-in user's id straight from the validated access token.
/// Every API token carries the user id as "sub" (JwtTokenService), and JwtBearer's OnTokenValidated has already
/// checked that this user exists, is active, unlocked and that the session is live (AccessTokenGuard). Endpoints
/// used to look the id up again by NIK on every request — one extra Users query per call, the most frequent query
/// in the 2026-11-09 load test.
public static class CurrentUser
{
    public static Guid? IdFromToken(ClaimsPrincipal user)
    {
        if (user.Identity?.IsAuthenticated != true) return null;
        var sub = user.FindFirstValue(JwtRegisteredClaimNames.Sub)
               ?? user.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(sub, out var id) ? id : null;
    }
}
