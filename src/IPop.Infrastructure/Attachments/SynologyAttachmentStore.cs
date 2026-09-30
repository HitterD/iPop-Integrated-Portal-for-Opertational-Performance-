using System.Security.Cryptography;
using IPop.Application.Attachments;
using IPop.Application.Files;
using IPop.Core.Entities;
using IPop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace IPop.Infrastructure.Attachments;

/// Files are written with a service account, never the uploader's own NAS credential (a user's
/// /home is not shared with anyone else). An explicit "Attachments" section wins; without one the
/// store reuses the Synology:ServiceAccount that chat and mail already upload with, and writes each
/// area under Synology:{Area}Folder (e.g. Synology:NewsFolder), defaulting to a sibling of
/// Synology:ChatFolder. When the NAS is not configured or fails, LocalRoot (if set) keeps uploads working.
public sealed class AttachmentOptions
{
    /// One limit for every area; the web client caps at the same size (Kestrel's 30 MB default body fits it).
    public const long DefaultMaxBytes = 25L * 1024 * 1024;
    public const string LocalPrefix = "local:";
    private const string DefaultSharedRoot = "/sja/SJA SPJ/ICT";

    public string Server { get; set; } = "spj";
    public string Share { get; set; } = "ipop";
    public string? Username { get; set; }
    public string? Password { get; set; }
    public long MaxBytes { get; set; } = DefaultMaxBytes;
    /// Area -> absolute NAS folder. Areas without an entry use /{Share}/ipop-attachments/{area}.
    public Dictionary<string, string> Folders { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// Local-disk fallback root (absolute). Null disables the fallback.
    public string? LocalRoot { get; set; }

    public bool HasServiceAccount => !string.IsNullOrWhiteSpace(Username) && !string.IsNullOrEmpty(Password);

    public string FolderFor(string area) =>
        Folders.TryGetValue(area, out var f) && !string.IsNullOrWhiteSpace(f) ? f : $"/{Share}/ipop-attachments/{area}";

    /// Synology resolves /home per logged-in account, so nobody else could read files put there.
    public static bool IsHomePath(string path) =>
        path.Equals("/home", StringComparison.OrdinalIgnoreCase) || path.StartsWith("/home/", StringComparison.OrdinalIgnoreCase);

    public static AttachmentOptions Bind(IConfiguration cfg, string? localRoot = null)
    {
        var section = cfg.GetSection("Attachments");
        var opt = section.Get<AttachmentOptions>() ?? new AttachmentOptions();
        opt.LocalRoot ??= localRoot;

        if (string.IsNullOrWhiteSpace(opt.Username))
        {
            // Same identity chat/mail upload with (Synology:ServiceAccount:*); no extra secrets needed.
            var server = cfg["Synology:ServiceAccount:Server"]?.Trim();
            if (!string.IsNullOrEmpty(server)) opt.Server = server.ToLowerInvariant();
            opt.Username = cfg["Synology:ServiceAccount:Username"]?.Trim();
            opt.Password = cfg["Synology:ServiceAccount:Password"];
        }

        // An explicit Attachments:Share keeps the legacy /{Share}/ipop-attachments layout.
        if (string.IsNullOrWhiteSpace(section["Share"]))
        {
            var parent = SharedParent(cfg);
            foreach (var area in AttachmentAreas.All)
            {
                if (opt.Folders.ContainsKey(area)) continue;
                var key = $"Synology:{char.ToUpperInvariant(area[0])}{area[1..]}Folder";
                var configured = Normalize(cfg[key]);
                opt.Folders[area] = configured is not null && !IsHomePath(configured) ? configured : $"{parent}/{area}";
            }
        }
        return opt;
    }

    /// Parent of Synology:ChatFolder / MailFolder (the shared ICT folder), never /home.
    private static string SharedParent(IConfiguration cfg)
    {
        foreach (var key in new[] { "Synology:ChatFolder", "Synology:MailFolder" })
        {
            var f = Normalize(cfg[key]);
            if (f is null || IsHomePath(f)) continue;
            var cut = f.LastIndexOf('/');
            if (cut > 0) return f[..cut];
        }
        return DefaultSharedRoot;
    }

    private static string? Normalize(string? path)
    {
        var p = path?.Trim().TrimEnd('/');
        if (string.IsNullOrEmpty(p)) return null;
        return p.StartsWith('/') ? p : "/" + p;
    }
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
    IPopDbContext db, ISynologyGateway gw, AttachmentOptions opt, AttachmentSession session,
    ILogger<SynologyAttachmentStore>? log = null) : IAttachmentStore
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
        var diskName = id.ToString("N") + ext;
        var storagePath = await StoreBytesAsync(area, now, diskName, buffer, ct);

        var row = new Attachment
        {
            Id = id, Area = area,
            FileName = Path.GetFileName(fileName).Trim() is { Length: > 0 } n ? Truncate(n, 255) : "file" + ext,
            ContentType = string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : Truncate(contentType, 127),
            Size = buffer.Length,
            Sha256 = Convert.ToHexStringLower(SHA256.HashData(buffer)),
            StoragePath = storagePath,
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
        if (IsLocal(row.StoragePath))
        {
            var full = LocalFullPath(row.StoragePath);
            if (full is null || !File.Exists(full)) return null;
            return (ToDto(row), File.OpenRead(full));
        }
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
        if (IsLocal(row.StoragePath))
        {
            if (LocalFullPath(row.StoragePath) is { } full && File.Exists(full)) File.Delete(full);
            return;
        }
        await WithSessionAsync(async sid => { await gw.DeleteAsync(opt.Server, sid, row.StoragePath, ct); return true; }, ct);
    }

    /// NAS first (service account, shared folder); local disk when the account is missing or the
    /// NAS fails and a LocalRoot is configured — the same fallback chat and mail uploads use.
    private async Task<string> StoreBytesAsync(string area, DateTime now, string diskName, byte[] buffer, CancellationToken ct)
    {
        var dir = $"{opt.FolderFor(area)}/{now:yyyy}/{now:MM}";
        if (AttachmentOptions.IsHomePath(dir))
            throw new SynoException("Attachment folder must be a shared folder, not /home.", authFailure: false);

        if (opt.HasServiceAccount)
        {
            try
            {
                await WithSessionAsync(async sid =>
                {
                    using var ms = new MemoryStream(buffer, writable: false);
                    await gw.UploadAsync(opt.Server, sid, dir, diskName, ms, ct);
                    return true;
                }, ct);
                return $"{dir}/{diskName}";
            }
            catch (Exception ex) when (opt.LocalRoot is not null && !ct.IsCancellationRequested)
            {
                log?.LogWarning(ex, "NAS upload of {Area} attachment to {Server}:{Dir} failed; storing on local disk instead.", area, opt.Server, dir);
            }
        }
        else if (opt.LocalRoot is null)
            throw new SynoException("Attachments service account is not configured", authFailure: true);
        else
            log?.LogWarning("No Synology service account configured (Synology:ServiceAccount:*); {Area} attachment stored on local disk.", area);

        var rel = $"{area}/{now:yyyy}/{now:MM}/{diskName}";
        var full = Path.Combine(opt.LocalRoot!, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        await File.WriteAllBytesAsync(full, buffer, ct);
        return AttachmentOptions.LocalPrefix + rel;
    }

    private static bool IsLocal(string storagePath) => storagePath.StartsWith(AttachmentOptions.LocalPrefix, StringComparison.Ordinal);

    /// Resolves a "local:" path under LocalRoot; null if it would escape the root.
    private string? LocalFullPath(string storagePath)
    {
        if (opt.LocalRoot is null) return null;
        var root = Path.GetFullPath(opt.LocalRoot);
        var full = Path.GetFullPath(Path.Combine(root, storagePath[AttachmentOptions.LocalPrefix.Length..]));
        var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        return full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? full : null;
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
