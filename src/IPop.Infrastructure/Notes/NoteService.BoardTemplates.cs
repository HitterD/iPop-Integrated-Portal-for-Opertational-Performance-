using IPop.Application.Notes;
using IPop.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace IPop.Infrastructure.Notes;

/// Whiteboard templates people save (round 6 Template Center): "My templates" and the shared "Team templates".
public sealed partial class NoteService
{
    public const int MaxBoardTemplateJsonBytes = 512 * 1024;
    public const int MaxBoardTemplatesPerUser = 100;
    public const int MaxBoardTemplateElements = 5000;

    private static NoteBoardTemplateDto TplDto(NoteBoardTemplate t, Guid me, string owner) =>
        new(t.Id, t.Name, t.Description, t.Json, t.Count, t.Shared, t.OwnerId == me, owner, t.CreatedAt);

    public async Task<IReadOnlyList<NoteBoardTemplateDto>> GetBoardTemplatesAsync(Guid userId, CancellationToken ct)
    {
        var rows = await (from t in db.NoteBoardTemplates.AsNoTracking()
                          join u in db.Users.AsNoTracking() on t.OwnerId equals u.Id
                          where t.OwnerId == userId || t.Shared
                          orderby t.CreatedAt descending
                          select new { t, u.Name }).Take(300).ToListAsync(ct);
        return rows.Select(r => TplDto(r.t, userId, r.Name)).ToList();
    }

    public async Task<NoteResult<NoteBoardTemplateDto>> SaveBoardTemplateAsync(Guid userId, SaveBoardTemplateRequest req, CancellationToken ct)
    {
        if (System.Text.Encoding.UTF8.GetByteCount(req.Json ?? "") > MaxBoardTemplateJsonBytes) return Fail<NoteBoardTemplateDto>("template_too_big");
        if (CountElements(req.Json ?? "", MaxBoardTemplateJsonBytes, MaxBoardTemplateElements) is not { } count) return Fail<NoteBoardTemplateDto>("invalid_template");
        if (await db.NoteBoardTemplates.CountAsync(t => t.OwnerId == userId, ct) >= MaxBoardTemplatesPerUser) return Fail<NoteBoardTemplateDto>("templates_full");
        var t = new NoteBoardTemplate
        {
            Id = Guid.NewGuid(), OwnerId = userId, Name = Clean(req.Name, 100) ?? "My template", Description = Clean(req.Description, 300),
            Json = req.Json!, Count = count, Shared = req.Shared, CreatedAt = DateTime.UtcNow,
        };
        db.NoteBoardTemplates.Add(t);
        await db.SaveChangesAsync(ct);
        var owner = await db.Users.AsNoTracking().Where(u => u.Id == userId).Select(u => u.Name).FirstAsync(ct);
        return Ok(TplDto(t, userId, owner));
    }

    public async Task<NoteResult<NoteBoardTemplateDto>> UpdateBoardTemplateAsync(Guid userId, Guid id, UpdateBoardTemplateRequest req, CancellationToken ct)
    {
        var t = await db.NoteBoardTemplates.FirstOrDefaultAsync(x => x.Id == id && x.OwnerId == userId, ct);
        if (t is null) return Fail<NoteBoardTemplateDto>("not_found");
        if (Clean(req.Name, 100) is { } name) t.Name = name;
        if (req.Description is not null) t.Description = Clean(req.Description, 300);
        if (req.Shared is { } shared) t.Shared = shared;
        await db.SaveChangesAsync(ct);
        var owner = await db.Users.AsNoTracking().Where(u => u.Id == userId).Select(u => u.Name).FirstAsync(ct);
        return Ok(TplDto(t, userId, owner));
    }

    /// The owner removes their own; an admin (notes.admin) may also remove one that is shared with the team.
    public async Task<NoteResult<bool>> DeleteBoardTemplateAsync(Guid userId, Guid id, bool isAdmin, CancellationToken ct)
    {
        var n = await db.NoteBoardTemplates.Where(t => t.Id == id && (t.OwnerId == userId || (isAdmin && t.Shared))).ExecuteDeleteAsync(ct);
        return n == 1 ? Ok(true) : Fail<bool>("not_found");
    }
}
