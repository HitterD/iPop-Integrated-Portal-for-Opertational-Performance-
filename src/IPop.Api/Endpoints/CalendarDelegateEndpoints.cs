using System.Text.Json;
using IPop.Application.Notifications;
using IPop.Core.Entities;
using IPop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IPop.Api.Endpoints;

/// Personal calendar grants on the access ladder (See event titles … Act on my behalf). The owner grants a
/// level in Calendar settings, or an admin grants it per user. Every grant/change/revoke notifies both people
/// (except the one who did it) and writes an audit row. "Edit my events" and "Act on my behalf" make the
/// person an assistant who can open the calendar as its owner (Calendars I manage).
public static class CalendarDelegateEndpoints
{
    public const int MaxDelegates = 50;

    public static IEndpointRouteBuilder MapCalendarDelegates(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/calendar").RequireAuthorization();

        // People who manage my calendar.
        g.MapGet("/delegates", async (HttpContext http, IPopDbContext db, CancellationToken ct) =>
        {
            var me = await GetUserIdAsync(http, db, ct);
            if (me is null) return Results.Unauthorized();
            return Results.Ok(await ListDelegatesAsync(db, me.Value, ct));
        });

        g.MapPut("/delegates/{nik}", async (string nik, SetDelegateRequest req, HttpContext http, IPopDbContext db,
            INotificationGatewayService? notifs, CancellationToken ct) =>
        {
            var me = await GetUserIdAsync(http, db, ct);
            if (me is null) return Results.Unauthorized();
            var r = await GrantAsync(db, notifs, MeetingEndpoints.NotifyLog(http), me.Value, nik, req.Level, me.Value, ct);
            return r is GrantResult.Granted or GrantResult.Changed or GrantResult.Unchanged
                ? Results.Ok(await ListDelegatesAsync(db, me.Value, ct)) : GrantError(r);
        });

        g.MapDelete("/delegates/{nik}", async (string nik, HttpContext http, IPopDbContext db,
            INotificationGatewayService? notifs, CancellationToken ct) =>
        {
            var me = await GetUserIdAsync(http, db, ct);
            if (me is null) return Results.Unauthorized();
            await RevokeAsync(db, notifs, MeetingEndpoints.NotifyLog(http), me.Value, nik, me.Value, ct);
            return Results.Ok(await ListDelegatesAsync(db, me.Value, ct));
        });

        // Calendars I manage for someone else.
        g.MapGet("/managed", async (HttpContext http, IPopDbContext db, CancellationToken ct) =>
        {
            var me = await GetUserIdAsync(http, db, ct);
            if (me is null) return Results.Unauthorized();
            return Results.Ok(await ListManagedAsync(db, me.Value, ct));
        });

        // GET /api/calendar/managed/{nik}/feed?from=&to= — the owner's events and meetings as their delegate sees them.
        g.MapGet("/managed/{nik}/feed", async (string nik, DateTime? from, DateTime? to, HttpContext http, IPopDbContext db, CancellationToken ct) =>
        {
            var me = await GetUserIdAsync(http, db, ct);
            if (me is null) return Results.Unauthorized();
            var owner = await db.Users.AsNoTracking().Where(u => u.Nik == nik && u.IsActive).Select(u => (Guid?)u.Id).FirstOrDefaultAsync(ct);
            if (owner is null) return Results.NotFound();
            var level = await LevelAsync(db, owner.Value, me.Value, ct);
            if (!DelegateLevel.CanEdit(level)) return Forbidden("You can't manage this calendar.");
            var f = AsUtc(from ?? DateTime.UtcNow.Date.AddDays(-7));
            var t = AsUtc(to ?? DateTime.UtcNow.Date.AddDays(42));
            if (t <= f || (t - f).TotalDays > 100) return Results.Json(new { code = "invalid_range", message = "Pick a shorter date range." }, statusCode: 422);
            return Results.Ok(await BuildManagedFeedAsync(db, owner.Value, level, f, t, ct));
        });

        // Admin → People: delegates of any user.
        var admin = app.MapGroup("/api/admin/users/{nik}/calendar-delegates").RequireAuthorization("AdminOnly");

        admin.MapGet("/", async (string nik, IPopDbContext db, CancellationToken ct) =>
        {
            var owner = await db.Users.AsNoTracking().Where(u => u.Nik == nik).Select(u => (Guid?)u.Id).FirstOrDefaultAsync(ct);
            return owner is null ? Results.NotFound() : Results.Ok(await ListDelegatesAsync(db, owner.Value, ct));
        });

        admin.MapPut("/{delegateNik}", async (string nik, string delegateNik, SetDelegateRequest req, HttpContext http, IPopDbContext db,
            INotificationGatewayService? notifs, CancellationToken ct) =>
        {
            var me = await GetUserIdAsync(http, db, ct);
            if (me is null) return Results.Unauthorized();
            var owner = await db.Users.AsNoTracking().Where(u => u.Nik == nik && u.IsActive).Select(u => (Guid?)u.Id).FirstOrDefaultAsync(ct);
            if (owner is null) return Results.NotFound();
            var r = await GrantAsync(db, notifs, MeetingEndpoints.NotifyLog(http), owner.Value, delegateNik, req.Level, me.Value, ct);
            return r is GrantResult.Granted or GrantResult.Changed or GrantResult.Unchanged
                ? Results.Ok(await ListDelegatesAsync(db, owner.Value, ct)) : GrantError(r);
        });

        admin.MapDelete("/{delegateNik}", async (string nik, string delegateNik, HttpContext http, IPopDbContext db,
            INotificationGatewayService? notifs, CancellationToken ct) =>
        {
            var me = await GetUserIdAsync(http, db, ct);
            if (me is null) return Results.Unauthorized();
            var owner = await db.Users.AsNoTracking().Where(u => u.Nik == nik).Select(u => (Guid?)u.Id).FirstOrDefaultAsync(ct);
            if (owner is null) return Results.NotFound();
            await RevokeAsync(db, notifs, MeetingEndpoints.NotifyLog(http), owner.Value, delegateNik, me.Value, ct);
            return Results.Ok(await ListDelegatesAsync(db, owner.Value, ct));
        });

        return app;
    }

    // ── Queries ──

    public static async Task<List<DelegateDto>> ListDelegatesAsync(IPopDbContext db, Guid ownerId, CancellationToken ct) =>
        await db.CalendarDelegates.AsNoTracking()
            .Where(d => d.OwnerId == ownerId && d.Delegate.IsActive)
            .OrderBy(d => d.CreatedAt)
            .Select(d => new DelegateDto(d.Delegate.Nik, d.Delegate.Name, d.Delegate.Department != null ? d.Delegate.Department.Name : null,
                d.Level, d.CreatedById != null && d.CreatedById != ownerId
                    ? db.Users.Where(u => u.Id == d.CreatedById).Select(u => u.Name).FirstOrDefault() : null,
                d.CreatedAt))
            .ToListAsync(ct);

    /// Calendars I can change for someone (Edit my events / Act on my behalf).
    public static async Task<List<ManagedCalendarDto>> ListManagedAsync(IPopDbContext db, Guid delegateId, CancellationToken ct) =>
        await db.CalendarDelegates.AsNoTracking()
            .Where(d => d.DelegateId == delegateId && d.Owner.IsActive && (d.Level == DelegateLevel.Edit || d.Level == DelegateLevel.Full))
            .OrderBy(d => d.Owner.Name)
            .Select(d => new ManagedCalendarDto(d.Owner.Nik, d.Owner.Name, d.Owner.Department != null ? d.Owner.Department.Name : null, d.Level))
            .ToListAsync(ct);

    /// The viewer's delegate level on the owner's calendar, or null.
    public static Task<string?> LevelAsync(IPopDbContext db, Guid ownerId, Guid viewerId, CancellationToken ct) =>
        db.CalendarDelegates.AsNoTracking().Where(d => d.OwnerId == ownerId && d.DelegateId == viewerId)
            .Select(d => (string?)d.Level).FirstOrDefaultAsync(ct);

    /// Owner → level for every calendar the viewer manages.
    public static async Task<Dictionary<Guid, string>> ManagedLevelsAsync(IPopDbContext db, Guid viewerId, CancellationToken ct) =>
        await db.CalendarDelegates.AsNoTracking().Where(d => d.DelegateId == viewerId && d.Owner.IsActive)
            .ToDictionaryAsync(d => d.OwnerId, d => d.Level, ct);

    /// The owner's personal events and meetings (no tasks, mail, news or chat-group events). Private items
    /// come back as an opaque Busy block for "Edit my events"; an "Act on my behalf" assistant sees them.
    public static async Task<List<CalendarEndpoints.CalendarFeedItem>> BuildManagedFeedAsync(IPopDbContext db, Guid ownerId, string level,
        DateTime from, DateTime to, CancellationToken ct)
    {
        var items = await CalendarEndpoints.BuildFeedAsync(db, ownerId, from, to, new CalendarEndpoints.FeedAccess(false, false, false, false), ct);
        var canEdit = DelegateLevel.CanEdit(level);
        return items.Select(i => i.Visibility == CalendarVisibility.Private && level != DelegateLevel.Full
                ? i with { Id = $"private:{i.Id}", Title = "Private", Url = null, Subtitle = null, EventId = null, MeetingId = null,
                    CanEdit = false, Location = null, Link = null }
                : i with { CanEdit = canEdit && i.CanEdit })
            .ToList();
    }

    // ── Grant / revoke ──

    public enum GrantResult { Granted, Changed, Unchanged, NotFound, Self, InvalidLevel, TooMany }

    public static async Task<GrantResult> GrantAsync(IPopDbContext db, INotificationGatewayService? notifs, ILogger? log,
        Guid ownerId, string? delegateNik, string? level, Guid actorId, CancellationToken ct)
    {
        if (!DelegateLevel.IsValid(level)) return GrantResult.InvalidLevel;
        var target = await db.Users.AsNoTracking().Where(u => u.Nik == delegateNik && u.IsActive).Select(u => (Guid?)u.Id).FirstOrDefaultAsync(ct);
        if (target is null) return GrantResult.NotFound;
        if (target == ownerId) return GrantResult.Self;
        var now = DateTime.UtcNow;
        var row = await db.CalendarDelegates.FirstOrDefaultAsync(d => d.OwnerId == ownerId && d.DelegateId == target, ct);
        GrantResult result;
        if (row is null)
        {
            if (await db.CalendarDelegates.CountAsync(d => d.OwnerId == ownerId, ct) >= MaxDelegates) return GrantResult.TooMany;
            db.CalendarDelegates.Add(new CalendarDelegate
            {
                Id = Guid.NewGuid(), OwnerId = ownerId, DelegateId = target.Value, Level = level!, CreatedById = actorId, CreatedAt = now, UpdatedAt = now,
            });
            result = GrantResult.Granted;
        }
        else if (row.Level == level) return GrantResult.Unchanged;
        else
        {
            row.Level = level!;
            row.UpdatedAt = now;
            result = GrantResult.Changed;
        }
        await AuditAsync(db, actorId, result == GrantResult.Granted ? "calendar.delegate.grant" : "calendar.delegate.change", ownerId, target.Value, level, now, ct);
        await db.SaveChangesAsync(ct);
        await NotifyGrantAsync(db, notifs, log, ownerId, target.Value, actorId, result == GrantResult.Granted ? "granted" : "changed", level, ct);
        return result;
    }

    public static async Task<bool> RevokeAsync(IPopDbContext db, INotificationGatewayService? notifs, ILogger? log,
        Guid ownerId, string? delegateNik, Guid actorId, CancellationToken ct)
    {
        var row = await db.CalendarDelegates.FirstOrDefaultAsync(d => d.OwnerId == ownerId && d.Delegate.Nik == delegateNik, ct);
        if (row is null) return false;
        db.CalendarDelegates.Remove(row);
        await AuditAsync(db, actorId, "calendar.delegate.revoke", ownerId, row.DelegateId, row.Level, DateTime.UtcNow, ct);
        await db.SaveChangesAsync(ct);
        await NotifyGrantAsync(db, notifs, log, ownerId, row.DelegateId, actorId, "revoked", null, ct);
        return true;
    }

    private static async Task AuditAsync(IPopDbContext db, Guid actorId, string action, Guid ownerId, Guid delegateId, string? level, DateTime now, CancellationToken ct)
    {
        var niks = await db.Users.AsNoTracking().Where(u => u.Id == actorId || u.Id == ownerId || u.Id == delegateId)
            .ToDictionaryAsync(u => u.Id, u => u.Nik, ct);
        db.AuditLogs.Add(new AuditLog
        {
            ActorNik = niks.GetValueOrDefault(actorId), Action = action, Entity = "CalendarDelegate", EntityId = niks.GetValueOrDefault(ownerId),
            DetailJson = JsonSerializer.Serialize(new { owner = niks.GetValueOrDefault(ownerId), @delegate = niks.GetValueOrDefault(delegateId), level }),
            CreatedAt = now,
        });
    }

    public static int LevelRank(string? level) => DelegateLevel.Rank(level);

    public static string LevelText(string? level) => level switch
    {
        DelegateLevel.Full => "act on behalf of",
        DelegateLevel.Edit => "see and edit the events in",
        DelegateLevel.View => "see all the details in",
        _ => "see the event titles in",
    };

    public static string LevelLabel(string? level) => CalendarAccess.Label(level);

    /// Both people hear about it, except whoever made the change.
    private static async Task NotifyGrantAsync(IPopDbContext db, INotificationGatewayService? notifs, ILogger? log,
        Guid ownerId, Guid delegateId, Guid actorId, string what, string? level, CancellationToken ct)
    {
        if (notifs is null) return;
        var names = await db.Users.AsNoTracking().Where(u => u.Id == actorId || u.Id == ownerId || u.Id == delegateId)
            .ToDictionaryAsync(u => u.Id, u => u.Name, ct);
        var ownerNik = await db.Users.AsNoTracking().Where(u => u.Id == ownerId).Select(u => u.Nik).FirstOrDefaultAsync(ct);
        var owner = names.GetValueOrDefault(ownerId) ?? "Someone";
        var dlg = names.GetValueOrDefault(delegateId) ?? "Someone";
        var by = actorId == ownerId || actorId == delegateId ? "" : $" Set by {names.GetValueOrDefault(actorId) ?? "an admin"}.";
        if (delegateId != actorId)
        {
            var (title, body) = what switch
            {
                "revoked" => ($"{owner} stopped sharing their calendar", $"You can see when {owner} is busy, like everyone else.{by}"),
                "changed" => ($"{owner} changed your calendar access", $"You can now {LevelText(level)} {owner}'s calendar ({LevelLabel(level)}).{by}"),
                _ => ($"{owner} shared their calendar with you", $"You can now {LevelText(level)} {owner}'s calendar ({LevelLabel(level)}).{by}"),
            };
            await MeetingEndpoints.PublishBestEffortAsync(notifs, log, "calendar.delegate", title, body, [delegateId], "normal",
                what == "revoked" ? "/calendar" : $"/calendar/person/{Uri.EscapeDataString(ownerNik ?? "")}");
        }
        if (ownerId != actorId)
        {
            var (title, body) = what switch
            {
                "revoked" => ($"{dlg} no longer has access to your calendar", $"{dlg} now sees only when you're busy.{by}"),
                "changed" => ($"{dlg}'s access to your calendar changed", $"{dlg} can now {LevelText(level)} your calendar ({LevelLabel(level)}).{by}"),
                _ => ($"Your calendar is shared with {dlg}", $"{dlg} can {LevelText(level)} your calendar ({LevelLabel(level)}).{by}"),
            };
            await MeetingEndpoints.PublishBestEffortAsync(notifs, log, "calendar.delegate", title, body, [ownerId], "normal", "/calendar");
        }
    }

    // ── Acting on behalf ──

    /// Whose calendar a write goes to: mine, or — with ownerNik — an owner I may edit for.
    /// Returns (owner, actedBy) where actedBy is null when I act for myself, or an error result.
    public static async Task<(Guid OwnerId, Guid? ActedBy, IResult? Error)> ResolveOwnerAsync(IPopDbContext db, Guid me, string? ownerNik,
        string required, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(ownerNik)) return (me, null, null);
        var owner = await db.Users.AsNoTracking().Where(u => u.Nik == ownerNik && u.IsActive).Select(u => new { u.Id, u.Name }).FirstOrDefaultAsync(ct);
        if (owner is null) return (me, null, Results.NotFound());
        if (owner.Id == me) return (me, null, null);
        var level = await LevelAsync(db, owner.Id, me, ct);
        if (DelegateLevel.Rank(level) < DelegateLevel.Rank(required))
            return (me, null, Forbidden(required == DelegateLevel.Full
                ? $"Only a full delegate can answer invites for {owner.Name}."
                : $"You can't change {owner.Name}'s calendar."));
        return (owner.Id, me, null);
    }

    /// For an existing event: the owner when I'm their edit/full delegate and the event isn't Private;
    /// otherwise me (the save then fails its own owner/organizer check).
    public static async Task<(Guid OwnerId, Guid? ActedBy)> ActorForEventAsync(IPopDbContext db, Guid eventId, Guid me, CancellationToken ct)
    {
        var ev = await db.CalendarEvents.AsNoTracking().Where(e => e.Id == eventId)
            .Select(e => new { e.OwnerId, Visibility = e.Meeting != null ? e.Meeting.Visibility : e.Visibility }).FirstOrDefaultAsync(ct);
        if (ev is null || ev.OwnerId == me || ev.Visibility == CalendarVisibility.Private) return (me, null);
        return DelegateLevel.CanEdit(await LevelAsync(db, ev.OwnerId, me, ct)) ? (ev.OwnerId, me) : (me, null);
    }

    /// "Mia added "Budget review" to your calendar." — unless the owner turned these off.
    public static async Task NotifyOwnerOfChangeAsync(IPopDbContext db, INotificationGatewayService? notifs, ILogger? log,
        Guid ownerId, Guid actorId, string verb, string title, CancellationToken ct)
    {
        if (notifs is null || ownerId == actorId) return;
        var notify = await db.CalendarSettings.AsNoTracking().Where(s => s.UserId == ownerId).Select(s => (bool?)s.NotifyDelegateChanges).FirstOrDefaultAsync(ct) ?? true;
        if (!notify) return;
        var actor = await db.Users.AsNoTracking().Where(u => u.Id == actorId).Select(u => u.Name).FirstOrDefaultAsync(ct) ?? "Your delegate";
        await MeetingEndpoints.PublishBestEffortAsync(notifs, log, "calendar.delegate.change", $"{actor} {verb} \"{title}\"",
            $"{actor} {verb} \"{title}\" on your calendar.", [ownerId], "low", "/calendar");
    }

    /// Full delegates get a copy of their owners' meeting invites ("for Y") so they can answer them.
    public static async Task ForwardInvitesAsync(IPopDbContext db, INotificationGatewayService? notifs, ILogger? log,
        IReadOnlyCollection<Guid> invited, string title, DateTime startAt, string organizerName, Guid? actorId, CancellationToken ct)
    {
        if (notifs is null || invited.Count == 0) return;
        var rows = await db.CalendarDelegates.AsNoTracking()
            .Where(d => invited.Contains(d.OwnerId) && d.Level == DelegateLevel.Full && d.Delegate.IsActive)
            .Select(d => new { d.OwnerId, d.DelegateId, OwnerName = d.Owner.Name })
            .ToListAsync(ct);
        foreach (var owner in rows.GroupBy(r => new { r.OwnerId, r.OwnerName }))
        {
            var to = owner.Select(r => r.DelegateId).Where(id => id != actorId).Distinct().ToList();
            await MeetingEndpoints.PublishBestEffortAsync(notifs, log, "meeting.invited", $"Meeting invite for {owner.Key.OwnerName}: {title}",
                $"{organizerName} invited {owner.Key.OwnerName} to \"{title}\" on {startAt.ToLocalTime():dd MMM, HH:mm}. You can answer on their behalf.",
                to, "normal", "/calendar");
        }
    }

    // ── Helpers ──

    private static IResult Forbidden(string message) => Results.Json(new { code = "forbidden", message }, statusCode: 403);

    private static IResult GrantError(GrantResult r) => r switch
    {
        GrantResult.NotFound => Results.Json(new { code = "not_found", message = "That person was not found." }, statusCode: 404),
        GrantResult.Self => Results.Json(new { code = "self", message = "That's your own calendar." }, statusCode: 422),
        GrantResult.TooMany => Results.Json(new { code = "too_many", message = $"You can share with up to {MaxDelegates} people. Share with a department or chat group instead." }, statusCode: 422),
        _ => Results.Json(new { code = "invalid_level", message = "Pick an access level." }, statusCode: 422),
    };

    private static DateTime AsUtc(DateTime dt) => dt.Kind switch
    {
        DateTimeKind.Utc => dt,
        DateTimeKind.Local => dt.ToUniversalTime(),
        _ => DateTime.SpecifyKind(dt, DateTimeKind.Utc),
    };

    private static async Task<Guid?> GetUserIdAsync(HttpContext http, IPopDbContext db, CancellationToken ct)
    {
        var nik = http.User.FindFirst("nik")?.Value ?? http.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(nik)) return null;
        return await db.Users.Where(u => u.Nik == nik).Select(u => (Guid?)u.Id).FirstOrDefaultAsync(ct);
    }

    public record SetDelegateRequest(string? Level);
    /// GrantedBy = the admin's name when an admin (not the owner) set it up.
    public record DelegateDto(string Nik, string Name, string? Department, string Level, string? GrantedBy, DateTime CreatedAt);
    public record ManagedCalendarDto(string Nik, string Name, string? Department, string Level);
}
