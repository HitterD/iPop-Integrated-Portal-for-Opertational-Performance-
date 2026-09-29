namespace IPop.Application.Files;

public record CredentialDto(string Server, bool HasCredential, bool IsInvalid, string? Username);

/// <summary>Outcome of one site in a sign-in-everywhere attempt. Status: connected | rejected | unreachable.</summary>
public record SiteConnectResult(string Server, string Status, string? Error);

public interface IFileAccessService
{
    Task<(bool Ok, string? Error)> SetCredentialAsync(Guid userId, string server, string username, string password, CancellationToken ct);
    /// <summary>
    /// Tries one username/password against every plant NAS at once and stores it wherever it is
    /// accepted. A site that rejects it keeps whatever credential it already had.
    /// </summary>
    Task<IReadOnlyList<SiteConnectResult>> ConnectAllAsync(Guid userId, string username, string password, CancellationToken ct);
    Task<(bool Found, CredentialDto? Credential)> GetCredentialAsync(Guid userId, string server, CancellationToken ct);
    Task<IReadOnlyList<CredentialDto>> ListCredentialsAsync(Guid userId, CancellationToken ct);
    Task ClearCredentialAsync(Guid userId, string server, CancellationToken ct);
    Task<IReadOnlyList<string>> ListSharesAsync(Guid userId, string server, CancellationToken ct);
    Task<IReadOnlyList<SynoItem>> ListFolderAsync(Guid userId, string server, string path, CancellationToken ct);
    Task<Stream> DownloadAsync(Guid userId, string server, string path, CancellationToken ct);
    Task UploadAsync(Guid userId, string server, string dir, string filename, Stream content, CancellationToken ct);
    Task RenameAsync(Guid userId, string server, string path, string name, CancellationToken ct);
    Task CreateFolderAsync(Guid userId, string server, string folderPath, string name, CancellationToken ct);
    Task DeleteAsync(Guid userId, string server, string path, CancellationToken ct);
    Task CopyMoveAsync(Guid userId, string server, IEnumerable<string> paths, string destDir, bool removeSrc, bool overwrite, CancellationToken ct);
    Task<SynoShareLink> CreateSharingLinkAsync(Guid userId, string server, string path, string? password, int? expiresInDays, CancellationToken ct);
    Task<IReadOnlyList<SynoShareLink>> ListSharingLinksAsync(Guid userId, string server, string? path, CancellationToken ct);
    Task DeleteSharingLinkAsync(Guid userId, string server, string id, CancellationToken ct);
    Task<SynoStorageInfo> GetStorageInfoAsync(Guid userId, string server, CancellationToken ct);
    void DropSession(Guid userId, string server);
    void DropAllSessions(Guid userId);

    // ── Service account ──────────────────────────────────────────────────────
    bool HasServiceAccount { get; }
    string? ServiceServer { get; }

    Task<Stream> DownloadAsServiceAsync(string path, CancellationToken ct);
    Task UploadAsServiceAsync(string dir, string filename, Stream content, CancellationToken ct);
}
