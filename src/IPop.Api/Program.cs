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

// Container/cluster switches (all default to today's behaviour on the Windows host):
// Workers:Outbound=false stops everything that reaches people or other systems (mail, push, iDesk, HRIS),
// for a test cluster running on a copy of the prod database. Workers:OpsHealth=false hands disk/backup
// alerts to the cluster's own monitoring.
var outboundWorkers = builder.Configuration.GetValue("Workers:Outbound", true);
var opsHealthWorker = builder.Configuration.GetValue("Workers:OpsHealth", true);
builder.Services.AddDbContext<IPopDbContext>(o =>
    o.UseNpgsql(builder.Configuration.GetConnectionString("IPopDb"))
     .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning)));
// Keys live in the database. Their isolation name defaults to the content root path, which differs between
// the old Windows host and the container (/data); DataProtection:ApplicationName pins the old one so
// existing protected data (OIDC signing keys, media tickets) still opens after a move.
var dataProtection = builder.Services.AddDataProtection().PersistKeysToDbContext<IPopDbContext>();
if (builder.Configuration["DataProtection:ApplicationName"] is { Length: > 0 } dpName) dataProtection.SetApplicationName(dpName);

builder.Services.AddScoped<IPasswordHasher, Argon2PasswordHasher>();
builder.Services.AddSingleton<ITokenService, JwtTokenService>();
builder.Services.AddScoped<IAuthWorkflow, AuthWorkflow>();
builder.Services.AddScoped<IPop.Application.Auth.IPasswordReset, IPop.Infrastructure.Auth.PasswordResetService>();
// Forgot/reset password are anonymous: 5 requests per IP per 15 minutes.
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    // Placing calls: ten per minute per signed-in user (NIK), so nobody can ring-spam a colleague.
    o.AddPolicy(IPop.Api.Endpoints.CallEndpoints.CallRateLimitPolicy, http => System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(
        http.User.FindFirst("nik")?.Value ?? http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1) }));
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
    // Chat attachment uploads: 30 requests per signed-in user per minute.
    o.AddPolicy(IPop.Api.Endpoints.ChatEndpoints.UploadRateLimitPolicy, http => System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(
        http.User.FindFirst("nik")?.Value ?? http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions { PermitLimit = 30, Window = TimeSpan.FromMinutes(1) }));
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
// Admin › Email (DB) overrides the file; the sender re-reads it per send, so no restart.
builder.Services.AddScoped<SmtpSettingsStore>();
builder.Services.AddScoped<IEmailSender, SmtpEmailSender>();
builder.Services.AddSingleton<EmailFailureAlerts>();
builder.Services.AddScoped<IEmailQueue, DbEmailQueue>();
builder.Services.AddScoped<EmailOutboxProcessor>();
if (outboundWorkers) builder.Services.AddHostedService<EmailOutboxWorker>();
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
// No client-wide timeout: the gateway gives API calls 100 s and file transfers (500 MB mail
// attachments) 30 min itself, per request.
// NAS TLS: a pinned public key per host (Synology:CertificatePins), else a normal chain check - never "accept any".
// ConnectTimeout: an unreachable plant NAS fails in 10 s instead of hanging until the browser gives up.
var nasPins = new IPop.Infrastructure.Files.NasCertificatePins(builder.Configuration);
builder.Services.AddSingleton(nasPins);
builder.Services.AddHttpClient<ISynologyGateway, HttpSynologyGateway>(c => c.Timeout = Timeout.InfiniteTimeSpan)
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
    {
        ConnectTimeout = TimeSpan.FromSeconds(10),
        PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        SslOptions = new System.Net.Security.SslClientAuthenticationOptions
        {
            RemoteCertificateValidationCallback = (sender, cert, _, errors) =>
                nasPins.Accept(sender is System.Net.Security.SslStream ssl ? ssl.TargetHostName : "", cert as System.Security.Cryptography.X509Certificates.X509Certificate2
                    ?? (cert is null ? null : new System.Security.Cryptography.X509Certificates.X509Certificate2(cert)), errors),
        },
    });

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(o =>
    {
        // SignalR browser clients can't set headers on the websocket handshake, so they send the JWT as
        // ?access_token=. Lift it onto the request here — only for the paths QueryTokenRule allows (/hubs).
        o.Events = new JwtBearerEvents
        {
            OnMessageReceived = ctx =>
            {
                var token = ctx.Request.Query["access_token"];
                if (!string.IsNullOrEmpty(token) && IPop.Api.QueryTokenRule.Allows(ctx.HttpContext.Request.Path))
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
IPop.Api.Sso.SsoSetup.AddIPopSso(builder.Services, builder.Configuration);

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
    .AddHubOptions<IPop.Api.Notes.NotesHub>(o => o.MaximumReceiveMessageSize = 768 * 1024)
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
builder.Services.AddScoped<IPop.Application.Approval.IApprovalService, IPop.Infrastructure.Approval.ApprovalService>();
builder.Services.AddScoped<IPop.Infrastructure.Approval.ApprovalService>();
builder.Services.AddScoped<IPop.Infrastructure.Approval.TreePlanner>();
builder.Services.AddScoped<IPop.Infrastructure.Approval.ApprovalImport>();
builder.Services.AddScoped<IChatNotifier, ChatNotifier>();
builder.Services.AddScoped<IStickerService, IPop.Infrastructure.Chat.StickerService>();
builder.Services.AddScoped<INotificationGateway, NotificationGateway>();
builder.Services.AddHostedService<ChatAttachmentGcWorker>();
builder.Services.AddHostedService<ChatMentionNotifyWorker>();
builder.Services.AddHostedService<IPop.Infrastructure.Projects.TaskReminderWorker>();
builder.Services.AddHostedService<IPop.Infrastructure.Projects.DeletedTaskPurgeWorker>();
builder.Services.AddHostedService<IPop.Infrastructure.Projects.TimerAutoStopWorker>();
builder.Services.AddHostedService<IPop.Infrastructure.Meetings.MeetingReminderWorker>();
builder.Services.AddScoped<IPop.Application.MeetingRooms.IMeetingRoomService, IPop.Infrastructure.MeetingRooms.MeetingRoomService>();
builder.Services.AddScoped<IPop.Application.MeetingRooms.IRoomBookingWriter, IPop.Api.MeetingRooms.RoomMeetingCreator>();
// Wrong room-password counts (5 / 5 min per user and booking), in memory: one API instance, resets on restart.
builder.Services.AddSingleton<IPop.Infrastructure.MeetingRooms.RoomPasswordLimiter>();
var meetingMedia = new IPop.Infrastructure.MeetingRooms.MeetingRoomMedia(builder.Configuration);
// Explicit deployment identity, independent of ASP.NET environment (production also uses Development).
IPop.Infrastructure.MeetingRooms.MeetingRoomMedia.ValidateDeployment(builder.Configuration);
builder.Services.AddSingleton(meetingMedia);
// When each hall room was last empty (overrun rule) + last metadata written; in memory, starts "empty at app start".
builder.Services.AddSingleton<IPop.Infrastructure.MeetingRooms.RoomOccupancyTracker>();
builder.Services.AddScoped<IPop.Application.MeetingRooms.IMeetingRecordingService, IPop.Infrastructure.MeetingRooms.MeetingRecordingService>();
builder.Services.AddSingleton<IPop.Infrastructure.MeetingRooms.MeetingChatRateLimiter>();
builder.Services.AddScoped<IPop.Application.MeetingRooms.IMeetingChatService, IPop.Infrastructure.MeetingRooms.MeetingChatService>();
builder.Services.AddScoped<IPop.Infrastructure.MeetingRooms.RecordingFileCache>();
// Recording file tickets: HMAC key HKDF-derived from Jwt:Key with its own purpose label (never the JWT key itself).
builder.Services.AddSingleton(new IPop.Infrastructure.MeetingRooms.RecordingTicketSigner(
    builder.Configuration["Jwt:Key"] ?? throw new InvalidOperationException("Jwt:Key is required")));
builder.Services.AddHostedService<IPop.Infrastructure.MeetingRooms.MeetingRecordingWorker>();
// Every 30 s: removes people no longer allowed in a hall room (booking ended / next booking / removed attendee) and keeps the room's booking metadata fresh.
builder.Services.AddHostedService<IPop.Infrastructure.MeetingRooms.MeetingRoomBookingWorker>();

// Mail module
builder.Services.AddScoped<IMailService, MailService>();
// Images pasted into mail bodies live in the attachment store (area "mail"); readable by the mail's participants.
builder.Services.AddScoped<IAttachmentAccessPolicy, IPop.Infrastructure.Mail.MailAttachmentPolicy>();

// Calls module (1:1 P2P WebRTC signaling; LiveKit tokens are for meetings only)
builder.Services.AddSingleton<IPop.Application.Calls.ILiveKitTokenService, IPop.Infrastructure.Calls.LiveKitTokenService>();
builder.Services.AddScoped<IPop.Application.Calls.ICallService, IPop.Infrastructure.Calls.CallService>();
builder.Services.AddSingleton<IPop.Api.Calls.CallConnections>();
// iPop Notifier: release channels (pilot → all) and the "new version" signal to running apps.
builder.Services.AddSingleton<IPop.Api.Notifier.NotifierReleases>();
builder.Services.AddHostedService<IPop.Api.Notifier.NotifierReleaseWatcher>();
builder.Services.AddHostedService<IPop.Api.Calls.CallRingTimeoutWorker>();
builder.Services.AddHostedService<IPop.Api.Calls.CallReaperWorker>();

// Projects module
builder.Services.AddScoped<IPop.Application.Projects.IProjectService, IPop.Infrastructure.Projects.ProjectService>();
builder.Services.AddScoped<IPop.Application.Projects.IProjectFieldService, IPop.Infrastructure.Projects.ProjectFieldService>();
builder.Services.AddScoped<IPop.Application.Projects.IPortfolioService, IPop.Infrastructure.Projects.PortfolioService>();
builder.Services.AddScoped<IPop.Application.Projects.ITaskFileService, IPop.Infrastructure.Projects.TaskFileService>();
builder.Services.AddScoped<IPop.Application.Projects.IMilestoneService, IPop.Infrastructure.Projects.MilestoneService>();
builder.Services.AddScoped<IPop.Application.Projects.IProjectTemplateService, IPop.Infrastructure.Projects.ProjectTemplateService>();
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
// Native Android app → FCM HTTP v1 (Fcm section: ProjectId, ServiceAccountPath, ApplicationId, ApiKey, SenderId). Missing = off.
var fcm = IPop.Infrastructure.Notifications.Push.FcmOptions.Bind(builder.Configuration);
builder.Services.AddSingleton(fcm);
if (fcm.IsConfigured)
    builder.Services.AddSingleton<IPop.Infrastructure.Notifications.Push.INativePushTransport, IPop.Infrastructure.Notifications.Push.FcmTransport>();
else
    builder.Services.AddSingleton<IPop.Infrastructure.Notifications.Push.INativePushTransport, IPop.Infrastructure.Notifications.Push.DisabledNativePushTransport>();
builder.Services.AddSingleton<IPop.Infrastructure.Notifications.Push.PushQueue>();
builder.Services.AddSingleton<IPop.Infrastructure.Notifications.Push.IDesktopToastSink, IPop.Api.Notifications.DesktopToastSink>();
builder.Services.AddScoped<IPop.Infrastructure.Notifications.Push.PushDispatcher>();
if (outboundWorkers) builder.Services.AddHostedService<IPop.Infrastructure.Notifications.Push.PushWorker>();

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
builder.Services.AddScoped<IAttachmentAccessPolicy, IPop.Api.Endpoints.RemoteMediaPolicy>();

// Notes module (Notion-like pages, Yjs co-editing over /hubs/notes)
builder.Services.AddScoped<IPop.Application.Notes.INoteService, IPop.Infrastructure.Notes.NoteService>();
builder.Services.AddScoped<IAttachmentAccessPolicy, IPop.Infrastructure.Notes.NotesAttachmentPolicy>();

// ICT tickets via iDesk (IDesk:ClientSecret >= 32 chars, else fail closed: create 503, webhook 503).
builder.Services.AddSingleton(IPop.Infrastructure.Tickets.IdeskOptions.Bind(builder.Configuration));
builder.Services.AddSingleton<IPop.Infrastructure.Tickets.TicketModuleCache>();
builder.Services.AddHttpClient<IPop.Application.Tickets.IIdeskClient, IPop.Infrastructure.Tickets.HttpIdeskClient>();
builder.Services.AddHttpClient<IPop.Infrastructure.Calls.ILiveKitRoomAdmin, IPop.Infrastructure.Calls.LiveKitRoomAdmin>(c => c.Timeout = TimeSpan.FromSeconds(5));
builder.Services.AddHttpClient<IPop.Infrastructure.Calls.ILiveKitEgress, IPop.Infrastructure.Calls.LiveKitEgress>(c => c.Timeout = TimeSpan.FromSeconds(10));
builder.Services.AddScoped<IPop.Infrastructure.Tickets.IdeskCalendarSync>();
builder.Services.AddScoped<IPop.Application.Tickets.ITicketService>(sp => new IPop.Infrastructure.Tickets.TicketService(
    sp.GetRequiredService<IPop.Infrastructure.Persistence.IPopDbContext>(), sp.GetRequiredService<IPop.Application.Tickets.IIdeskClient>(),
    sp.GetRequiredService<IPop.Infrastructure.Tickets.TicketModuleCache>(), sp.GetRequiredService<IPop.Application.Notifications.INotificationGatewayService>(),
    sp.GetRequiredService<IPop.Application.Realtime.IRealtime>(), sp.GetRequiredService<ILogger<IPop.Infrastructure.Tickets.TicketService>>(),
    sp.GetRequiredService<IPop.Infrastructure.Tickets.IdeskCalendarSync>(),
    sp.GetService<IPop.Infrastructure.Notifications.Push.IDesktopToastSink>()));
if (outboundWorkers) builder.Services.AddHostedService<IPop.Infrastructure.Tickets.TicketOutboxWorker>();
if (outboundWorkers) builder.Services.AddHostedService<IPop.Infrastructure.Tickets.TicketReconcileWorker>();
if (outboundWorkers) builder.Services.AddHostedService<IPop.Infrastructure.Tickets.IdeskCalendarWorker>();

builder.Services.AddScoped<IHrisSyncService, HrisSyncService>();
// Adapter by config (spec §4): Production → HttpHrisGateway (Polly), otherwise JSON file.
if (builder.Configuration["Hris:Mode"] == "Production")
    builder.Services.AddHttpClient<IHrisGateway, HttpHrisGateway>();
else
    builder.Services.AddSingleton<IHrisGateway, JsonHrisGateway>();
if (outboundWorkers) builder.Services.AddHostedService<HrisSyncWorker>();
builder.Services.AddHostedService<MailScheduledDeliveryWorker>();
builder.Services.AddSingleton<MailNotifySignal>();
builder.Services.AddHostedService<MailNotifyWorker>();
builder.Services.AddHostedService<IPop.Infrastructure.Notifications.NotificationRetentionWorker>();
// Hourly: nightly DB backup still succeeding, data drives not filling up → alert admins.
if (opsHealthWorker) builder.Services.AddHostedService<IPop.Infrastructure.Ops.OpsHealthWorker>();
// Every 5 min: files kept on the server while the NAS was down move to the NAS.
builder.Services.AddHostedService(sp => new IPop.Infrastructure.Files.LocalToNasWorker(
    sp.GetRequiredService<IServiceScopeFactory>(), sp.GetRequiredService<IConfiguration>(),
    sp.GetRequiredService<ILogger<IPop.Infrastructure.Files.LocalToNasWorker>>(), builder.Environment.ContentRootPath));
builder.Services.AddHostedService<IPop.Infrastructure.Notifications.NotificationLinkBackfill>();
builder.Services.AddHostedService<IPop.Infrastructure.Files.FileOperationWorker>();
builder.Services.AddHostedService<IPop.Infrastructure.Files.FileOperationRetentionWorker>();

// Pairing perangkat SJA Notifier lewat NIK (Sja:AppSecret wajib di user-secrets/env).
builder.Services.AddHttpClient<ISjaGateway, HttpSjaGateway>();

// DevExpress Office File API (Word, Excel, PDF, PowerPoint, Skia Cross-Platform)
builder.Services.AddDevExpressDocumentServices();

builder.Services.AddScoped<IPop.Application.Search.ISearchService, IPop.Infrastructure.Search.SearchService>();
// Global search (Ctrl+K): one provider per module, each with its own access rules; searched one request at a time.
builder.Services.AddScoped<IPop.Application.Search.ISearchModuleProvider, IPop.Infrastructure.Search.Providers.NavigationSearchProvider>();
builder.Services.AddScoped<IPop.Application.Search.ISearchModuleProvider, IPop.Infrastructure.Search.Providers.DirectorySearchProvider>();
builder.Services.AddScoped<IPop.Application.Search.ISearchModuleProvider, IPop.Infrastructure.Search.Providers.ProjectSearchProvider>();
builder.Services.AddScoped<IPop.Application.Search.ISearchModuleProvider, IPop.Infrastructure.Search.Providers.TaskSearchProvider>();
builder.Services.AddScoped<IPop.Application.Search.ISearchModuleProvider, IPop.Infrastructure.Search.Providers.NotesSearchProvider>();
builder.Services.AddScoped<IPop.Application.Search.ISearchModuleProvider, IPop.Infrastructure.Search.Providers.ChatSearchProvider>();
builder.Services.AddScoped<IPop.Application.Search.ISearchModuleProvider, IPop.Infrastructure.Search.Providers.MailSearchProvider>();
builder.Services.AddScoped<IPop.Application.Search.ISearchModuleProvider, IPop.Infrastructure.Search.Providers.NewsSearchProvider>();
builder.Services.AddScoped<IPop.Application.Search.ISearchModuleProvider, IPop.Infrastructure.Search.Providers.CalendarSearchProvider>();
builder.Services.AddScoped<IPop.Application.Search.ISearchModuleProvider, IPop.Infrastructure.Search.Providers.TicketSearchProvider>();
builder.Services.AddScoped<IPop.Application.Search.ISearchModuleProvider, IPop.Infrastructure.Search.Providers.FilesSearchProvider>();
builder.Services.AddScoped<IPop.Application.Search.ISearchModuleProvider, IPop.Infrastructure.Search.Providers.CallsSearchProvider>();
builder.Services.AddScoped<IPop.Application.Search.ISearchModuleProvider, IPop.Infrastructure.Search.Providers.NotificationsSearchProvider>();
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

// "--migrate-only": the cluster's migration Job (one run per release, before the API pods roll).
var migrateOnly = args.Contains("--migrate-only");
if (app.Environment.IsDevelopment() && !migrateOnly) app.MapOpenApi();
if (app.Environment.IsDevelopment() || migrateOnly)
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<IPopDbContext>();
    await db.Database.MigrateAsync();
    await DevSeed.EnsureSeededAsync(db, scope.ServiceProvider.GetRequiredService<IPop.Application.Auth.IPasswordHasher>());
    // Reference data: built-in project templates (idempotent, by fixed id).
    await IPop.Infrastructure.Projects.BuiltInProjectTemplates.EnsureSeededAsync(db);
}
if (migrateOnly)
{
    app.Logger.LogInformation("Database migrated and seeded; exiting (--migrate-only).");
    return;
}
{
    using var scope = app.Services.CreateScope();
    await IPop.Infrastructure.MeetingRooms.RoomPasswordBackfill.RunAsync(
        scope.ServiceProvider.GetRequiredService<IPopDbContext>(), DateTime.UtcNow, CancellationToken.None, app.Logger);
}
{
    // One-time move of group photos from chat-files/avatars into the database (no-op afterwards).
    using var scope = app.Services.CreateScope();
    try { await IPop.Api.Endpoints.ChatEndpoints.ImportLocalGroupAvatarsAsync(scope.ServiceProvider.GetRequiredService<IPopDbContext>(), app.Environment.ContentRootPath, CancellationToken.None); }
    catch (Exception ex) { app.Logger.LogWarning(ex, "Group photo import skipped"); }
}

// Behind NPM/Zoraxy/Traefik every request arrives from the proxy, so per-IP rate limits ("forgot password": 5 per
// 15 min), audit logs and session IPs saw one address for the whole company. Take the client address from the
// forwarded headers, but only when the request comes straight from a trusted proxy (Notifier:TrustedProxies).
app.Use((http, next) =>
{
    if (IPop.Api.Notifier.ClientAddress.Of(http, http.RequestServices.GetRequiredService<IConfiguration>()) is { } client
        && System.Net.IPAddress.TryParse(client, out var ip))
        http.Connection.RemoteIpAddress = ip;
    return next(http);
});
app.UseCors("web-dev");
// Dev serves http and https side by side (http for LAN browsing, https for calls' microphone);
// redirecting would push http pages onto the self-signed port and break their CORS preflights.
// Behind the cluster ingress TLS ends at the reverse proxy, so Https:Redirect=false there.
if (!app.Environment.IsDevelopment() && app.Configuration.GetValue("Https:Redirect", true)) app.UseHttpsRedirection();
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
app.MapApproval();
app.MapFiles();
app.MapOnlyOffice();
app.MapChat();
// Group photos: random 128-bit file names, served without auth so <img> tags work.
app.MapGet("/api/chat-avatars/{name}", async (string name, IWebHostEnvironment env, IPopDbContext db, CancellationToken ct) =>
{
    if (IPop.Api.Endpoints.ChatEndpoints.GroupAvatarName(name) is not { } key) return Results.NotFound();
    var bytes = await db.ChatGroupAvatars.AsNoTracking().Where(a => a.Name == key).Select(a => a.Content).FirstOrDefaultAsync(ct);
    if (bytes is not null) return Results.File(bytes, "image/jpeg");
    // Older photos not imported yet: still on local disk.
    var path = IPop.Api.Endpoints.ChatEndpoints.GroupAvatarPath(env.ContentRootPath, name);
    return path is not null && File.Exists(path) ? Results.File(path, "image/jpeg", enableRangeProcessing: false) : Results.NotFound();
}).AllowAnonymous();
app.MapStickers();
app.MapCalls();
app.MapMail();
app.MapMailGroups();
app.MapMailInlineImages();
app.MapProjects();
app.MapProjectFields();
app.MapProjectTime();
app.MapProjectPortfolio();
app.MapMeetings();
app.MapInvitePicker();
app.MapMeetingRooms();
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
app.MapEmailSettings();
app.MapAttachments();
app.MapMediaStream();
app.MapDashboardAndSearch();
app.MapLinkPreview();
app.MapHub<ChatHub>("/hubs/chat").RequireAuthorization();
app.MapHub<IPop.Api.Calls.CallHub>("/hubs/calls").RequireAuthorization();
// Notes edits travel as base64 Yjs updates of up to 512 KB (NoteService.MaxUpdateBytes): the 32 KB SignalR default
// silently dropped every update over ~24 KB (a pasted table), the client retried forever and the edit was lost.
app.MapHub<IPop.Api.Notes.NotesHub>("/hubs/notes", o => o.ApplicationMaxBufferSize = 768 * 1024).RequireAuthorization();
app.MapHub<IPop.Api.Notifications.NotificationHub>("/hubs/notification").RequireAuthorization();
app.MapMe();
app.MapUiPrefs();
app.MapNotifier();
app.MapRemote();
IPop.Api.Notifier.NotifierLinkEndpoints.MapNotifierLink(app);
if (IPop.Api.Sso.SsoSetup.Enabled(app.Configuration))
{
    IPop.Api.Sso.SsoEndpoints.MapSso(app, app.Configuration["Oidc:WebBase"] ?? "");
    try { await IPop.Api.Sso.SsoSetup.SeedClientsAsync(app.Services, app.Configuration, CancellationToken.None); }
    catch (Exception ex) { app.Logger.LogWarning(ex, "Sign in with iPop: client registration skipped (database not migrated yet?)"); }
}
app.MapProfile();
// Liveness/readiness for the cluster: process up and database reachable.
app.MapGet("/healthz", async (IPopDbContext db, CancellationToken ct) =>
    await db.Database.CanConnectAsync(ct) ? Results.Ok(new { status = "ok" }) : Results.StatusCode(StatusCodes.Status503ServiceUnavailable))
    .AllowAnonymous();
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
