using System.Security.Cryptography;
using IPop.Application.Attachments;
using IPop.Application.Files;
using IPop.Core.Entities;
using IPop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace IPop.Infrastructure.Attachments;

/// Bound from the "Attachments" config section. Files are written with this service account,
/// never the uploader's own NAS credential (a user's /home is not shared with anyone else).
public sealed class AttachmentOptions
{
    public string Server { get; set; } = "spj";
    public string Share { get; set; } = "ipop";
    public string? Username { get; set; }
    public string? Password { get; set; }
    public long MaxBytes { get; set; } = 50L * 1024 * 1024;

    public static AttachmentOptions Bind(IConfiguration cfg) =>
        cfg.GetSection("Attachments").Get<AttachmentOptions>() ?? new AttachmentOptions();
}

/// Singleton: the service-account session id outlives a request scope.
public sealed class AttachmentSession
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private string? _sid;

    public async Task<string> GetAsync(ISynologyGateway gw, AttachmentOptions opt, bool refresh, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            if (_sid is null || refresh)
            {
                if (string.IsNullOrEmpty(opt.Username))
                    throw new SynoException("Attachments service account is not configured", authFailure: true);
                _sid = await gw.AuthAsync(opt.Server, opt.Username, opt.Password ?? "", ct);
            }
            return _sid;
        }
        finally { _lock.Release(); }
    }
}

public sealed class SynologyAttachmentStore(
    IPopDbContext db, ISynologyGateway gw, AttachmentOptions opt, AttachmentSession session) : IAttachmentStore
{
    private static readonly HashSet<string> BlockedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".dll", ".com", ".scr", ".msi", ".msp", ".bat", ".cmd", ".ps1", ".vbs", ".vbe",
        ".js", ".jse", ".wsf", ".wsh", ".hta", ".cpl", ".jar", ".lnk", ".reg", ".sh", ".app", ".dmg",
    };

    public async Task<StoredAttachment> PutAsync(string area, string fileName, string contentType,
        Stream content, Guid uploaderId, CancellationToken ct)
    {
        if (!AttachmentAreas.All.Contains(area))
            throw new ArgumentException($"Unknown attachment area '{area}'", nameof(area));

        var buffer = await ReadLimitedAsync(content, opt.MaxBytes, ct);
        if (buffer.Length == 0) throw new AttachmentRejectedException("empty", "File is empty.");
        var ext = SafeExtension(fileName);
        if (BlockedExtensions.Contains(ext) || LooksExecutable(buffer))
            throw new AttachmentRejectedException("blocked_type", "Executable files are not allowed.");

        var id = Guid.NewGuid();
        var now = DateTime.UtcNow;
        var dir = $"/{opt.Share}/ipop-attachments/{area}/{now:yyyy}/{now:MM}";
        var diskName = id.ToString("N") + ext;

        await WithSessionAsync(async sid =>
        {
            using var ms = new MemoryStream(buffer, writable: false);
            await gw.UploadAsync(opt.Server, sid, dir, diskName, ms, ct);
            return true;
        }, ct);

        var row = new Attachment
        {
            Id = id, Area = area,
            FileName = Path.GetFileName(fileName).Trim() is { Length: > 0 } n ? Truncate(n, 255) : "file" + ext,
            ContentType = string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : Truncate(contentType, 127),
            Size = buffer.Length,
            Sha256 = Convert.ToHexStringLower(SHA256.HashData(buffer)),
            StoragePath = $"{dir}/{diskName}",
            UploaderId = uploaderId, CreatedAt = now,
        };
        db.Attachments.Add(row);
        await db.SaveChangesAsync(ct);
        return ToDto(row);
    }

    public async Task<(StoredAttachment Meta, Stream Content)?> OpenAsync(Guid id, CancellationToken ct)
    {
        var row = await db.Attachments.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id && a.DeletedAt == null, ct);
        if (row is null) return null;
        var stream = await WithSessionAsync(sid => gw.DownloadAsync(opt.Server, sid, row.StoragePath, ct), ct);
        return (ToDto(row), stream);
    }

    /// Soft-deletes the row and removes the file. The row is marked first so a NAS failure
    /// leaves an orphan file (harmless) rather than a live link to a missing file.
    public async Task DeleteAsync(Guid id, CancellationToken ct)
    {
        var row = await db.Attachments.FirstOrDefaultAsync(a => a.Id == id && a.DeletedAt == null, ct);
        if (row is null) return;
        row.DeletedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        await WithSessionAsync(async sid => { await gw.DeleteAsync(opt.Server, sid, row.StoragePath, ct); return true; }, ct);
    }

    private async Task<T> WithSessionAsync<T>(Func<string, Task<T>> call, CancellationToken ct)
    {
        var sid = await session.GetAsync(gw, opt, refresh: false, ct);
        try { return await call(sid); }
        catch (SynoException ex) when (ex.IsSessionExpired)
        {
            sid = await session.GetAsync(gw, opt, refresh: true, ct);
            return await call(sid);
        }
    }

    private static async Task<byte[]> ReadLimitedAsync(Stream s, long max, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await s.ReadAsync(chunk, ct)) > 0)
        {
            if (ms.Length + read > max)
                throw new AttachmentRejectedException("too_large", $"File exceeds {max / (1024 * 1024)} MB.");
            ms.Write(chunk, 0, read);
        }
        return ms.ToArray();
    }

    /// Magic numbers for PE (MZ), ELF and Mach-O — catches renamed executables.
    internal static bool LooksExecutable(ReadOnlySpan<byte> b) =>
        (b.Length >= 2 && b[0] == 'M' && b[1] == 'Z')
        || (b.Length >= 4 && b[0] == 0x7F && b[1] == 'E' && b[2] == 'L' && b[3] == 'F')
        || (b.Length >= 4 && ((b[0] == 0xCF || b[0] == 0xCE) && b[1] == 0xFA && b[2] == 0xED && b[3] == 0xFE))
        || (b.Length >= 4 && b[0] == 0xFE && b[1] == 0xED && b[2] == 0xFA && (b[3] == 0xCE || b[3] == 0xCF));

    /// Only a short alphanumeric extension from the user's name reaches the NAS path.
    private static string SafeExtension(string fileName)
    {
        var ext = Path.GetExtension(fileName);
        return ext.Length is > 1 and <= 10 && ext.Skip(1).All(char.IsAsciiLetterOrDigit) ? ext.ToLowerInvariant() : "";
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];

    private static StoredAttachment ToDto(Attachment a) => new(a.Id, a.FileName, a.ContentType, a.Size, a.Sha256);
}
