using System.Text;
using IPop.Api.Endpoints;
using IPop.Api.Presence;
using IPop.Application.Auth;
using IPop.Application.Files;
using IPop.Application.Hris;
using IPop.Application.Realtime;
using IPop.Infrastructure.Auth;
using IPop.Infrastructure.Files;
using IPop.Infrastructure.Hris;
using IPop.Infrastructure.Persistence;
using IPop.Infrastructure.Rbac;
using IPop.Application.Rbac;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();
builder.Services.AddDbContext<IPopDbContext>(o =>
    o.UseNpgsql(builder.Configuration.GetConnectionString("IPopDb")));
builder.Services.AddDataProtection().PersistKeysToDbContext<IPopDbContext>();

builder.Services.AddScoped<IPasswordHasher, Argon2PasswordHasher>();
builder.Services.AddSingleton<ITokenService, JwtTokenService>();
builder.Services.AddScoped<IAuthWorkflow, AuthWorkflow>();
builder.Services.AddScoped<IPermissionEvaluator, PermissionEvaluator>();
builder.Services.AddScoped<IFileAccessService, FileAccessService>();
builder.Services.AddScoped<OfficeOptions>(_ => new OfficeOptions
{
    SofficePath = builder.Configuration["Office:SofficePath"] ?? "soffice",
    TempDirectory = builder.Configuration["Office:TempDirectory"] ?? Path.GetTempPath(),
});
builder.Services.AddHttpClient<ISynologyGateway, HttpSynologyGateway>()
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
    {
        ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
    });

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(o =>
    {
        // SignalR browser clients can't set headers on the websocket handshake,
        // so they send the JWT as ?access_token=. Lift it onto the request here.
        o.Events = new JwtBearerEvents
        {
            OnMessageReceived = ctx =>
            {
                var token = ctx.Request.Query["access_token"];
                if (!string.IsNullOrEmpty(token) && ctx.HttpContext.Request.Path.StartsWithSegments("/hubs"))
                    ctx.Token = token;
                return Task.CompletedTask;
            }
        };
        o.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true, ValidateAudience = true, ValidateLifetime = true,
            ValidIssuer = builder.Configuration["Jwt:Issuer"] ?? "ipop",
            ValidAudience = builder.Configuration["Jwt:Audience"] ?? "ipop",
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(
                builder.Configuration["Jwt:Key"] ?? throw new InvalidOperationException("Jwt:Key is required"))),
            ClockSkew = TimeSpan.Zero,
        };
    });
builder.Services.AddAuthorization(o =>
    o.AddPolicy("AdminOnly", p => p.RequireRole("Admin")));

// Dev-only CORS: Blazor WASM dev server runs on 5103, API on 5083 (spec §6 wiring).
// ponytail: deployment consolidates to single origin — drop this policy then.
var webOrigins = builder.Configuration.GetSection("Cors:WebOrigins").Get<string[]>()
    ?? new[] { "http://localhost:5103", "https://localhost:5103" };
builder.Services.AddCors(o => o.AddPolicy("web-dev", p => p
    .WithOrigins(webOrigins)
    .AllowAnyHeader().AllowAnyMethod().AllowCredentials()));

builder.Services.AddSignalR();
builder.Services.AddSingleton<PresenceBroadcaster>();
builder.Services.AddSingleton<IDirectoryChangeNotifier, DirectoryChangeNotifier>();
builder.Services.AddSingleton<IAuditChangeNotifier, AuditChangeNotifier>();
builder.Services.AddHostedService<PresenceSweepWorker>();

builder.Services.AddScoped<IHrisSyncService, HrisSyncService>();
// Adapter by config (spec §4): Production → HttpHrisGateway (Polly), otherwise JSON file.
if (builder.Configuration["Hris:Mode"] == "Production")
    builder.Services.AddHttpClient<IHrisGateway, HttpHrisGateway>();
else
    builder.Services.AddSingleton<IHrisGateway, JsonHrisGateway>();
builder.Services.AddHostedService<HrisSyncWorker>();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<IPopDbContext>();
    await db.Database.MigrateAsync();
    await DevSeed.EnsureSeededAsync(db, scope.ServiceProvider.GetRequiredService<IPop.Application.Auth.IPasswordHasher>());
}

app.UseCors("web-dev");
app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();

app.MapAuth();
app.MapHris();
app.MapAdmin();
app.MapAudit();
app.MapRbac();
app.MapDirectory();
app.MapFiles();
app.MapPresence();
app.MapHub<PresenceHub>("/hubs/presence").RequireAuthorization();

app.Run();
