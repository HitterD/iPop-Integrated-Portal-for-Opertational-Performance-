using IPop.Application.Notes;
using IPop.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace IPop.Infrastructure.Notes;

public sealed partial class NoteService
{
    // ── Import ───────────────────────────────────────────────────────────────
    // The browser converts the export (tools/notes-editor/src/transfer.js); the server only creates the
    // empty pages here, then each page's content arrives through the normal snapshot endpoint.

    public async Task<NoteResult<ImportNotesResultDto>> ImportAsync(Guid userId, ImportNotesRequest req, CancellationToken ct)
    {
        if (req.Pages is not { Count: > 0 } || req.Pages.Count > NoteImport.MaxPages) return Fail<ImportNotesResultDto>("invalid_import");
        if (!NoteImport.Sources.Contains(req.Source)) return Fail<ImportNotesResultDto>("invalid_import");
        if (req.ParentId is { } parentId)
        {
            var access = await GetAccessAsync(userId, parentId, ct);
            if (access < NoteAccess.Edit) return Fail<ImportNotesResultDto>(Denied(access));
        }

        // Parents must come before their children, so the tree can be built in one pass.
        var ids = new Dictionary<string, Guid>(StringComparer.Ordinal);
        var now = DateTime.UtcNow;
        var order = new Dictionary<Guid, int>();
        var pages = new List<NotePage>(req.Pages.Count);
        foreach (var p in req.Pages)
        {
            if (string.IsNullOrEmpty(p.Key) || ids.ContainsKey(p.Key)) return Fail<ImportNotesResultDto>("invalid_import");
            Guid? parent = req.ParentId;
            if (p.ParentKey is { } pk)
            {
                if (!ids.TryGetValue(pk, out var pid)) return Fail<ImportNotesResultDto>("invalid_import");
                parent = pid;
            }
            var id = Guid.NewGuid();
            ids[p.Key] = id;
            var siblings = parent ?? Guid.Empty;
            order[siblings] = order.GetValueOrDefault(siblings) + 1;
            pages.Add(new NotePage
            {
                Id = id, OwnerId = userId, ParentId = parent,
                Title = Clean(p.Title, 200) ?? "", Emoji = Clean(p.Emoji, 16),
                Cover = p.Cover is { } c && NoteStyle.Covers.Contains(c) ? c : null,
                Accent = "blue", Font = "default",
                ImportSource = req.Source, ImportKey = Clean(p.ImportKey, 80),
                // Sub-pages keep the export's order; the imported top page sorts like any new page.
                SortOrder = parent == req.ParentId ? 0 : order[siblings],
                CreatedAt = now, UpdatedAt = now, UpdatedById = userId,
            });
        }
        db.NotePages.AddRange(pages);
        await db.SaveChangesAsync(ct);
        return Ok(new ImportNotesResultDto(req.Pages.Select(p => new ImportedNoteDto(p.Key, ids[p.Key])).ToList()));
    }

    public async Task<IReadOnlyList<PreviousImportDto>> FindImportedAsync(Guid userId, IReadOnlyList<string> importKeys, CancellationToken ct)
    {
        var keys = (importKeys ?? []).Where(k => !string.IsNullOrEmpty(k)).Distinct().Take(NoteImport.MaxPages).ToList();
        if (keys.Count == 0) return [];
        var found = await db.NotePages.AsNoTracking()
            .Where(p => p.OwnerId == userId && p.DeletedAt == null && p.ImportKey != null && keys.Contains(p.ImportKey))
            .OrderByDescending(p => p.CreatedAt)
            .Select(p => new PreviousImportDto(p.ImportKey!, p.Id, p.Title, p.CreatedAt))
            .ToListAsync(ct);
        // One row per source page: the most recent import.
        return found.GroupBy(f => f.ImportKey).Select(g => g.First()).ToList();
    }
}
