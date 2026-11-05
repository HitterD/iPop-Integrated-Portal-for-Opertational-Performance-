using System.Text.Json;
using IPop.Application.Approval;
using IPop.Core.Entities;
using IPop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IPop.Infrastructure.Approval;

public record ChainChange(string Nik, string Name, string Kind, string?[]? Before, string?[] After);

public record ImportPlan(IReadOnlyList<ChainChange> Chains, TreePlan Tree, IReadOnlyList<string> UnknownEmployeeNiks,
    IReadOnlyList<string> InactiveApproverNiks, IReadOnlyList<string> UnknownApproverNiks, IReadOnlyList<string> Warnings,
    int Unchanged, int SkippedResigned, string Token, IReadOnlyDictionary<string, WorkbookRow> Rows);

public record ImportResult(int ChainsWritten, int UsersPlaced, int UnitsCreated, int UnitsDeactivated);

/// Workbook import: PlanAsync is read-only (the preview); ApplyAsync writes chains, ManagerNik,
/// history and tree placement in one transaction. The server always re-plans from the uploaded
/// bytes before applying — the client never sends a plan back.
public sealed class ApprovalImport(IPopDbContext db, ApprovalService approvals, TreePlanner tree)
{
    public async Task<ImportPlan> PlanAsync(WorkbookParseResult parsed, bool fileIsComplete, string token, CancellationToken ct)
    {
        var warnings = parsed.Errors.ToList();
        foreach (var g in parsed.Rows.GroupBy(r => r.Nik).Where(g => g.Count() > 1))
            warnings.Add($"NIK {g.Key} appears {g.Count()} times (rows {string.Join(", ", g.Select(r => r.RowNumber))}); the last row (row {g.Last().RowNumber}) is used.");
        var rows = parsed.Rows.GroupBy(r => r.Nik).ToDictionary(g => g.Key, g => g.Last());

        var existing = await db.ApproverChains.AsNoTracking().ToDictionaryAsync(c => c.Nik, ct);
        var people = (await db.Users.AsNoTracking().Select(u => new { u.Nik, u.Name, u.IsActive }).ToListAsync(ct))
            .GroupBy(u => u.Nik).ToDictionary(g => g.Key, g => g.First());

        var changes = new List<ChainChange>();
        var unchanged = 0;
        foreach (var r in rows.Values.OrderBy(r => r.RowNumber))
        {
            if (existing.TryGetValue(r.Nik, out var cur))
            {
                if (cur.Slots.SequenceEqual(r.Slots)) { unchanged++; continue; }
                changes.Add(new ChainChange(r.Nik, people.TryGetValue(r.Nik, out var p) ? p.Name : r.Name, "changed", cur.Slots, r.Slots));
            }
            else changes.Add(new ChainChange(r.Nik, people.TryGetValue(r.Nik, out var p) ? p.Name : r.Name, "new", null, r.Slots));
        }
        if (fileIsComplete)
            foreach (var c in existing.Values.Where(c => !rows.ContainsKey(c.Nik) && c.Slots.Any(s => s is not null)).OrderBy(c => c.Nik))
                changes.Add(new ChainChange(c.Nik, people.TryGetValue(c.Nik, out var p) ? p.Name : c.SourceName ?? c.Nik, "cleared",
                    c.Slots, new string?[ApproverChain.SlotCount]));

        var approvers = rows.Values.SelectMany(r => r.Slots).OfType<string>().Distinct().ToList();
        return new ImportPlan(changes, await tree.PlanAsync(rows.Values.ToList(), ct),
            rows.Keys.Where(n => !people.ContainsKey(n)).Order().ToList(),
            approvers.Where(n => people.TryGetValue(n, out var p) && !p.IsActive).Order().ToList(),
            approvers.Where(n => !people.ContainsKey(n)).Order().ToList(),
            warnings, unchanged, parsed.SkippedResigned, token, rows);
    }

    public async Task<ImportResult> ApplyAsync(ImportPlan plan, string actorNik, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            var written = await approvals.WriteAsync(plan.Chains.Select(c => (c.Nik, c.After)).ToList(), actorNik, "import", ct,
                chain =>
                {
                    if (!plan.Rows.TryGetValue(chain.Nik, out var r)) return;
                    chain.SourceName = r.Name;
                    chain.SourceJobTitle = r.JobTitle;
                    chain.SourceLevel = r.Level;
                    chain.SourceWorkLocation = r.WorkLocation;
                });
            await tree.ApplyAsync(plan.Tree, ct);
            var result = new ImportResult(written, plan.Tree.Moves.Count, plan.Tree.Creates.Count, plan.Tree.Deactivations.Count);
            db.AuditLogs.Add(new AuditLog
            {
                Id = Guid.NewGuid(), ActorNik = actorNik, Action = "approval.import", Entity = "approver_chain",
                DetailJson = JsonSerializer.Serialize(new { result.ChainsWritten, result.UsersPlaced, result.UnitsCreated, result.UnitsDeactivated, plan.Token }),
                CreatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync(ct);
            ApprovalService.ForgetLookups();
            await tx.CommitAsync(ct);
            return result;
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None);
            db.ChangeTracker.Clear();
            throw;
        }
    }
}
