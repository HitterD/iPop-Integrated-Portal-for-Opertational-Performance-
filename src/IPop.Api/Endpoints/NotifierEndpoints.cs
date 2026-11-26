using System.Collections.Concurrent;
using System.Security.Claims;
using IPop.Application.Approval;
using IPop.Application.Documents;
using IPop.Application.Notifier;
using IPop.Core.Entities;
using IPop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IPop.Api.Endpoints;

public record NotifierDeviceReport(string DeviceKey, string MachineName, string? WindowsUser, string? OsVersion,
    string? AppVersion, IReadOnlyList<string>? LocalIps, string? MachineGuid = null);

public record NotifierDeviceRow(Guid Id, string MachineName, string? WindowsUser, string? OsVersion, string? AppVersion,
    string? LocalIps, string? RemoteIp, DateTime LastSeenAt, bool Online, string UserName, string? Department, string? Site,
    bool Unattended = false, bool HelperService = false, Guid? MachineId = null, string? RemoteId = null,
    bool UnattendedSwitch = false, bool HasPassword = false, DateTime? LockedUntil = null);

/// A leader of the signed-in person for the Notifier's priority inbox. Reason = why ("Manager", "General Manager",
/// "BOD / GM level"). Never NIK or position (office rule).
public record NotifierLeader(Guid Id, string Name, string? Department, string Reason);
public record NotifierPerson(Guid Id, string Name, string? Department);
public record NotifierLeaders(IReadOnlyList<NotifierLeader> Leaders, IReadOnlyList<NotifierPerson> Vips);

/// iPop Notifier desktop app (docs/notifier/PLAN.md):
///   POST /api/notifier/devices/register  — the app reports this PC (sign-in and every heartbeat)
///   GET  /api/notifier/devices           — ICT/IT department or Admin: every PC for remote help (mockup v2 §3)
///   GET  /api/notifier/me                — who may see the hidden ICT menu
///   GET  /api/notifier/leaders           — my leaders (approver chain + GM/Director/BOD) and my VIPs (1.5.0 priority inbox)
///   GET  /api/notifier/people?q=         — VIP picker: active people by name/email
///   POST /api/notifier/convert-preview   — Word/PowerPoint/Excel bytes → PDF for the viewer (nothing stored)
/// Settings live in /api/me/ui-prefs/notifier (UiPrefsEndpoints).
public static class NotifierEndpoints
{
    /// A device counts as online when it reported within this window (the app heartbeats every 60 s).
    public static readonly TimeSpan OnlineWindow = NotifierDevice.OnlineWindow;
    private static readonly string[] IctDepartmentCodes = ["ICT", "ICT-1 Support", "ICT-AIS"];

    public static IEndpointRouteBuilder MapNotifier(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/notifier").RequireAuthorization();

        g.MapPost("/devices/register", async (NotifierDeviceReport report, HttpContext http, IPopDbContext db, IConfiguration cfg, CancellationToken ct) =>
        {
            var userId = await MyId(http, db, ct);
            if (userId is null) return Results.Unauthorized();
            // The PC's own address (behind the proxy every PC used to show up as the proxy).
            var status = await RegisterAsync(db, userId.Value, report, IPop.Api.Notifier.ClientAddress.Of(http, cfg), DateTime.UtcNow, ct);
            return status == 204 ? Results.NoContent() : Results.BadRequest(new { message = "Invalid device report." });
        });

        g.MapPost("/devices/log", async (HttpContext http, IPopDbContext db, IConfiguration cfg, CancellationToken ct) =>
        {
            var userId = await MyId(http, db, ct);
            if (userId is null) return Results.Unauthorized();
            using var reader = new StreamReader(http.Request.Body);
            var dir = cfg["Notifier:LogPath"] ?? Path.Combine(AppContext.BaseDirectory, "notifier-logs");
            var status = await IPop.Api.Notifier.NotifierDeviceLogs.SaveAsync(db, userId.Value, http.Request.Query["machine"].ToString(), reader, dir, ct);
            return status == 204 ? Results.NoContent() : Results.NotFound(new { message = "This PC isn't registered for your account." });
        }).RequireRateLimiting(IPop.Api.Notifier.NotifierDeviceLogs.RateLimitPolicy);

        g.MapGet("/me", async (HttpContext http, IPopDbContext db, IConfiguration cfg, CancellationToken ct) =>
        {
            var userId = await MyId(http, db, ct);
            if (userId is null) return Results.Unauthorized();
            var since = DateTime.UtcNow - NotifierDevice.OnlineWindow;
            // My own PCs with the app (computer name only): iPop's notification settings explain the browser pause.
            var pcs = await db.NotifierDevices.AsNoTracking().Where(d => d.UserId == userId && d.LastSeenAt >= since)
                .OrderBy(d => d.MachineName).Select(d => d.MachineName).ToListAsync(ct);
            // Where "Open in iPop" goes: the public web address (prod: https://ipop.santos.co.id), not the API host.
            var webBase = cfg["Notifier:WebBase"] ?? cfg["Oidc:WebBase"];
            var ict = await IsIctOrAdminAsync(db, userId.Value, http.User.IsInRole("Admin"), ct);
            return Results.Ok(new { ictTools = ict, onlinePcs = pcs, webBase, isAdmin = http.User.IsInRole("Admin"),
                canRemote = ict && await RemoteEndpoints.CanHelpAsync(db, userId.Value, http.User.IsInRole("Admin"), ct) });
        });

        g.MapGet("/devices", async (string? q, HttpContext http, IPopDbContext db, CancellationToken ct) =>
        {
            var userId = await MyId(http, db, ct);
            if (userId is null) return Results.Unauthorized();
            if (!await IsIctOrAdminAsync(db, userId.Value, http.User.IsInRole("Admin"), ct)) return Results.Forbid();
            return Results.Ok(await ListAsync(db, q, DateTime.UtcNow, ct));
        });

        g.MapGet("/leaders", async (HttpContext http, IPopDbContext db, CancellationToken ct) =>
        {
            var userId = await MyId(http, db, ct);
            if (userId is null) return Results.Unauthorized();
            return Results.Ok(await LeadersAsync(db, userId.Value, DateTime.UtcNow, ct));
        });

        g.MapGet("/people", async (string? q, HttpContext http, IPopDbContext db, CancellationToken ct) =>
        {
            var userId = await MyId(http, db, ct);
            if (userId is null) return Results.Unauthorized();
            return Results.Ok(await PeopleAsync(db, userId.Value, q, ct));
        });

        g.MapPost("/convert-preview", async (string? name, HttpContext http, IWordDocumentService word, IPresentationService slides,
            ISpreadsheetService sheets, CancellationToken ct) =>
        {
            var (status, pdf, message) = await ConvertPreviewAsync(http.Request.Body, http.Request.ContentLength, name, word, slides, sheets, ct);
            if (status != 200) return Results.Json(new { message }, statusCode: status);
            var file = Path.GetFileName(name ?? "preview");
            return Results.File(IPop.Infrastructure.Documents.PdfTitle.Set(pdf!, file), "application/pdf",
                $"{Path.GetFileNameWithoutExtension(file)}.pdf");
        }).WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(MaxPreviewBytes + 1));

        return app;
    }

    // ── 1.5.0 priority inbox (docs/notifier/PRIORITY-150.md) ────────────────────

    public const long MaxPreviewBytes = 60L * 1024 * 1024;
    private static readonly SemaphoreSlim Conversions = new(2, 2);
    private static readonly TimeSpan LeadersTtl = TimeSpan.FromMinutes(10);
    /// Per person: their leaders, with the chain version they were built from. Bounded (oldest half dropped beyond
    /// <see cref="MaxCachedPeople"/>); a chain write (ApproverChainVersion) invalidates every entry at once.
    private static readonly ConcurrentDictionary<Guid, (DateTime At, long Version, IReadOnlyList<NotifierLeader> Leaders)> LeadersCache = new();
    private const int MaxCachedPeople = 5000;
    /// The BOD / GM level is the same for everyone: computed once per <see cref="LeadersTtl"/> (or chain change).
    private static (DateTime At, long Version, IReadOnlyCollection<string> Niks)? topLevel;
    private static readonly SemaphoreSlim TopGate = new(1, 1);
    public const string TopLevelReason = "BOD / GM level";
    private static readonly string[] TopLevels = ["GENERAL MANAGER", "DIRECTOR"];

    /// My leaders: everyone in my own approver chain (reason = the slot's label), every GM/Director (by the HC
    /// workbook's level) and everyone sitting in slot 8 (GM) or 9 (Director) of any chain ("BOD / GM level"), minus
    /// me. Leaders are cached 10 minutes per person; VIPs are read fresh from my Notifier settings.
    public static async Task<NotifierLeaders> LeadersAsync(IPopDbContext db, Guid userId, DateTime now, CancellationToken ct)
    {
        IReadOnlyList<NotifierLeader> leaders;
        var version = ApproverChainVersion.Current;
        if (LeadersCache.TryGetValue(userId, out var hit) && now - hit.At < LeadersTtl && hit.Version == version) leaders = hit.Leaders;
        else
        {
            leaders = await ComputeLeadersAsync(db, userId, await TopLevelNiksAsync(db, now, ct), ct);
            if (LeadersCache.Count >= MaxCachedPeople) TrimCache();
            LeadersCache[userId] = (now, version, leaders);
        }

        var prefs = await db.UserUiPrefs.AsNoTracking().Where(p => p.UserId == userId && p.Key == "notifier").Select(p => p.Json).FirstOrDefaultAsync(ct);
        var vipIds = NotifierSettings.Parse(prefs).Vips;
        var found = vipIds.Count == 0 ? [] : await db.Users.AsNoTracking().Where(u => vipIds.Contains(u.Id) && u.IsActive)
            .Select(u => new NotifierPerson(u.Id, u.Name, u.Department != null ? u.Department.Name : null)).ToListAsync(ct);
        var byId = found.ToDictionary(v => v.Id);
        var vips = vipIds.Where(byId.ContainsKey).Select(id => byId[id]).ToList();   // in the order the person added them
        return new NotifierLeaders(leaders, vips);
    }

    /// Drops the cached leaders (tests; a chain import could call it too).
    public static void ForgetLeaders(Guid? userId = null)
    {
        if (userId is { } id) LeadersCache.TryRemove(id, out _);
        else LeadersCache.Clear();
        topLevel = null;
    }

    private static void TrimCache()
    {
        foreach (var k in LeadersCache.OrderBy(e => e.Value.At).Take(LeadersCache.Count / 2).Select(e => e.Key).ToList())
            LeadersCache.TryRemove(k, out _);
    }

    /// Every GM/Director by the workbook level plus everyone in slot 8 (GM) or 9 (Director) of any chain.
    private static async Task<IReadOnlyCollection<string>> TopLevelNiksAsync(IPopDbContext db, DateTime now, CancellationToken ct)
    {
        var version = ApproverChainVersion.Current;
        // Only the real (PostgreSQL) database is cached, as ApprovalService does: test databases never share it.
        var cacheable = db.Database.IsNpgsql();
        if (cacheable && topLevel is { } t && now - t.At < LeadersTtl && t.Version == version) return t.Niks;
        await TopGate.WaitAsync(ct);
        try
        {
            if (cacheable && topLevel is { } t2 && now - t2.At < LeadersTtl && t2.Version == version) return t2.Niks;
            var top = await db.ApproverChains.AsNoTracking()
                .Where(c => c.SourceLevel != null && TopLevels.Contains(c.SourceLevel.Trim().ToUpper()))
                .Select(c => c.Nik).ToListAsync(ct);
            var slotTop = await db.ApproverChains.AsNoTracking()
                .Where(c => c.Slot8Nik != null || c.Slot9Nik != null)
                .Select(c => new { c.Slot8Nik, c.Slot9Nik }).ToListAsync(ct);
            var niks = top.Concat(slotTop.SelectMany(s => new[] { s.Slot8Nik, s.Slot9Nik })).OfType<string>()
                .Select(n => n.Trim()).Where(n => n.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (cacheable) topLevel = (now, version, niks);
            return niks;
        }
        finally { TopGate.Release(); }
    }

    private static async Task<IReadOnlyList<NotifierLeader>> ComputeLeadersAsync(IPopDbContext db, Guid userId, IReadOnlyCollection<string> topNiks, CancellationToken ct)
    {
        var myNik = await db.Users.AsNoTracking().Where(u => u.Id == userId).Select(u => u.Nik).FirstOrDefaultAsync(ct);
        if (myNik is null) return [];
        var reasonByNik = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var mine = await db.ApproverChains.AsNoTracking().FirstOrDefaultAsync(c => c.Nik == myNik, ct);
        if (mine is not null)
        {
            var slots = mine.Slots;
            for (var i = 0; i < ApproverChain.SlotCount; i++)
                if (slots[i] is { Length: > 0 } nik) reasonByNik.TryAdd(nik.Trim(), ApproverSlots.Labels[i]);
        }

        foreach (var nik in topNiks) reasonByNik.TryAdd(nik, TopLevelReason);
        reasonByNik.Remove(myNik);
        if (reasonByNik.Count == 0) return [];

        var niks = reasonByNik.Keys.ToList();
        var users = await db.Users.AsNoTracking().Where(u => niks.Contains(u.Nik) && u.IsActive && u.Id != userId)
            .Select(u => new { u.Id, u.Nik, u.Name, Department = u.Department != null ? u.Department.Name : null }).ToListAsync(ct);
        // My own chain first (slot order), then the GM/BOD level, each by name.
        var order = ApproverSlots.Labels.Select((l, i) => (l, i)).ToDictionary(x => x.l, x => x.i);
        return users
            .Select(u => new NotifierLeader(u.Id, u.Name, u.Department, reasonByNik[u.Nik]))
            .OrderBy(l => order.GetValueOrDefault(l.Reason, int.MaxValue)).ThenBy(l => l.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// VIP picker: up to 12 active people (not me) whose name or email contains <paramref name="q"/> (2+ characters).
    public static async Task<List<NotifierPerson>> PeopleAsync(IPopDbContext db, Guid userId, string? q, CancellationToken ct)
    {
        var term = (q ?? "").Trim().ToLowerInvariant();
        if (term.Length < 2) return [];
        if (term.Length > 100) term = term[..100];
        return await db.Users.AsNoTracking()
            .Where(u => u.IsActive && u.Id != userId && (u.Name.ToLower().Contains(term) || u.Email.ToLower().Contains(term)))
            .OrderBy(u => u.Name).Take(12)
            .Select(u => new NotifierPerson(u.Id, u.Name, u.Department != null ? u.Department.Name : null))
            .ToListAsync(ct);
    }

    /// 200 + PDF, 413 over 60 MB, 415 not Word/PowerPoint (.pptx)/Excel, 400 empty, 422 the file couldn't be converted.
    /// Two conversions at a time; the rest wait. The app already holds the bytes (downloaded with the person's own
    /// access), so there is no access check and nothing is stored.
    public static async Task<(int Status, byte[]? Pdf, string? Message)> ConvertPreviewAsync(Stream body, long? length, string? name,
        IWordDocumentService word, IPresentationService slides, ISpreadsheetService sheets, CancellationToken ct)
    {
        const string tooLarge = "This file is too large to preview (60 MB at most).";
        var ext = Path.GetExtension(name ?? "").ToLowerInvariant();
        var kind = ext switch
        {
            ".doc" or ".docx" or ".rtf" or ".odt" => "word",
            ".pptx" => "slides",
            ".xlsx" or ".xls" or ".xlsm" or ".csv" => "sheet",
            _ => null,
        };
        if (kind is null) return (415, null, "Only Word, PowerPoint (.pptx) and Excel files can be previewed.");
        if (length > MaxPreviewBytes) return (413, null, tooLarge);

        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int n;
        while ((n = await body.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + n > MaxPreviewBytes) return (413, null, tooLarge);
            buffer.Write(chunk, 0, n);
        }
        if (buffer.Length == 0) return (400, null, "The file is empty.");
        buffer.Position = 0;

        await Conversions.WaitAsync(ct);
        try
        {
            var pdf = kind switch
            {
                "word" => await word.ConvertToPdfAsync(buffer, ct),
                "slides" => await slides.ConvertToPdfAsync(buffer, ct),
                _ => await sheets.ConvertToPdfAsync(buffer, ext, ct),
            };
            return (200, pdf, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (422, null, "This file couldn't be prepared for preview. Download it to open it.");
        }
        finally { Conversions.Release(); }
    }


    /// 204 saved, 400 invalid. Upserts by (user, device key); trims everything to the column sizes.
    public static async Task<int> RegisterAsync(IPopDbContext db, Guid userId, NotifierDeviceReport r, string? remoteIp, DateTime now, CancellationToken ct)
    {
        var key = (r.DeviceKey ?? "").Trim();
        var machine = (r.MachineName ?? "").Trim();
        if (key.Length is < 8 or > 64 || !key.All(c => char.IsAsciiLetterOrDigit(c) || c == '-')) return 400;
        if (machine.Length is 0 or > 64) return 400;
        var ips = r.LocalIps?.Select(i => i.Trim()).Where(i => System.Net.IPAddress.TryParse(i, out _)).Take(8).ToList() ?? [];
        var row = await db.NotifierDevices.FirstOrDefaultAsync(d => d.UserId == userId && d.DeviceKey == key, ct);
        if (row is null)
        {
            row = new NotifierDevice { Id = Guid.NewGuid(), UserId = userId, DeviceKey = key, MachineName = machine, FirstSeenAt = now };
            db.NotifierDevices.Add(row);
        }
        row.MachineName = machine;
        row.WindowsUser = Cut(r.WindowsUser, 128);
        row.OsVersion = Cut(r.OsVersion, 128);
        row.AppVersion = Cut(r.AppVersion, 32);
        row.LocalIps = ips.Count == 0 ? null : Cut(string.Join(", ", ips), 256);
        row.RemoteIp = Cut(remoteIp, 64);
        row.LastSeenAt = now;
        // One 9-digit remote ID per physical PC, shared by everyone who signs in to Windows on it (BIGIS #8).
        await IPop.Api.Notifier.RemoteMachines.LinkAsync(db, row, r.MachineGuid, now, ct);
        await db.SaveChangesAsync(ct);
        return 204;
    }

    /// The hidden ICT menu: Admin role, or a member of an ICT/IT department (or one of its sub-units).
    public static async Task<bool> IsIctOrAdminAsync(IPopDbContext db, Guid userId, bool isAdmin, CancellationToken ct)
    {
        if (isAdmin) return true;
        var deptId = await db.Users.AsNoTracking().Where(u => u.Id == userId).Select(u => u.DepartmentId).FirstOrDefaultAsync(ct);
        for (var hops = 0; deptId is { } id && hops < 6; hops++)
        {
            var d = await db.Departments.AsNoTracking().Where(x => x.Id == id).Select(x => new { x.Code, x.Name, x.ParentId }).FirstOrDefaultAsync(ct);
            if (d is null) return false;
            if (IctDepartmentCodes.Contains(d.Code, StringComparer.OrdinalIgnoreCase) || d.Name.Equals("ICT", StringComparison.OrdinalIgnoreCase)
                || d.Name.Equals("IT", StringComparison.OrdinalIgnoreCase)) return true;
            deptId = d.ParentId;
        }
        return false;
    }

    public static async Task<List<NotifierDeviceRow>> ListAsync(IPopDbContext db, string? q, DateTime now, CancellationToken ct)
    {
        var since = now - OnlineWindow;
        var query = db.NotifierDevices.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(q))
        {
            var term = $"%{q.Trim()}%";
            var digits = IPop.Api.Notifier.RemoteMachines.ParseId(q);
            query = query.Where(d => (digits != null && d.Machine != null && d.Machine.ConnectId == digits) || EF.Functions.ILike(d.MachineName, term) || EF.Functions.ILike(d.User.Name, term)
                || EF.Functions.ILike(d.User.Nik, term) || (d.RemoteIp != null && EF.Functions.ILike(d.RemoteIp, term))
                || (d.LocalIps != null && EF.Functions.ILike(d.LocalIps, term)));
        }
        return await query.OrderByDescending(d => d.LastSeenAt).Take(500)
            .Select(d => new NotifierDeviceRow(d.Id, d.MachineName, d.WindowsUser, d.OsVersion, d.AppVersion, d.LocalIps, d.RemoteIp,
                d.LastSeenAt, d.LastSeenAt >= since, d.User.Name, d.User.Department != null ? d.User.Department.Name : null,
                d.User.Site != null ? d.User.Site.Name : null,
                d.Machine != null && d.Machine.Unattended && d.Machine.PasswordHash != null, d.HelperService, d.MachineId,
                d.Machine != null ? d.Machine.ConnectId : null, d.Machine != null && d.Machine.Unattended,
                d.Machine != null && d.Machine.PasswordHash != null, d.Machine != null ? d.Machine.LockedUntil : null))
            .ToListAsync(ct);
    }

    private static string? Cut(string? s, int max) => string.IsNullOrWhiteSpace(s) ? null : s.Trim() is var t && t.Length > max ? t[..max] : s.Trim();

    /// The caller: the token's user id (sub) first, as the notification gateway does; only a token without it falls
    /// back to the NIK claim (review M5). No NIK claim either = nobody (never "the user whose NIK is null").
    internal static async Task<Guid?> MyId(HttpContext http, IPopDbContext db, CancellationToken ct)
    {
        if (IPop.Api.Auth.CurrentUser.IdFromToken(http.User) is { } fromToken) return fromToken;
        var nik = http.User.FindFirstValue("nik");
        if (string.IsNullOrEmpty(nik)) return null;
        return await db.Users.AsNoTracking().Where(u => u.Nik == nik).Select(u => (Guid?)u.Id).FirstOrDefaultAsync(ct);
    }
}
