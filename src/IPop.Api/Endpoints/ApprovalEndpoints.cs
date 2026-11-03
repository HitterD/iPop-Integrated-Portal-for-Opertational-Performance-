using System.Security.Cryptography;
using IPop.Api.Rbac;
using IPop.Application.Approval;
using IPop.Application.Documents;
using IPop.Application.Rbac;
using IPop.Application.Realtime;
using IPop.Infrastructure.Approval;
using IPop.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace IPop.Api.Endpoints;

/// Approver chains (HC's matrix). Everyone reads their own chain (names only); approval.read sees
/// all; approval.manage edits and imports. Import applies the Directory tree too, which needs
/// directory.manage.
public static class ApprovalEndpoints
{
    public const string OrgPermission = PermissionCodes.DirectoryManage;
    private const long MaxUpload = 10 * 1024 * 1024;

    public record SetSlotsRequest(string?[]? Slots);
    public record BulkRequest(string[] Niks, int Slot, string? ApproverNik);
    public record ReplaceRequest(string OldNik, string? NewNik);
    public record CoreResult(int Status, string? Message, object? Body);

    private static string? CallerNik(HttpContext http) => http.User.FindFirst("nik")?.Value;

    public static void MapApproval(this WebApplication app)
    {
        var g = app.MapGroup("/api/approval").RequireAuthorization();

        g.MapGet("/me", async (HttpContext http, IApprovalService svc, CancellationToken ct) =>
            CallerNik(http) is { } nik ? Results.Ok(await svc.MineAsync(nik, ct)) : Results.Unauthorized());

        g.MapGet("/chains", async ([AsParameters] ListQuery q, IApprovalService svc, CancellationToken ct) =>
            Results.Ok(await svc.ListAsync(new ChainQuery(q.Search, q.Site, q.Department, q.Division, q.Issue, q.Page ?? 1, q.PageSize ?? 50), ct)))
            .RequirePermission(PermissionCodes.ApprovalRead);

        g.MapGet("/chains/{nik}", async (string nik, IApprovalService svc, CancellationToken ct) =>
            await svc.GetAsync(nik, ct) is { } v ? Results.Ok(v) : Results.NotFound())
            .RequirePermission(PermissionCodes.ApprovalRead);

        g.MapGet("/chains/{nik}/history", async (string nik, IApprovalService svc, CancellationToken ct) =>
            Results.Ok(await svc.HistoryAsync(nik, ct)))
            .RequirePermission(PermissionCodes.ApprovalRead);

        g.MapPut("/chains/{nik}", async (string nik, SetSlotsRequest req, HttpContext http, IApprovalService svc, CancellationToken ct) =>
            ToResult(await PutChainAsync(svc, nik, req, CallerNik(http)!, ct)))
            .RequirePermission(PermissionCodes.ApprovalManage);

        g.MapPost("/chains/bulk", async (BulkRequest req, HttpContext http, IApprovalService svc, CancellationToken ct) =>
        {
            if (req.Slot is < 1 or > 9 || req.Niks is not { Length: > 0 })
                return Results.BadRequest(new { code = "invalid", message = "Pick at least one person and a slot from 1 to 9." });
            return Results.Ok(new { updated = await svc.BulkSetSlotAsync(req.Niks, req.Slot, req.ApproverNik, CallerNik(http)!, ct) });
        }).RequirePermission(PermissionCodes.ApprovalManage);

        g.MapPost("/replace", async (ReplaceRequest req, HttpContext http, IApprovalService svc, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.OldNik))
                return Results.BadRequest(new { code = "invalid", message = "Choose the approver to replace." });
            return Results.Ok(new { updated = await svc.ReplaceEverywhereAsync(req.OldNik, req.NewNik, CallerNik(http)!, ct) });
        }).RequirePermission(PermissionCodes.ApprovalManage);

        g.MapPost("/import/preview", async (HttpRequest request, ISpreadsheetService sheets, ApprovalImport import, IPopDbContext db, CancellationToken ct) =>
        {
            var (bytes, complete, _, error) = await ReadUploadAsync(request, ct);
            if (error is not null) return error;
            var parsed = await ParseAsync(sheets, db, bytes!, ct);
            if (parsed.Rows.Count == 0 && parsed.Errors.Count > 0)
                return Results.BadRequest(new { code = "invalid_file", message = string.Join(" ", parsed.Errors) });
            var plan = await import.PlanAsync(parsed, complete, TokenOf(bytes!, complete), ct);
            return Results.Ok(Preview(plan));
        }).RequirePermission(PermissionCodes.ApprovalManage).DisableAntiforgery();

        g.MapPost("/import/apply", async (HttpRequest request, HttpContext http, ISpreadsheetService sheets, ApprovalImport import,
            IPopDbContext db, IPermissionEvaluator perms, IDirectoryChangeNotifier directory, CancellationToken ct) =>
        {
            var (bytes, complete, token, error) = await ReadUploadAsync(request, ct);
            if (error is not null) return error;
            var nik = CallerNik(http)!;
            var userId = await db.Users.AsNoTracking().Where(u => u.Nik == nik).Select(u => u.Id).FirstAsync(ct);
            var canDirectory = await perms.IsAllowedAsync(userId, PermissionCodes.DirectoryManage, null, ct);
            var parsed = await ParseAsync(sheets, db, bytes!, ct);
            var r = await ApplyCoreAsync(db, import, parsed, bytes!, complete, token ?? "", nik, canDirectory, ct);
            if (r.Status == 200 && r.Body is ImportResult { UsersPlaced: > 0 } or ImportResult { UnitsCreated: > 0 } or ImportResult { UnitsDeactivated: > 0 })
                await directory.NotifyAsync(ct);
            return ToResult(r);
        }).RequirePermission(PermissionCodes.ApprovalManage).DisableAntiforgery();

        g.MapGet("/export", async (IPopDbContext db, ISpreadsheetService sheets, CancellationToken ct) =>
        {
            var bytes = await sheets.CreateSampleSpreadsheetAsync(ApproverWorkbook.PreferredSheet, await ExportRowsAsync(db, ct), ct);
            return Results.File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                $"approvers-{DateTime.UtcNow:yyyy-MM-dd}.xlsx");
        }).RequirePermission(PermissionCodes.ApprovalRead);
    }

    public record ListQuery(string? Search, string? Site, string? Department, string? Division, string? Issue, int? Page, int? PageSize);

    private static IResult ToResult(CoreResult r) => r.Status == 200
        ? Results.Ok(r.Body)
        : Results.Json(new { code = r.Status switch { 400 => "invalid", 403 => "forbidden", 409 => "stale_preview", _ => "error" }, message = r.Message },
            statusCode: r.Status);

    public static async Task<CoreResult> PutChainAsync(IApprovalService svc, string nik, SetSlotsRequest req, string actorNik, CancellationToken ct)
    {
        if (req.Slots is not { Length: 9 }) return new(400, "Send all 9 approver slots.", null);
        return new(200, null, await svc.SetSlotsAsync(nik, req.Slots, actorNik, ct));
    }

    /// Re-plans from the uploaded bytes, so what is applied is exactly what was previewed.
    public static async Task<CoreResult> ApplyCoreAsync(IPopDbContext db, ApprovalImport import, WorkbookParseResult parsed, byte[] bytes,
        bool complete, string token, string actorNik, bool canDirectory, CancellationToken ct)
    {
        var expected = TokenOf(bytes, complete);
        if (!CryptographicOperations.FixedTimeEquals(System.Text.Encoding.ASCII.GetBytes(expected), System.Text.Encoding.ASCII.GetBytes(token)))
            return new(409, "This file changed since the preview. Preview it again before applying.", null);
        // Same guard as preview: an unreadable file must never be applied — with "complete list"
        // ticked it would clear everyone's approvers.
        if (parsed.Rows.Count == 0)
            return new(400, parsed.Errors.Count > 0 ? string.Join(" ", parsed.Errors) : "The file has no employee rows.", null);
        var plan = await import.PlanAsync(parsed, complete, expected, ct);
        if (plan.Tree.HasChanges && !canDirectory)
            return new(403, "Directory changes need the Directory permission. Ask an admin, or remove the Division/Department columns.", null);
        return new(200, null, await import.ApplyAsync(plan, actorNik, ct));
    }

    public static string TokenOf(byte[] bytes, bool complete) =>
        Convert.ToHexString(SHA256.HashData([.. bytes, complete ? (byte)1 : (byte)0]));

    private static async Task<(byte[]? Bytes, bool Complete, string? Token, IResult? Error)> ReadUploadAsync(HttpRequest request, CancellationToken ct)
    {
        if (!request.HasFormContentType) return (null, false, null, Results.BadRequest(new { code = "invalid_file", message = "Upload an .xlsx file." }));
        var form = await request.ReadFormAsync(ct);
        var file = form.Files.GetFile("file");
        if (file is null || file.Length == 0) return (null, false, null, Results.BadRequest(new { code = "invalid_file", message = "Choose an .xlsx file." }));
        if (file.Length > MaxUpload) return (null, false, null, Results.BadRequest(new { code = "too_large", message = "The file is larger than 10 MB." }));
        if (!file.FileName.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase))
            return (null, false, null, Results.BadRequest(new { code = "invalid_file", message = "Only .xlsx files can be imported." }));
        using var ms = new MemoryStream();
        await file.CopyToAsync(ms, ct);
        var complete = string.Equals(form["complete"], "true", StringComparison.OrdinalIgnoreCase);
        return (ms.ToArray(), complete, form["token"].ToString(), null);
    }

    private static async Task<WorkbookParseResult> ParseAsync(ISpreadsheetService sheets, IPopDbContext db, byte[] bytes, CancellationToken ct)
    {
        using var ms = new MemoryStream(bytes);
        var known = (await db.Users.AsNoTracking().Select(u => u.Nik).ToListAsync(ct)).ToHashSet();
        try { return ApproverWorkbook.Parse(await sheets.ReadSheetDataAsync(ms, ApproverWorkbook.PreferredSheet, ct), known); }
        catch (Exception) { return new WorkbookParseResult([], 0, ["The file could not be read as an Excel workbook."]); }
    }

    public record PreviewDto(string Token, int Unchanged, int SkippedResigned, int New, int Changed, int Cleared,
        IReadOnlyList<ChainChangeDto> Chains, IReadOnlyList<UnitCreate> UnitsToCreate, IReadOnlyList<UserMove> Moves,
        IReadOnlyList<UnitDeactivate> UnitsToDeactivate, IReadOnlyList<string> Unmapped, IReadOnlyList<string> UnknownEmployees,
        IReadOnlyList<string> InactiveApprovers, IReadOnlyList<string> UnknownApprovers, IReadOnlyList<string> Warnings);
    public record ChainChangeDto(string Nik, string Name, string Kind, string?[]? Before, string?[] After);

    private static PreviewDto Preview(ImportPlan p) => new(p.Token, p.Unchanged, p.SkippedResigned,
        p.Chains.Count(c => c.Kind == "new"), p.Chains.Count(c => c.Kind == "changed"), p.Chains.Count(c => c.Kind == "cleared"),
        p.Chains.Select(c => new ChainChangeDto(c.Nik, c.Name, c.Kind, c.Before, c.After)).ToList(),
        p.Tree.Creates, p.Tree.Moves, p.Tree.Deactivations, p.Tree.Unmapped,
        p.UnknownEmployeeNiks, p.InactiveApproverNiks, p.UnknownApproverNiks, p.Warnings);

    private static async Task<List<string[]>> ExportRowsAsync(IPopDbContext db, CancellationToken ct)
    {
        var chains = await db.ApproverChains.AsNoTracking().ToListAsync(ct);
        var users = (await db.Users.AsNoTracking()
                .Select(u => new { u.Nik, u.Name, Site = u.Site!.Name, Dept = u.Department!.Name, Div = u.Division!.Name, u.Position, u.Location })
                .ToListAsync(ct)).GroupBy(u => u.Nik).ToDictionary(g => g.Key, g => g.First());
        var rows = chains.OrderBy(c => users.TryGetValue(c.Nik, out var u) ? u.Name : c.SourceName ?? c.Nik).Select(c =>
        {
            users.TryGetValue(c.Nik, out var u);
            return new WorkbookRow(0, c.Nik, u?.Name ?? c.SourceName ?? c.Nik, null, u?.Dept, null, u?.Div,
                c.SourceJobTitle ?? u?.Position, c.SourceLevel, c.SourceWorkLocation ?? u?.Location, c.Slots);
        });
        return ApproverWorkbook.ToSheet(rows, n => users.TryGetValue(n, out var p) ? p.Name : null);
    }
}
