using System.Text.Json;
using IPop.Application.Approval;
using IPop.Core.Entities;
using IPop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IPop.Infrastructure.Approval;

/// Owns every approver-chain write: slots as HC gave them, ManagerNik kept equal to the first
/// approver, and one AuditLog row per changed chain (that log is the per-employee history).
public sealed class ApprovalService(IPopDbContext db, Func<DateTime> now) : IApprovalService
{
    public const string AuditAction = "approval.chain.update";

    public ApprovalService(IPopDbContext db) : this(db, () => DateTime.UtcNow) { }

    // ---- pure helpers -------------------------------------------------------------------

    /// Drops empty slots and collapses a NIK equal to the previous visible step.
    public static IReadOnlyList<ChainStep> Compact(IReadOnlyList<string?> slots,
        Func<string, (string? Name, bool Active, bool Known)> lookup)
    {
        var steps = new List<ChainStep>();
        for (var i = 0; i < slots.Count && i < ApproverChain.SlotCount; i++)
        {
            var nik = slots[i];
            if (string.IsNullOrWhiteSpace(nik)) continue;
            if (steps.Count > 0 && steps[^1].Nik == nik) continue;
            var (name, active, known) = lookup(nik);
            steps.Add(new ChainStep(i + 1, ApproverSlots.Labels[i], nik, name, active, known));
        }
        return steps;
    }

    /// The direct manager: first non-empty slot that isn't the employee themself.
    public static string? FirstManager(string nik, IReadOnlyList<string?> slots) =>
        slots.FirstOrDefault(s => !string.IsNullOrWhiteSpace(s) && s != nik);

    // ---- reads --------------------------------------------------------------------------

    private sealed record Person(string Nik, string Name, bool Active, Guid? SiteId, Guid? DepartmentId, Guid? DivisionId);
    private sealed record Lookups(Dictionary<string, Person> People, Dictionary<Guid, (string Code, string Name)> Sites,
        Dictionary<Guid, (string Code, string Name, Guid? ParentId)> Departments,
        Dictionary<Guid, (string Code, string Name)> Divisions);

    private async Task<Lookups> LoadLookupsAsync(CancellationToken ct)
    {
        var people = await db.Users.AsNoTracking()
            .Select(u => new Person(u.Nik, u.Name, u.IsActive, u.SiteId, u.DepartmentId, u.DivisionId))
            .ToListAsync(ct);
        var sites = await db.Sites.AsNoTracking().ToDictionaryAsync(s => s.Id, s => (s.Code, s.Name), ct);
        var depts = await db.Departments.AsNoTracking().ToDictionaryAsync(d => d.Id, d => (d.Code, d.Name, d.ParentId), ct);
        var divs = await db.Divisions.AsNoTracking().ToDictionaryAsync(d => d.Id, d => (d.Code, d.Name), ct);
        return new Lookups(people.GroupBy(p => p.Nik).ToDictionary(g => g.Key, g => g.First()), sites, depts, divs);
    }

    private static ChainView BuildView(string nik, ApproverChain? chain, Lookups l)
    {
        l.People.TryGetValue(nik, out var me);
        var slots = chain?.Slots ?? new string?[ApproverChain.SlotCount];
        var steps = Compact(slots, n => l.People.TryGetValue(n, out var p) ? (p.Name, p.Active, true) : (null, false, false));
        var issues = new List<string>();
        if (chain is null) issues.Add(ChainIssues.NotInMatrix);
        else if (steps.Count == 0) issues.Add(ChainIssues.EmptyChain);
        if (steps.Any(s => s.Known && !s.Active)) issues.Add(ChainIssues.InactiveApprover);
        if (steps.Any(s => !s.Known)) issues.Add(ChainIssues.UnknownApprover);

        string? plant = me?.SiteId is { } sid && l.Sites.TryGetValue(sid, out var site) ? site.Name : chain?.SourceWorkLocation;
        string? division = me?.DepartmentId is { } did ? DeptPath(did, l) : null;
        string? department = me?.DivisionId is { } vid && l.Divisions.TryGetValue(vid, out var dv) ? dv.Name : null;
        string? updatedBy = chain?.UpdatedByNik is { } un && l.People.TryGetValue(un, out var up) ? up.Name : chain?.UpdatedByNik;
        return new ChainView(nik, me?.Name ?? chain?.SourceName, plant, division, department, slots, steps, issues,
            chain?.UpdatedAt, updatedBy);
    }

    private static string DeptPath(Guid id, Lookups l)
    {
        var names = new List<string>();
        Guid? cur = id;
        for (var guard = 0; cur is { } c && guard < 20 && l.Departments.TryGetValue(c, out var d); guard++)
        {
            names.Insert(0, d.Name);
            cur = d.ParentId;
        }
        return string.Join(" › ", names);
    }

    private static bool UnderDepartment(Guid? deptId, string code, Lookups l)
    {
        for (var guard = 0; deptId is { } c && guard < 20 && l.Departments.TryGetValue(c, out var d); guard++)
        {
            if (string.Equals(d.Code, code, StringComparison.OrdinalIgnoreCase)) return true;
            deptId = d.ParentId;
        }
        return false;
    }

    public async Task<ChainView?> GetAsync(string nik, CancellationToken ct)
    {
        nik = WorkbookNik.Normalize(nik, await KnownNiksAsync(ct)) ?? nik;
        var chain = await db.ApproverChains.AsNoTracking().FirstOrDefaultAsync(c => c.Nik == nik, ct);
        var l = await LoadLookupsAsync(ct);
        if (chain is null && !l.People.ContainsKey(nik)) return null;
        return BuildView(nik, chain, l);
    }

    public async Task<IReadOnlyList<MyApproverStep>> MineAsync(string nik, CancellationToken ct)
    {
        var view = await GetAsync(nik, ct);
        return view is null ? [] : view.Steps.Select(s => new MyApproverStep(s.Label, s.Name ?? "Unknown person", s.Active && s.Known)).ToList();
    }

    public async Task<ChainPage> ListAsync(ChainQuery q, CancellationToken ct)
    {
        var l = await LoadLookupsAsync(ct);
        var chains = await db.ApproverChains.AsNoTracking().ToDictionaryAsync(c => c.Nik, ct);
        // Everyone HC should see: active accounts, plus chains for NIKs HRIS hasn't created yet.
        var niks = l.People.Values.Where(p => p.Active).Select(p => p.Nik)
            .Concat(chains.Keys.Where(k => !l.People.ContainsKey(k)))
            .Distinct();

        IEnumerable<ChainView> rows = niks.Select(n => BuildView(n, chains.GetValueOrDefault(n), l));
        if (!string.IsNullOrWhiteSpace(q.Search))
        {
            var s = q.Search.Trim();
            rows = rows.Where(v => v.Nik.Contains(s, StringComparison.OrdinalIgnoreCase)
                || (v.Name?.Contains(s, StringComparison.OrdinalIgnoreCase) ?? false));
        }
        if (!string.IsNullOrWhiteSpace(q.SiteCode))
            rows = rows.Where(v => l.People.TryGetValue(v.Nik, out var p) && p.SiteId is { } sid
                && l.Sites.TryGetValue(sid, out var site) && string.Equals(site.Code, q.SiteCode, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(q.DepartmentCode))
            rows = rows.Where(v => l.People.TryGetValue(v.Nik, out var p) && UnderDepartment(p.DepartmentId, q.DepartmentCode, l));
        if (!string.IsNullOrWhiteSpace(q.DivisionCode))
            rows = rows.Where(v => l.People.TryGetValue(v.Nik, out var p) && p.DivisionId is { } vid
                && l.Divisions.TryGetValue(vid, out var dv) && string.Equals(dv.Code, q.DivisionCode, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(q.Issue))
            rows = rows.Where(v => v.Issues.Contains(q.Issue));

        var list = rows.OrderBy(v => v.Name ?? v.Nik, StringComparer.OrdinalIgnoreCase).ToList();
        var size = Math.Clamp(q.PageSize, 1, 200);
        var page = Math.Max(1, q.Page);
        return new ChainPage(list.Skip((page - 1) * size).Take(size).ToList(), list.Count, page, size);
    }

    public async Task<IReadOnlyList<ChainHistoryEntry>> HistoryAsync(string nik, CancellationToken ct)
    {
        nik = WorkbookNik.Normalize(nik, await KnownNiksAsync(ct)) ?? nik;
        var logs = await db.AuditLogs.AsNoTracking()
            .Where(a => a.Action == AuditAction && a.EntityId == nik)
            .OrderByDescending(a => a.CreatedAt).Take(100).ToListAsync(ct);
        if (logs.Count == 0) return [];
        var l = await LoadLookupsAsync(ct);
        string Name(string? n) => n is null ? "—" : l.People.TryGetValue(n, out var p) ? p.Name : n;
        return logs.Select(a =>
        {
            var d = a.DetailJson is null ? null : JsonSerializer.Deserialize<ChangeDetail>(a.DetailJson);
            var parts = new List<string>();
            if (d is not null)
                for (var i = 0; i < ApproverChain.SlotCount; i++)
                {
                    var b = d.Before?.ElementAtOrDefault(i); var f = d.After?.ElementAtOrDefault(i);
                    if (b != f) parts.Add($"{ApproverSlots.Labels[i]}: {Name(b)} → {Name(f)}");
                }
            var summary = parts.Count == 0 ? "No change" : string.Join("; ", parts);
            if (d?.Source == "import") summary = "Imported from workbook — " + summary;
            return new ChainHistoryEntry(a.CreatedAt, a.ActorNik is null ? null : Name(a.ActorNik), summary);
        }).ToList();
    }

    // ---- writes -------------------------------------------------------------------------

    /// Every NIK iPop knows, so a real 7-digit NIK isn't padded into someone else's.
    private async Task<HashSet<string>> KnownNiksAsync(CancellationToken ct) =>
        (await db.Users.AsNoTracking().Select(u => u.Nik).ToListAsync(ct)).ToHashSet();

    public sealed record ChangeDetail(string?[]? Before, string?[]? After, string? Source);

    public async Task<ChainView> SetSlotsAsync(string nik, IReadOnlyList<string?> slots, string actorNik, CancellationToken ct)
    {
        if (slots.Count != ApproverChain.SlotCount) throw new ArgumentException("Exactly 9 slots are required.", nameof(slots));
        var known = await KnownNiksAsync(ct);
        nik = WorkbookNik.Normalize(nik, known) ?? throw new ArgumentException("NIK is required.", nameof(nik));
        await WriteAsync([(nik, slots.Select(x => WorkbookNik.Normalize(x, known)).ToArray())], actorNik, null, ct);
        await db.SaveChangesAsync(ct);
        return (await GetAsync(nik, ct))!;
    }

    public async Task<int> BulkSetSlotAsync(IReadOnlyList<string> niks, int slot, string? approverNik, string actorNik, CancellationToken ct)
    {
        if (slot is < 1 or > ApproverChain.SlotCount) throw new ArgumentOutOfRangeException(nameof(slot));
        var known = await KnownNiksAsync(ct);
        var approver = WorkbookNik.Normalize(approverNik, known);
        var targets = niks.Select(x => WorkbookNik.Normalize(x, known)).OfType<string>().Distinct().ToList();
        var existing = await db.ApproverChains.AsNoTracking().Where(c => targets.Contains(c.Nik)).ToDictionaryAsync(c => c.Nik, ct);
        var changes = targets.Select(n =>
        {
            var s = existing.TryGetValue(n, out var c) ? c.Slots : new string?[ApproverChain.SlotCount];
            s[slot - 1] = approver;
            return (n, s);
        }).ToList();
        var n2 = await WriteAsync(changes, actorNik, "bulk", ct);
        await db.SaveChangesAsync(ct);
        return n2;
    }

    public async Task<int> ReplaceEverywhereAsync(string oldNik, string? newNik, string actorNik, CancellationToken ct)
    {
        var known = await KnownNiksAsync(ct);
        var from = WorkbookNik.Normalize(oldNik, known) ?? throw new ArgumentException("Old approver is required.", nameof(oldNik));
        var to = WorkbookNik.Normalize(newNik, known);
        var hits = await db.ApproverChains.AsNoTracking().Where(c =>
            c.Slot1Nik == from || c.Slot2Nik == from || c.Slot3Nik == from || c.Slot4Nik == from || c.Slot5Nik == from
            || c.Slot6Nik == from || c.Slot7Nik == from || c.Slot8Nik == from || c.Slot9Nik == from).ToListAsync(ct);
        var changes = hits.Select(c => (c.Nik, c.Slots.Select(s => s == from ? to : s).ToArray())).ToList();
        var n = await WriteAsync(changes, actorNik, "replace", ct);
        await db.SaveChangesAsync(ct);
        return n;
    }

    /// Stages chain upserts, ManagerNik and history; the caller saves (so import can wrap it in
    /// one transaction). Returns how many chains actually changed.
    public async Task<int> WriteAsync(IReadOnlyList<(string Nik, string?[] Slots)> changes, string actorNik, string? source,
        CancellationToken ct, Action<ApproverChain>? fill = null)
    {
        if (changes.Count == 0) return 0;
        var niks = changes.Select(c => c.Nik).ToList();
        var chains = await db.ApproverChains.Where(c => niks.Contains(c.Nik)).ToDictionaryAsync(c => c.Nik, ct);
        var users = await db.Users.Where(u => niks.Contains(u.Nik)).ToDictionaryAsync(u => u.Nik, ct);
        var at = now();
        var changed = 0;
        foreach (var (nik, slots) in changes)
        {
            var isNew = !chains.TryGetValue(nik, out var chain);
            if (isNew)
            {
                chain = new ApproverChain { Nik = nik, UpdatedAt = at };
                db.ApproverChains.Add(chain);
                chains[nik] = chain;
            }
            var before = isNew ? null : chain!.Slots;
            fill?.Invoke(chain!);
            if (!isNew && before!.SequenceEqual(slots)) continue;
            chain!.SetSlots(slots);
            chain.UpdatedAt = at;
            chain.UpdatedByNik = actorNik;
            changed++;

            if (users.TryGetValue(nik, out var user) && FirstManager(nik, slots) is { } mgr && user.ManagerNik != mgr)
            {
                user.ManagerNik = mgr;
                user.UpdatedAt = at;
            }
            db.AuditLogs.Add(new AuditLog
            {
                Id = Guid.NewGuid(), ActorNik = actorNik, Action = AuditAction, Entity = "approver_chain", EntityId = nik,
                DetailJson = JsonSerializer.Serialize(new ChangeDetail(before ?? new string?[ApproverChain.SlotCount], slots, source)),
                CreatedAt = at,
            });
        }
        return changed;
    }
}
