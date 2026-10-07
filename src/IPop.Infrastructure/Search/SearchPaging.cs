using IPop.Application.Rbac;
using IPop.Application.Search;
using IPop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IPop.Infrastructure.Search;

/// Shared pieces of the module providers: ranked paging over SQL tiers, admin/permission checks.
internal static class SearchPaging
{
    /// Pages across ranked tiers (exact title › title › body). Each tier must already be ordered.
    /// Totals are counted in SQL for every tier; only the page's slice is materialised.
    public static async Task<(List<T> Items, int Total)> PageAsync<T>(IReadOnlyList<IQueryable<T>> orderedTiers, int skip, int take, CancellationToken ct)
    {
        var items = new List<T>();
        var total = 0;
        foreach (var tier in orderedTiers)
        {
            var count = await tier.CountAsync(ct);
            if (take > 0)
            {
                if (skip >= count) skip -= count;
                else
                {
                    var slice = await tier.Skip(skip).Take(take).ToListAsync(ct);
                    items.AddRange(slice);
                    take -= slice.Count;
                    skip = 0;
                }
            }
            total += count;
        }
        return (items, total);
    }

    /// In-memory counterpart for sources that are already bounded (NAS pages, calendar candidates).
    public static (List<T> Items, int Total) Page<T>(IEnumerable<T> ranked, int skip, int take)
    {
        var all = ranked.ToList();
        return (all.Skip(skip).Take(take).ToList(), all.Count);
    }

    public static Task<bool> IsAdminAsync(IPopDbContext db, Guid userId, CancellationToken ct) =>
        db.UserRoles.AsNoTracking().AnyAsync(ur => ur.UserId == userId && ur.Role.Name == "Admin", ct);

    /// No evaluator (unit tests) = allowed; the API always supplies one.
    public static Task<bool> AllowedAsync(IPermissionEvaluator? perms, Guid userId, string code, CancellationToken ct) =>
        perms is null ? Task.FromResult(true) : perms.IsAllowedAsync(userId, code, null, ct);

    private static readonly string[] Palette = ["#e06c75", "#98c379", "#e5c07b", "#61afef", "#c678dd", "#56b6c2"];
    public static string AvatarColorFor(string key) => Palette[Math.Abs(key.GetHashCode()) % Palette.Length];

    public static string Initials(string name)
    {
        var s = string.Concat(name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(w => w[0])).ToUpperInvariant();
        return s.Length > 2 ? s[..2] : s;
    }
}
