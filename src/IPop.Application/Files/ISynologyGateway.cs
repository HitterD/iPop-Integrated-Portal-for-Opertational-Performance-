namespace IPop.Application.Files;

public record SynoItem(string Name, string Path, bool IsDir, long Size, long Mtime);
public record SynoShareLink(string Id, string Url, string Path, DateTime? DateExpired, bool HasPassword);
public record SynoStorageInfo(string Server, long UsedBytes, long TotalBytes, long FreeBytes);
/// <summary>One page of a running FileStation search; <c>Finished</c> once the NAS has walked the whole tree.</summary>
public record SynoSearchPage(IReadOnlyList<SynoItem> Items, int Total, bool Finished);

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

    Task RenameAsync(string server, string sid, string path, string name, CancellationToken ct);
    Task CreateFolderAsync(string server, string sid, string folderPath, string name, CancellationToken ct);
    Task CopyMoveAsync(string server, string sid, IEnumerable<string> paths, string destDir, bool removeSrc, bool overwrite, CancellationToken ct);
    Task<SynoShareLink> CreateSharingLinkAsync(string server, string sid, string path, string? password, int? expiresInDays, CancellationToken ct);
    Task<IReadOnlyList<SynoShareLink>> ListSharingLinksAsync(string server, string sid, CancellationToken ct);
    Task DeleteSharingLinkAsync(string server, string sid, string id, CancellationToken ct);
    Task<SynoStorageInfo> GetStorageInfoAsync(string server, string sid, CancellationToken ct);

    // SYNO.FileStation.Search runs as a task on the NAS: start it, page through results while it
    // walks the tree, clean it up when the user cancels. Default bodies keep test doubles compiling.
    Task<string> SearchStartAsync(string server, string sid, IReadOnlyList<string> folderPaths, string pattern, CancellationToken ct)
        => throw new NotSupportedException("Search is not supported by this gateway.");
    Task<SynoSearchPage> SearchListAsync(string server, string sid, string taskId, int offset, int limit, CancellationToken ct)
        => throw new NotSupportedException("Search is not supported by this gateway.");
    Task SearchStopAsync(string server, string sid, string taskId, CancellationToken ct) => Task.CompletedTask;
}
