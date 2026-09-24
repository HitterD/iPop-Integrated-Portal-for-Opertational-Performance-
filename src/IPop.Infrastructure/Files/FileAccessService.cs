using System.Collections.Concurrent;
using IPop.Application.Files;
using IPop.Core.Entities;
using IPop.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace IPop.Infrastructure.Files;

public class FileAccessService(
    IPopDbContext db,
    ISynologyGateway gateway,
    IDataProtectionProvider protector,
    IConfiguration cfg,
    ILogger<FileAccessService> log) : IFileAccessService
{
    private const string Purpose = "synology-credential";
    private readonly IDataProtector _protector = protector.CreateProtector(Purpose);
    private readonly ConcurrentDictionary<(Guid, string), string> _sids = new();

    // The service-account session is keyed on its own, never mixed with per-user sessions.
    private string? _serviceSid;
    private readonly SemaphoreSlim _serviceLock = new(1, 1);

    public async Task<(bool Ok, string? Error)> SetCredentialAsync(Guid userId, string server, string username, string password, CancellationToken ct)
    {
        server = NormalizeServer(server);
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
            return (false, "Username and password are required.");

        string sid;
        try { sid = await gateway.AuthAsync(server, username.Trim(), password, ct); }
        catch (SynoException ex) when (ex.IsAuthFailure)
        {
            return (false, "Synology rejected the credentials.");
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Synology auth check failed for user {UserId} server {Server}: {Message}", userId, server, ex.Message);
            return (false, $"Could not reach the NAS ({ex.GetType().Name}: {ex.Message}).");
        }

        _sids[(userId, server)] = sid;
        var row = await db.SynologyCredentials.FirstOrDefaultAsync(x => x.UserId == userId && x.Server == server, ct);
        if (row is null)
        {
            row = new SynologyCredential { UserId = userId, Server = server, Username = username.Trim(), ProtectedPassword = _protector.Protect(password), IsInvalid = false, UpdatedAt = DateTime.UtcNow };
            db.SynologyCredentials.Add(row);
        }
        else
        {
            row.Username = username.Trim();
            row.ProtectedPassword = _protector.Protect(password);
            row.IsInvalid = false;
            row.UpdatedAt = DateTime.UtcNow;
        }
        await db.SaveChangesAsync(ct);
        return (true, null);
    }

    public async Task<(bool Found, CredentialDto? Credential)> GetCredentialAsync(Guid userId, string server, CancellationToken ct)
    {
        server = NormalizeServer(server);
        var row = await db.SynologyCredentials.AsNoTracking().FirstOrDefaultAsync(x => x.UserId == userId && x.Server == server, ct);
        if (row is null) return (false, null);
        return (true, new CredentialDto(row.Server, true, row.IsInvalid, row.Username));
    }

    public async Task<IReadOnlyList<CredentialDto>> ListCredentialsAsync(Guid userId, CancellationToken ct)
    {
        var rows = await db.SynologyCredentials.AsNoTracking().Where(x => x.UserId == userId).ToListAsync(ct);
        return rows.Select(r => new CredentialDto(r.Server, true, r.IsInvalid, r.Username)).ToList();
    }

    public async Task ClearCredentialAsync(Guid userId, string server, CancellationToken ct)
    {
        server = NormalizeServer(server);
        var row = await db.SynologyCredentials.FirstOrDefaultAsync(x => x.UserId == userId && x.Server == server, ct);
        if (row is not null) db.SynologyCredentials.Remove(row);
        _sids.TryRemove((userId, server), out _);
        await db.SaveChangesAsync(ct);
    }

    public void DropSession(Guid userId, string server) => _sids.TryRemove((userId, NormalizeServer(server)), out _);
    public void DropAllSessions(Guid userId)
    {
        foreach (var k in _sids.Keys.Where(k => k.Item1 == userId).ToList())
            _sids.TryRemove(k, out _);
    }

    public Task<IReadOnlyList<string>> ListSharesAsync(Guid userId, string server, CancellationToken ct)
        => WithSid(server, userId, (sid, c) => gateway.ListSharesAsync(server, sid, c), ct);

    public Task<IReadOnlyList<SynoItem>> ListFolderAsync(Guid userId, string server, string path, CancellationToken ct)
        => WithSid(server, userId, (sid, c) => gateway.ListFolderAsync(server, sid, path, c), ct);

    public Task<Stream> DownloadAsync(Guid userId, string server, string path, CancellationToken ct)
        => WithSid(server, userId, (sid, c) => gateway.DownloadAsync(server, sid, path, c), ct);

    public Task RenameAsync(Guid userId, string server, string path, string name, CancellationToken ct)
        => WithSid(server, userId, (sid, c) => gateway.RenameAsync(server, sid, path, name, c), ct);

    public Task CreateFolderAsync(Guid userId, string server, string folderPath, string name, CancellationToken ct)
        => WithSid(server, userId, (sid, c) => gateway.CreateFolderAsync(server, sid, folderPath, name, c), ct);

    public Task DeleteAsync(Guid userId, string server, string path, CancellationToken ct)
        => WithSid(server, userId, (sid, c) => gateway.DeleteAsync(server, sid, path, c), ct);

    public Task CopyMoveAsync(Guid userId, string server, IEnumerable<string> paths, string destDir, bool removeSrc, bool overwrite, CancellationToken ct)
        => WithSid(server, userId, (sid, c) => gateway.CopyMoveAsync(server, sid, paths, destDir, removeSrc, overwrite, c), ct);

    public Task<SynoShareLink> CreateSharingLinkAsync(Guid userId, string server, string path, string? password, int? expiresInDays, CancellationToken ct)
        => WithSid(server, userId, (sid, c) => gateway.CreateSharingLinkAsync(server, sid, path, password, expiresInDays, c), ct);

    public Task<IReadOnlyList<SynoShareLink>> ListSharingLinksAsync(Guid userId, string server, string? path, CancellationToken ct)
        => WithSid(server, userId, async (sid, c) =>
        {
            var links = await gateway.ListSharingLinksAsync(server, sid, c);
            if (!string.IsNullOrWhiteSpace(path))
                return links.Where(l => l.Path == path).ToList();
            return links;
        }, ct);

    public Task DeleteSharingLinkAsync(Guid userId, string server, string id, CancellationToken ct)
        => WithSid(server, userId, (sid, c) => gateway.DeleteSharingLinkAsync(server, sid, id, c), ct);

    public Task<SynoStorageInfo> GetStorageInfoAsync(Guid userId, string server, CancellationToken ct)
        => WithSid(server, userId, (sid, c) => gateway.GetStorageInfoAsync(server, sid, c), ct);

    public async Task UploadAsync(Guid userId, string server, string dir, string filename, Stream content, CancellationToken ct)
    {
        server = NormalizeServer(server);
        var sid = await GetSidAsync(userId, server, ct);
        try { await gateway.UploadAsync(server, sid, dir, filename, content, ct); }
        catch (SynoException ex) when (ex.IsSessionExpired)
        {
            _sids.TryRemove((userId, server), out _);
            var fresh = await GetSidAsync(userId, server, ct);
            await gateway.UploadAsync(server, fresh, dir, filename, content, ct);
        }
    }

    private async Task<T> WithSid<T>(string server, Guid userId, Func<string, CancellationToken, Task<T>> action, CancellationToken ct)
    {
        server = NormalizeServer(server);
        var sid = await GetSidAsync(userId, server, ct);
        try { return await action(sid, ct); }
        catch (SynoException ex) when (ex.IsSessionExpired)
        {
            _sids.TryRemove((userId, server), out _);
            var fresh = await GetSidAsync(userId, server, ct);
            return await action(fresh, ct);
        }
    }

    private Task WithSid(string server, Guid userId, Func<string, CancellationToken, Task> action, CancellationToken ct)
        => WithSid<bool>(server, userId, async (sid, c) => { await action(sid, c); return true; }, ct);

    private async Task<string> GetSidAsync(Guid userId, string server, CancellationToken ct)
    {
        server = NormalizeServer(server);
        if (_sids.TryGetValue((userId, server), out var cached)) return cached;

        var row = await db.SynologyCredentials.AsNoTracking().FirstOrDefaultAsync(x => x.UserId == userId && x.Server == server, ct)
            ?? throw new SynoException("No Synology credential stored", authFailure: true);
        if (row.IsInvalid)
            throw new SynoException("Stored credential is invalid — re-enter it", authFailure: true);

        string password;
        try { password = _protector.Unprotect(row.ProtectedPassword); }
        catch { throw new SynoException("Could not decrypt stored credential", authFailure: true); }

        try
        {
            var sid = await gateway.AuthAsync(server, row.Username, password, ct);
            _sids[(userId, server)] = sid;
            return sid;
        }
        catch (SynoException ex) when (ex.IsAuthFailure)
        {
            await db.SynologyCredentials.Where(x => x.UserId == userId && x.Server == server)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.IsInvalid, true), ct);
            _sids.TryRemove((userId, server), out _);
            throw new SynoException("Stored credential rejected — re-enter it", authFailure: true);
        }
    }

    // ── Service account ──────────────────────────────────────────────────────

    private string? ServiceUser => cfg["Synology:ServiceAccount:Username"]?.Trim();
    private string? ServicePass => cfg["Synology:ServiceAccount:Password"];

    public string? ServiceServer
    {
        get
        {
            var s = cfg["Synology:ServiceAccount:Server"]?.Trim();
            return string.IsNullOrWhiteSpace(s) ? null : s.ToLowerInvariant();
        }
    }

    public bool HasServiceAccount =>
        !string.IsNullOrWhiteSpace(ServiceUser) &&
        !string.IsNullOrWhiteSpace(ServicePass) &&
        !string.IsNullOrWhiteSpace(ServiceServer);

    public Task<Stream> DownloadAsServiceAsync(string path, CancellationToken ct)
        => WithServiceSid((srv, sid, c) => gateway.DownloadAsync(srv, sid, path, c), ct);

    public Task UploadAsServiceAsync(string dir, string filename, Stream content, CancellationToken ct)
        => WithServiceSid(async (srv, sid, c) =>
        {
            await gateway.UploadAsync(srv, sid, dir, filename, content, c);
            return true;
        }, ct);

    private async Task<T> WithServiceSid<T>(Func<string, string, CancellationToken, Task<T>> action, CancellationToken ct)
    {
        var server = ServiceServer
            ?? throw new SynoException("No Synology service account configured", authFailure: true);

        var sid = await GetServiceSidAsync(server, ct);
        try { return await action(server, sid, ct); }
        catch (SynoException ex) when (ex.IsSessionExpired)
        {
            _serviceSid = null;
            var fresh = await GetServiceSidAsync(server, ct);
            return await action(server, fresh, ct);
        }
    }

    private async Task<string> GetServiceSidAsync(string server, CancellationToken ct)
    {
        if (_serviceSid is { } cached) return cached;

        // One login at a time: a burst of attachment reads on a cold service session would
        // otherwise open a Synology session per request.
        await _serviceLock.WaitAsync(ct);
        try
        {
            if (_serviceSid is { } raced) return raced;

            var user = ServiceUser;
            var pass = ServicePass;
            if (string.IsNullOrWhiteSpace(user) || string.IsNullOrWhiteSpace(pass))
                throw new SynoException("Synology service account is not configured", authFailure: true);

            try
            {
                var sid = await gateway.AuthAsync(server, user, pass, ct);
                _serviceSid = sid;
                return sid;
            }
            catch (SynoException ex) when (ex.IsAuthFailure)
            {
                log.LogError("Synology service account '{User}' rejected by server '{Server}'. Chat attachments cannot be stored on the NAS until this is fixed.", user, server);
                throw;
            }
        }
        finally { _serviceLock.Release(); }
    }

    private static string NormalizeServer(string s) => s.Trim().ToLowerInvariant() switch
    {
        "spj" or "krw" or "jtb" or "smg" or "ict" => s.Trim().ToLowerInvariant(),
        _ => throw new SynoException($"Unknown server '{s}' — expected spj/krw/jtb/smg/ict")
    };
}
