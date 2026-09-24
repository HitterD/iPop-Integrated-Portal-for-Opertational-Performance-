namespace IPop.Application.Files;

public record SynoItem(string Name, string Path, bool IsDir, long Size, long Mtime);

public class SynoException : Exception
{
    public int? Code { get; }
    public bool IsAuthFailure { get; }
    public bool IsSessionExpired { get; }
    public SynoException(string message, int? code = null, bool authFailure = false, bool sessionExpired = false) : base(message)
    { Code = code; IsAuthFailure = authFailure; IsSessionExpired = sessionExpired; }
}

public interface ISynologyGateway
{
    Task<string> AuthAsync(string server, string username, string password, CancellationToken ct);
    Task<IReadOnlyList<string>> ListSharesAsync(string server, string sid, CancellationToken ct);
    Task<IReadOnlyList<SynoItem>> ListFolderAsync(string server, string sid, string path, CancellationToken ct);
    Task<Stream> DownloadAsync(string server, string sid, string path, CancellationToken ct);
    Task UploadAsync(string server, string sid, string dir, string filename, Stream content, CancellationToken ct);
    Task DeleteAsync(string server, string sid, string path, CancellationToken ct);
}
