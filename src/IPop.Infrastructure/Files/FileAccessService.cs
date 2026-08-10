using System.Collections.Concurrent;
using IPop.Application.Files;
using IPop.Core.Entities;
using IPop.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace IPop.Infrastructure.Files;

public class FileAccessService(
    IPopDbContext db,
    ISynologyGateway gateway,
    IDataProtectionProvider protector,
    ILogger<FileAccessService> log) : IFileAccessService
{
    private const string Purpose = "synology-credential";
    private readonly IDataProtector _protector = protector.CreateProtector(Purpose);
    private readonly ConcurrentDictionary<(Guid, string), string> _sids = new();

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

    private static string NormalizeServer(string s) => s.Trim().ToLowerInvariant() switch
    {
        "spj" or "krw" or "jtb" or "smg" => s.Trim().ToLowerInvariant(),
        _ => throw new SynoException($"Unknown server '{s}' — expected spj/krw/jtb/smg")
    };
}
