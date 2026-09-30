using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using IPop.Api.Rbac;
using IPop.Application.Files;
using IPop.Core.Entities;
using IPop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IPop.Api.Endpoints;

public static class OnlyOfficeEndpoints
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> _docVersions = new();
    public static IEndpointRouteBuilder MapOnlyOffice(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/onlyoffice");

        // 1. Endpoint config: Diakses oleh browser pengguna (terproteksi auth iPop)
        g.MapGet("/config", async (string server, string path, HttpContext http, IConfiguration cfg, IPopDbContext db, IFileAccessService fileSvc, CancellationToken ct) =>
        {
            var nik = http.User.FindFirst("nik")?.Value;
            if (string.IsNullOrWhiteSpace(nik)) return Results.Unauthorized();
            var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Nik == nik, ct);
            if (user is null) return Results.Unauthorized();

            if (string.IsNullOrWhiteSpace(server) || string.IsNullOrWhiteSpace(path))
                return Results.BadRequest(new { code = "server_and_path_required" });

            // Validasi kredensial Synology user sebelum membuka editor kolaborasi
            var cred = await fileSvc.GetCredentialAsync(user.Id, server.Trim().ToLowerInvariant(), ct);
            if (!cred.Found || cred.Credential is null || !cred.Credential.HasCredential || cred.Credential.IsInvalid)
            {
                return Results.Json(new {
                    code = "synology_credential_required",
                    message = $"Synology credentials for server {server.ToUpperInvariant()} are not connected or invalid. Please connect your Synology account first in Files."
                }, statusCode: 401);
            }

            var ext = Path.GetExtension(path).ToLowerInvariant();
            var docType = GetDocumentType(ext);
            if (docType is null)
                return Results.BadRequest(new { code = "unsupported_office_format", message = "Document format is not supported by ONLYOFFICE." });

            var serverUrl = cfg["OnlyOffice:ServerUrl"] ?? "http://localhost:8088";
            var configuredPublicUrl = cfg["OnlyOffice:PublicServerUrl"];
            string publicServerUrl;
            if (!string.IsNullOrWhiteSpace(configuredPublicUrl) && !configuredPublicUrl.Contains("localhost"))
            {
                publicServerUrl = configuredPublicUrl;
            }
            else
            {
                var reqHost = http.Request.Host.Host;
                var onlyOfficePort = 8088;
                if (Uri.TryCreate(serverUrl, UriKind.Absolute, out var parsedUri) && parsedUri.Port > 0)
                {
                    onlyOfficePort = parsedUri.Port;
                }
                publicServerUrl = $"{http.Request.Scheme}://{reqHost}:{onlyOfficePort}";
            }
            var internalApiUrl = cfg["OnlyOffice:InternalApiUrl"] ?? "http://host.docker.internal:5083";

            var cleanPath = path.Trim();
            var token = CreateFileToken(user.Id, server, cleanPath);
            var key = GenerateDocumentKey(server, cleanPath);

            var downloadUrl = $"{internalApiUrl}/api/onlyoffice/file?server={Uri.EscapeDataString(server)}&path={Uri.EscapeDataString(cleanPath)}&userId={user.Id}&token={token}";
            var callbackUrl = $"{internalApiUrl}/api/onlyoffice/callback?server={Uri.EscapeDataString(server)}&path={Uri.EscapeDataString(cleanPath)}&userId={user.Id}&token={token}";

            var config = new
            {
                documentType = docType,
                document = new
                {
                    title = Path.GetFileName(cleanPath),
                    url = downloadUrl,
                    fileType = ext.TrimStart('.'),
                    key = key,
                    permissions = new
                    {
                        edit = true,
                        download = true,
                        print = true,
                        comment = true,
                        review = true,
                        fillForms = true
                    }
                },
                editorConfig = new
                {
                    mode = "edit",
                    lang = "en",
                    coEditing = new
                    {
                        mode = "fast",
                        change = true
                    },
                    user = new
                    {
                        id = user.Id.ToString(),
                        name = string.IsNullOrWhiteSpace(user.Name) ? user.Nik : user.Name
                    },
                    callbackUrl = callbackUrl,
                    customization = new
                    {
                        autosave = true,
                        forcesave = true,
                        comments = true,
                        chat = true,
                        zoom = 100,
                        compactHeader = false,
                        toolbarNoTabs = false
                    }
                },
                serverUrl = publicServerUrl
            };

            return Results.Ok(config);
        }).RequireAuthorization().RequirePermission("files.read");

        // 2. Endpoint file: Diunduh oleh ONLYOFFICE server (tanpa cookie, validasi token)
        g.MapGet("/file", async (string server, string path, Guid userId, string token, IFileAccessService svc, CancellationToken ct) =>
        {
            if (!ValidateFileToken(userId, server, path, token))
                return Results.Unauthorized();

            try
            {
                var stream = await svc.DownloadAsync(userId, server, path.Trim(), ct);
                var filename = Path.GetFileName(path);
                var ext = Path.GetExtension(path).ToLowerInvariant();
                var mime = GetMimeType(ext);
                return Results.File(stream, mime, filename);
            }
            catch (SynoException ex) when (ex.IsAuthFailure) { return Results.Json(new { code = "synology_credential_required", message = ex.Message }, statusCode: 401); }
            catch (SynoException ex) { return Results.Json(new { code = "synology_error", message = ex.Message }, statusCode: 502); }
        });

        // 3. Endpoint callback: Dipanggil oleh ONLYOFFICE server saat autosave / selesai edit
        g.MapPost("/callback", async (string server, string path, Guid userId, string token, HttpContext http, IFileAccessService svc, HttpClient httpClient, IPopDbContext db, ILoggerFactory loggerFactory, CancellationToken ct) =>
        {
            var logger = loggerFactory.CreateLogger("OnlyOfficeEndpoints");
            if (!ValidateFileToken(userId, server, path, token))
                return Results.Unauthorized();

            CallbackPayload? payload;
            try
            {
                payload = await http.Request.ReadFromJsonAsync<CallbackPayload>(cancellationToken: ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning("OnlyOffice callback parse failed: {Error}", ex.Message);
                return Results.Json(new { error = 1 });
            }

            if (payload is null)
                return Results.Json(new { error = 1 });

            // Status 2 = Dokumen siap disimpan (user selesai edit dan menutup dokumen)
            // Status 6 = Dokumen sedang diedit, tetapi user memicu forcesave atau autosave
            if (payload.Status is 2 or 6)
            {
                if (string.IsNullOrWhiteSpace(payload.Url))
                {
                    logger.LogWarning("OnlyOffice callback status {Status} but URL is empty", payload.Status);
                    return Results.Json(new { error = 1 });
                }

                try
                {
                    // Unduh dokumen editan terbaru dari server ONLYOFFICE
                    using var response = await httpClient.GetAsync(payload.Url, HttpCompletionOption.ResponseHeadersRead, ct);
                    if (!response.IsSuccessStatusCode)
                    {
                        logger.LogError("Gagal mengunduh dokumen dari OnlyOffice URL: {StatusCode}", response.StatusCode);
                        return Results.Json(new { error = 1 });
                    }

                    await using var fileStream = await response.Content.ReadAsStreamAsync(ct);
                    var dir = Path.GetDirectoryName(path.Trim())?.Replace('\\', '/') ?? "/";
                    if (string.IsNullOrWhiteSpace(dir)) dir = "/";
                    var filename = Path.GetFileName(path.Trim());

                    // Unggah versi editan terbaru kembali ke Synology NAS
                    await svc.UploadAsync(userId, server, dir, filename, fileStream, ct);

                    // Catat riwayat audit
                    var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, ct);
                    db.AuditLogs.Add(new AuditLog
                    {
                        ActorNik = user?.Nik,
                        Action = "files.onlyoffice_save",
                        Entity = "File",
                        EntityId = $"{server}:{path}",
                        CreatedAt = DateTime.UtcNow
                    });
                    await db.SaveChangesAsync(ct);

                    logger.LogInformation("Dokumen {Path} berhasil disimpan kembali ke Synology NAS via OnlyOffice.", path);

                    // Sesi dokumen ditutup dan disimpan penuh -> increment versi agar sesi berikutnya memuat versi terbaru dari Synology
                    if (payload.Status == 2)
                    {
                        InvalidateDocumentSession(server, path);
                        logger.LogInformation("Sesi dokumen {Path} diinvalidasi untuk pembaruan cache.", path);
                    }
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Gagal mengunggah dokumen editan OnlyOffice ke Synology: {Error}", ex.Message);
                    return Results.Json(new { error = 1 });
                }
            }

            // ONLYOFFICE mengharapkan { error: 0 } sebagai respons sukses
            return Results.Json(new { error = 0 });
        });

        return app;
    }

    private static string? GetDocumentType(string ext) => ext switch
    {
        ".docx" or ".doc" or ".rtf" or ".txt" or ".odt" => "word",
        ".xlsx" or ".xls" or ".csv" or ".ods" => "cell",
        ".pptx" or ".ppt" or ".odp" => "slide",
        _ => null
    };

    private static string GetMimeType(string ext) => ext switch
    {
        ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        ".doc" => "application/msword",
        ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        ".xls" => "application/vnd.ms-excel",
        ".pptx" => "application/vnd.openxmlformats-officedocument.presentationml.presentation",
        ".ppt" => "application/vnd.ms-powerpoint",
        ".csv" => "text/csv",
        _ => "application/octet-stream"
    };

    private static string GenerateDocumentKey(string server, string path)
    {
        var docId = $"{server}:{path}".ToLowerInvariant();
        var version = _docVersions.GetOrAdd(docId, 1);
        var raw = $"{docId}:v{version}";
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(bytes)[..20];
    }

    private static void InvalidateDocumentSession(string server, string path)
    {
        var docId = $"{server}:{path}".ToLowerInvariant();
        _docVersions.AddOrUpdate(docId, 2, (_, v) => v + 1);
    }

    private const string SecretSalt = "ipop-onlyoffice-session-salt-2026";

    private static string CreateFileToken(Guid userId, string server, string path)
    {
        var raw = $"{userId:N}:{server}:{path}:{SecretSalt}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(hash)[..16];
    }

    private static bool ValidateFileToken(Guid userId, string server, string path, string token)
    {
        if (string.IsNullOrWhiteSpace(token)) return false;
        var expected = CreateFileToken(userId, server, path);
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(token));
    }

    private record CallbackPayload(
        [property: JsonPropertyName("status")] int Status,
        [property: JsonPropertyName("url")] string? Url,
        [property: JsonPropertyName("users")] List<string>? Users,
        [property: JsonPropertyName("key")] string? Key
    );
}
