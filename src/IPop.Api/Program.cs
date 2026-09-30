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
builder.Services.AddSingleton(AttachmentOptions.Bind(builder.Configuration));
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
builder.Services.AddSingleton<PresenceBroadcaster>();
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
builder.Services.AddScoped<INotificationGateway, NotificationGateway>();
builder.Services.AddHostedService<ChatAttachmentGcWorker>();
builder.Services.AddHostedService<ChatMentionNotifyWorker>();
builder.Services.AddHostedService<IPop.Infrastructure.Projects.TaskReminderWorker>();
builder.Services.AddHostedService<IPop.Infrastructure.Meetings.MeetingReminderWorker>();

// Mail module
builder.Services.AddScoped<IMailService, MailService>();

// Calls module (WebRTC signaling + LiveKit SFU access tokens)
builder.Services.AddSingleton<IPop.Application.Calls.ILiveKitTokenService, IPop.Infrastructure.Calls.LiveKitTokenService>();
builder.Services.AddScoped<IPop.Application.Calls.ICallService, IPop.Infrastructure.Calls.CallService>();

// Projects module
builder.Services.AddScoped<IPop.Application.Projects.IProjectService, IPop.Infrastructure.Projects.ProjectService>();

builder.Services.AddScoped<INotificationGatewayService, NotificationGatewayService>();
// Real-time notification push (SignalR) — broadcaster impl + hub registered with hubs below.
builder.Services.AddSingleton<IPop.Application.Notifications.INotificationRealtimeBroadcaster, IPop.Api.Notifications.NotificationBroadcaster>();

// News & Memos module (single table with type enum: news/announcement/memo)
builder.Services.AddScoped<IPop.Application.News.INewsService, IPop.Infrastructure.News.NewsService>();

// Notes module (Notion-like pages, Yjs co-editing over /hubs/notes)
builder.Services.AddScoped<IPop.Application.Notes.INoteService, IPop.Infrastructure.Notes.NoteService>();
builder.Services.AddScoped<IAttachmentAccessPolicy, IPop.Infrastructure.Notes.NotesAttachmentPolicy>();

builder.Services.AddScoped<IHrisSyncService, HrisSyncService>();
// Adapter by config (spec §4): Production → HttpHrisGateway (Polly), otherwise JSON file.
if (builder.Configuration["Hris:Mode"] == "Production")
    builder.Services.AddHttpClient<IHrisGateway, HttpHrisGateway>();
else
    builder.Services.AddSingleton<IHrisGateway, JsonHrisGateway>();
builder.Services.AddHostedService<HrisSyncWorker>();
builder.Services.AddHostedService<MailScheduledDeliveryWorker>();
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
app.MapCalls();
app.MapMail();
app.MapProjects();
app.MapMeetings();
app.MapNews();
app.MapNotes();
app.MapNotificationGateway();
app.MapPresence();
app.MapDocumentDemo();
app.MapSettings();
app.MapAttachments();
app.MapDashboardAndSearch();
app.MapHub<ChatHub>("/hubs/chat").RequireAuthorization();
app.MapHub<IPop.Api.Calls.CallHub>("/hubs/calls").RequireAuthorization();
app.MapHub<IPop.Api.Notes.NotesHub>("/hubs/notes").RequireAuthorization();
app.MapHub<IPop.Api.Notifications.NotificationHub>("/hubs/notification").RequireAuthorization();
app.MapMe();
app.MapProfile();
app.MapHub<AppHub>("/hubs/app").RequireAuthorization();

app.Run();
