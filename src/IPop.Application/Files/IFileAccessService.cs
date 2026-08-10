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
}
