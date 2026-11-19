using System.Text.Json;
using IPop.Application.Projects;
using IPop.Core.Entities;
using IPop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using static IPop.Infrastructure.Projects.ProjectAccess;

namespace IPop.Infrastructure.Projects;

/// Built-in automation rules per project. Rows are created with the project (4 default rules on) or lazily on
/// first read for older projects (everything off, so opening an old project never starts sending notices).
public sealed class ProjectAutomationService(IPopDbContext db) : IProjectAutomationService
{
    private static readonly JsonElement EmptyObject = JsonDocument.Parse("{}").RootElement.Clone();

    private async Task EnsureRowsAsync(Guid projectId, CancellationToken ct)
    {
        var have = await db.ProjectAutomations.Where(a => a.ProjectId == projectId).Select(a => a.Kind).ToListAsync(ct);
        if (ProjectAutomationCatalogue.All.All(e => e.Kind == ProjectAutomationCatalogue.FormToIntake || have.Contains(e.Kind))) return;
        // Opening an older project fires several first reads at once. On PostgreSQL concurrent inserts of the same
        // (ProjectId, Kind) keys raced into unique violations and even deadlocks (40P01 -> HTTP 500), so serialize
        // on a transaction-scoped advisory lock and re-read inside it (same pattern as EnsureStatusesAsync).
        Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction? own = null;
        var added = new List<ProjectAutomation>();
        try
        {
            if (db.Database.IsNpgsql())
            {
                if (db.Database.CurrentTransaction is null) own = await db.Database.BeginTransactionAsync(ct);
                var key = "ipop-project-automations-" + projectId;
                await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtext({key}))", ct);
                have = await db.ProjectAutomations.Where(a => a.ProjectId == projectId).Select(a => a.Kind).ToListAsync(ct);
            }
            var now = DateTime.UtcNow;
            foreach (var e in ProjectAutomationCatalogue.All.Where(e => e.Kind != ProjectAutomationCatalogue.FormToIntake && !have.Contains(e.Kind)))
            {
                var row = new ProjectAutomation { Id = Guid.NewGuid(), ProjectId = projectId, Kind = e.Kind, ConfigJson = e.DefaultConfigJson, IsEnabled = false, CreatedAt = now };
                db.ProjectAutomations.Add(row);
                added.Add(row);
            }
            if (added.Count > 0) await db.SaveChangesAsync(ct);
            if (own is not null) await own.CommitAsync(ct);
        }
        catch (DbUpdateException) when (own is null)
        {
            // Other providers (no lock): a concurrent first read created the rows (unique ProjectId+Kind); theirs are as good as ours.
            foreach (var row in added) db.Entry(row).State = EntityState.Detached;
        }
        finally
        {
            if (own is not null) await own.DisposeAsync();
        }
    }

    private static JsonElement ParseConfig(string json)
    {
        try { return JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json).RootElement.Clone(); }
        catch (JsonException) { return EmptyObject; }
    }

    public async Task<ProjectOpResult<AutomationListDto>> GetAsync(Guid projectId, Guid userId, CancellationToken ct)
    {
        if (!await ProjectExistsAsync(db, projectId, ct)) return Fail<AutomationListDto>("project_not_found");
        if (!await CanViewAsync(db, projectId, userId, ct)) return Fail<AutomationListDto>("forbidden");
        await EnsureRowsAsync(projectId, ct);
        return Ok(await ListAsync(projectId, ct));
    }

    private async Task<AutomationListDto> ListAsync(Guid projectId, CancellationToken ct)
    {
        var rows = await db.ProjectAutomations.AsNoTracking().Where(a => a.ProjectId == projectId).ToListAsync(ct);
        var formOpen = await db.ProjectForms.AsNoTracking().AnyAsync(f => f.ProjectId == projectId && f.IsOpen, ct);
        var list = new List<AutomationDto>();
        foreach (var e in ProjectAutomationCatalogue.All)
        {
            if (e.Kind == ProjectAutomationCatalogue.FormToIntake)
            {
                list.Add(new AutomationDto(e.Kind, e.Title, e.Description, formOpen, 0, null, EmptyObject));
                continue;
            }
            var row = rows.FirstOrDefault(r => r.Kind == e.Kind);
            list.Add(new AutomationDto(e.Kind, e.Title, e.Description, row?.IsEnabled ?? false, row?.RunCount ?? 0, row?.LastRunAt, ParseConfig(row?.ConfigJson ?? e.DefaultConfigJson)));
        }
        return new AutomationListDto(list, list.First(a => a.Kind == ProjectAutomationCatalogue.DependentsFollow).IsEnabled);
    }

    public async Task<ProjectOpResult<AutomationDto>> SaveAsync(Guid projectId, Guid userId, string kind, SaveAutomationRequest request, CancellationToken ct)
    {
        if (!await ProjectExistsAsync(db, projectId, ct)) return Fail<AutomationDto>("project_not_found");
        if (!await CanManageAsync(db, projectId, userId, ct)) return Fail<AutomationDto>("forbidden");
        var entry = ProjectAutomationCatalogue.Find(kind ?? "");
        if (entry is null) return Fail<AutomationDto>("automation_not_found");
        if (request is null) return Fail<AutomationDto>("invalid_request");

        if (kind == ProjectAutomationCatalogue.FormToIntake)
        {
            await ProjectFormService.SetOpenAsync(db, projectId, request.IsEnabled, ct);
            return Ok((await ListAsync(projectId, ct)).Rules.First(r => r.Kind == kind));
        }

        string? configJson = null;
        if (request.Config is { ValueKind: not (JsonValueKind.Undefined or JsonValueKind.Null) } cfg)
        {
            var error = await ValidateConfigAsync(projectId, kind, cfg, request.IsEnabled, ct);
            if (error is not null) return Fail<AutomationDto>(error);
            configJson = cfg.GetRawText();
        }
        await EnsureRowsAsync(projectId, ct);
        var row = await db.ProjectAutomations.FirstAsync(a => a.ProjectId == projectId && a.Kind == kind, ct);
        if (configJson is null && request.IsEnabled && kind == ProjectAutomationCatalogue.WeeklyRecurringTask)
        {
            var error = await ValidateConfigAsync(projectId, kind, ParseConfig(row.ConfigJson), true, ct);
            if (error is not null) return Fail<AutomationDto>(error);
        }
        if (configJson is not null) row.ConfigJson = configJson;
        row.IsEnabled = request.IsEnabled;
        await db.SaveChangesAsync(ct);
        return Ok((await ListAsync(projectId, ct)).Rules.First(r => r.Kind == kind));
    }

    private async Task<string?> ValidateConfigAsync(Guid projectId, string kind, JsonElement cfg, bool enabling, CancellationToken ct)
    {
        if (cfg.ValueKind != JsonValueKind.Object || cfg.GetRawText().Length > 1000) return "invalid_config";
        string? Str(string name) => cfg.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()?.Trim() : null;
        switch (kind)
        {
            case ProjectAutomationCatalogue.StuckNotifyLead:
                if (cfg.TryGetProperty("statusLabel", out _) && Str("statusLabel") is not { Length: > 0 and <= 40 }) return "invalid_config";
                return null;
            case ProjectAutomationCatalogue.WeeklyRecurringTask:
            {
                var title = Str("title") ?? "";
                if (title.Length > 200 || (enabling && title.Length == 0)) return "invalid_title";
                if (!cfg.TryGetProperty("weekday", out var wd) || wd.ValueKind != JsonValueKind.Number || !wd.TryGetInt32(out var day) || day is < 1 or > 7) return "invalid_weekday";
                if (Str("assigneeId") is { Length: > 0 } a && (!Guid.TryParse(a, out var aid) || !await db.ProjectMembers.AnyAsync(m => m.ProjectId == projectId && m.UserId == aid
                    && (m.Role == "owner" || m.Role == "manager" || m.Role == "member"), ct))) return "invalid_assignee";
                if (Str("groupId") is { Length: > 0 } g && (!Guid.TryParse(g, out var gid) || !await db.ProjectGroups.AnyAsync(x => x.Id == gid && x.ProjectId == projectId, ct))) return "invalid_group";
                return null;
            }
            default:
                return null; // nothing to configure
        }
    }
}
