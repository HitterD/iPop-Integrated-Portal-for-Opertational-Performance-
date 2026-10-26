namespace IPop.Application.Auth;

/// Use-case boundary for the whole auth workflow (spec §3). Implemented in Infrastructure
/// because it orchestrates the DbContext; the contract lives here so Api never references EF.
public interface IAuthWorkflow
{
    Task<(LoginResponse? Ok, AuthError? Err, int Status)> LoginAsync(LoginRequest req, string? ip, CancellationToken ct);
    /// nik comes from the verified access token, never from the request body.
    Task<(AuthError? Err, int Status)> ChangePasswordAsync(string nik, ChangePasswordRequest req, string? ip, CancellationToken ct);
    Task<(RefreshResponse? Ok, AuthError? Err, int Status)> RefreshAsync(RefreshRequest req, string? ip, CancellationToken ct);
    Task LogoutAsync(LogoutRequest req, CancellationToken ct);
    /// A new session for a person already proven elsewhere (iPop Notifier signed in from the browser session).
    /// Null when the account is gone, deactivated or locked.
    Task<LoginResponse?> IssueSessionAsync(Guid userId, string? deviceFingerprint, string? ip, string auditAction, CancellationToken ct);
}
