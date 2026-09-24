using IPop.Application.Auth;

namespace IPop.Api.Endpoints;

public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuth(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/auth");

        g.MapPost("/login", async (LoginRequest req, IAuthWorkflow wf, HttpContext http, CancellationToken ct) =>
        {
            var (ok, err, status) = await wf.LoginAsync(req, Ip(http), ct);
            return status == 200 ? Results.Ok(ok) : Results.Json(err, statusCode: status);
        });

        // Authenticated: the NIK comes from the token, so a caller can only change their own password.
        g.MapPost("/change-password", async (ChangePasswordRequest req, IAuthWorkflow wf, HttpContext http, CancellationToken ct) =>
        {
            var nik = http.User.FindFirst("nik")?.Value;
            if (nik is null) return Results.Unauthorized();
            var (err, status) = await wf.ChangePasswordAsync(nik, req, Ip(http), ct);
            return status == 200 ? Results.Ok() : Results.Json(err, statusCode: status);
        }).RequireAuthorization();

        g.MapPost("/refresh", async (RefreshRequest req, IAuthWorkflow wf, HttpContext http, CancellationToken ct) =>
        {
            var (ok, err, status) = await wf.RefreshAsync(req, Ip(http), ct);
            return status == 200 ? Results.Ok(ok) : Results.Json(err, statusCode: status);
        });

        // Anonymous; always 202 so the response never reveals whether an account exists.
        // The link points back at the web origin that asked (validated against the CORS list).
        g.MapPost("/forgot-password", async (ForgotPasswordRequest req, IPasswordReset svc,
            IConfiguration cfg, HttpContext http, CancellationToken ct) =>
        {
            await svc.RequestAsync(req, ResetUrlBase(http, cfg), Ip(http), ct);
            return Results.Accepted();
        }).RequireRateLimiting("auth-reset");

        g.MapPost("/reset-password", async (ResetPasswordRequest req, IPasswordReset svc, HttpContext http, CancellationToken ct) =>
        {
            var (err, status) = await svc.ResetAsync(req, Ip(http), ct);
            return status == 200 ? Results.Ok() : Results.Json(err, statusCode: status);
        }).RequireRateLimiting("auth-reset");

        g.MapPost("/logout", async (LogoutRequest req, IAuthWorkflow wf, CancellationToken ct) =>
        {
            await wf.LogoutAsync(req, ct);
            return Results.Ok();
        });

        return app;
    }

    private static string? Ip(HttpContext http) => http.Connection.RemoteIpAddress?.ToString();

    internal static string ResetUrlBase(HttpContext http, IConfiguration cfg)
    {
        var allowed = cfg.GetSection("Cors:WebOrigins").Get<string[]>() ?? [];
        var origin = http.Request.Headers.Origin.ToString();
        var web = allowed.Contains(origin, StringComparer.OrdinalIgnoreCase)
            ? origin
            : cfg["App:PublicWebUrl"] ?? allowed.FirstOrDefault() ?? "http://localhost:5103";
        return web.TrimEnd('/') + "/reset-password";
    }
}
