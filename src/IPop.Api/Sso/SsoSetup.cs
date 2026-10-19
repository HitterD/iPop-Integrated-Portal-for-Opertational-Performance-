using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using IPop.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using OpenIddict.Abstractions;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace IPop.Api.Sso;

/// OpenIddict wiring for "Sign in with iPop". Off unless <c>Oidc:Enabled</c> is true, so deploying the code changes
/// nothing until ICT switches it on. Signing/encryption keys are generated once and kept in AppSettings, protected
/// with ASP.NET Data Protection (keys already persisted in the database), so every API restart signs with the same key.
public static class SsoSetup
{
    public const string KeysSetting = "oidc.keys";

    public static bool Enabled(IConfiguration c) => c.GetValue<bool>("Oidc:Enabled");

    public static void AddIPopSso(this IServiceCollection services, IConfiguration config)
    {
        if (!Enabled(config)) return;
        services.AddMemoryCache();
        services.AddOpenIddict()
            .AddCore(o => o.UseEntityFrameworkCore().UseDbContext<IPopDbContext>())
            .AddServer(o =>
            {
                var issuer = config["Oidc:Issuer"];
                if (!string.IsNullOrWhiteSpace(issuer)) o.SetIssuer(new Uri(issuer));
                o.SetAuthorizationEndpointUris("connect/authorize")
                 .SetTokenEndpointUris("connect/token")
                 .SetUserInfoEndpointUris("connect/userinfo")
                 .SetEndSessionEndpointUris("connect/logout");
                // Behind the TLS proxy the API sees http: advertise the public (issuer) addresses in discovery,
                // or apps would be sent to http://… for authorize/token/JWKS.
                if (!string.IsNullOrWhiteSpace(issuer))
                {
                    var b = new Uri(issuer.TrimEnd('/') + "/");
                    o.SetConfigurationEndpointUris(".well-known/openid-configuration").SetJsonWebKeySetEndpointUris(".well-known/jwks");
                    o.AddEventHandler<OpenIddict.Server.OpenIddictServerEvents.HandleConfigurationRequestContext>(h => h.UseInlineHandler(ctx =>
                    {
                        ctx.AuthorizationEndpoint = new Uri(b, "connect/authorize");
                        ctx.TokenEndpoint = new Uri(b, "connect/token");
                        ctx.UserInfoEndpoint = new Uri(b, "connect/userinfo");
                        ctx.EndSessionEndpoint = new Uri(b, "connect/logout");
                        ctx.JsonWebKeySetEndpoint = new Uri(b, ".well-known/jwks");
                        return default;
                    }).SetOrder(int.MaxValue));
                }
                o.AllowAuthorizationCodeFlow().RequireProofKeyForCodeExchange()
                 .AllowRefreshTokenFlow()
                 .AllowPasswordFlow();
                o.RegisterScopes(Scopes.OpenId, Scopes.Profile, Scopes.Email, Scopes.OfflineAccess);
                o.SetAccessTokenLifetime(TimeSpan.FromMinutes(15)).SetIdentityTokenLifetime(TimeSpan.FromMinutes(15))
                 .SetRefreshTokenLifetime(TimeSpan.FromDays(30)).SetAuthorizationCodeLifetime(TimeSpan.FromMinutes(2));
                o.DisableAccessTokenEncryption(); // apps validate access tokens themselves (JWKS)
                var (signing, encryption) = LoadKeys(services);
                o.AddSigningCertificate(signing).AddEncryptionCertificate(encryption);
                var aspnet = o.UseAspNetCore()
                    .EnableAuthorizationEndpointPassthrough().EnableTokenEndpointPassthrough()
                    .EnableUserInfoEndpointPassthrough().EnableEndSessionEndpointPassthrough();
                // The public proxy terminates TLS; the API itself is reached over http inside the network.
                if (config.GetValue<bool>("Oidc:AllowHttp")) aspnet.DisableTransportSecurityRequirement();
            })
            .AddValidation(o => { o.UseLocalServer(); o.UseAspNetCore(); });
    }

    /// RSA certificates for signing and encrypting tokens: created on first start, then read back.
    private static (X509Certificate2 Signing, X509Certificate2 Encryption) LoadKeys(IServiceCollection services)
    {
        using var sp = services.BuildServiceProvider();
        using var scope = sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IPopDbContext>();
        var protector = scope.ServiceProvider.GetRequiredService<IDataProtectionProvider>().CreateProtector("ipop.oidc.keys.v1");
        var row = db.AppSettings.AsNoTracking().FirstOrDefault(s => s.Key == KeysSetting);
        if (row is not null)
        {
            var parts = protector.Unprotect(System.Text.Json.JsonSerializer.Deserialize<string>(row.ValueJson)!).Split('|');
            return (Load(parts[0]), Load(parts[1]));
        }
        var signing = Create("CN=iPop OIDC signing", X509KeyUsageFlags.DigitalSignature);
        var encryption = Create("CN=iPop OIDC encryption", X509KeyUsageFlags.KeyEncipherment);
        var payload = protector.Protect(Convert.ToBase64String(signing.Export(X509ContentType.Pfx)) + "|" + Convert.ToBase64String(encryption.Export(X509ContentType.Pfx)));
        db.AppSettings.Add(new IPop.Core.Entities.AppSetting { Key = KeysSetting, ValueJson = System.Text.Json.JsonSerializer.Serialize(payload), UpdatedAt = DateTime.UtcNow, UpdatedByNik = "system" });
        db.SaveChanges();
        return (signing, encryption);

        static X509Certificate2 Load(string b64) => X509CertificateLoader.LoadPkcs12(Convert.FromBase64String(b64), null, X509KeyStorageFlags.EphemeralKeySet);
        static X509Certificate2 Create(string subject, X509KeyUsageFlags usage)
        {
            using var rsa = RSA.Create(2048);
            var req = new CertificateRequest(subject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            req.CertificateExtensions.Add(new X509KeyUsageExtension(usage, critical: true));
            using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(10));
            return X509CertificateLoader.LoadPkcs12(cert.Export(X509ContentType.Pfx), null, X509KeyStorageFlags.EphemeralKeySet | X509KeyStorageFlags.Exportable);
        }
    }

    /// Registers/updates the OIDC clients listed in config (Oidc:Clients). Secrets come from config, never from code.
    /// Example (appsettings.Local.json, not in git):
    /// "Oidc": { "Enabled": true, "Issuer": "https://ipop.santos.co.id", "Clients": [ { "ClientId": "idesk",
    ///   "DisplayName": "iDesk", "ClientSecret": "…", "RedirectUris": ["https://idesk…/api/auth/oidc/callback"],
    ///   "PostLogoutRedirectUris": ["https://idesk…/login"], "AllowPassword": true } ] }
    public static async Task SeedClientsAsync(IServiceProvider services, IConfiguration config, CancellationToken ct)
    {
        if (!Enabled(config)) return;
        using var scope = services.CreateScope();
        var manager = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
        foreach (var c in config.GetSection("Oidc:Clients").GetChildren())
        {
            var clientId = c["ClientId"];
            var secret = c["ClientSecret"];
            if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(secret)) continue;
            var d = new OpenIddictApplicationDescriptor
            {
                ClientId = clientId, ClientSecret = secret, DisplayName = c["DisplayName"] ?? clientId,
                ClientType = ClientTypes.Confidential, ConsentType = ConsentTypes.Implicit,
            };
            foreach (var u in c.GetSection("RedirectUris").Get<string[]>() ?? []) d.RedirectUris.Add(new Uri(u));
            foreach (var u in c.GetSection("PostLogoutRedirectUris").Get<string[]>() ?? []) d.PostLogoutRedirectUris.Add(new Uri(u));
            d.Permissions.UnionWith([
                Permissions.Endpoints.Authorization, Permissions.Endpoints.Token, Permissions.Endpoints.EndSession,
                Permissions.GrantTypes.AuthorizationCode, Permissions.GrantTypes.RefreshToken, Permissions.ResponseTypes.Code,
                Permissions.Scopes.Profile, Permissions.Scopes.Email, Permissions.Prefixes.Scope + Scopes.OfflineAccess,
            ]);
            if (c.GetValue<bool>("AllowPassword")) d.Permissions.Add(Permissions.GrantTypes.Password);
            d.Requirements.Add(Requirements.Features.ProofKeyForCodeExchange);
            var existing = await manager.FindByClientIdAsync(clientId, ct);
            if (existing is null) await manager.CreateAsync(d, ct);
            else await manager.UpdateAsync(existing, d, ct);
        }
    }
}
