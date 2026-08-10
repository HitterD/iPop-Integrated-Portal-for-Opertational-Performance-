using IPop.Api.Rbac;
using IPop.Application.Files;
using IPop.Core.Entities;
using IPop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IPop.Api.Endpoints;

public static class FileEndpoints
{
    public static IEndpointRouteBuilder MapFiles(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/files").RequireAuthorization().RequirePermission("files.read");

        g.MapGet("/servers", () => Results.Ok(new[]
        {
            new { id = "spj", label = "SPJ", host = "192.168.2.16" },
            new { id = "krw", label = "KRW", host = "10.12.2.6" },
            new { id = "jtb", label = "JTB", host = "10.11.2.6" },
            new { id = "smg", label = "SMG", host = "10.14.2.16" },
        }));

        g.MapGet("/credentials", async (HttpContext http, IFileAccessService svc, CancellationToken ct) =>
        {
            var userId = await UserIdAsync(http);
            if (userId is null) return Results.Unauthorized();
            return Results.Ok(await svc.ListCredentialsAsync(userId.Value, ct));
        });

        g.MapGet("/credential", async (string? server, HttpContext http, IFileAccessService svc, CancellationToken ct) =>
        {
            var userId = await UserIdAsync(http);
            if (userId is null) return Results.Unauthorized();
            if (string.IsNullOrWhiteSpace(server))
            {
                var all = await svc.ListCredentialsAsync(userId.Value, ct);
                return Results.Ok(all);
            }
            var (found, dto) = await svc.GetCredentialAsync(userId.Value, server, ct);
            return found ? Results.Ok(dto) : Results.Ok(new { hasCredential = false, server });
        });

        g.MapPut("/credential", async (CredentialRequest req, HttpContext http, IFileAccessService svc, CancellationToken ct) =>
        {
            var userId = await UserIdAsync(http);
            if (userId is null) return Results.Unauthorized();
            if (string.IsNullOrWhiteSpace(req.Server)) return Results.BadRequest(new { code = "server_required", message = "Server is required (spj/krw/jtb/smg)." });
            var (ok, err) = await svc.SetCredentialAsync(userId.Value, req.Server, req.Username, req.Password, ct);
            return ok ? Results.Ok() : Results.Json(new { code = "synology_auth_failed", message = err }, statusCode: 400);
        });

        g.MapDelete("/credential", async (string? server, HttpContext http, IFileAccessService svc, CancellationToken ct) =>
        {
            var userId = await UserIdAsync(http);
            if (userId is null) return Results.Unauthorized();
            if (string.IsNullOrWhiteSpace(server)) return Results.BadRequest(new { code = "server_required" });
            await svc.ClearCredentialAsync(userId.Value, server, ct);
            return Results.Ok();
        });

        g.MapGet("/shares", async (string? server, HttpContext http, IFileAccessService svc, IPopDbContext db, CancellationToken ct) =>
        {
            var userId = await UserIdAsync(http);
            if (userId is null) return Results.Unauthorized();
            if (string.IsNullOrWhiteSpace(server)) return Results.BadRequest(new { code = "server_required" });
            try { var r = await svc.ListSharesAsync(userId.Value, server, ct); await Audit(http, db, "files.list_shares", $"{server}:/", ct); return Results.Ok(r); }
            catch (SynoException ex) when (ex.IsAuthFailure) { return Results.Json(new { code = "synology_credential_required", message = ex.Message }, statusCode: 401); }
            catch (SynoException ex) { return Results.Json(new { code = "synology_error", message = ex.Message }, statusCode: 502); }
        });

        g.MapGet("", async (string? server, string? path, HttpContext http, IFileAccessService svc, IPopDbContext db, CancellationToken ct) =>
        {
            var userId = await UserIdAsync(http);
            if (userId is null) return Results.Unauthorized();
            if (string.IsNullOrWhiteSpace(server)) return Results.BadRequest(new { code = "server_required" });
            var p = string.IsNullOrWhiteSpace(path) ? "/" : path.Trim();
            try { var r = await svc.ListFolderAsync(userId.Value, server, p, ct); await Audit(http, db, "files.list", $"{server}:{p}", ct); return Results.Ok(r); }
            catch (SynoException ex) when (ex.IsAuthFailure) { return Results.Json(new { code = "synology_credential_required", message = ex.Message }, statusCode: 401); }
            catch (SynoException ex) { return Results.Json(new { code = "synology_error", message = ex.Message }, statusCode: 502); }
        });

        g.MapGet("/download", async (string server, string path, HttpContext http, IFileAccessService svc, IPopDbContext db, CancellationToken ct) =>
        {
            var userId = await UserIdAsync(http);
            if (userId is null) return Results.Unauthorized();
            if (string.IsNullOrWhiteSpace(path)) return Results.BadRequest(new { code = "path_required" });
            try
            {
                var stream = await svc.DownloadAsync(userId.Value, server, path.Trim(), ct);
                await Audit(http, db, "files.download", $"{server}:{path.Trim()}", ct);
                return Results.File(stream, "application/octet-stream", Path.GetFileName(path));
            }
            catch (SynoException ex) when (ex.IsAuthFailure) { return Results.Json(new { code = "synology_credential_required", message = ex.Message }, statusCode: 401); }
            catch (SynoException ex) { return Results.Json(new { code = "synology_error", message = ex.Message }, statusCode: 502); }
        });

        g.MapGet("/preview", async (string server, string path, HttpContext http, IFileAccessService svc, IPopDbContext db, OfficeOptions office, CancellationToken ct) =>
        {
            var userId = await UserIdAsync(http);
            if (userId is null) return Results.Unauthorized();
            if (string.IsNullOrWhiteSpace(path)) return Results.BadRequest(new { code = "path_required" });
            var ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
            if (ext is not (".docx" or ".doc" or ".pptx" or ".ppt"))
                return Results.BadRequest(new { code = "unsupported", message = "Only Word/PowerPoint files can be previewed." });
            try
            {
                await using var src = await svc.DownloadAsync(userId.Value, server, path.Trim(), ct);
                await Audit(http, db, "files.preview", $"{server}:{path.Trim()}", ct);

                var tempDir = Path.Combine(office.TempDirectory, "ipop-office");
                Directory.CreateDirectory(tempDir);
                var inFile = Path.Combine(tempDir, $"{Guid.NewGuid():N}{ext}");
                var outFile = Path.ChangeExtension(inFile, ".pdf");
                try
                {
                    await using (var fs = File.Create(inFile))
                        await src.CopyToAsync(fs, ct);
                    var result = await office.ConvertAsync(inFile, tempDir, ct);
                    if (result is not 0) return Results.Json(new { code = "conversion_failed", message = "LibreOffice could not convert this file." }, statusCode: 500);
                    if (!File.Exists(outFile)) return Results.Json(new { code = "conversion_failed" }, statusCode: 500);
                    var bytes = await File.ReadAllBytesAsync(outFile, ct);
                    return Results.File(bytes, "application/pdf", $"{Path.GetFileNameWithoutExtension(path)}.pdf");
                }
                finally
                {
                    if (File.Exists(inFile)) File.Delete(inFile);
                    if (File.Exists(outFile)) File.Delete(outFile);
                }
            }
            catch (SynoException ex) when (ex.IsAuthFailure) { return Results.Json(new { code = "synology_credential_required", message = ex.Message }, statusCode: 401); }
            catch (SynoException ex) { return Results.Json(new { code = "synology_error", message = ex.Message }, statusCode: 502); }
        });

        g.MapPost("/upload", async (HttpContext http, IFileAccessService svc, IPopDbContext db, CancellationToken ct) =>
        {
            var userId = await UserIdAsync(http);
            if (userId is null) return Results.Unauthorized();
            var form = await http.Request.ReadFormAsync(ct);
            var server = form["server"].ToString();
            var dir = form["path"].ToString();
            var file = form.Files.GetFile("file") ?? form.Files.FirstOrDefault();
            if (string.IsNullOrWhiteSpace(server) || string.IsNullOrWhiteSpace(dir) || file is null)
                return Results.BadRequest(new { code = "server_path_and_file_required" });
            try
            {
                await using var s = file.OpenReadStream();
                await svc.UploadAsync(userId.Value, server.Trim(), dir.Trim(), file.FileName, s, ct);
                await Audit(http, db, "files.upload", $"{server.Trim()}:{dir.Trim()}/{file.FileName}", ct);
                return Results.Ok();
            }
            catch (SynoException ex) when (ex.IsAuthFailure) { return Results.Json(new { code = "synology_credential_required", message = ex.Message }, statusCode: 401); }
            catch (SynoException ex) { return Results.Json(new { code = "synology_error", message = ex.Message }, statusCode: 502); }
        });

        return app;
    }

    private static async Task<Guid?> UserIdAsync(HttpContext http)
    {
        var nik = http.User.FindFirst("nik")?.Value;
        if (string.IsNullOrEmpty(nik)) return null;
        var db = http.RequestServices.GetRequiredService<IPopDbContext>();
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Nik == nik);
        return user?.Id;
    }

    private static async Task Audit(HttpContext http, IPopDbContext db, string action, string path, CancellationToken ct)
    {
        db.AuditLogs.Add(new AuditLog { ActorNik = http.User.FindFirst("nik")?.Value, Action = action, Entity = "File", EntityId = path, IpAddress = http.Connection.RemoteIpAddress?.ToString(), CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync(ct);
    }

    public record CredentialRequest(string Server, string Username, string Password);
}
