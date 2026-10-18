using System.Collections.Immutable;
using System.Security.Claims;
using System.Security.Cryptography;
using IPop.Application.Auth;
using IPop.Infrastructure.Persistence;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Primitives;
using OpenIddict.Abstractions;
using OpenIddict.Server.AspNetCore;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace IPop.Api.Sso;

/// "Sign in with iPop" — iPop as an OpenID Connect provider (docs/sso/OIDC-DESIGN.md, docs/sso/idesk-sign-in-with-ipop.md).
/// iPop's browser session is a bearer token, not a cookie, so /connect/authorize hands the browser to the Web page
/// /sso/authorize, which (signed in) calls /api/sso/approve and comes back with a 30-second single-use grant.
public static class SsoEndpoints
{
    public const string PendingPrefix = "sso:pending:";
    public const string GrantPrefix = "sso:grant:";
    public static readonly TimeSpan PendingLifetime = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan GrantLifetime = TimeSpan.FromSeconds(30);
    public const string GrantParameter = "ipop_grant";
    public const string Scheme = OpenIddictServerAspNetCoreDefaults.AuthenticationScheme;

    public sealed record PendingRequest(string ClientId, string ClientName, string Query);
    public sealed record ApproveRequest(string Req);
    private sealed record Grant(Guid UserId, string Req);

    public static IEndpointRouteBuilder MapSso(this IEndpointRouteBuilder app, string webBase)
    {
        // 1 · App → iPop. Without a valid one-time grant, park the request and send the browser to the Web page.
        app.MapMethods("/connect/authorize", ["GET", "POST"], async (HttpContext http, IMemoryCache cache, IOpenIddictApplicationManager apps, IPopDbContext db) =>
        {
            var request = http.GetOpenIddictServerRequest() ?? throw new InvalidOperationException("No OpenID Connect request.");
            var grantId = request.GetParameter(GrantParameter)?.ToString();
            if (grantId is { Length: > 0 } && cache.Get<Grant>(GrantPrefix + grantId) is { } grant)
            {
                cache.Remove(GrantPrefix + grantId); // single use
                var principal = await PrincipalAsync(db, grant.UserId, request.GetScopes(), http.RequestAborted);
                if (principal is null) return Forbid(Errors.AccessDenied, "This account can't sign in.");
                return Results.SignIn(principal, authenticationScheme: Scheme);
            }

            var client = await apps.FindByClientIdAsync(request.ClientId!) ?? throw new InvalidOperationException("Unknown client.");
            var name = await apps.GetDisplayNameAsync(client) ?? request.ClientId!;
            var query = string.Join("&", http.Request.Query
                .Where(p => p.Key != GrantParameter)
                .SelectMany(p => p.Value.Select(v => $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(v ?? "")}")));
            var id = Token();
            cache.Set(PendingPrefix + id, new PendingRequest(request.ClientId!, name, query), PendingLifetime);
            return Results.Redirect($"{webBase.TrimEnd('/')}/sso/authorize?req={id}");
        });

        // 2 · The Web page asks who the app is (to show "Continue to iDesk").
        app.MapGet("/api/sso/pending/{req}", (string req, IMemoryCache cache) =>
            cache.Get<PendingRequest>(PendingPrefix + req) is { } p
                ? Results.Ok(new { client = p.ClientName, clientId = p.ClientId })
                : Results.NotFound(new { message = "This sign-in link has expired. Go back and try again." }))
            .RequireAuthorization();

        // 3 · Signed-in user approves → one-time grant + the URL to continue (same origin as the API).
        app.MapPost("/api/sso/approve", async (ApproveRequest body, HttpContext http, IMemoryCache cache, IPopDbContext db) =>
        {
            var nik = http.User.FindFirstValue("nik");
            var userId = await db.Users.AsNoTracking().Where(u => u.Nik == nik && u.IsActive).Select(u => (Guid?)u.Id).FirstOrDefaultAsync(http.RequestAborted);
            if (userId is null) return Results.Unauthorized();
            if (cache.Get<PendingRequest>(PendingPrefix + body.Req) is not { } pending)
                return Results.NotFound(new { message = "This sign-in link has expired. Go back and try again." });
            cache.Remove(PendingPrefix + body.Req);
            var grant = Token();
            cache.Set(GrantPrefix + grant, new Grant(userId.Value, body.Req), GrantLifetime);
            var continueUrl = $"/connect/authorize?{pending.Query}&{GrantParameter}={grant}";
            return Results.Ok(new { continueUrl, client = pending.ClientName });
        }).RequireAuthorization();

        // 4 · Code / refresh / password exchange.
        app.MapPost("/connect/token", async (HttpContext http, IPopDbContext db, IAuthWorkflow auth) =>
        {
            var request = http.GetOpenIddictServerRequest() ?? throw new InvalidOperationException("No OpenID Connect request.");
            if (request.IsAuthorizationCodeGrantType() || request.IsRefreshTokenGrantType())
            {
                var result = await http.AuthenticateAsync(Scheme);
                var sub = result.Principal?.GetClaim(Claims.Subject);
                if (!Guid.TryParse(sub, out var id)) return Forbid(Errors.InvalidGrant, "The grant is no longer valid.");
                // Re-read the user: a deactivated account or a changed name stops / updates at refresh time.
                var principal = await PrincipalAsync(db, id, result.Principal!.GetScopes(), http.RequestAborted);
                return principal is null ? Forbid(Errors.InvalidGrant, "This account can't sign in.") : Results.SignIn(principal, authenticationScheme: Scheme);
            }
            if (request.IsPasswordGrantType())
            {
                // NIK + iPop password typed into a registered app's own form (confidential clients only — enforced by
                // OpenIddict client permissions). Same lockout/audit path as the iPop login page.
                var (ok, err, _) = await auth.LoginAsync(new LoginRequest(request.Username ?? "", request.Password ?? "", "oidc:" + request.ClientId),
                    http.Connection.RemoteIpAddress?.ToString(), http.RequestAborted);
                if (ok is null) return Forbid(Errors.InvalidGrant, err?.Message ?? "Wrong employee ID or password.");
                if (ok.MustChangePassword) return Forbid(Errors.InvalidGrant, "Set a new password in iPop first, then sign in again.");
                // LoginAsync opened an iPop session for this sign-in; the app gets OIDC tokens only, so close it again.
                await auth.LogoutAsync(new LogoutRequest(ok.RefreshToken), http.RequestAborted);
                var id = await db.Users.AsNoTracking().Where(u => u.Nik == request.Username).Select(u => u.Id).FirstAsync(http.RequestAborted);
                var principal = await PrincipalAsync(db, id, request.GetScopes(), http.RequestAborted);
                return principal is null ? Forbid(Errors.InvalidGrant, "This account can't sign in.") : Results.SignIn(principal, authenticationScheme: Scheme);
            }
            return Forbid(Errors.UnsupportedGrantType, "This grant type is not supported.");
        });

        // 5 · Profile for the app.
        app.MapMethods("/connect/userinfo", ["GET", "POST"], async (HttpContext http, IPopDbContext db) =>
        {
            var result = await http.AuthenticateAsync(Scheme);
            if (!Guid.TryParse(result.Principal?.GetClaim(Claims.Subject), out var id)) return Results.Challenge(authenticationSchemes: [Scheme]);
            var u = await Profile(db, id, http.RequestAborted);
            return u is null ? Results.Challenge(authenticationSchemes: [Scheme]) : Results.Ok(new Dictionary<string, object?>
            {
                [Claims.Subject] = id.ToString(), ["nik"] = u.Nik, [Claims.Name] = u.Name, [Claims.Email] = u.Email,
                ["department"] = u.Department, ["site"] = u.Site,
            });
        });

        // 6 · Sign out (RP-initiated): iPop's own session lives in the browser, so only the redirect is needed.
        app.MapMethods("/connect/logout", ["GET", "POST"], () => Results.SignOut(authenticationSchemes: [Scheme]));

        return app;
    }

    private sealed record ProfileRow(string Nik, string Name, string? Email, string? Department, string? Site);

    private static Task<ProfileRow?> Profile(IPopDbContext db, Guid id, CancellationToken ct) =>
        db.Users.AsNoTracking().Where(u => u.Id == id && u.IsActive)
            .Select(u => new ProfileRow(u.Nik, u.Name, u.Email, u.Department != null ? u.Department.Name : null, u.Site != null ? u.Site.Name : null))
            .FirstOrDefaultAsync(ct);

    /// Claims an app receives. NIK is included because it is the account-matching key (apps show it to admins only).
    public static async Task<ClaimsPrincipal?> PrincipalAsync(IPopDbContext db, Guid userId, ImmutableArray<string> scopes, CancellationToken ct)
    {
        var u = await Profile(db, userId, ct);
        if (u is null) return null;
        var identity = new ClaimsIdentity(Scheme, Claims.Name, Claims.Role);
        identity.SetClaim(Claims.Subject, userId.ToString())
            .SetClaim("nik", u.Nik)
            .SetClaim(Claims.Name, u.Name)
            .SetClaim(Claims.Email, u.Email)
            .SetClaim("department", u.Department)
            .SetClaim("site", u.Site);
        identity.SetScopes(scopes.Intersect([Scopes.OpenId, Scopes.Profile, Scopes.Email, Scopes.OfflineAccess]));
        identity.SetDestinations(c => c.Type switch
        {
            Claims.Subject => [Destinations.AccessToken, Destinations.IdentityToken],
            "nik" or Claims.Name or Claims.Email or "department" or "site" => [Destinations.AccessToken, Destinations.IdentityToken],
            _ => [Destinations.AccessToken],
        });
        return new ClaimsPrincipal(identity);
    }

    private static IResult Forbid(string error, string description) => Results.Forbid(
        new AuthenticationProperties(new Dictionary<string, string?>
        {
            [OpenIddictServerAspNetCoreConstants.Properties.Error] = error,
            [OpenIddictServerAspNetCoreConstants.Properties.ErrorDescription] = description,
        }), [Scheme]);

    private static string Token() => Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
}
