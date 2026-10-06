using System.Text;
using IPop.Api.Chat;
using IPop.Api.Endpoints;
using IPop.Api.Presence;
using IPop.Api.Realtime;
using IPop.Application.Auth;
using IPop.Application.Chat;
using IPop.Application.Files;
using IPop.Application.Hris;
using IPop.Application.Mail;
using IPop.Application.Realtime;
using IPop.Infrastructure.Auth;
using IPop.Infrastructure.Chat;
using IPop.Infrastructure.Files;
using IPop.Infrastructure.Hris;
using IPop.Infrastructure.Mail;
using IPop.Infrastructure.Persistence;
using IPop.Infrastructure.Rbac;
using IPop.Infrastructure.Notifications;
using IPop.Infrastructure.Sja;
using IPop.Infrastructure.Documents;
using IPop.Application.Rbac;
using IPop.Application.Notifications;
using IPop.Application.Sja;
using IPop.Application.Settings;
using IPop.Infrastructure.Settings;
using IPop.Application.Email;
using IPop.Infrastructure.Email;
using IPop.Application.Attachments;
using IPop.Infrastructure.Attachments;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);
// No "Server: Kestrel" fingerprint (pentest).
builder.WebHost.ConfigureKestrel(k => k.AddServerHeader = false);

// Local-only secrets (NAS service account). Git-ignored, optional, and loaded last so it wins over
// the committed appsettings files. In production these come from environment variables instead.
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true);

builder.Services.AddOpenApi();
builder.Services.AddDbContext<IPopDbContext>(o =>
    o.UseNpgsql(builder.Configuration.GetConnectionString("IPopDb"))
     .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning)));
builder.Services.AddDataProtection().PersistKeysToDbContext<IPopDbContext>();

builder.Services.AddScoped<IPasswordHasher, Argon2PasswordHasher>();
builder.Services.AddSingleton<ITokenService, JwtTokenService>();
builder.Services.AddScoped<IAuthWorkflow, AuthWorkflow>();
builder.Services.AddScoped<IPop.Application.Auth.IPasswordReset, IPop.Infrastructure.Auth.PasswordResetService>();
// Forgot/reset password are anonymous: 5 requests per IP per 15 minutes.
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy("auth-reset", http => System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(
        http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions { PermitLimit = 5, Window = TimeSpan.FromMinutes(15) }));
    // Call toast "Tolak" is anonymous (signed token); cap it per IP.
    o.AddPolicy("push-action", http => System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(
        http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions { PermitLimit = 30, Window = TimeSpan.FromMinutes(1) }));
    // Link previews make the server fetch the web: 60 lookups per user per minute.
    o.AddPolicy(IPop.Api.Endpoints.LinkPreviewEndpoints.RateLimitPolicy, http => System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(
        http.User.FindFirst("nik")?.Value ?? http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions { PermitLimit = 60, Window = TimeSpan.FromMinutes(1) }));
    // Signed preview images are anonymous (<img>); cap per client address.
    o.AddPolicy(IPop.Api.Endpoints.LinkPreviewEndpoints.ImageRateLimitPolicy, http => System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(
        http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions { PermitLimit = 240, Window = TimeSpan.FromMinutes(1) }));
    // iDesk webhook is anonymous (HMAC-signed); iDesk itself sends at most 120/min.
    o.AddPolicy(IPop.Api.Endpoints.TicketEndpoints.WebhookRateLimitPolicy, http => System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(
        http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions { PermitLimit = 120, Window = TimeSpan.FromMinutes(1) }));
});
builder.Services.AddScoped<IPermissionEvaluator, PermissionEvaluator>();
builder.Services.AddSingleton<SynologySessionCache>();
builder.Services.AddScoped<IFileAccessService, FileAccessService>();
builder.Services.AddScoped<ISettings, DbSettings>();

var smtp = SmtpOptions.Bind(builder.Configuration);
builder.Services.AddSingleton(smtp);
if (smtp.IsConfigured) builder.Services.AddSingleton<IEmailSender, SmtpEmailSender>();
else builder.Services.AddSingleton<IEmailSender, LogEmailSender>();
builder.Services.AddScoped<IEmailQueue, DbEmailQueue>();
builder.Services.AddScoped<EmailOutboxProcessor>();
builder.Services.AddHostedService<EmailOutboxWorker>();
// Requests over 1 s land in logs/slow-requests.log (see SlowRequestLog).
builder.Services.AddSingleton<IPop.Api.Diagnostics.SlowRequestLog>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<IPop.Api.Diagnostics.SlowRequestLog>());
// Reuses Synology:ServiceAccount + Synology:{Area}Folder when no "Attachments" section exists;
// attachment-files/ under ContentRoot is the local fallback (like chat-files/ and mail-files/).
builder.Services.AddSingleton(AttachmentOptions.Bind(builder.Configuration,
    Path.Combine(builder.Environment.ContentRootPath, "attachment-files")));
builder.Services.AddSingleton<AttachmentSession>();
builder.Services.AddScoped<IAttachmentStore, SynologyAttachmentStore>();
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
            },
            OnTokenValidated = async ctx =>
            {
                var sub = ctx.Principal?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
                          ?? ctx.Principal?.FindFirst("sub")?.Value;
                if (!Guid.TryParse(sub, out var userId)) { ctx.Fail("no subject"); return; }
                Guid? family = Guid.TryParse(ctx.Principal!.FindFirst(AccessTokenGuard.SessionClaim)?.Value, out var f) ? f : null;
                var db = ctx.HttpContext.RequestServices.GetRequiredService<IPopDbContext>();
                switch (await AccessTokenGuard.CheckAsync(db, userId, family, DateTime.UtcNow, ctx.HttpContext.RequestAborted))
                {
                    case AccessCheck.Rejected:
                        ctx.Fail("session revoked, account locked or deactivated");
                        break;
                    case AccessCheck.MustChangePassword:
                        ((System.Security.Claims.ClaimsIdentity)ctx.Principal.Identity!)
                            .AddClaim(new System.Security.Claims.Claim(AccessTokenGuard.PasswordChangeClaim, "1"));
                        break;
                }
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

var redisConnection = builder.Configuration.GetConnectionString("Redis")
    ?? throw new InvalidOperationException("ConnectionStrings:Redis is required");
builder.Services.AddSignalR()
    .AddStackExchangeRedis(
        redisConnection,
        options => { options.Configuration.ChannelPrefix = RedisChannel.Literal("ipop-chat"); });
builder.Services.AddSingleton<IRealtime, HubRealtime>();
builder.Services.AddScoped<IBadgeProvider, IPop.Infrastructure.Badges.ChatBadgeProvider>();
builder.Services.AddScoped<IBadgeProvider, IPop.Infrastructure.Badges.MailBadgeProvider>();
builder.Services.AddScoped<IBadgeProvider, IPop.Infrastructure.Badges.NotificationBadgeProvider>();
builder.Services.AddScoped<IBadgeProvider, IPop.Infrastructure.Badges.NewsBadgeProvider>();
builder.Services.AddScoped<IBadgeProvider, IPop.Infrastructure.Badges.CallsBadgeProvider>();
builder.Services.AddScoped<IBadgeProvider, IPop.Infrastructure.Badges.TicketsBadgeProvider>();
builder.Services.AddSingleton<PresenceBroadcaster>();
builder.Services.AddSingleton<PresenceConnections>();
builder.Services.AddSingleton<PresenceDevices>();
builder.Services.AddSingleton<IDirectoryChangeNotifier, DirectoryChangeNotifier>();
builder.Services.AddSingleton<IAuditChangeNotifier, AuditChangeNotifier>();
builder.Services.AddHostedService<PresenceSweepWorker>();

// Background service safety: an unhandled exception in background workers must not crash the whole host
builder.Services.Configure<HostOptions>(options =>
{
    options.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.Ignore;
});

// Chat module
builder.Services.AddScoped<IChatService, ChatService>();
builder.Services.AddScoped<IChatNotifier, ChatNotifier>();
builder.Services.AddScoped<IStickerService, IPop.Infrastructure.Chat.StickerService>();
builder.Services.AddScoped<INotificationGateway, NotificationGateway>();
builder.Services.AddHostedService<ChatAttachmentGcWorker>();
builder.Services.AddHostedService<ChatMentionNotifyWorker>();
builder.Services.AddHostedService<IPop.Infrastructure.Projects.TaskReminderWorker>();
builder.Services.AddHostedService<IPop.Infrastructure.Projects.DeletedTaskPurgeWorker>();
builder.Services.AddHostedService<IPop.Infrastructure.Projects.TimerAutoStopWorker>();
builder.Services.AddHostedService<IPop.Infrastructure.Meetings.MeetingReminderWorker>();

// Mail module
builder.Services.AddScoped<IMailService, MailService>();

// Calls module (1:1 P2P WebRTC signaling; LiveKit tokens are for meetings only)
builder.Services.AddSingleton<IPop.Application.Calls.ILiveKitTokenService, IPop.Infrastructure.Calls.LiveKitTokenService>();
builder.Services.AddScoped<IPop.Application.Calls.ICallService, IPop.Infrastructure.Calls.CallService>();
builder.Services.AddSingleton<IPop.Api.Calls.CallConnections>();
builder.Services.AddHostedService<IPop.Api.Calls.CallRingTimeoutWorker>();

// Projects module
builder.Services.AddScoped<IPop.Application.Projects.IProjectService, IPop.Infrastructure.Projects.ProjectService>();
builder.Services.AddScoped<IPop.Application.Projects.IProjectFieldService, IPop.Infrastructure.Projects.ProjectFieldService>();
builder.Services.AddScoped<IPop.Application.Projects.IPortfolioService, IPop.Infrastructure.Projects.PortfolioService>();
builder.Services.AddScoped<IPop.Application.Projects.ITaskFileService, IPop.Infrastructure.Projects.TaskFileService>();
builder.Services.AddScoped<IPop.Application.Projects.IMilestoneService, IPop.Infrastructure.Projects.MilestoneService>();
builder.Services.AddScoped<IAttachmentAccessPolicy, IPop.Infrastructure.Projects.ProjectsAttachmentPolicy>();
builder.Services.AddScoped<IPop.Application.Projects.IProjectTimeService, IPop.Infrastructure.Projects.ProjectTimeService>();

builder.Services.AddScoped<INotificationGatewayService, NotificationGatewayService>();
// Real-time notification push (SignalR) — broadcaster impl + hub registered with hubs below.
builder.Services.AddSingleton<IPop.Application.Notifications.INotificationRealtimeBroadcaster, IPop.Api.Notifications.NotificationBroadcaster>();
// Web Push → Windows toasts. No VAPID keys (Push:VapidPublicKey/PrivateKey) = push off, fail-closed.
var push = IPop.Infrastructure.Notifications.Push.PushOptions.Bind(builder.Configuration);
builder.Services.AddSingleton(push);
if (push.IsConfigured)
    builder.Services.AddSingleton<IPop.Infrastructure.Notifications.Push.IWebPushTransport, IPop.Infrastructure.Notifications.Push.WebPushTransport>();
else
    builder.Services.AddSingleton<IPop.Infrastructure.Notifications.Push.IWebPushTransport, IPop.Infrastructure.Notifications.Push.DisabledWebPushTransport>();
builder.Services.AddSingleton<IPop.Infrastructure.Notifications.Push.PushQueue>();
builder.Services.AddScoped<IPop.Infrastructure.Notifications.Push.PushDispatcher>();
builder.Services.AddHostedService<IPop.Infrastructure.Notifications.Push.PushWorker>();

// News & Memos module (single table with type enum: news/announcement/memo)
builder.Services.AddScoped<IPop.Application.News.INewsService, IPop.Infrastructure.News.NewsService>();
builder.Services.AddScoped<IPop.Application.News.INewsComposerService, IPop.Infrastructure.News.NewsComposerService>();
builder.Services.AddHostedService<IPop.Infrastructure.News.NewsComposerScheduleWorker>();
builder.Services.AddSingleton(new IPop.Infrastructure.News.NewsComposerMediaOptions
{
    LocalRoot = Path.Combine(builder.Environment.ContentRootPath, "news-composer-media")
});
builder.Services.AddScoped<IPop.Application.News.INewsComposerMediaService, IPop.Infrastructure.News.NewsComposerMediaService>();
// 24h GC of unreferenced composer media; all safety lives in the service, this is only the schedule.
builder.Services.AddHostedService<IPop.Infrastructure.News.NewsComposerMediaCleanupWorker>();
builder.Services.AddScoped<IPop.Application.News.INewsPublicationHooks>(sp =>
    (IPop.Infrastructure.News.NewsService)sp.GetRequiredService<IPop.Application.News.INewsService>());
// news-videos/ under ContentRoot holds the streaming copies; the NAS keeps the archive.
builder.Services.AddSingleton(new IPop.Infrastructure.News.NewsVideoOptions { LocalRoot = Path.Combine(builder.Environment.ContentRootPath, "news-videos") });
builder.Services.AddScoped<IPop.Application.News.INewsVideoService, IPop.Infrastructure.News.NewsVideoService>();
builder.Services.AddScoped<IAttachmentAccessPolicy, IPop.Infrastructure.News.NewsAttachmentPolicy>();

// Notes module (Notion-like pages, Yjs co-editing over /hubs/notes)
builder.Services.AddScoped<IPop.Application.Notes.INoteService, IPop.Infrastructure.Notes.NoteService>();
builder.Services.AddScoped<IAttachmentAccessPolicy, IPop.Infrastructure.Notes.NotesAttachmentPolicy>();

// ICT tickets via iDesk (IDesk:ClientSecret >= 32 chars, else fail closed: create 503, webhook 503).
builder.Services.AddSingleton(IPop.Infrastructure.Tickets.IdeskOptions.Bind(builder.Configuration));
builder.Services.AddSingleton<IPop.Infrastructure.Tickets.TicketModuleCache>();
builder.Services.AddHttpClient<IPop.Application.Tickets.IIdeskClient, IPop.Infrastructure.Tickets.HttpIdeskClient>();
builder.Services.AddScoped<IPop.Application.Tickets.ITicketService, IPop.Infrastructure.Tickets.TicketService>();
builder.Services.AddHostedService<IPop.Infrastructure.Tickets.TicketOutboxWorker>();
builder.Services.AddHostedService<IPop.Infrastructure.Tickets.TicketReconcileWorker>();

builder.Services.AddScoped<IHrisSyncService, HrisSyncService>();
// Adapter by config (spec §4): Production → HttpHrisGateway (Polly), otherwise JSON file.
if (builder.Configuration["Hris:Mode"] == "Production")
    builder.Services.AddHttpClient<IHrisGateway, HttpHrisGateway>();
else
    builder.Services.AddSingleton<IHrisGateway, JsonHrisGateway>();
builder.Services.AddHostedService<HrisSyncWorker>();
builder.Services.AddHostedService<MailScheduledDeliveryWorker>();
builder.Services.AddSingleton<MailNotifySignal>();
builder.Services.AddHostedService<MailNotifyWorker>();
builder.Services.AddHostedService<IPop.Infrastructure.Notifications.NotificationRetentionWorker>();
builder.Services.AddHostedService<IPop.Infrastructure.Notifications.NotificationLinkBackfill>();
builder.Services.AddHostedService<IPop.Infrastructure.Files.FileOperationWorker>();
builder.Services.AddHostedService<IPop.Infrastructure.Files.FileOperationRetentionWorker>();

// Pairing perangkat SJA Notifier lewat NIK (Sja:AppSecret wajib di user-secrets/env).
builder.Services.AddHttpClient<ISjaGateway, HttpSjaGateway>();

// DevExpress Office File API (Word, Excel, PDF, PowerPoint, Skia Cross-Platform)
builder.Services.AddDevExpressDocumentServices();

builder.Services.AddScoped<IPop.Application.Search.ISearchService, IPop.Infrastructure.Search.SearchService>();
builder.Services.AddScoped<IPop.Application.Dashboard.IDashboardService, IPop.Infrastructure.Dashboard.DashboardService>();

// Link previews: server-side OpenGraph fetch behind an SSRF guard, signed image proxy.
builder.Services.AddSingleton(IPop.Infrastructure.Links.LinkPreviewOptions.Bind(builder.Configuration));
builder.Services.AddSingleton<IPop.Infrastructure.Links.LinkPreviewCache>();
builder.Services.AddSingleton<IPop.Infrastructure.Links.IDnsResolver, IPop.Infrastructure.Links.SystemDnsResolver>();
builder.Services.AddSingleton(sp => new IPop.Infrastructure.Links.SafeHttpFetcher(sp.GetRequiredService<IPop.Infrastructure.Links.IDnsResolver>()));
// HMAC key is HKDF-derived from Jwt:Key with its own purpose label (never the JWT key itself).
builder.Services.AddSingleton(new IPop.Infrastructure.Links.LinkPreviewImageSigner(builder.Configuration["Jwt:Key"]!));
builder.Services.AddScoped<IPop.Infrastructure.Links.InternalLinkResolver>();
builder.Services.AddScoped<IPop.Infrastructure.Links.LinkPreviewService>();

var app = builder.Build();

// Unhandled errors answer a bare 500 — never a stack trace, even though the deployed API runs
// in Development for DevSeed (pentest: sanitize framework errors). Sits inside the developer
// exception page, so it catches first.
// Optional client allowlist (pentest H-06): with Security:AllowedClientNetworks set, only those
// networks (the https reverse proxy, this host, Docker) reach the API; LAN browsers go through
// the proxy. Unset = open, as in development.
var allowedNetworks = app.Configuration.GetSection("Security:AllowedClientNetworks").Get<string[]>()?
    .Select(System.Net.IPNetwork.Parse).ToArray() ?? [];
if (allowedNetworks.Length > 0)
{
    app.Use(async (ctx, next) =>
    {
        var ip = ctx.Connection.RemoteIpAddress;
        if (ip is { IsIPv4MappedToIPv6: true }) ip = ip.MapToIPv4();
        if (ip is null || !allowedNetworks.Any(n => n.Contains(ip)))
        {
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }
        await next();
    });
}

IPop.Api.Diagnostics.SlowRequestLog.UseSlowRequestLog(app);

app.UseExceptionHandler(e => e.Run(async ctx =>
{
    // Malformed bodies/parameters keep their 4xx, just without the binder's details.
    var bad = ctx.Features.Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerFeature>()?.Error as BadHttpRequestException;
    ctx.Response.StatusCode = bad?.StatusCode ?? StatusCodes.Status500InternalServerError;
    await ctx.Response.WriteAsJsonAsync(new { code = bad is null ? "server_error" : "bad_request" });
}));
app.Use(async (ctx, next) =>
{
    ctx.Response.Headers.XContentTypeOptions = "nosniff";
    await next();
});

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<IPopDbContext>();
    await db.Database.MigrateAsync();
    await DevSeed.EnsureSeededAsync(db, scope.ServiceProvider.GetRequiredService<IPop.Application.Auth.IPasswordHasher>());
}

app.UseCors("web-dev");
// Dev serves http and https side by side (http for LAN browsing, https for calls' microphone);
// redirecting would push http pages onto the self-signed port and break their CORS preflights.
if (!app.Environment.IsDevelopment()) app.UseHttpsRedirection();
app.UseAuthentication();
// A default/reset password must be replaced before anything else is reachable (the client's
// /set-password redirect alone was bypassable with the token it had already been given).
app.Use(async (http, next) =>
{
    if (http.User.HasClaim(AccessTokenGuard.PasswordChangeClaim, "1")
        && !AccessTokenGuard.AllowedDuringPasswordChange(http.Request.Path)
        && http.GetEndpoint()?.Metadata.GetMetadata<Microsoft.AspNetCore.Authorization.IAllowAnonymous>() is null
        && (http.Request.Path.StartsWithSegments("/api") || http.Request.Path.StartsWithSegments("/hubs")))
    {
        http.Response.StatusCode = StatusCodes.Status403Forbidden;
        await http.Response.WriteAsJsonAsync(new { code = "password_change_required", message = "Set a new password first." });
        return;
    }
    await next();
});
app.UseAuthorization();
app.UseRateLimiter();

app.MapAuth();
app.MapDeviceLink();
app.MapHris();
app.MapAdmin();
app.MapAudit();
app.MapRbac();
app.MapDirectory();
app.MapFiles();
app.MapOnlyOffice();
app.MapChat();
// Group photos: random 128-bit file names, served without auth so <img> tags work.
app.MapGet("/api/chat-avatars/{name}", (string name, IWebHostEnvironment env) =>
{
    var path = IPop.Api.Endpoints.ChatEndpoints.GroupAvatarPath(env.ContentRootPath, name);
    if (path is null || !File.Exists(path)) return Results.NotFound();
    return Results.File(path, "image/jpeg", enableRangeProcessing: false);
}).AllowAnonymous();
app.MapStickers();
app.MapCalls();
app.MapMail();
app.MapMailGroups();
app.MapProjects();
app.MapProjectFields();
app.MapProjectTime();
app.MapProjectPortfolio();
app.MapMeetings();
app.MapCalendar();
app.MapCalendarSharing();
app.MapCalendarDelegates();
app.MapCalendarGroupShares();
app.MapCalendarHolidays();
app.MapNews();
app.MapNewsComposer();
app.MapNewsComposerMedia();
app.MapNewsVideos();
app.MapNotes();
app.MapTickets();
app.MapNotificationGateway();
app.MapPush();
app.MapPresence();
app.MapDocumentDemo();
app.MapSettings();
app.MapAttachments();
app.MapDashboardAndSearch();
app.MapLinkPreview();
app.MapHub<ChatHub>("/hubs/chat").RequireAuthorization();
app.MapHub<IPop.Api.Calls.CallHub>("/hubs/calls").RequireAuthorization();
app.MapHub<IPop.Api.Notes.NotesHub>("/hubs/notes").RequireAuthorization();
app.MapHub<IPop.Api.Notifications.NotificationHub>("/hubs/notification").RequireAuthorization();
app.MapMe();
app.MapProfile();
app.MapHub<AppHub>("/hubs/app").RequireAuthorization();

// News video posters were not archived to the NAS before 2026-10; copy any missing ones once the
// API is up (best effort, in the background, so startup isn't held up by the NAS).
app.Lifetime.ApplicationStarted.Register(() => _ = Task.Run(async () =>
{
    try
    {
        await using var scope = app.Services.CreateAsyncScope();
        if (scope.ServiceProvider.GetRequiredService<IPop.Application.News.INewsVideoService>() is IPop.Infrastructure.News.NewsVideoService svc)
            await svc.ArchiveMissingPostersAsync(CancellationToken.None);
    }
    catch (Exception ex) { app.Logger.LogWarning(ex, "Archiving news video posters at startup failed."); }
}));

app.Run();
