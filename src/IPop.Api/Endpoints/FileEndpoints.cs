using System.IO.Compression;
using System.Text.Json;
using IPop.Api.Rbac;
using IPop.Application.Documents;
using IPop.Application.Files;
using IPop.Application.Realtime;
using IPop.Core.Entities;
using IPop.Infrastructure.Files;
using IPop.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http.Features;
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

        // Sign in once for every plant: the same account is tried on all four NAS in parallel.
        g.MapPut("/credential/all", async (ConnectAllRequest req, HttpContext http, IFileAccessService svc, CancellationToken ct) =>
        {
            var userId = await UserIdAsync(http);
            if (userId is null) return Results.Unauthorized();
            return Results.Ok(await svc.ConnectAllAsync(userId.Value, req.Username, req.Password, ct));
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
            try
            {
                var r = await svc.ListFolderAsync(userId.Value, server, p, ct);
                // Filter out #recycle from regular browse listings
                if (!p.Contains("#recycle"))
                {
                    r = r.Where(x => !x.Name.Equals("#recycle", StringComparison.OrdinalIgnoreCase)).ToList();
                }
                await Audit(http, db, "files.list", $"{server}:{p}", ct);
                return Results.Ok(r);
            }
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

        // Word/PowerPoint -> PDF with the DevExpress Office File API (in-process; no LibreOffice install).
        g.MapGet("/preview", async (string server, string path, HttpContext http, IFileAccessService svc, IPopDbContext db,
            IWordDocumentService word, IPresentationService slides, CancellationToken ct) =>
        {
            var userId = await UserIdAsync(http);
            if (userId is null) return Results.Unauthorized();
            if (string.IsNullOrWhiteSpace(path)) return Results.BadRequest(new { code = "path_required" });
            var ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
            if (ext is not (".docx" or ".doc" or ".rtf" or ".odt" or ".pptx"))
                return Results.BadRequest(new { code = "unsupported", message = "Only Word and PowerPoint (.pptx) files can be previewed." });
            try
            {
                await using var src = await svc.DownloadAsync(userId.Value, server, path.Trim(), ct);
                await Audit(http, db, "files.preview", $"{server}:{path.Trim()}", ct);

                using var buffer = new MemoryStream();
                await src.CopyToAsync(buffer, ct);
                buffer.Position = 0;
                byte[] pdf;
                try
                {
                    pdf = ext == ".pptx"
                        ? await slides.ConvertToPdfAsync(buffer, ct)
                        : await word.ConvertToPdfAsync(buffer, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    return Results.Json(new { code = "conversion_failed", message = "This document couldn't be rendered for preview. Download it to open locally." }, statusCode: 500);
                }
                return Results.File(pdf, "application/pdf", $"{Path.GetFileNameWithoutExtension(path)}.pdf");
            }
            catch (SynoException ex) when (ex.IsAuthFailure) { return Results.Json(new { code = "synology_credential_required", message = ex.Message }, statusCode: 401); }
            catch (SynoException ex) { return Results.Json(new { code = "synology_error", message = ex.Message }, statusCode: 502); }
        });

        g.MapPost("/upload", async (HttpContext http, IFileAccessService svc, IPopDbContext db, IRealtime realtime, CancellationToken ct) =>
        {
            var userId = await UserIdAsync(http);
            if (userId is null) return Results.Unauthorized();
            http.Features.Set<IFormFeature>(new FormFeature(http.Request, new FormOptions { MultipartBodyLengthLimit = MaxUploadRequestBytes }));
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
                await FilesRealtime.PushAsync(realtime, server, [dir]);
                return Results.Ok();
            }
            catch (SynoException ex) when (ex.IsAuthFailure) { return Results.Json(new { code = "synology_credential_required", message = ex.Message }, statusCode: 401); }
            catch (SynoException ex) { return Results.Json(new { code = "synology_error", message = ex.Message }, statusCode: 502); }
        }).WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(MaxUploadRequestBytes));

        // ── Rename ─────────────────────────────────────────────────────────────
        g.MapPost("/rename", async (RenameFileRequest req, HttpContext http, IFileAccessService svc, IPopDbContext db, IRealtime realtime, CancellationToken ct) =>
        {
            var userId = await UserIdAsync(http);
            if (userId is null) return Results.Unauthorized();
            if (string.IsNullOrWhiteSpace(req.Server) || string.IsNullOrWhiteSpace(req.Path))
                return Results.BadRequest(new { code = "invalid_request", message = "Server and path are required." });
            if (IsInvalidFileName(req.Name))
                return Results.BadRequest(new { code = "invalid_name", message = "File name cannot be empty or contain illegal characters." });

            try
            {
                await svc.RenameAsync(userId.Value, req.Server.Trim(), req.Path.Trim(), req.Name.Trim(), ct);
                await Audit(http, db, "files.rename", $"{req.Server.Trim()}:{req.Path.Trim()} -> {req.Name.Trim()}", ct);
                await FilesRealtime.PushAsync(realtime, req.Server, [FilesRealtime.ParentDir(req.Path)]);
                return Results.Ok();
            }
            catch (SynoException ex) when (ex.IsAuthFailure) { return Results.Json(new { code = "synology_credential_required", message = ex.Message }, statusCode: 401); }
            catch (SynoException ex) { return Results.Json(new { code = "synology_error", message = ex.Message }, statusCode: 502); }
        });

        // ── Create Folder ──────────────────────────────────────────────────────
        g.MapPost("/folder", async (CreateFolderRequest req, HttpContext http, IFileAccessService svc, IPopDbContext db, IRealtime realtime, CancellationToken ct) =>
        {
            var userId = await UserIdAsync(http);
            if (userId is null) return Results.Unauthorized();
            if (string.IsNullOrWhiteSpace(req.Server) || string.IsNullOrWhiteSpace(req.Path))
                return Results.BadRequest(new { code = "invalid_request", message = "Server and path are required." });
            if (IsInvalidFileName(req.Name))
                return Results.BadRequest(new { code = "invalid_name", message = "Folder name cannot be empty or contain illegal characters." });

            try
            {
                await svc.CreateFolderAsync(userId.Value, req.Server.Trim(), req.Path.Trim(), req.Name.Trim(), ct);
                await Audit(http, db, "files.folder.create", $"{req.Server.Trim()}:{req.Path.Trim()}/{req.Name.Trim()}", ct);
                await FilesRealtime.PushAsync(realtime, req.Server, [req.Path]);
                return Results.Ok();
            }
            catch (SynoException ex) when (ex.IsAuthFailure) { return Results.Json(new { code = "synology_credential_required", message = ex.Message }, statusCode: 401); }
            catch (SynoException ex) { return Results.Json(new { code = "synology_error", message = ex.Message }, statusCode: 502); }
        });

        // ── Delete ─────────────────────────────────────────────────────────────
        g.MapPost("/delete", async (DeleteFilesRequest req, HttpContext http, IFileAccessService svc, IPopDbContext db, IRealtime realtime, CancellationToken ct) =>
        {
            var userId = await UserIdAsync(http);
            if (userId is null) return Results.Unauthorized();
            if (string.IsNullOrWhiteSpace(req.Server) || req.Paths is not { Length: > 0 })
                return Results.BadRequest(new { code = "invalid_request", message = "Server and paths are required." });

            try
            {
                foreach (var p in req.Paths)
                {
                    await svc.DeleteAsync(userId.Value, req.Server.Trim(), p.Trim(), ct);
                    await Audit(http, db, "files.delete", $"{req.Server.Trim()}:{p.Trim()}", ct);
                }
                await FilesRealtime.PushAsync(realtime, req.Server, req.Paths.Select(FilesRealtime.ParentDir));
                return Results.Ok();
            }
            catch (SynoException ex) when (ex.IsAuthFailure) { return Results.Json(new { code = "synology_credential_required", message = ex.Message }, statusCode: 401); }
            catch (SynoException ex) { return Results.Json(new { code = "synology_error", message = ex.Message }, statusCode: 502); }
        });

        // ── Move (Same server) ─────────────────────────────────────────────────
        g.MapPost("/move", async (MoveFilesRequest req, HttpContext http, IFileAccessService svc, IPopDbContext db, IRealtime realtime, CancellationToken ct) =>
        {
            var userId = await UserIdAsync(http);
            if (userId is null) return Results.Unauthorized();
            if (string.IsNullOrWhiteSpace(req.Server) || req.Paths is not { Length: > 0 } || string.IsNullOrWhiteSpace(req.DestDir))
                return Results.BadRequest(new { code = "invalid_request", message = "Server, paths, and destination directory are required." });

            try
            {
                await svc.CopyMoveAsync(userId.Value, req.Server.Trim(), req.Paths, req.DestDir.Trim(), removeSrc: true, req.Overwrite ?? false, ct);
                await Audit(http, db, "files.move", $"{req.Server.Trim()}:{string.Join(",", req.Paths)} -> {req.DestDir.Trim()}", ct);
                await FilesRealtime.PushAsync(realtime, req.Server, req.Paths.Select(FilesRealtime.ParentDir).Append(req.DestDir));
                return Results.Ok();
            }
            catch (SynoException ex) when (ex.IsAuthFailure) { return Results.Json(new { code = "synology_credential_required", message = ex.Message }, statusCode: 401); }
            catch (SynoException ex) { return Results.Json(new { code = "synology_error", message = ex.Message }, statusCode: 502); }
        });

        // ── Copy (Same server) ─────────────────────────────────────────────────
        g.MapPost("/copy", async (CopyFilesRequest req, HttpContext http, IFileAccessService svc, IPopDbContext db, IRealtime realtime, CancellationToken ct) =>
        {
            var userId = await UserIdAsync(http);
            if (userId is null) return Results.Unauthorized();
            if (string.IsNullOrWhiteSpace(req.Server) || req.Paths is not { Length: > 0 } || string.IsNullOrWhiteSpace(req.DestDir))
                return Results.BadRequest(new { code = "invalid_request", message = "Server, paths, and destination directory are required." });

            try
            {
                await svc.CopyMoveAsync(userId.Value, req.Server.Trim(), req.Paths, req.DestDir.Trim(), removeSrc: false, req.Overwrite ?? false, ct);
                await Audit(http, db, "files.copy", $"{req.Server.Trim()}:{string.Join(",", req.Paths)} -> {req.DestDir.Trim()}", ct);
                await FilesRealtime.PushAsync(realtime, req.Server, [req.DestDir]);
                return Results.Ok();
            }
            catch (SynoException ex) when (ex.IsAuthFailure) { return Results.Json(new { code = "synology_credential_required", message = ex.Message }, statusCode: 401); }
            catch (SynoException ex) { return Results.Json(new { code = "synology_error", message = ex.Message }, statusCode: 502); }
        });

        // ── Transfer across servers ───────────────────────────────────────────
        g.MapPost("/transfer", async (TransferFilesRequest req, HttpContext http, IFileAccessService svc, IPopDbContext db, CancellationToken ct) =>
        {
            var userId = await UserIdAsync(http);
            if (userId is null) return Results.Unauthorized();
            if (string.IsNullOrWhiteSpace(req.SourceServer) || string.IsNullOrWhiteSpace(req.DestServer) || req.Paths is not { Length: > 0 })
                return Results.BadRequest(new { code = "invalid_request", message = "Source, destination, and paths are required." });

            // Validate caller holds credentials for destination server
            var (hasDest, _) = await svc.GetCredentialAsync(userId.Value, req.DestServer, ct);
            if (!hasDest)
            {
                return Results.Json(new { code = "synology_credential_required", message = $"Connect to {req.DestServer} first." }, statusCode: 401);
            }

            var transferItems = req.Paths.Select(p => new FileTransferItem(p, Path.GetFileName(p), 0)).ToList();
            var op = new FileOperation
            {
                Id = Guid.NewGuid(),
                UserId = userId.Value,
                Kind = req.Mode?.ToLowerInvariant() is "move" ? "move" : "copy",
                SourceServer = req.SourceServer.Trim().ToLowerInvariant(),
                DestServer = req.DestServer.Trim().ToLowerInvariant(),
                DestDir = req.DestDir?.Trim(),
                ItemsJson = JsonSerializer.Serialize(transferItems),
                Status = "Queued",
                TotalFiles = transferItems.Count,
                CompletedFiles = 0,
                FailedFiles = 0,
                BytesTransferred = 0,
                CreatedAt = DateTime.UtcNow
            };

            db.FileOperations.Add(op);
            await Audit(http, db, "files.transfer", $"{req.SourceServer}->{req.DestServer}:{op.TotalFiles} files", ct);
            await db.SaveChangesAsync(ct);

            return Results.Ok(new { operationId = op.Id });
        });

        g.MapGet("/operations", async (HttpContext http, IPopDbContext db, CancellationToken ct) =>
        {
            var userId = await UserIdAsync(http);
            if (userId is null) return Results.Unauthorized();

            var ops = await db.FileOperations.AsNoTracking()
                .Where(o => o.UserId == userId.Value)
                .OrderByDescending(o => o.CreatedAt)
                .Take(20)
                .ToListAsync(ct);
            return Results.Ok(ops);
        });

        g.MapGet("/operations/{id:guid}", async (Guid id, HttpContext http, IPopDbContext db, CancellationToken ct) =>
        {
            var userId = await UserIdAsync(http);
            if (userId is null) return Results.Unauthorized();

            var op = await db.FileOperations.AsNoTracking()
                .FirstOrDefaultAsync(o => o.Id == id && o.UserId == userId.Value, ct);
            return op is not null ? Results.Ok(op) : Results.NotFound();
        });

        // ── Recycle Bin ────────────────────────────────────────────────────────
        g.MapGet("/recycle", async (string server, string? path, HttpContext http, IFileAccessService svc, IPopDbContext db, CancellationToken ct) =>
        {
            var userId = await UserIdAsync(http);
            if (userId is null) return Results.Unauthorized();
            if (string.IsNullOrWhiteSpace(server)) return Results.BadRequest(new { code = "server_required" });

            try
            {
                // If path is specified, browse that recycle folder, else browse root recycle bins of shares
                var targetPath = string.IsNullOrWhiteSpace(path) ? "/" : path.Trim();
                if (targetPath == "/")
                {
                    var shares = await svc.ListSharesAsync(userId.Value, server, ct);
                    // The share listing above has already opened the NAS session, so these run
                    // side by side on it instead of one share after another.
                    var bins = await Task.WhenAll(shares.Select(async sh =>
                    {
                        try { return await svc.ListFolderAsync(userId.Value, server, $"{sh.TrimEnd('/')}/#recycle", ct); }
                        catch (SynoException) { return (IReadOnlyList<SynoItem>)[]; /* share has no #recycle */ }
                    }));
                    return Results.Ok(bins.SelectMany(b => b).ToList());
                }
                else
                {
                    var items = await svc.ListFolderAsync(userId.Value, server, targetPath, ct);
                    return Results.Ok(items);
                }
            }
            catch (SynoException ex) when (ex.IsAuthFailure) { return Results.Json(new { code = "synology_credential_required", message = ex.Message }, statusCode: 401); }
            catch (SynoException ex) { return Results.Json(new { code = "synology_error", message = ex.Message }, statusCode: 502); }
        });

        g.MapPost("/recycle/restore", async (RestoreRecycleRequest req, HttpContext http, IFileAccessService svc, IPopDbContext db, IRealtime realtime, CancellationToken ct) =>
        {
            var userId = await UserIdAsync(http);
            if (userId is null) return Results.Unauthorized();
            if (string.IsNullOrWhiteSpace(req.Server) || req.Paths is not { Length: > 0 })
                return Results.BadRequest(new { code = "invalid_request", message = "Server and paths are required." });

            var dest = string.IsNullOrWhiteSpace(req.DestDir) ? "/" : req.DestDir.Trim();
            try
            {
                if (dest == "/" || string.IsNullOrEmpty(dest))
                {
                    var groups = req.Paths.GroupBy(p =>
                    {
                        var idx = p.IndexOf("/#recycle", StringComparison.OrdinalIgnoreCase);
                        return idx > 0 ? p[..idx] : "/";
                    });

                    foreach (var group in groups)
                    {
                        await svc.CopyMoveAsync(userId.Value, req.Server.Trim(), group.ToArray(), group.Key, removeSrc: true, true, ct);
                    }
                }
                else
                {
                    await svc.CopyMoveAsync(userId.Value, req.Server.Trim(), req.Paths, dest, removeSrc: true, true, ct);
                }
                await Audit(http, db, "files.recycle.restore", $"{req.Server.Trim()}:{string.Join(",", req.Paths)} -> {dest}", ct);
                // The recycle folders the items left, plus wherever they landed.
                var restoredInto = dest == "/"
                    ? req.Paths.Select(p => { var i = p.IndexOf("/#recycle", StringComparison.OrdinalIgnoreCase); return i > 0 ? p[..i] : "/"; })
                    : [dest];
                await FilesRealtime.PushAsync(realtime, req.Server, req.Paths.Select(FilesRealtime.ParentDir).Concat(restoredInto));
                return Results.Ok();
            }
            catch (SynoException ex) when (ex.IsAuthFailure) { return Results.Json(new { code = "synology_credential_required", message = ex.Message }, statusCode: 401); }
            catch (SynoException ex) { return Results.Json(new { code = "synology_error", message = ex.Message }, statusCode: 502); }
        });

        g.MapPost("/recycle/empty", async (EmptyRecycleRequest req, HttpContext http, IFileAccessService svc, IPopDbContext db, IRealtime realtime, CancellationToken ct) =>
        {
            var userId = await UserIdAsync(http);
            if (userId is null) return Results.Unauthorized();
            if (string.IsNullOrWhiteSpace(req.Server)) return Results.BadRequest(new { code = "server_required" });

            try
            {
                var shares = await svc.ListSharesAsync(userId.Value, req.Server.Trim(), ct);
                var emptied = new List<string>();
                foreach (var sh in shares)
                {
                    try
                    {
                        var recyclePath = $"{sh.TrimEnd('/')}/#recycle";
                        var items = await svc.ListFolderAsync(userId.Value, req.Server.Trim(), recyclePath, ct);
                        foreach (var it in items)
                        {
                            await svc.DeleteAsync(userId.Value, req.Server.Trim(), it.Path, ct);
                        }
                        if (items.Count > 0) emptied.Add(recyclePath);
                    }
                    catch { }
                }
                await Audit(http, db, "files.recycle.empty", $"{req.Server.Trim()}:#recycle", ct);
                await FilesRealtime.PushAsync(realtime, req.Server, emptied);
                return Results.Ok();
            }
            catch (SynoException ex) when (ex.IsAuthFailure) { return Results.Json(new { code = "synology_credential_required", message = ex.Message }, statusCode: 401); }
            catch (SynoException ex) { return Results.Json(new { code = "synology_error", message = ex.Message }, statusCode: 502); }
        });

        // ── Storage Info ───────────────────────────────────────────────────────
        g.MapGet("/storage", async (string? server, HttpContext http, IFileAccessService svc, CancellationToken ct) =>
        {
            var userId = await UserIdAsync(http);
            if (userId is null) return Results.Unauthorized();
            var s = string.IsNullOrWhiteSpace(server) ? "spj" : server.Trim().ToLowerInvariant();
            try
            {
                var info = await svc.GetStorageInfoAsync(userId.Value, s, ct);
                return Results.Ok(info);
            }
            catch (SynoException ex) when (ex.IsAuthFailure) { return Results.Json(new { code = "synology_credential_required", message = ex.Message }, statusCode: 401); }
            catch (SynoException ex) { return Results.Json(new { code = "synology_error", message = ex.Message }, statusCode: 502); }
        });

        // ── Search ─────────────────────────────────────────────────────────────
        // FileStation search is a task on the NAS: start returns a task id, the page polls list
        // until Finished (results stream in as the NAS walks the tree), delete cancels it.
        g.MapPost("/search", async (SearchStartRequest req, HttpContext http, IFileAccessService svc, CancellationToken ct) =>
        {
            var userId = await UserIdAsync(http);
            if (userId is null) return Results.Unauthorized();
            var pattern = req.Pattern?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(req.Server) || pattern.Length < 2)
                return Results.BadRequest(new { code = "server_and_pattern_required" });
            try
            {
                var paths = req.Paths is { Length: > 0 } ? req.Paths : (await svc.ListSharesAsync(userId.Value, req.Server, ct)).ToArray();
                if (paths.Length == 0) return Results.Ok(new { taskId = (string?)null });
                var taskId = await svc.SearchStartAsync(userId.Value, req.Server, paths, pattern, ct);
                return Results.Ok(new { taskId });
            }
            catch (SynoException ex) when (ex.IsAuthFailure) { return Results.Json(new { code = "synology_credential_required", message = ex.Message }, statusCode: 401); }
            catch (SynoException ex) { return Results.Json(new { code = "synology_error", message = ex.Message }, statusCode: 502); }
        });

        g.MapGet("/search", async (string server, string taskId, int? offset, HttpContext http, IFileAccessService svc, CancellationToken ct) =>
        {
            var userId = await UserIdAsync(http);
            if (userId is null) return Results.Unauthorized();
            try
            {
                var page = await svc.SearchListAsync(userId.Value, server, taskId, Math.Max(0, offset ?? 0), 200, ct);
                var items = page.Items.Where(i => !i.Path.Contains("/#recycle", StringComparison.OrdinalIgnoreCase)).ToList();
                return Results.Ok(new { items, page.Total, page.Finished });
            }
            catch (SynoException ex) when (ex.IsAuthFailure) { return Results.Json(new { code = "synology_credential_required", message = ex.Message }, statusCode: 401); }
            catch (SynoException ex) { return Results.Json(new { code = "synology_error", message = ex.Message }, statusCode: 502); }
        });

        g.MapDelete("/search", async (string server, string taskId, HttpContext http, IFileAccessService svc, CancellationToken ct) =>
        {
            var userId = await UserIdAsync(http);
            if (userId is null) return Results.Unauthorized();
            await svc.SearchStopAsync(userId.Value, server, taskId, ct);
            return Results.NoContent();
        });

        // ── Personal places (stars, recents) ───────────────────────────────────
        // Per-user JSON blob in AppSettings so stars follow the person across browsers and sites
        // without a schema change. The page owns the shape; the API only bounds its size.
        g.MapGet("/prefs", async (HttpContext http, IPopDbContext db, CancellationToken ct) =>
        {
            var userId = await UserIdAsync(http);
            if (userId is null) return Results.Unauthorized();
            var row = await db.AppSettings.AsNoTracking().FirstOrDefaultAsync(x => x.Key == PrefsKey(userId.Value), ct);
            return Results.Content(row?.ValueJson ?? "{}", "application/json");
        });

        g.MapPut("/prefs", async (JsonElement body, HttpContext http, IPopDbContext db, CancellationToken ct) =>
        {
            var userId = await UserIdAsync(http);
            if (userId is null) return Results.Unauthorized();
            if (body.ValueKind != JsonValueKind.Object) return Results.BadRequest(new { code = "object_required" });
            var json = body.GetRawText();
            if (json.Length > 64 * 1024) return Results.BadRequest(new { code = "prefs_too_large" });
            var key = PrefsKey(userId.Value);
            var row = await db.AppSettings.FirstOrDefaultAsync(x => x.Key == key, ct);
            if (row is null) db.AppSettings.Add(row = new AppSetting { Key = key, ValueJson = json });
            row.ValueJson = json;
            row.UpdatedAt = DateTime.UtcNow;
            row.UpdatedByNik = http.User.FindFirst("nik")?.Value;
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        // ── Sharing Links ──────────────────────────────────────────────────────
        g.MapGet("/sharing", async (string? server, string? path, HttpContext http, IFileAccessService svc, CancellationToken ct) =>
        {
            var userId = await UserIdAsync(http);
            if (userId is null) return Results.Unauthorized();
            if (string.IsNullOrWhiteSpace(server)) return Results.BadRequest(new { code = "server_required" });

            try
            {
                var links = await svc.ListSharingLinksAsync(userId.Value, server.Trim(), path, ct);
                return Results.Ok(links);
            }
            catch (SynoException ex) when (ex.IsAuthFailure) { return Results.Json(new { code = "synology_credential_required", message = ex.Message }, statusCode: 401); }
            catch (SynoException ex) { return Results.Json(new { code = "synology_error", message = ex.Message }, statusCode: 502); }
        });

        g.MapPost("/sharing", async (CreateShareLinkRequest req, HttpContext http, IFileAccessService svc, IPopDbContext db, IRealtime realtime, CancellationToken ct) =>
        {
            var userId = await UserIdAsync(http);
            if (userId is null) return Results.Unauthorized();
            if (string.IsNullOrWhiteSpace(req.Server) || string.IsNullOrWhiteSpace(req.Path))
                return Results.BadRequest(new { code = "invalid_request", message = "Server and path are required." });

            try
            {
                var link = await svc.CreateSharingLinkAsync(userId.Value, req.Server.Trim(), req.Path.Trim(), req.Password, req.ExpiresInDays ?? 14, ct);
                await Audit(http, db, "files.share.create", $"{req.Server.Trim()}:{req.Path.Trim()} -> {link.Url}", ct);
                await FilesRealtime.PushAsync(realtime, req.Server, [FilesRealtime.ParentDir(req.Path)]);
                return Results.Ok(link);
            }
            catch (SynoException ex) when (ex.IsAuthFailure) { return Results.Json(new { code = "synology_credential_required", message = ex.Message }, statusCode: 401); }
            catch (SynoException ex) { return Results.Json(new { code = "synology_error", message = ex.Message }, statusCode: 502); }
        });

        g.MapDelete("/sharing", async (string server, string shareId, HttpContext http, IFileAccessService svc, IPopDbContext db, IRealtime realtime, CancellationToken ct) =>
        {
            var userId = await UserIdAsync(http);
            if (userId is null) return Results.Unauthorized();
            if (string.IsNullOrWhiteSpace(server) || string.IsNullOrWhiteSpace(shareId))
                return Results.BadRequest(new { code = "invalid_request", message = "Server and shareId are required." });

            try
            {
                await svc.DeleteSharingLinkAsync(userId.Value, server.Trim(), shareId.Trim(), ct);
                await Audit(http, db, "files.share.delete", $"{server.Trim()}:{shareId.Trim()}", ct);
                // Only the link id is known here, not its folder: the server-wide scope makes
                // every open view of this server re-check its share badges.
                await FilesRealtime.PushAsync(realtime, server, [FilesRealtime.Wildcard]);
                return Results.Ok();
            }
            catch (SynoException ex) when (ex.IsAuthFailure) { return Results.Json(new { code = "synology_credential_required", message = ex.Message }, statusCode: 401); }
            catch (SynoException ex) { return Results.Json(new { code = "synology_error", message = ex.Message }, statusCode: 502); }
        });

        // ── Bulk Download (Streamed Zip) ───────────────────────────────────────
        g.MapPost("/bulk-download", async (BulkDownloadRequest req, HttpContext http, IFileAccessService svc, IPopDbContext db, CancellationToken ct) =>
        {
            var userId = await UserIdAsync(http);
            if (userId is null) return Results.Unauthorized();
            if (string.IsNullOrWhiteSpace(req.Server) || req.Paths is not { Length: > 0 })
                return Results.BadRequest(new { code = "invalid_request", message = "Server and paths are required." });

            var memStream = new MemoryStream();
            using (var zip = new ZipArchive(memStream, ZipArchiveMode.Create, leaveOpen: true))
            {
                foreach (var p in req.Paths)
                {
                    try
                    {
                        await using var fileStream = await svc.DownloadAsync(userId.Value, req.Server.Trim(), p.Trim(), ct);
                        var entryName = Path.GetFileName(p);
                        var entry = zip.CreateEntry(entryName, CompressionLevel.Fastest);
                        await using var entryStream = entry.Open();
                        await fileStream.CopyToAsync(entryStream, ct);
                    }
                    catch { /* best-effort skip unreadable file in batch */ }
                }
            }

            memStream.Position = 0;
            await Audit(http, db, "files.bulk-download", $"{req.Server.Trim()}:{req.Paths.Length} items", ct);
            return Results.File(memStream, "application/zip", $"ipop_{req.Server.Trim()}_{DateTime.UtcNow:yyyyMMdd-HHmm}.zip");
        });

        return app;
    }

    private static bool IsInvalidFileName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 255) return true;
        return name.IndexOfAny(['\\', '/', ':', '*', '?', '"', '<', '>', '|']) >= 0;
    }

    private static async Task<Guid?> UserIdAsync(HttpContext http)
    {
        var nik = http.User.FindFirst("nik")?.Value;
        if (string.IsNullOrEmpty(nik)) return null;
        var db = http.RequestServices.GetRequiredService<IPopDbContext>();
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Nik == nik);
        return user?.Id;
    }

    private static string PrefsKey(Guid userId) => $"files.prefs:{userId:N}";

    // Matches the page's 100 MB per-file limit, plus room for the multipart envelope.
    private const long MaxUploadRequestBytes = 100L * 1024 * 1024 + 1024 * 1024;

    private static async Task Audit(HttpContext http, IPopDbContext db, string action, string path, CancellationToken ct)
    {
        db.AuditLogs.Add(new AuditLog { ActorNik = http.User.FindFirst("nik")?.Value, Action = action, Entity = "File", EntityId = path, IpAddress = http.Connection.RemoteIpAddress?.ToString(), CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync(ct);
    }

    public record CredentialRequest(string Server, string Username, string Password);
    public record ConnectAllRequest(string Username, string Password);
    public record SearchStartRequest(string Server, string[]? Paths, string? Pattern);
    public record RenameFileRequest(string Server, string Path, string Name);
    public record CreateFolderRequest(string Server, string Path, string Name);
    public record DeleteFilesRequest(string Server, string[] Paths);
    public record MoveFilesRequest(string Server, string[] Paths, string DestDir, bool? Overwrite = null);
    public record CopyFilesRequest(string Server, string[] Paths, string DestDir, bool? Overwrite = null);
    public record TransferFilesRequest(string SourceServer, string[] Paths, string DestServer, string? DestDir, string Mode);
    public record CreateShareLinkRequest(string Server, string Path, int? ExpiresInDays, string? Password);
    public record RestoreRecycleRequest(string Server, string[] Paths, string? DestDir);
    public record EmptyRecycleRequest(string Server);
    public record BulkDownloadRequest(string Server, string[] Paths);
}
