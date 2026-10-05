using System.Text.Json;
using IPop.Core.Entities;
using IPop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IPop.Api.Endpoints;

/// National and company holidays. Everyone can list them; admins add and remove them (Admin → Calendar).
/// They show on every calendar and Find a time skips them.
public static class CalendarHolidayEndpoints
{
    public record HolidayDto(Guid Id, DateTime Date, string Title, string Kind);
    public record SaveHolidayRequest(DateTime Date, string? Title, string? Kind);

    public static IEndpointRouteBuilder MapCalendarHolidays(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/calendar/holidays", async (int? year, IPopDbContext db, CancellationToken ct) =>
            Results.Ok(await ListAsync(db, year ?? DateTime.UtcNow.Year, ct))).RequireAuthorization();

        var admin = app.MapGroup("/api/admin/calendar/holidays").RequireAuthorization("AdminOnly");
        admin.MapPost("/", async (SaveHolidayRequest req, HttpContext http, IPopDbContext db, CancellationToken ct) =>
        {
            var title = req.Title?.Trim() ?? "";
            if (title.Length is 0 or > 120) return Results.Json(new { code = "invalid_title", message = "Give the holiday a name (max 120 characters)." }, statusCode: 422);
            var date = DateTime.SpecifyKind(req.Date.Date, DateTimeKind.Utc);
            if (await db.CompanyHolidays.AnyAsync(h => h.Date == date && h.Title == title, ct))
                return Results.Json(new { code = "duplicate", message = "That holiday is already on the calendar." }, statusCode: 409);
            var nik = http.User.FindFirst("nik")?.Value;
            var me = await db.Users.AsNoTracking().Where(u => u.Nik == nik).Select(u => (Guid?)u.Id).FirstOrDefaultAsync(ct);
            var h = new CompanyHoliday { Id = Guid.NewGuid(), Date = date, Title = title, Kind = req.Kind == "company" ? "company" : "national", CreatedById = me, CreatedAt = DateTime.UtcNow };
            db.CompanyHolidays.Add(h);
            db.AuditLogs.Add(new AuditLog { ActorNik = nik, Action = "calendar.holiday.add", Entity = "CompanyHoliday", EntityId = h.Id.ToString(),
                DetailJson = JsonSerializer.Serialize(new { date = date.ToString("yyyy-MM-dd"), title, kind = h.Kind }), CreatedAt = DateTime.UtcNow });
            await db.SaveChangesAsync(ct);
            return Results.Ok(await ListAsync(db, date.Year, ct));
        });
        admin.MapDelete("/{id:guid}", async (Guid id, HttpContext http, IPopDbContext db, CancellationToken ct) =>
        {
            var h = await db.CompanyHolidays.FirstOrDefaultAsync(x => x.Id == id, ct);
            if (h is null) return Results.NotFound();
            db.CompanyHolidays.Remove(h);
            db.AuditLogs.Add(new AuditLog { ActorNik = http.User.FindFirst("nik")?.Value, Action = "calendar.holiday.remove", Entity = "CompanyHoliday",
                EntityId = id.ToString(), DetailJson = JsonSerializer.Serialize(new { date = h.Date.ToString("yyyy-MM-dd"), h.Title }), CreatedAt = DateTime.UtcNow });
            await db.SaveChangesAsync(ct);
            return Results.Ok(await ListAsync(db, h.Date.Year, ct));
        });
        return app;
    }

    public static async Task<List<HolidayDto>> ListAsync(IPopDbContext db, int year, CancellationToken ct)
    {
        var from = new DateTime(year, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        return await db.CompanyHolidays.AsNoTracking().Where(h => h.Date >= from && h.Date < from.AddYears(1))
            .OrderBy(h => h.Date).Select(h => new HolidayDto(h.Id, h.Date, h.Title, h.Kind)).ToListAsync(ct);
    }
}
