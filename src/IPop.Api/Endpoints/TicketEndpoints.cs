using System.Security.Claims;
using System.Text;
using IPop.Api.Rbac;
using IPop.Application.Tickets;
using IPop.Infrastructure.Persistence;
using IPop.Infrastructure.Tickets;
using Microsoft.EntityFrameworkCore;

namespace IPop.Api.Endpoints;

/// /api/tickets — ICT tickets via iDesk (docs/specs/idesk-tickets.md). Everyone sees only their own
/// tickets, admins included. /api/idesk/webhook is iDesk's signed callback.
public static class TicketEndpoints
{
    public const string WebhookRateLimitPolicy = "idesk-webhook";
    /// 5 × 10 MB as base64 (×4/3) plus the JSON around it.
    public const long MaxCreateRequestBytes = 80L * 1024 * 1024;
    /// Webhook bodies are one ticket snapshot; anything this big is not from iDesk.
    private const int MaxWebhookBytes = 256 * 1024;

    public static IEndpointRouteBuilder MapTickets(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/tickets").RequireAuthorization().RequirePermission("tickets.read");

        g.MapGet("/modules", async (ITicketService svc, CancellationToken ct) =>
            await svc.GetModulesAsync(ct) is { } modules ? Results.Ok(modules) : Unreachable());

        g.MapGet("", (string? state, HttpContext http, ITicketService svc, CancellationToken ct) =>
            Run(http, async me => Results.Ok(await svc.ListAsync(me, OpenOnly(state), ct))));

        g.MapPost("/sync", (string? state, HttpContext http, ITicketService svc, CancellationToken ct) =>
            Run(http, async me => Results.Ok(await svc.SyncAsync(me, OpenOnly(state), ct))));

        g.MapGet("/{id:guid}", (Guid id, HttpContext http, ITicketService svc, IServiceScopeFactory scopes, CancellationToken ct) =>
            Run(http, async me =>
            {
                var detail = await svc.GetAsync(me, id, ct);
                if (detail is null) return NotFound();
                // Answer from the mirror now; a refresh that finds changes reaches the page as tickets.changed.
                if (detail.TicketNumber is not null && detail.Status is not (TicketStatus.Sending or TicketStatus.Failed))
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await using var scope = scopes.CreateAsyncScope();
                            if (scope.ServiceProvider.GetRequiredService<ITicketService>() is TicketService s)
                                await s.RefreshAsync(id, TimeSpan.FromMinutes(1), CancellationToken.None);
                        }
                        catch { /* best effort: reconcile catches up */ }
                    });
                return Results.Ok(detail);
            }));

        // The ticket's public chat from iDesk (read-only); opening it clears the reply counter.
        // ?peek=1 (Notifier popup): show the chat without marking it read.
        g.MapGet("/{id:guid}/messages", (Guid id, string? peek, HttpContext http, ITicketService svc, CancellationToken ct) =>
            Run(http, async me => await svc.GetMessagesAsync(me, id, ct, peek is "1" or "true") is { } m ? Results.Ok(m) : NotFound()));

        // Notifications for the assigned agent point at /idesk/tickets/{id}: the ticket in iDesk, opened through
        // "Sign in with iPop" when that is on. Answers the link; the page or app opens it.
        g.MapGet("/idesk/{ideskTicketId:guid}/link", (Guid ideskTicketId) =>
            Results.Ok(new { url = IdeskLinks.Public($"{IdeskOptions.CurrentPublicUrl}/tickets/{ideskTicketId}",
                IdeskOptions.CurrentPublicUrl, IdeskOptions.CurrentSignInWithIpop) }));

        // A text reply from the iPop Notifier toast: on my ticket (id), or as the assigned agent on an iDesk ticket id.
        g.MapPost("/{ticketRef}/reply", (string ticketRef, TicketReplyRequest req, HttpContext http, ITicketService svc, CancellationToken ct) =>
            Run(http, async me =>
            {
                var r = await svc.ReplyAsync(me, ticketRef, req.Content ?? "", ct);
                return r.Kind switch
                {
                    TicketReplyKind.Sent => Results.Ok(new { ok = true }),
                    TicketReplyKind.Invalid => Results.Json(new { code = "invalid", message = r.Message }, statusCode: 400),
                    TicketReplyKind.NotFound => NotFound(),
                    TicketReplyKind.NotSent => Results.Json(new { code = "not_sent", message = r.Message }, statusCode: 409),
                    TicketReplyKind.Rejected => Results.Json(new { code = "rejected", message = r.Message ?? "iDesk didn't accept this reply." }, statusCode: 400),
                    TicketReplyKind.NotConfigured => NotConfigured(),
                    _ => Unreachable(r.Message),
                };
            }));

        g.MapPost("", (CreateTicketRequest req, HttpContext http, ITicketService svc, CancellationToken ct) =>
            Run(http, async me =>
            {
                var r = await svc.CreateAsync(me, req, ct);
                return r.Kind switch
                {
                    TicketCreateKind.Created => Results.Json(new CreateTicketResultDto(r.Ticket!, false), statusCode: 201),
                    TicketCreateKind.Queued => Results.Json(new CreateTicketResultDto(r.Ticket!, true), statusCode: 202),
                    TicketCreateKind.Invalid => Results.Json(new { code = "invalid", message = r.Message, field = r.Field }, statusCode: 400),
                    TicketCreateKind.Rejected => Results.Json(new { code = "rejected", message = r.Message }, statusCode: 400),
                    TicketCreateKind.NotRegistered => NotRegistered(r.Message),
                    _ => NotConfigured(),
                };
            }))
            .RequirePermission("tickets.create")
            .WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(MaxCreateRequestBytes));

        g.MapPost("/{id:guid}/retry", (Guid id, HttpContext http, ITicketService svc, CancellationToken ct) =>
            Run(http, async me =>
            {
                var r = await svc.RetryAsync(me, id, ct);
                return r.Kind switch
                {
                    TicketRetryKind.Sent => Results.Ok(r.Ticket),
                    TicketRetryKind.NotFound => NotFound(),
                    TicketRetryKind.NotFailed => Results.Json(new { code = "not_pending", message = "This ticket was already sent." }, statusCode: 409),
                    TicketRetryKind.Rejected => Results.Json(new { code = "rejected", message = r.Message }, statusCode: 400),
                    TicketRetryKind.NotRegistered => NotRegistered(r.Message),
                    TicketRetryKind.NotConfigured => NotConfigured(),
                    _ => Unreachable(r.Message),
                };
            })).RequirePermission("tickets.create");

        g.MapDelete("/{id:guid}", (Guid id, HttpContext http, ITicketService svc, CancellationToken ct) =>
            Run(http, async me => await svc.DiscardAsync(me, id, ct) switch
            {
                TicketDiscardKind.Discarded => Results.NoContent(),
                TicketDiscardKind.NotFound => NotFound(),
                _ => Results.Json(new { code = "not_pending", message = "Only a ticket that hasn't reached iDesk can be discarded." }, statusCode: 409),
            })).RequirePermission("tickets.create");

        // iDesk → iPop. Anonymous but HMAC-signed; behind the proxy, so it also passes the
        // Security:AllowedClientNetworks allowlist. No JWT means the forced-password-change gate
        // never applies (it keys on a claim) — AllowAnonymous makes that explicit.
        app.MapPost("/api/idesk/webhook", async (HttpContext http, IdeskOptions options, ITicketService svc,
            ILoggerFactory logs, CancellationToken ct) =>
        {
            if (!options.IsConfigured) return Results.StatusCode(503);
            if (http.Request.ContentLength > MaxWebhookBytes) return Results.StatusCode(413);
            var body = await ReadBodyAsync(http.Request, ct);
            if (body is null) return Results.StatusCode(413);

            var h = http.Request.Headers;
            if (!string.Equals(h[IdeskSignature.ClientIdHeader].ToString(), options.ClientId, StringComparison.Ordinal)
                || !IdeskSignature.Verify(options.ClientSecret, h[IdeskSignature.TimestampHeader], h[IdeskSignature.SignatureHeader], body, DateTimeOffset.UtcNow))
                return Results.Unauthorized();

            var kind = await svc.ApplyWebhookAsync(body, ct);
            if (kind == TicketWebhookKind.Invalid)
            {
                logs.CreateLogger("IPop.Tickets.Webhook").LogWarning("Signed iDesk webhook with an unreadable body ignored");
                return Results.BadRequest(new { code = "invalid" });
            }
            return Results.Ok(new { ok = true, result = kind.ToString() });
        }).AllowAnonymous().DisableAntiforgery().RequireRateLimiting(WebhookRateLimitPolicy);

        return app;
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static bool OpenOnly(string? state) => !string.Equals(state, "all", StringComparison.OrdinalIgnoreCase);

    private static async Task<string?> ReadBodyAsync(HttpRequest req, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        var buffer = new byte[16 * 1024];
        int n;
        while ((n = await req.Body.ReadAsync(buffer, ct)) > 0)
        {
            if (ms.Length + n > MaxWebhookBytes) return null;
            ms.Write(buffer, 0, n);
        }
        return Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
    }

    private static IResult NotFound() => Results.Json(new { code = "not_found", message = "Ticket not found." }, statusCode: 404);
    private static IResult NotRegistered(string? message) =>
        Results.Json(new { code = "not_registered", message = message ?? TicketService.NotRegisteredMessage }, statusCode: 422);
    private static IResult NotConfigured() =>
        Results.Json(new { code = "not_configured", message = "iDesk is not configured on this server." }, statusCode: 503);
    private static IResult Unreachable(string? message = null) =>
        Results.Json(new { code = "unreachable", message = message ?? "iDesk can't be reached right now." }, statusCode: 503);

    private static async Task<Guid?> MeAsync(HttpContext http)
    {
        var nik = http.User.FindFirst("nik")?.Value ?? http.User.FindFirst(ClaimTypes.Name)?.Value;
        if (string.IsNullOrEmpty(nik)) return null;
        var db = http.RequestServices.GetRequiredService<IPopDbContext>();
        return await db.Users.AsNoTracking().Where(u => u.Nik == nik).Select(u => (Guid?)u.Id).FirstOrDefaultAsync();
    }

    private static async Task<IResult> Run(HttpContext http, Func<Guid, Task<IResult>> body)
    {
        var me = await MeAsync(http);
        return me is null ? Results.Unauthorized() : await body(me.Value);
    }
}

public sealed record TicketReplyRequest(string? Content);
