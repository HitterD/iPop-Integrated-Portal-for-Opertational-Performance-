using System.Text.Json;
using IPop.Application.Notes;
using IPop.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace IPop.Infrastructure.Notes;

/// Whiteboard library: groups of elements people saved to reuse. Mine + the team library (items their owners shared).
public sealed partial class NoteService
{
    public const int MaxLibraryJsonBytes = 256 * 1024;
    public const int MaxLibraryItemsPerUser = 200;
    public const int MaxLibraryElements = 2000;

    private static NoteLibraryItemDto LibDto(NoteLibraryItem i, Guid me, string owner) =>
        new(i.Id, i.Name, i.Json, i.Count, i.Shared, i.OwnerId == me, owner, i.CreatedAt);

    public async Task<IReadOnlyList<NoteLibraryItemDto>> GetLibraryAsync(Guid userId, CancellationToken ct)
    {
        var mine = await (from i in db.NoteLibraryItems.AsNoTracking()
                          join u in db.Users.AsNoTracking() on i.OwnerId equals u.Id
                          where i.OwnerId == userId
                          orderby i.CreatedAt descending
                          select new { i, u.Name }).Take(MaxLibraryItemsPerUser).ToListAsync(ct);
        var shared = await (from i in db.NoteLibraryItems.AsNoTracking()
                            join u in db.Users.AsNoTracking() on i.OwnerId equals u.Id
                            where i.Shared && i.OwnerId != userId
                            orderby i.CreatedAt descending
                            select new { i, u.Name }).Take(200).ToListAsync(ct);
        return mine.Concat(shared).OrderByDescending(r => r.i.CreatedAt)
            .Select(r => LibDto(r.i, userId, r.Name)).ToList();
    }

    /// The elements must be a JSON array of objects that each have a string id and a string type; anything else is refused,
    /// so the library never hands a board something it cannot draw.
    internal static int? CountElements(string json) => CountElements(json, MaxLibraryJsonBytes, MaxLibraryElements);

    internal static int? CountElements(string json, int maxBytes, int maxElements)
    {
        if (string.IsNullOrWhiteSpace(json) || System.Text.Encoding.UTF8.GetByteCount(json) > maxBytes) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;
            var n = 0;
            foreach (var e in doc.RootElement.EnumerateArray())
            {
                if (e.ValueKind != JsonValueKind.Object
                    || !e.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String
                    || !e.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String) return null;
                if (++n > maxElements) return null;
            }
            return n == 0 ? null : n;
        }
        catch (JsonException) { return null; }
    }

    public async Task<NoteResult<NoteLibraryItemDto>> SaveLibraryItemAsync(Guid userId, SaveLibraryItemRequest req, CancellationToken ct)
    {
        if (CountElements(req.Json) is not { } count) return Fail<NoteLibraryItemDto>("invalid_library_item");
        if (await db.NoteLibraryItems.CountAsync(i => i.OwnerId == userId, ct) >= MaxLibraryItemsPerUser) return Fail<NoteLibraryItemDto>("library_full");
        var item = new NoteLibraryItem
        {
            Id = Guid.NewGuid(), OwnerId = userId, Name = Clean(req.Name, 100) ?? (count == 1 ? "1 item" : $"{count} items"),
            Json = req.Json, Count = count, Shared = req.Shared, CreatedAt = DateTime.UtcNow,
        };
        db.NoteLibraryItems.Add(item);
        await db.SaveChangesAsync(ct);
        var owner = await db.Users.AsNoTracking().Where(u => u.Id == userId).Select(u => u.Name).FirstAsync(ct);
        return Ok(LibDto(item, userId, owner));
    }

    public async Task<NoteResult<NoteLibraryItemDto>> UpdateLibraryItemAsync(Guid userId, Guid itemId, UpdateLibraryItemRequest req, CancellationToken ct)
    {
        var item = await db.NoteLibraryItems.FirstOrDefaultAsync(i => i.Id == itemId && i.OwnerId == userId, ct);
        if (item is null) return Fail<NoteLibraryItemDto>("not_found");
        if (Clean(req.Name, 100) is { } name) item.Name = name;
        if (req.Shared is { } shared) item.Shared = shared;
        await db.SaveChangesAsync(ct);
        var owner = await db.Users.AsNoTracking().Where(u => u.Id == userId).Select(u => u.Name).FirstAsync(ct);
        return Ok(LibDto(item, userId, owner));
    }

    public async Task<NoteResult<bool>> DeleteLibraryItemAsync(Guid userId, Guid itemId, CancellationToken ct, bool isAdmin = false)
    {
        var n = await db.NoteLibraryItems.Where(i => i.Id == itemId && (i.OwnerId == userId || (isAdmin && i.Shared))).ExecuteDeleteAsync(ct);
        return n == 1 ? Ok(true) : Fail<bool>("not_found");
    }
}
