namespace IPop.Application.Auth;

public record LoginRequest(string Nik, string Password, string? DeviceFingerprint);
public record LoginResponse(string AccessToken, string RefreshToken, DateTime AccessTokenExpiresAt, bool MustChangePassword);

/// No Nik: the endpoint requires a bearer token and takes the NIK from the "nik" claim.
/// Trusting a body-supplied NIK would let anyone reset any account (spec §3.1).
/// CurrentPassword is required for a voluntary change (account menu), not for the forced
/// first-login change, where the user just typed the default password to get in.
public record ChangePasswordRequest(string NewPassword, string ConfirmPassword, string? CurrentPassword = null);

public record RefreshRequest(string RefreshToken);
/// MustChangePassword rides along so a page reload cannot bypass the force-change gate.
public record RefreshResponse(string AccessToken, string RefreshToken, DateTime AccessTokenExpiresAt, bool MustChangePassword);

public record LogoutRequest(string RefreshToken);

/// Field-level errors for password policy failures (spec §3.1).
public record AuthError(string Code, string Message, Dictionary<string, string[]>? Fields = null);
