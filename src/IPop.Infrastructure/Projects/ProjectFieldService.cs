using System.Globalization;
using System.Text.Json;
using IPop.Application.Projects;
using IPop.Core.Entities;
using IPop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IPop.Infrastructure.Projects;

public sealed class ProjectFieldService(IPopDbContext db) : IProjectFieldService
{
    private static readonly string[] Types = ["Text", "Number", "Date", "Dropdown", "Person", "Link"];

    public async Task<ProjectFieldResult<IReadOnlyList<ProjectFieldDto>>> ListAsync(Guid actorId, Guid projectId, CancellationToken ct)
    {
        if (!await CanRead(actorId, projectId, ct)) return Fail<IReadOnlyList<ProjectFieldDto>>("forbidden");
        var fields = await db.Set<ProjectField>().AsNoTracking().Where(f => f.ProjectId == projectId)
            .OrderBy(f => f.SortOrder).ThenBy(f => f.Id).ToListAsync(ct);
        return new(fields.Select(Map).ToList(), null);
    }

    public async Task<ProjectFieldResult<ProjectFieldDto>> CreateAsync(Guid actorId, Guid projectId, CreateProjectFieldRequest request, CancellationToken ct)
    {
        if (!await CanManage(actorId, projectId, ct)) return Fail<ProjectFieldDto>("forbidden");
        var name = request.Name?.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > 100) return Fail<ProjectFieldDto>("invalid_name");
        var type = Types.FirstOrDefault(t => string.Equals(t, request.Type, StringComparison.OrdinalIgnoreCase));
        if (type is null) return Fail<ProjectFieldDto>("invalid_type");
        var options = ValidateOptions(type, request.Options);
        if (options is null) return Fail<ProjectFieldDto>("invalid_options");
        if (await db.Set<ProjectField>().CountAsync(f => f.ProjectId == projectId, ct) >= 10)
            return Fail<ProjectFieldDto>("field_limit");
        if (await db.Set<ProjectField>().AnyAsync(f => f.ProjectId == projectId && f.Name.ToLower() == name.ToLower(), ct))
            return Fail<ProjectFieldDto>("duplicate_name");
        var last = await db.Set<ProjectField>().Where(f => f.ProjectId == projectId)
            .Select(f => (int?)f.SortOrder).MaxAsync(ct) ?? 0;
        var field = new ProjectField
        {
            Id = Guid.NewGuid(), ProjectId = projectId, Name = name, Type = type,
            OptionsJson = type == "Dropdown" ? JsonSerializer.Serialize(options) : null,
            SortOrder = last + 100, CreatedAt = DateTime.UtcNow
        };
        db.Set<ProjectField>().Add(field);
        await db.SaveChangesAsync(ct);
        return new(Map(field), null);
    }

    public async Task<ProjectFieldResult<ProjectFieldDto>> UpdateAsync(Guid actorId, Guid projectId, Guid fieldId, UpdateProjectFieldRequest request, CancellationToken ct)
    {
        if (!await CanManage(actorId, projectId, ct)) return Fail<ProjectFieldDto>("forbidden");
        var field = await db.Set<ProjectField>().FirstOrDefaultAsync(f => f.ProjectId == projectId && f.Id == fieldId, ct);
        if (field is null) return Fail<ProjectFieldDto>("field_not_found");
        if (request.Name is not null)
        {
            var name = request.Name.Trim();
            if (name.Length is 0 or > 100) return Fail<ProjectFieldDto>("invalid_name");
            if (await db.Set<ProjectField>().AnyAsync(f => f.ProjectId == projectId && f.Id != fieldId && f.Name.ToLower() == name.ToLower(), ct))
                return Fail<ProjectFieldDto>("duplicate_name");
            field.Name = name;
        }
        if (request.Options is not null)
        {
            var options = ValidateOptions(field.Type, request.Options);
            if (options is null) return Fail<ProjectFieldDto>("invalid_options");
            if (field.Type == "Dropdown")
            {
                var existing = await db.Set<ProjectTaskFieldValue>().AnyAsync(v => v.ProjectFieldId == fieldId && !options.Contains(v.Value), ct);
                if (existing) return Fail<ProjectFieldDto>("option_in_use");
                field.OptionsJson = JsonSerializer.Serialize(options);
            }
        }
        if (request.SortOrder.HasValue) field.SortOrder = request.SortOrder.Value;
        await db.SaveChangesAsync(ct);
        return new(Map(field), null);
    }

    public async Task<ProjectFieldResult<bool>> DeleteAsync(Guid actorId, Guid projectId, Guid fieldId, CancellationToken ct)
    {
        if (!await CanManage(actorId, projectId, ct)) return Fail<bool>("forbidden");
        var field = await db.Set<ProjectField>().FirstOrDefaultAsync(f => f.ProjectId == projectId && f.Id == fieldId, ct);
        if (field is null) return Fail<bool>("field_not_found");
        db.Set<ProjectTaskFieldValue>().RemoveRange(db.Set<ProjectTaskFieldValue>().Where(v => v.ProjectFieldId == fieldId));
        db.Set<ProjectField>().Remove(field);
        await db.SaveChangesAsync(ct);
        return new(true, null);
    }

    public async Task<ProjectFieldResult<IReadOnlyList<ProjectFieldValueDto>>> GetTaskValuesAsync(Guid actorId, Guid projectId, Guid taskId, CancellationToken ct)
    {
        if (!await CanRead(actorId, projectId, ct)) return Fail<IReadOnlyList<ProjectFieldValueDto>>("forbidden");
        if (!await db.ProjectTasks.AnyAsync(t => t.Id == taskId && t.ProjectId == projectId, ct))
            return Fail<IReadOnlyList<ProjectFieldValueDto>>("task_not_found");
        var values = await db.Set<ProjectTaskFieldValue>().AsNoTracking()
            .Where(v => v.ProjectTaskId == taskId && v.Field.ProjectId == projectId)
            .Select(v => new ProjectFieldValueDto(v.ProjectFieldId, v.Value)).ToListAsync(ct);
        return new(values, null);
    }

    public async Task<ProjectFieldResult<IReadOnlyList<ProjectTaskFieldValuesDto>>> GetValuesForTasksAsync(Guid actorId, Guid projectId, IReadOnlyList<Guid> taskIds, CancellationToken ct)
    {
        if (!await CanRead(actorId, projectId, ct)) return Fail<IReadOnlyList<ProjectTaskFieldValuesDto>>("forbidden");
        if (taskIds.Count > 200 || taskIds.Distinct().Count() != taskIds.Count) return Fail<IReadOnlyList<ProjectTaskFieldValuesDto>>("invalid_task_ids");
        var rows = await db.Set<ProjectTaskFieldValue>().AsNoTracking()
            .Where(v => taskIds.Contains(v.ProjectTaskId) && v.Task.ProjectId == projectId && v.Field.ProjectId == projectId)
            .Select(v => new { v.ProjectTaskId, v.ProjectFieldId, v.Value }).ToListAsync(ct);
        var result = rows.GroupBy(v => v.ProjectTaskId).Select(group => new ProjectTaskFieldValuesDto(group.Key,
            group.Select(v => new ProjectFieldValueDto(v.ProjectFieldId, v.Value)).ToList())).ToList();
        return new(result, null);
    }

    public async Task<ProjectFieldResult<ProjectFieldValueDto?>> SetTaskValueAsync(Guid actorId, Guid projectId, Guid taskId, Guid fieldId, string? value, CancellationToken ct)
    {
        if (!await CanWrite(actorId, projectId, ct)) return Fail<ProjectFieldValueDto?>("forbidden");
        if (!await db.ProjectTasks.AnyAsync(t => t.Id == taskId && t.ProjectId == projectId, ct))
            return Fail<ProjectFieldValueDto?>("task_not_found");
        var field = await db.Set<ProjectField>().AsNoTracking().FirstOrDefaultAsync(f => f.Id == fieldId && f.ProjectId == projectId, ct);
        if (field is null) return Fail<ProjectFieldValueDto?>("field_not_found");
        var row = await db.Set<ProjectTaskFieldValue>().FirstOrDefaultAsync(v => v.ProjectTaskId == taskId && v.ProjectFieldId == fieldId, ct);
        if (string.IsNullOrWhiteSpace(value))
        {
            if (row is not null)
            {
                db.Set<ProjectTaskFieldValue>().Remove(row);
                await db.SaveChangesAsync(ct);
            }
            return new(null, null);
        }
        var normalized = NormalizeValue(field, value.Trim());
        if (normalized is null) return Fail<ProjectFieldValueDto?>("invalid_value");
        if (field.Type == "Person" && !await db.ProjectMembers.AnyAsync(m => m.ProjectId == projectId && m.UserId == Guid.Parse(normalized) && m.User.IsActive, ct))
            return Fail<ProjectFieldValueDto?>("invalid_value");
        if (row is null)
        {
            row = new ProjectTaskFieldValue
            {
                Id = Guid.NewGuid(), ProjectTaskId = taskId, ProjectFieldId = fieldId,
                Value = normalized, UpdatedAt = DateTime.UtcNow
            };
            db.Set<ProjectTaskFieldValue>().Add(row);
        }
        else { row.Value = normalized; row.UpdatedAt = DateTime.UtcNow; }
        await db.SaveChangesAsync(ct);
        return new(new ProjectFieldValueDto(fieldId, normalized), null);
    }

    private static string? NormalizeValue(ProjectField field, string value)
    {
        if (value.Length > 2048) return null;
        switch (field.Type)
        {
            case "Text": return value;
            case "Number":
                return decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var number)
                    ? number.ToString(CultureInfo.InvariantCulture) : null;
            case "Date":
                return DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
                    ? day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : null;
            case "Dropdown": return Options(field).Contains(value) ? value : null;
            case "Person":
                return Guid.TryParse(value, out var userId) && userId != Guid.Empty ? userId.ToString() : null;
            case "Link":
                return Uri.TryCreate(value, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
                    ? value : null;
            default: return null;
        }
    }

    private static List<string>? ValidateOptions(string type, IReadOnlyList<string>? submitted)
    {
        if (type != "Dropdown") return submitted is null or { Count: 0 } ? [] : null;
        if (submitted is null || submitted.Count is < 1 or > 50) return null;
        var options = submitted.Select(o => o?.Trim() ?? "").ToList();
        return options.Any(o => o.Length is 0 or > 100) || options.Distinct(StringComparer.OrdinalIgnoreCase).Count() != options.Count
            ? null : options;
    }

    private static IReadOnlyList<string> Options(ProjectField field) =>
        field.OptionsJson is null ? [] : JsonSerializer.Deserialize<List<string>>(field.OptionsJson) ?? [];

    private static ProjectFieldDto Map(ProjectField field) =>
        new(field.Id, field.ProjectId, field.Name, field.Type, Options(field), field.SortOrder);

    private Task<bool> CanRead(Guid actorId, Guid projectId, CancellationToken ct) =>
        db.Projects.AnyAsync(p => p.Id == projectId && p.IsActive &&
            (p.OwnerId == actorId || p.Members.Any(m => m.UserId == actorId) ||
             db.UserRoles.Any(r => r.UserId == actorId && r.Role.Name == "Admin")), ct);

    private Task<bool> CanWrite(Guid actorId, Guid projectId, CancellationToken ct) =>
        db.Projects.AnyAsync(p => p.Id == projectId && p.IsActive &&
            (p.OwnerId == actorId || p.Members.Any(m => m.UserId == actorId && m.Role != "viewer")), ct);

    private Task<bool> CanManage(Guid actorId, Guid projectId, CancellationToken ct) =>
        db.Projects.AnyAsync(p => p.Id == projectId && p.IsActive &&
            (p.OwnerId == actorId || p.Members.Any(m => m.UserId == actorId && (m.Role == "owner" || m.Role == "manager"))), ct);

    private static ProjectFieldResult<T> Fail<T>(string error) => new(default, error);
}
