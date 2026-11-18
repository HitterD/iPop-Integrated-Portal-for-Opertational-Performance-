using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using IPop.Application.Notifications;
using IPop.Application.Projects;
using IPop.Core.Entities;
using IPop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using static IPop.Infrastructure.Projects.ProjectAccess;

namespace IPop.Infrastructure.Projects;

/// Per-project request form. Internal only: every caller is a signed-in iPop user, there is no
/// anonymous or public link. The "file" question type is not supported in this stage.
public sealed partial class ProjectFormService(IPopDbContext db, INotificationGatewayService? gateway = null) : IProjectFormService
{
    public const int MaxQuestions = 12;
    public const int MaxSubmissionsPerHour = 10;
    public const int MaxAnswerLength = 2000;
    public static readonly string[] Scopes = ["project", "department", "everyone"];
    public static readonly string[] QuestionTypes = ["text", "longtext", "choice", "date"];
    public const string IntakeGroupName = "Intake";

    [GeneratedRegex("^[a-z0-9_]{1,40}$")]
    private static partial Regex KeyPattern();

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static IReadOnlyList<FormQuestionDto> DefaultQuestions() =>
    [
        new("title", "What do you need?", "text", true, null, "title"),
        new("details", "Tell us more", "longtext", false, null, "description"),
        new("due", "When do you need it?", "date", false, null, "dueDate"),
    ];

    private static IReadOnlyList<FormQuestionDto> Parse(string json)
    {
        try { return JsonSerializer.Deserialize<List<FormQuestionDto>>(json, Json) ?? []; }
        catch (JsonException) { return []; }
    }

    public async Task<ProjectOpResult<ProjectFormDto>> GetAsync(Guid projectId, Guid userId, CancellationToken ct)
    {
        if (!await ProjectExistsAsync(db, projectId, ct)) return Fail<ProjectFormDto>("project_not_found");
        if (!await CanManageAsync(db, projectId, userId, ct)) return Fail<ProjectFormDto>("forbidden");
        var form = await db.ProjectForms.FirstOrDefaultAsync(f => f.ProjectId == projectId, ct);
        if (form is null)
        {
            var now = DateTime.UtcNow;
            form = new ProjectForm
            {
                Id = Guid.NewGuid(), ProjectId = projectId, IsOpen = false, Title = "Request something", Intro = "",
                AudienceScope = "project", QuestionsJson = JsonSerializer.Serialize(DefaultQuestions(), Json), CreatedAt = now, UpdatedAt = now
            };
            db.ProjectForms.Add(form);
            await db.SaveChangesAsync(ct);
        }
        return Ok(await ToDtoAsync(form, ct));
    }

    public async Task<ProjectOpResult<ProjectFormDto>> SaveAsync(Guid projectId, Guid userId, SaveProjectFormRequest request, CancellationToken ct)
    {
        if (!await ProjectExistsAsync(db, projectId, ct)) return Fail<ProjectFormDto>("project_not_found");
        if (!await CanManageAsync(db, projectId, userId, ct)) return Fail<ProjectFormDto>("forbidden");
        if (request is null) return Fail<ProjectFormDto>("invalid_request");

        var title = request.Title?.Trim() ?? "";
        if (title.Length is 0 or > 120) return Fail<ProjectFormDto>("invalid_title");
        var intro = request.Intro?.Trim() ?? "";
        if (intro.Length > 500) return Fail<ProjectFormDto>("invalid_intro");
        var scope = request.AudienceScope?.Trim().ToLowerInvariant() ?? "";
        if (!Scopes.Contains(scope)) return Fail<ProjectFormDto>("invalid_scope");

        if (request.AssigneeId is { } aid && !await db.ProjectMembers.AnyAsync(m => m.ProjectId == projectId && m.UserId == aid
            && (m.Role == "owner" || m.Role == "manager" || m.Role == "member") && m.User.IsActive, ct))
            return Fail<ProjectFormDto>("invalid_assignee");

        var questions = request.Questions ?? [];
        if (questions.Count > MaxQuestions) return Fail<ProjectFormDto>("too_many_questions");
        var fieldIds = await db.ProjectFields.AsNoTracking().Where(f => f.ProjectId == projectId).Select(f => f.Id).ToListAsync(ct);
        var clean = new List<FormQuestionDto>();
        foreach (var q in questions)
        {
            var key = q?.Key?.Trim() ?? "";
            if (!KeyPattern().IsMatch(key) || clean.Any(c => c.Key == key)) return Fail<ProjectFormDto>("invalid_key");
            var label = q!.Label?.Trim() ?? "";
            if (label.Length is 0 or > 120) return Fail<ProjectFormDto>("invalid_label");
            var type = q.Type?.Trim().ToLowerInvariant() ?? "";
            if (!QuestionTypes.Contains(type)) return Fail<ProjectFormDto>("invalid_type");
            List<string>? options = null;
            if (type == "choice")
            {
                options = (q.Options ?? []).Select(o => o?.Trim() ?? "").Where(o => o.Length > 0).Distinct().ToList();
                if (options.Count is 0 or > 20 || options.Any(o => o.Length > 100)) return Fail<ProjectFormDto>("invalid_options");
            }
            var maps = q.MapsTo?.Trim() ?? "none";
            if (maps.StartsWith("field:", StringComparison.Ordinal))
            {
                if (!Guid.TryParse(maps[6..], out var fid) || !fieldIds.Contains(fid)) return Fail<ProjectFormDto>("invalid_mapping");
            }
            else if (maps is not ("title" or "description" or "dueDate" or "none")) return Fail<ProjectFormDto>("invalid_mapping");
            if (maps == "title" && type is not ("text" or "longtext")) return Fail<ProjectFormDto>("invalid_mapping");
            if (maps == "dueDate" && type != "date") return Fail<ProjectFormDto>("invalid_mapping");
            if (maps is "title" or "description" or "dueDate" && clean.Any(c => c.MapsTo == maps)) return Fail<ProjectFormDto>("duplicate_mapping");
            clean.Add(new FormQuestionDto(key, label, type, maps == "title" || q.Required, options, maps));
        }
        if (clean.Count(c => c.MapsTo == "title") != 1) return Fail<ProjectFormDto>("title_mapping_required");
        var json = JsonSerializer.Serialize(clean, Json);
        if (json.Length > 4000) return Fail<ProjectFormDto>("form_too_large");

        var form = await db.ProjectForms.FirstOrDefaultAsync(f => f.ProjectId == projectId, ct);
        var now = DateTime.UtcNow;
        if (form is null)
        {
            form = new ProjectForm { Id = Guid.NewGuid(), ProjectId = projectId, CreatedAt = now };
            db.ProjectForms.Add(form);
        }
        form.IsOpen = request.IsOpen;
        form.Title = title;
        form.Intro = intro;
        form.AudienceScope = scope;
        form.AssigneeId = request.AssigneeId;
        form.QuestionsJson = json;
        form.UpdatedAt = now;
        if (form.IsOpen) await EnsureIntakeGroupAsync(form, now, ct);
        await db.SaveChangesAsync(ct);
        return Ok(await ToDtoAsync(form, ct));
    }

    /// Opens or closes the form (the "form_to_intake" automation row toggles this). No-op when there is no form yet and closing.
    internal static async Task SetOpenAsync(IPopDbContext db, Guid projectId, bool open, CancellationToken ct)
    {
        var form = await db.ProjectForms.FirstOrDefaultAsync(f => f.ProjectId == projectId, ct);
        if (form is null)
        {
            if (!open) return;
            var now = DateTime.UtcNow;
            form = new ProjectForm
            {
                Id = Guid.NewGuid(), ProjectId = projectId, Title = "Request something", AudienceScope = "project",
                QuestionsJson = JsonSerializer.Serialize(DefaultQuestions(), Json), CreatedAt = now, UpdatedAt = now
            };
            db.ProjectForms.Add(form);
        }
        form.IsOpen = open;
        form.UpdatedAt = DateTime.UtcNow;
        if (open)
        {
            var svc = new ProjectFormService(db);
            await svc.EnsureIntakeGroupAsync(form, DateTime.UtcNow, ct);
        }
        await db.SaveChangesAsync(ct);
    }

    private async Task EnsureIntakeGroupAsync(ProjectForm form, DateTime now, CancellationToken ct)
    {
        if (form.IntakeGroupId is { } gid && await db.ProjectGroups.AnyAsync(g => g.Id == gid, ct)) return;
        var existing = await db.ProjectGroups.Where(g => g.ProjectId == form.ProjectId && g.Name == IntakeGroupName)
            .Select(g => (Guid?)g.Id).FirstOrDefaultAsync(ct);
        if (existing is not null) { form.IntakeGroupId = existing; return; }
        var max = await db.ProjectGroups.Where(g => g.ProjectId == form.ProjectId).Select(g => (int?)g.SortOrder).MaxAsync(ct) ?? 0;
        var group = new ProjectGroup { Id = Guid.NewGuid(), ProjectId = form.ProjectId, Name = IntakeGroupName, Color = "#0A84FF", SortOrder = max + 100 };
        db.ProjectGroups.Add(group);
        form.IntakeGroupId = group.Id;
    }

    private async Task<ProjectFormDto> ToDtoAsync(ProjectForm form, CancellationToken ct)
    {
        var assigneeId = form.AssigneeId;
        string? assigneeName = null;
        if (assigneeId is { } a) assigneeName = await db.Users.AsNoTracking().Where(u => u.Id == a).Select(u => u.Name).FirstOrDefaultAsync(ct);
        var count = await db.ProjectFormSubmissions.CountAsync(s => s.FormId == form.Id, ct);
        return new ProjectFormDto(form.ProjectId, form.IsOpen, form.Title, form.Intro, form.AudienceScope, form.AssigneeId,
            assigneeName, form.IntakeGroupId, Parse(form.QuestionsJson), count);
    }

    private async Task<bool> MayUseAsync(ProjectForm form, Guid userId, CancellationToken ct)
    {
        if (!await db.Users.AnyAsync(u => u.Id == userId && u.IsActive, ct)) return false;
        if (form.AudienceScope == "everyone") return true;
        if (await db.ProjectMembers.AnyAsync(m => m.ProjectId == form.ProjectId && m.UserId == userId, ct)) return true;
        if (form.AudienceScope != "department") return false;
        var ownerDept = await db.Projects.AsNoTracking().Where(p => p.Id == form.ProjectId)
            .Select(p => p.Owner.DepartmentId).FirstOrDefaultAsync(ct);
        if (ownerDept is null) return false;
        return await db.Users.AnyAsync(u => u.Id == userId && u.DepartmentId == ownerDept, ct);
    }

    public async Task<ProjectOpResult<PublicFormDto>> GetPublicAsync(Guid projectId, Guid userId, CancellationToken ct)
    {
        var form = await db.ProjectForms.AsNoTracking().FirstOrDefaultAsync(f => f.ProjectId == projectId && f.IsOpen
            && f.Project.IsActive && f.Project.Status != "completed" && f.Project.Status != "cancelled", ct);
        if (form is null) return Fail<PublicFormDto>("form_not_found");
        if (!await MayUseAsync(form, userId, ct)) return Fail<PublicFormDto>("forbidden");
        var project = await db.Projects.AsNoTracking().Where(p => p.Id == projectId).Select(p => new { p.Name, p.Color }).FirstAsync(ct);
        return Ok(new PublicFormDto(projectId, project.Name, project.Color, form.Title, form.Intro, Parse(form.QuestionsJson)));
    }

    public async Task<ProjectOpResult<SubmitFormResultDto>> SubmitAsync(Guid projectId, Guid userId, SubmitFormRequest request, CancellationToken ct)
    {
        var form = await db.ProjectForms.FirstOrDefaultAsync(f => f.ProjectId == projectId && f.IsOpen
            && f.Project.IsActive && f.Project.Status != "completed" && f.Project.Status != "cancelled", ct);
        if (form is null) return Fail<SubmitFormResultDto>("form_not_found");
        if (!await MayUseAsync(form, userId, ct)) return Fail<SubmitFormResultDto>("forbidden");

        var since = DateTime.UtcNow.AddHours(-1);
        if (await db.ProjectFormSubmissions.CountAsync(s => s.FormId == form.Id && s.SubmitterId == userId && s.CreatedAt > since, ct) >= MaxSubmissionsPerHour)
            return Fail<SubmitFormResultDto>("rate_limited");

        var questions = Parse(form.QuestionsJson);
        var answers = request?.Answers ?? [];
        var values = new Dictionary<string, string>();
        foreach (var q in questions)
        {
            var raw = answers.TryGetValue(q.Key, out var v) ? v?.Trim() ?? "" : "";
            if (raw.Length == 0)
            {
                if (q.Required) return Fail<SubmitFormResultDto>("required_missing");
                continue;
            }
            if (raw.Length > MaxAnswerLength) return Fail<SubmitFormResultDto>("answer_too_long");
            if (q.Type == "choice" && !(q.Options ?? []).Contains(raw)) return Fail<SubmitFormResultDto>("invalid_choice");
            if (q.Type == "date")
            {
                if (!DateTime.TryParseExact(raw, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) return Fail<SubmitFormResultDto>("invalid_date");
                raw = d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            }
            values[q.Key] = raw;
        }

        var titleQ = questions.First(q => q.MapsTo == "title");
        var title = values.GetValueOrDefault(titleQ.Key) ?? "";
        if (title.Length == 0) return Fail<SubmitFormResultDto>("required_missing");
        if (title.Length > 200) title = title[..200];

        DateTime? due = null;
        if (questions.FirstOrDefault(q => q.MapsTo == "dueDate") is { } dueQ && values.TryGetValue(dueQ.Key, out var dueText))
            due = DateTime.SpecifyKind(DateTime.ParseExact(dueText, "yyyy-MM-dd", CultureInfo.InvariantCulture), DateTimeKind.Utc);

        var sb = new StringBuilder();
        if (questions.FirstOrDefault(q => q.MapsTo == "description") is { } descQ && values.TryGetValue(descQ.Key, out var body)) sb.Append(body);
        foreach (var q in questions.Where(q => q.MapsTo is "none"))
            if (values.TryGetValue(q.Key, out var val))
            {
                if (sb.Length > 0) sb.Append("\n\n");
                sb.Append(q.Label).Append(": ").Append(val);
            }
        var description = sb.Length == 0 ? null : sb.ToString();
        if (description is { Length: > 4000 }) description = description[..4000];

        await ProjectStatusService.EnsureStatusesAsync(db, projectId, ct);
        var todoOption = await db.ProjectStatusOptions.AsNoTracking().Where(o => o.ProjectId == projectId && o.Stage == "todo")
            .OrderBy(o => o.SortOrder).ThenBy(o => o.CreatedAt).Select(o => (Guid?)o.Id).FirstOrDefaultAsync(ct);
        await EnsureIntakeGroupAsync(form, DateTime.UtcNow, ct);
        var groupId = form.IntakeGroupId!.Value;

        var project = await db.Projects.AsNoTracking().Where(p => p.Id == projectId).Select(p => new { p.OwnerId, p.Name }).FirstAsync(ct);
        var assigneeId = form.AssigneeId ?? project.OwnerId;
        var now = DateTime.UtcNow;
        var maxSort = await db.ProjectTasks.Where(t => t.ProjectId == projectId && t.Status == "todo" && t.ParentTaskId == null)
            .Select(t => (int?)t.SortOrder).MaxAsync(ct) ?? 0;
        var task = new ProjectTask
        {
            Id = Guid.NewGuid(), ProjectId = projectId, GroupId = groupId, Title = title, Description = description,
            Status = "todo", StatusOptionId = todoOption, DueDate = due, SortOrder = maxSort + 100,
            CreatedById = userId, CreatedAt = now, UpdatedAt = now
        };
        db.ProjectTasks.Add(task);
        if (await db.ProjectMembers.AnyAsync(m => m.ProjectId == projectId && m.UserId == assigneeId, ct))
            db.ProjectTaskAssignees.Add(new ProjectTaskAssignee { TaskId = task.Id, UserId = assigneeId, AssignedAt = now });

        foreach (var q in questions.Where(q => q.MapsTo.StartsWith("field:", StringComparison.Ordinal)))
        {
            if (!values.TryGetValue(q.Key, out var fv) || !Guid.TryParse(q.MapsTo[6..], out var fieldId)) continue;
            var field = await db.ProjectFields.AsNoTracking().FirstOrDefaultAsync(f => f.Id == fieldId && f.ProjectId == projectId, ct);
            if (field is null) continue;
            var stored = field.Type switch
            {
                "Number" => decimal.TryParse(fv, NumberStyles.Number, CultureInfo.InvariantCulture, out var n) ? n.ToString(CultureInfo.InvariantCulture) : null,
                "Date" => DateTime.TryParseExact(fv, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _) ? fv : null,
                "Dropdown" => (field.OptionsJson is { } oj && JsonSerializer.Deserialize<List<string>>(oj)!.Contains(fv)) ? fv : null,
                "Person" => null,
                _ => fv.Length <= 2048 ? fv : fv[..2048]
            };
            if (stored is not null)
                db.ProjectTaskFieldValues.Add(new ProjectTaskFieldValue { Id = Guid.NewGuid(), ProjectTaskId = task.Id, ProjectFieldId = fieldId, Value = stored, UpdatedAt = now });
        }

        db.ProjectFormSubmissions.Add(new ProjectFormSubmission
            { Id = Guid.NewGuid(), ProjectId = projectId, FormId = form.Id, SubmitterId = userId, TaskId = task.Id, CreatedAt = now });
        var submitterName = await db.Users.AsNoTracking().Where(u => u.Id == userId).Select(u => u.Name).FirstOrDefaultAsync(ct) ?? userId.ToString();
        db.ProjectTaskActivities.Add(new ProjectTaskActivity
            { Id = Guid.NewGuid(), TaskId = task.Id, ActorId = userId, Action = "form_request", NewValue = submitterName, CreatedAt = now });
        await db.SaveChangesAsync(ct);
        await new ProjectService(db).RecalculateProgressAsync(projectId, ct);

        if (gateway is not null && assigneeId != userId)
        {
            try
            {
                await gateway.PublishInternalAsync("projects", "project.form_request", $"New request: {title}",
                    $"{submitterName} sent a request in {project.Name}: \"{title}\".",
                    new NotificationTarget(UserId: assigneeId), "normal", $"/projects/{projectId}?task={task.Id}", ct);
            }
            catch (Exception) { /* the request is saved; the notice is advisory */ }
        }
        return Ok(new SubmitFormResultDto(task.Id, title));
    }
}
