using System.Text.Json;
using System.Text.RegularExpressions;
using IPop.Application.Projects;
using IPop.Core.Entities;
using IPop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IPop.Infrastructure.Projects;

/// Per-project custom statuses and the tab layout. A task's <see cref="ProjectTask.Status"/> stays the
/// four-stage key; <see cref="ProjectTask.StatusOptionId"/> only says which label of that stage it shows.
public sealed partial class ProjectStatusService(IPopDbContext db) : IProjectStatusService
{
    [GeneratedRegex("^#[0-9a-fA-F]{6}$")]
    private static partial Regex HexColor();

    public async Task<ProjectStatusResult<IReadOnlyList<ProjectStatusOptionDto>>> GetAsync(Guid projectId, Guid userId, CancellationToken ct)
    {
        if (!await db.Projects.AnyAsync(p => p.Id == projectId && p.IsActive, ct)) return Fail<IReadOnlyList<ProjectStatusOptionDto>>("project_not_found");
        if (!await CanViewAsync(projectId, userId, ct)) return Fail<IReadOnlyList<ProjectStatusOptionDto>>("forbidden");
        await EnsureStatusesAsync(projectId, ct);
        return new(await ListAsync(projectId, ct), null);
    }

    public async Task<ProjectStatusResult<IReadOnlyList<ProjectStatusOptionDto>>> SaveAsync(Guid projectId, Guid userId, SaveStatusOptionsRequest request, CancellationToken ct)
    {
        ProjectStatusResult<IReadOnlyList<ProjectStatusOptionDto>> Error(string code) => Fail<IReadOnlyList<ProjectStatusOptionDto>>(code);
        if (!await db.Projects.AnyAsync(p => p.Id == projectId && p.IsActive, ct)) return Error("project_not_found");
        if (!await CanManageAsync(projectId, userId, ct)) return Error("forbidden");

        var submitted = request?.Options ?? [];
        if (submitted.Count > ProjectStatusDefaults.MaxOptions) return Error("too_many");
        var rows = new List<(Guid? Id, string Label, string Color, string Stage)>();
        foreach (var o in submitted)
        {
            var label = o?.Label?.Trim() ?? "";
            if (label.Length is 0 or > ProjectStatusDefaults.MaxLabelLength) return Error("invalid_label");
            var color = o!.Color?.Trim() ?? "";
            if (!HexColor().IsMatch(color)) return Error("invalid_color");
            var stage = o.Stage?.Trim().ToLowerInvariant() ?? "";
            if (!ProjectStatusDefaults.Stages.Contains(stage)) return Error("invalid_stage");
            rows.Add((o.Id, label, color.ToLowerInvariant(), stage));
        }
        if (rows.Select(r => r.Label).Distinct(StringComparer.OrdinalIgnoreCase).Count() != rows.Count) return Error("duplicate_label");
        if (!rows.Any(r => r.Stage == "todo") || !rows.Any(r => r.Stage == "done")) return Error("stage_required");

        await EnsureStatusesAsync(projectId, ct);
        var existing = await db.ProjectStatusOptions.Where(o => o.ProjectId == projectId).ToListAsync(ct);
        var byId = existing.ToDictionary(o => o.Id);
        var keptIds = rows.Where(r => r.Id.HasValue).Select(r => r.Id!.Value).ToList();
        if (keptIds.Distinct().Count() != keptIds.Count || keptIds.Any(id => !byId.ContainsKey(id))) return Error("invalid_status_option");

        var removed = existing.Where(o => !keptIds.Contains(o.Id)).ToList();
        var removedIds = removed.Select(o => o.Id).ToList();
        var removedInUse = await db.ProjectTasks
            .Where(t => t.ProjectId == projectId && t.StatusOptionId != null && removedIds.Contains(t.StatusOptionId.Value))
            .Select(t => t.StatusOptionId!.Value).Distinct().ToListAsync(ct);
        var reassign = request!.Reassign ?? new Dictionary<Guid, Guid>();
        foreach (var id in removedInUse)
            if (!reassign.TryGetValue(id, out var target) || !keptIds.Contains(target)) return Error("reassign_required");

        // Option rows first, then every task follows its option's (possibly new) stage.
        var now = DateTime.UtcNow;
        var finalStage = new Dictionary<Guid, string>();
        for (var i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            ProjectStatusOption option;
            if (r.Id is { } id) option = byId[id];
            else
            {
                option = new ProjectStatusOption { Id = Guid.NewGuid(), ProjectId = projectId, Label = r.Label, Color = r.Color, Stage = r.Stage, CreatedAt = now };
                db.ProjectStatusOptions.Add(option);
            }
            option.Label = r.Label;
            option.Color = r.Color;
            option.Stage = r.Stage;
            option.SortOrder = (i + 1) * 100;
            finalStage[option.Id] = r.Stage;
        }

        // Binned (soft-deleted) tasks follow too, so an Undo never restores a mismatched stage.
        var existingIds = existing.Select(o => o.Id).ToList();
        var tasks = await db.ProjectTasks.IgnoreQueryFilters()
            .Where(t => t.ProjectId == projectId && t.StatusOptionId != null && existingIds.Contains(t.StatusOptionId.Value))
            .ToListAsync(ct);
        foreach (var t in tasks)
        {
            var optionId = t.StatusOptionId!.Value;
            if (removedIds.Contains(optionId))
            {
                // Only binned tasks can reach here without a reassign entry; they fall back to "no option".
                if (reassign.TryGetValue(optionId, out var target) && finalStage.ContainsKey(target)) t.StatusOptionId = target;
                else { t.StatusOptionId = null; continue; }
            }
            var stage = finalStage[t.StatusOptionId!.Value];
            if (t.Status != stage)
            {
                ProjectService.SetStatus(t, stage, now);
                t.UpdatedAt = now;
            }
        }
        db.ProjectStatusOptions.RemoveRange(removed);
        await db.SaveChangesAsync(ct);
        await new ProjectService(db).RecalculateProgressAsync(projectId, ct);
        return new(await ListAsync(projectId, ct), null);
    }

    public async Task<ProjectStatusResult<IReadOnlyList<string>>> SaveTabsAsync(Guid projectId, Guid userId, SaveProjectTabsRequest request, CancellationToken ct)
    {
        var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == projectId && p.IsActive, ct);
        if (project is null) return Fail<IReadOnlyList<string>>("project_not_found");
        if (!await CanManageAsync(projectId, userId, ct)) return Fail<IReadOnlyList<string>>("forbidden");
        var tabs = (request?.Tabs ?? []).Select(k => k?.Trim().ToLowerInvariant() ?? "").ToList();
        if (tabs.Count is 0 or > ProjectStatusDefaults.MaxTabs || tabs.Distinct().Count() != tabs.Count
            || tabs.Any(k => !ProjectStatusDefaults.TabKeys.Contains(k)))
            return Fail<IReadOnlyList<string>>("invalid_tabs");
        project.TabsJson = JsonSerializer.Serialize(tabs);
        project.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return new(tabs, null);
    }

    /// Writes the default set the first time a project's statuses are touched and points existing
    /// tasks at the first option of their stage. Idempotent: does nothing once rows exist.
    public Task EnsureStatusesAsync(Guid projectId, CancellationToken ct) => EnsureStatusesAsync(db, projectId, ct);

    internal static async Task EnsureStatusesAsync(IPopDbContext db, Guid projectId, CancellationToken ct)
    {
        if (await db.ProjectStatusOptions.AnyAsync(o => o.ProjectId == projectId, ct)) return;
        var now = DateTime.UtcNow;
        var options = ProjectStatusDefaults.Options.Select((d, i) => new ProjectStatusOption
        {
            Id = Guid.NewGuid(), ProjectId = projectId, Label = d.Label, Color = d.Color, Stage = d.Stage,
            SortOrder = (i + 1) * 100, CreatedAt = now
        }).ToList();
        db.ProjectStatusOptions.AddRange(options);
        var tasks = await db.ProjectTasks.IgnoreQueryFilters()
            .Where(t => t.ProjectId == projectId && t.StatusOptionId == null).ToListAsync(ct);
        foreach (var t in tasks)
            t.StatusOptionId = options.FirstOrDefault(o => o.Stage == t.Status)?.Id;
        await db.SaveChangesAsync(ct);
    }

    private async Task<IReadOnlyList<ProjectStatusOptionDto>> ListAsync(Guid projectId, CancellationToken ct)
    {
        var counts = await db.ProjectTasks.AsNoTracking()
            .Where(t => t.ProjectId == projectId && t.StatusOptionId != null)
            .GroupBy(t => t.StatusOptionId!.Value).Select(g => new { g.Key, N = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.N, ct);
        var options = await db.ProjectStatusOptions.AsNoTracking().Where(o => o.ProjectId == projectId)
            .OrderBy(o => o.SortOrder).ThenBy(o => o.CreatedAt).ToListAsync(ct);
        return options.Select(o => new ProjectStatusOptionDto(o.Id, o.Label, o.Color, o.Stage, o.SortOrder, counts.GetValueOrDefault(o.Id))).ToList();
    }

    private async Task<bool> CanViewAsync(Guid projectId, Guid userId, CancellationToken ct) =>
        await db.ProjectMembers.AnyAsync(m => m.ProjectId == projectId && m.UserId == userId, ct)
        || await db.UserRoles.AnyAsync(ur => ur.UserId == userId && ur.Role.Name == "Admin", ct);

    /// Same rule as editing groups: project owner or manager.
    private Task<bool> CanManageAsync(Guid projectId, Guid userId, CancellationToken ct) =>
        db.ProjectMembers.AnyAsync(m => m.ProjectId == projectId && m.UserId == userId && (m.Role == "owner" || m.Role == "manager"), ct);

    private static ProjectStatusResult<T> Fail<T>(string code) => new(default, code);
}
