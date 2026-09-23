namespace IPop.Application.Files;

public record CredentialDto(string Server, bool HasCredential, bool IsInvalid, string? Username);

public interface IFileAccessService
{
    Task<(bool Ok, string? Error)> SetCredentialAsync(Guid userId, string server, string username, string password, CancellationToken ct);
    Task<(bool Found, CredentialDto? Credential)> GetCredentialAsync(Guid userId, string server, CancellationToken ct);
    Task<IReadOnlyList<CredentialDto>> ListCredentialsAsync(Guid userId, CancellationToken ct);
    Task ClearCredentialAsync(Guid userId, string server, CancellationToken ct);
    Task<IReadOnlyList<string>> ListSharesAsync(Guid userId, string server, CancellationToken ct);
    Task<IReadOnlyList<SynoItem>> ListFolderAsync(Guid userId, string server, string path, CancellationToken ct);
    Task<Stream> DownloadAsync(Guid userId, string server, string path, CancellationToken ct);
    Task UploadAsync(Guid userId, string server, string dir, string filename, Stream content, CancellationToken ct);
    void DropSession(Guid userId, string server);
    void DropAllSessions(Guid userId);

    // ── Service account ──────────────────────────────────────────────────────
    // Shared content (chat attachments) must be readable by every participant, so it cannot be
    // stored under one member's identity: Synology resolves "/home" to the logged-in account's own
    // home, and FileStation permissions follow the account. These overloads authenticate as the
    // configured service account instead of a user, so the writer and every later reader are the
    // same identity.

    /// True when a service account is configured (Synology:ServiceAccount).
    bool HasServiceAccount { get; }

    /// The server key the service account is configured against, or null when unconfigured.
    string? ServiceServer { get; }

    Task<Stream> DownloadAsServiceAsync(string path, CancellationToken ct);
    Task UploadAsServiceAsync(string dir, string filename, Stream content, CancellationToken ct);
}
