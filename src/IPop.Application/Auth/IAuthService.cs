namespace IPop.Application.Auth;

public interface IPasswordHasher
{
    string Hash(string password);
    bool Verify(string password, string hash);
}

public interface ITokenService
{
    /// roles are emitted as ClaimTypes.Role claims so [Authorize(Roles=...)] / policies work.
    /// sessionFamily binds the token to its login so a revoke takes effect before the token expires.
    string CreateAccessToken(Guid userId, string nik, IEnumerable<string> roles, out DateTime expiresAt, Guid? sessionFamily = null);
    string CreateRefreshToken();          // raw token returned to client
    string HashRefreshToken(string raw);  // only the hash is stored (spec §3.7)
}
