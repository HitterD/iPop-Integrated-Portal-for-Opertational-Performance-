using IPop.Core.Entities;
using IPop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IPop.Api.Endpoints;

/// Admin → Organization → Chart: positions with reporting lines. The browser edits a draft and sends the
/// whole chart on Publish, which replaces it in one transaction and writes each person's Position from it
/// (HRIS value kept in User.HrisPosition). ManagerNik is owned by the approver matrix (ApprovalService),
/// so Publish never writes or clears it.
public static class OrgChartEndpoints
{
    internal const int MaxPositions = 5000;
    internal const int MaxTitle = 200;

    /// Mounted on the /api/admin/org group, so it is AdminOnly and Publish fires directoryChanged.
    public static RouteGroupBuilder MapOrgChart(this RouteGroupBuilder org)
    {
        org.MapGet("/chart", async (IPopDbContext db, CancellationToken ct) => Results.Ok(await LoadAsync(db, ct)));

        org.MapPost("/chart/publish", async (PublishChartRequest req, IPopDbContext db, HttpContext http,
            CancellationToken ct) =>
        {
            var (status, error, version) = await PublishAsync(db, req, http.User.FindFirst("nik")?.Value, DateTime.UtcNow, ct);
            return status == 200 ? Results.Ok(new { version }) : Results.Json(error, statusCode: status);
        });
        return org;
    }

    internal static async Task<ChartDto> LoadAsync(IPopDbContext db, CancellationToken ct)
    {
        var last = await db.OrgChartPublications.OrderByDescending(p => p.Id).AsNoTracking().FirstOrDefaultAsync(ct);
        var positions = await db.OrgPositions.AsNoTracking()
            .Select(p => new
            {
                p.Id, p.Title, p.ParentId, p.SortOrder,
                Unit = p.Department == null ? null : p.Department.Code,
                Plant = p.Site == null ? null : p.Site.Code,
                People = p.Holders.OrderBy(h => h.SortOrder).Select(h => h.User.Nik).ToList(),
            })
            .ToListAsync(ct);
        var units = await db.Departments.AsNoTracking().Where(d => d.IsActive)
            .OrderBy(d => d.SortOrder).ThenBy(d => d.Name)
            .Select(d => new ChartUnitDto(d.Code, d.Name)).ToListAsync(ct);
        var plants = await db.Sites.AsNoTracking().Where(s => s.IsActive).OrderBy(s => s.Name)
            .Select(s => new ChartUnitDto(s.Code, s.Name)).ToListAsync(ct);
        // Active people for the picker and import matching, plus anyone still holding a box after leaving.
        var people = await db.Users.AsNoTracking()
            .Where(u => u.IsActive || db.OrgPositionHolders.Any(h => h.UserId == u.Id))
            .OrderBy(u => u.Name)
            .Select(u => new ChartPersonDto(u.Nik, u.Name, u.IsActive)).ToListAsync(ct);
        return new ChartDto(last?.Id ?? 0, last?.PublishedAt, last?.PublishedByNik,
            positions.Select(p => new ChartPositionDto(p.Id.ToString(), p.Title, p.ParentId?.ToString(), p.Unit, p.Plant,
                p.SortOrder, p.People)).ToList(),
            units, plants, people);
    }

    /// Replaces the whole chart. Returns (200, null, newVersion) or an HTTP status with an error body.
    internal static async Task<(int Status, object? Error, int Version)> PublishAsync(IPopDbContext db,
        PublishChartRequest req, string? actorNik, DateTime now, CancellationToken ct)
    {
        static object Err(string code, string message) => new { code, message };
        var list = req.Positions ?? [];
        if (list.Count > MaxPositions)
            return (400, Err("too_many", $"A chart can have at most {MaxPositions} positions."), 0);

        var current = await db.OrgChartPublications.MaxAsync(p => (int?)p.Id, ct) ?? 0;
        if (!req.Force && req.Version != current)
            return (409, Err("version_conflict", "Someone else published the chart after you started editing."), 0);

        // ── validate the shape before touching anything
        var byId = new Dictionary<string, PublishPositionDto>();
        foreach (var p in list)
        {
            if (string.IsNullOrWhiteSpace(p.Id) || !byId.TryAdd(p.Id, p))
                return (400, Err("invalid_id", "Every position needs a unique id."), 0);
            if (string.IsNullOrWhiteSpace(p.Title) || p.Title.Trim().Length > MaxTitle)
                return (400, Err("invalid_title", $"Position titles must be 1–{MaxTitle} characters."), 0);
        }
        foreach (var p in list)
        {
            if (p.ParentId is not null && !byId.ContainsKey(p.ParentId))
                return (400, Err("unknown_parent", $"“{p.Title}” reports to a position that is not in the chart."), 0);
            var seen = new HashSet<string> { p.Id };
            for (var at = p.ParentId; at is not null; at = byId[at].ParentId)
                if (!seen.Add(at))
                    return (400, Err("cycle", $"“{p.Title}” ends up reporting to itself."), 0);
        }

        var unitCodes = list.Select(p => p.Unit).OfType<string>().Distinct().ToList();
        var units = await db.Departments.Where(d => unitCodes.Contains(d.Code)).ToDictionaryAsync(d => d.Code, d => d.Id, ct);
        var plantCodes = list.Select(p => p.Plant).OfType<string>().Distinct().ToList();
        var plants = await db.Sites.Where(s => plantCodes.Contains(s.Code)).ToDictionaryAsync(s => s.Code, s => s.Id, ct);
        if (unitCodes.FirstOrDefault(c => !units.ContainsKey(c)) is { } badUnit)
            return (400, Err("unknown_unit", $"Unknown division “{badUnit}”."), 0);
        if (plantCodes.FirstOrDefault(c => !plants.ContainsKey(c)) is { } badPlant)
            return (400, Err("unknown_plant", $"Unknown plant “{badPlant}”."), 0);
        var niks = list.SelectMany(p => p.People ?? []).Distinct().ToList();
        var users = await db.Users.Where(u => niks.Contains(u.Nik)).ToDictionaryAsync(u => u.Nik, u => u.Id, ct);
        if (niks.FirstOrDefault(n => !users.ContainsKey(n)) is { } badNik)
            return (400, Err("unknown_person", $"No employee with NIK {badNik}."), 0);

        // Existing ids are kept so links to a box survive a publish; new boxes (client ids) get a Guid.
        var ids = list.ToDictionary(p => p.Id, p => Guid.TryParse(p.Id, out var g) ? g : Guid.NewGuid());
        var rows = list.Select(p => new OrgPosition
        {
            Id = ids[p.Id], Title = p.Title.Trim(),
            ParentId = p.ParentId is null ? null : ids[p.ParentId],
            DepartmentId = p.Unit is null ? null : units[p.Unit],
            SiteId = p.Plant is null ? null : plants[p.Plant],
            SortOrder = p.SortOrder,
        }).ToList();
        var holders = list.SelectMany(p => (p.People ?? []).Distinct()
            .Select((nik, i) => new OrgPositionHolder { PositionId = ids[p.Id], UserId = users[nik], SortOrder = i })).ToList();

        // Who each person is on the chart: their highest box (pre-order walk), and the first holder of the
        // nearest box above it that someone else holds.
        var kids = list.ToLookup(p => p.ParentId);
        var primary = new Dictionary<string, PublishPositionDto>();
        void Walk(string? parent)
        {
            foreach (var p in kids[parent].OrderBy(k => k.SortOrder))
            {
                foreach (var nik in p.People ?? []) primary.TryAdd(nik, p);
                Walk(p.Id);
            }
        }
        Walk(null);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.OrgPositionHolders.ExecuteDeleteAsync(ct);
        await db.OrgPositions.ExecuteUpdateAsync(s => s.SetProperty(p => p.ParentId, (Guid?)null), ct);
        await db.OrgPositions.ExecuteDeleteAsync(ct);
        // The bulk deletes bypass the change tracker; forget any rows it still holds so kept ids can be re-added.
        foreach (var e in db.ChangeTracker.Entries().Where(e => e.Entity is OrgPosition or OrgPositionHolder).ToList())
            e.State = EntityState.Detached;
        db.OrgPositions.AddRange(rows);
        db.OrgPositionHolders.AddRange(holders);
        await db.SaveChangesAsync(ct);

        var placed = await db.Users.Where(u => primary.Keys.Contains(u.Nik)).ToListAsync(ct);
        foreach (var u in placed)
        {
            var p = primary[u.Nik];
            var title = p.Title.Trim();
            if (u.Position == title) continue;
            u.Position = title;
            u.UpdatedAt = now;
        }
        // Not on the chart = no position. Skipped while the chart is empty, so publishing nothing never
        // wipes everyone.
        if (primary.Count > 0)
            await db.Users.Where(u => !db.OrgPositionHolders.Any(h => h.UserId == u.Id) && u.Position != null)
                .ExecuteUpdateAsync(s => s.SetProperty(u => u.Position, (string?)null)
                    .SetProperty(u => u.UpdatedAt, now), ct);

        var pub = new OrgChartPublication { PublishedAt = now, PublishedByNik = actorNik, PositionCount = rows.Count };
        db.OrgChartPublications.Add(pub);
        await db.SaveChangesAsync(ct);
        AdminEndpoints.Audit(db, actorNik, "admin.org.chart.publish", pub.Id.ToString(), now, "OrgChart");
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return (200, null, pub.Id);
    }

    public record ChartDto(int Version, DateTime? PublishedAt, string? PublishedByNik,
        List<ChartPositionDto> Positions, List<ChartUnitDto> Units, List<ChartUnitDto> Plants, List<ChartPersonDto> People);
    /// Unit = division (Department entity) code; Plant = site code, null = shown on every plant.
    public record ChartPositionDto(string Id, string Title, string? ParentId, string? Unit, string? Plant,
        int Order, List<string> People);
    public record ChartUnitDto(string Code, string Name);
    public record ChartPersonDto(string Nik, string Name, bool Active);

    public record PublishChartRequest(int Version, List<PublishPositionDto>? Positions, bool Force = false);
    public record PublishPositionDto(string Id, string Title, string? ParentId, string? Unit, string? Plant,
        int SortOrder, List<string>? People);
}
