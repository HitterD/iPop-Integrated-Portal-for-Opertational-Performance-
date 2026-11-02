using IPop.Application.Approval;
using IPop.Core.Entities;
using IPop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IPop.Infrastructure.Approval;

public record UnitCreate(string Kind, string Code, string Name, string? ParentCode, string? SiteCode, string Path = "");
public record UserMove(string Nik, string Name, string? FromPath, string ToPath, string? SiteCode, string DepartmentCode, string? DivisionCode);
public record UnitDeactivate(string Kind, string Code, string Path);
public record TreePlan(IReadOnlyList<UnitCreate> Creates, IReadOnlyList<UserMove> Moves,
    IReadOnlyList<UnitDeactivate> Deactivations, IReadOnlyList<string> Unmapped)
{
    public bool HasChanges => Creates.Count > 0 || Moves.Count > 0 || Deactivations.Count > 0;
}

/// Places workbook people in the existing tree (UI Plant › Division › Department = DB Site ›
/// Department › Division), creating only the units that are missing. Planning never writes;
/// ApplyAsync stages the writes for the caller's transaction. Deterministic for a given DB state,
/// so preview and apply produce the same plan.
public sealed class TreePlanner(IPopDbContext db)
{
    private sealed class DeptNode
    {
        public Guid Id; public required string Code; public required string Name; public string? ParentCode; public string? SiteCode;
        public bool IsNew;
    }
    private sealed class DivNode
    {
        public Guid Id; public required string Code; public required string Name; public required string DeptCode; public string? SiteCode;
        public bool IsNew; public bool Generic;
    }

    public async Task<TreePlan> PlanAsync(IReadOnlyList<WorkbookRow> rows, CancellationToken ct)
    {
        var sites = await db.Sites.AsNoTracking().Where(s => s.IsActive).ToListAsync(ct);
        var siteById = sites.ToDictionary(s => s.Id);
        var siteByCode = sites.ToDictionary(s => s.Code, StringComparer.OrdinalIgnoreCase);
        var deptRows = await db.Departments.AsNoTracking().Where(d => d.IsActive).ToListAsync(ct);
        var deptCodeById = deptRows.ToDictionary(d => d.Id, d => d.Code);
        var depts = deptRows.Select(d => new DeptNode
        {
            Id = d.Id, Code = d.Code, Name = d.Name,
            ParentCode = d.ParentId is { } p && deptCodeById.TryGetValue(p, out var pc) ? pc : null,
            SiteCode = d.SiteId is { } s && siteById.TryGetValue(s, out var site) ? site.Code : null,
        }).ToDictionary(d => d.Code, StringComparer.OrdinalIgnoreCase);
        var divRows = await db.Divisions.AsNoTracking().Where(d => d.IsActive && d.DepartmentId != null).ToListAsync(ct);
        var divs = divRows.Where(d => deptCodeById.ContainsKey(d.DepartmentId!.Value)).Select(d => new DivNode
        {
            Id = d.Id, Code = d.Code, Name = d.Name, DeptCode = deptCodeById[d.DepartmentId!.Value],
            SiteCode = d.SiteId is { } s && siteById.TryGetValue(s, out var site) ? site.Code : null,
            Generic = TreeMapping.IsGenericUnit(d.Name),
        }).ToDictionary(d => d.Code, StringComparer.OrdinalIgnoreCase);
        var allCodes = new HashSet<string>(
            (await db.Departments.AsNoTracking().Select(d => d.Code).ToListAsync(ct))
            .Concat(await db.Divisions.AsNoTracking().Select(d => d.Code).ToListAsync(ct)), StringComparer.OrdinalIgnoreCase);

        var users = (await db.Users.AsNoTracking().Where(u => u.IsActive)
                .Select(u => new { u.Id, u.Nik, u.Name, u.DepartmentId, u.DivisionId, u.SiteId }).ToListAsync(ct))
            .GroupBy(u => u.Nik).ToDictionary(g => g.Key, g => g.First());
        var divCodeById = divs.Values.ToDictionary(d => d.Id, d => d.Code);

        var creates = new List<UnitCreate>();
        var moves = new List<UserMove>();
        var unmapped = new Dictionary<string, int>();
        // Where each workbook person ends up (division code or null), for the generic-unit sweep.
        var finalDivision = new Dictionary<string, string?>();
        void Unmapped(string what) => unmapped[what] = unmapped.GetValueOrDefault(what) + 1;

        string UniqueCode(string baseCode)
        {
            var code = baseCode;
            for (var n = 2; allCodes.Contains(code); n++) code = $"{baseCode}{n}";
            allCodes.Add(code);
            return code;
        }
        IEnumerable<DeptNode> Children(string code) => depts.Values.Where(d => string.Equals(d.ParentCode, code, StringComparison.OrdinalIgnoreCase));
        // Plant-line subtrees (Powder, RTD) are reached only through their line, never by a deep search
        // from the top — otherwise a unit created under RTD would make "Maintenance" ambiguous next time.
        bool IsLine(DeptNode d) => TreeMapping.Key(d.Name) is "POWDER" or "RTD";
        IEnumerable<DeptNode> Descendants(string code)
        {
            foreach (var c in Children(code).Where(c => !IsLine(c)).OrderBy(c => c.Code, StringComparer.Ordinal))
            {
                yield return c;
                foreach (var g in Descendants(c.Code)) yield return g;
            }
        }
        DeptNode FindOrCreateDept(DeptNode parent, string rawName, string? siteCode, bool deep)
        {
            // Accept the existing DB name (via alias) or the workbook's own name, so a unit created from the
            // workbook is found again on the next import.
            var keys = new HashSet<string> { TreeMapping.Key(rawName) };
            if (TreeMapping.Aliases.GetValueOrDefault(TreeMapping.Squash(rawName)) is { } alias) keys.Add(TreeMapping.Key(alias));
            var hit = Children(parent.Code).OrderBy(c => c.Code, StringComparer.Ordinal).FirstOrDefault(c => keys.Contains(TreeMapping.Key(c.Name)));
            if (hit is null && deep)
            {
                var deepHits = Descendants(parent.Code).Where(c => keys.Contains(TreeMapping.Key(c.Name))).ToList();
                if (deepHits.Count == 1) hit = deepHits[0];
            }
            if (hit is not null) return hit;
            var name = TreeMapping.Title(rawName);
            var node = new DeptNode
            {
                Id = Guid.NewGuid(), Code = UniqueCode($"{parent.Code}-{TreeMapping.Abbr(rawName)}"), Name = name,
                ParentCode = parent.Code, SiteCode = siteCode, IsNew = true,
            };
            depts[node.Code] = node;
            creates.Add(new UnitCreate("department", node.Code, node.Name, parent.Code, siteCode, DeptPath(node.Code)));
            return node;
        }
        DivNode FindOrCreateDiv(DeptNode dept, string rawName, string? siteCode)
        {
            var key = TreeMapping.Key(rawName);
            var hit = divs.Values.Where(d => string.Equals(d.DeptCode, dept.Code, StringComparison.OrdinalIgnoreCase)
                    && TreeMapping.Key(d.Name) == key)
                .OrderBy(d => string.Equals(d.SiteCode, siteCode, StringComparison.OrdinalIgnoreCase) ? 0 : d.SiteCode is null ? 1 : 2)
                .ThenBy(d => d.Code, StringComparer.Ordinal)
                .FirstOrDefault(d => d.SiteCode is null || string.Equals(d.SiteCode, siteCode, StringComparison.OrdinalIgnoreCase));
            if (hit is not null) return hit;
            var infix = siteCode is not null && TreeMapping.SiteInfix.TryGetValue(siteCode, out var i)
                && !string.Equals(dept.SiteCode, siteCode, StringComparison.OrdinalIgnoreCase) ? $"-{i}" : "";
            var node = new DivNode
            {
                Id = Guid.NewGuid(), Code = UniqueCode($"{dept.Code}{infix}-{TreeMapping.Abbr(rawName)}"), Name = TreeMapping.Title(rawName),
                DeptCode = dept.Code, SiteCode = siteCode, IsNew = true,
            };
            divs[node.Code] = node;
            creates.Add(new UnitCreate("division", node.Code, node.Name, dept.Code, siteCode, $"{DeptPath(dept.Code)} › {node.Name}"));
            return node;
        }
        string DeptPath(string? code)
        {
            var names = new List<string>();
            for (var guard = 0; code is not null && guard < 20 && depts.TryGetValue(code, out var d); guard++)
            {
                names.Insert(0, d.Name);
                code = d.ParentCode;
            }
            return string.Join(" › ", names);
        }

        // Last row wins for a repeated NIK (the import planner warns about it).
        var latest = rows.GroupBy(r => r.Nik).Select(g => g.Last()).OrderBy(r => r.RowNumber);
        foreach (var r in latest)
        {
            if (!users.TryGetValue(r.Nik, out var user)) continue;
            if (r.WorkLocation is null || !TreeMapping.Sites.TryGetValue(r.WorkLocation.Trim(), out var siteCode) || !siteByCode.ContainsKey(siteCode))
            {
                Unmapped($"Work Location \"{r.WorkLocation}\"");
                continue;
            }
            if (TreeMapping.Division(r.Division) is not { } map || !depts.TryGetValue(map.TopCode, out var top))
            {
                Unmapped($"Division \"{r.Division}\"");
                continue;
            }

            var parent = map.Line is { } line ? FindOrCreateDept(top, line, siteCode, deep: false) : top;
            DeptNode target = parent;
            DivNode? unit = null;
            if (!TreeMapping.IsRoleDepartment(r.Department, r.Division))
            {
                var deptName = r.Department!.Trim();
                var bagian = TreeMapping.StripLocation(r.Bagian ?? "");
                // "FINANCE, TAX, ACCOUNTING - TAX" + Bagian "TAX" → sub-division FINANCE, TAX, ACCOUNTING, unit TAX.
                var dash = deptName.LastIndexOf(" - ", StringComparison.Ordinal);
                if (dash > 0 && TreeMapping.Key(deptName[(dash + 3)..]) == TreeMapping.Key(bagian))
                    deptName = deptName[..dash].Trim();
                target = FindOrCreateDept(parent, deptName, map.Line is null ? null : siteCode, deep: true);

                var bKey = TreeMapping.Key(TreeMapping.Aliases.GetValueOrDefault(TreeMapping.Squash(bagian)) ?? bagian);
                var placeholder = bKey.Length == 0 || bKey == TreeMapping.Key(deptName) || bKey == TreeMapping.Key(target.Name)
                    || bKey == TreeMapping.Key(r.Department) || bKey == TreeMapping.Key(r.Division) || bKey == "OPERATION"
                    || TreeMapping.Squash(bagian).StartsWith("PLANT ", StringComparison.Ordinal);
                if (!placeholder) unit = FindOrCreateDiv(target, bagian, siteCode);
            }

            finalDivision[r.Nik] = unit?.Code;
            var site = siteByCode[siteCode];
            var curDept = user.DepartmentId is { } cd && deptCodeById.TryGetValue(cd, out var cdc) ? cdc : null;
            var curDiv = user.DivisionId is { } cv && divCodeById.TryGetValue(cv, out var cvc) ? cvc : null;
            if (user.SiteId == site.Id && string.Equals(curDept, target.Code, StringComparison.OrdinalIgnoreCase)
                && string.Equals(curDiv, unit?.Code, StringComparison.OrdinalIgnoreCase))
                continue;
            var from = curDept is null ? null : DeptPath(curDept) + (curDiv is not null && divs.TryGetValue(curDiv, out var od) ? $" › {od.Name}" : "");
            moves.Add(new UserMove(r.Nik, user.Name, from,
                $"{site.Name} › {DeptPath(target.Code)}{(unit is null ? "" : $" › {unit.Name}")}",
                siteCode, target.Code, unit?.Code));
        }

        // Generic role buckets (Head, Staff, …) left with nobody are switched off.
        var occupancy = users.Values.Select(u => finalDivision.TryGetValue(u.Nik, out var f)
                ? f : u.DivisionId is { } dv && divCodeById.TryGetValue(dv, out var dc) ? dc : null)
            .OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var deactivations = divs.Values.Where(d => d.Generic && !d.IsNew && !occupancy.Contains(d.Code))
            .OrderBy(d => d.Code, StringComparer.Ordinal)
            .Select(d => new UnitDeactivate("division", d.Code, $"{DeptPath(d.DeptCode)} › {d.Name}")).ToList();

        return new TreePlan(creates, moves, deactivations,
            unmapped.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key} — {kv.Value} people").ToList());
    }

    public async Task ApplyAsync(TreePlan plan, CancellationToken ct)
    {
        if (!plan.HasChanges) return;
        var siteIds = await db.Sites.ToDictionaryAsync(s => s.Code, s => s.Id, StringComparer.OrdinalIgnoreCase, ct);
        var deptIds = await db.Departments.ToDictionaryAsync(d => d.Code, d => d.Id, StringComparer.OrdinalIgnoreCase, ct);
        var divIds = await db.Divisions.ToDictionaryAsync(d => d.Code, d => d.Id, StringComparer.OrdinalIgnoreCase, ct);
        Guid? Site(string? code) => code is not null && siteIds.TryGetValue(code, out var id) ? id : null;

        foreach (var c in plan.Creates)   // parents are always listed before their children
        {
            if (c.Kind == "department")
            {
                var d = new Department { Id = Guid.NewGuid(), Code = c.Code, Name = c.Name, SiteId = Site(c.SiteCode),
                    ParentId = c.ParentCode is not null ? deptIds[c.ParentCode] : null };
                db.Departments.Add(d);
                deptIds[c.Code] = d.Id;
            }
            else
            {
                var v = new Division { Id = Guid.NewGuid(), Code = c.Code, Name = c.Name, SiteId = Site(c.SiteCode),
                    DepartmentId = deptIds[c.ParentCode!] };
                db.Divisions.Add(v);
                divIds[c.Code] = v.Id;
            }
        }

        var niks = plan.Moves.Select(m => m.Nik).ToList();
        var users = await db.Users.Where(u => niks.Contains(u.Nik)).ToDictionaryAsync(u => u.Nik, ct);
        var now = DateTime.UtcNow;
        foreach (var m in plan.Moves)
        {
            if (!users.TryGetValue(m.Nik, out var u)) continue;
            u.SiteId = Site(m.SiteCode);
            u.DepartmentId = deptIds[m.DepartmentCode];
            u.DivisionId = m.DivisionCode is null ? null : divIds[m.DivisionCode];
            u.UpdatedAt = now;
        }

        var off = plan.Deactivations.Select(d => d.Code).ToList();
        foreach (var d in await db.Divisions.Where(d => off.Contains(d.Code)).ToListAsync(ct)) d.IsActive = false;
    }
}
