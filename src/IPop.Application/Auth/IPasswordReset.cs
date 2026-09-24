namespace IPop.Application.Auth;

/// Nik or email — whichever the user remembers.
public record ForgotPasswordRequest(string Identifier);
public record ResetPasswordRequest(string Token, string NewPassword, string ConfirmPassword);

public interface IPasswordReset
{
    /// Always succeeds from the caller's point of view (no account enumeration); when the
    /// account exists, is active and has an email, a link to {resetUrlBase}?token=… is queued.
    Task RequestAsync(ForgotPasswordRequest req, string resetUrlBase, string? ip, CancellationToken ct);

    Task<(AuthError? Err, int Status)> ResetAsync(ResetPasswordRequest req, string? ip, CancellationToken ct);
}
