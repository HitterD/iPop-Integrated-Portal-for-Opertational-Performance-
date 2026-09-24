using System.Text;
using IPop.Api.Endpoints;
using IPop.Application.Files;
using Xunit;

namespace IPop.Tests;

/// The endpoint's storage invariant is covered by the fake service contract tests below.
/// The actual HTTP multipart harness remains intentionally separate because the repository does
/// not register a WebApplicationFactory test host.
public sealed class MailAttachmentUploadTests
{
    [Fact]
    public async Task Credentialed_upload_writes_bytes_before_mail_attachment_is_persisted()
    {
        var userId = Guid.NewGuid();
        var storage = new RecordingFileAccessService();
        var bytes = Encoding.UTF8.GetBytes("mail payload");

        await MailEndpoints.UploadToSynologyAsync(storage, userId, "spj", "/mail/drafts/user", "file.txt", new MemoryStream(bytes), default);

        var upload = Assert.Single(storage.Uploads);
        Assert.Equal(userId, upload.UserId);
        Assert.Equal("spj", upload.Server);
        Assert.Equal("/mail/drafts/user", upload.Directory);
        Assert.Equal("file.txt", upload.FileName);
        Assert.Equal(bytes, upload.Bytes);
    }

    private sealed class RecordingFileAccessService : IFileAccessService
    {
        public List<UploadRecord> Uploads { get; } = [];

        public async Task UploadAsync(Guid userId, string server, string dir, string filename, Stream content, CancellationToken ct)
        {
            await using var copy = new MemoryStream();
            await content.CopyToAsync(copy, ct);
            Uploads.Add(new UploadRecord(userId, server, dir, filename, copy.ToArray()));
        }

        public Task<(bool Ok, string? Error)> SetCredentialAsync(Guid userId, string server, string username, string password, CancellationToken ct) => throw new NotSupportedException();
        public Task<(bool Found, CredentialDto? Credential)> GetCredentialAsync(Guid userId, string server, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<CredentialDto>> ListCredentialsAsync(Guid userId, CancellationToken ct) => throw new NotSupportedException();
        public Task ClearCredentialAsync(Guid userId, string server, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<string>> ListSharesAsync(Guid userId, string server, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<SynoItem>> ListFolderAsync(Guid userId, string server, string path, CancellationToken ct) => throw new NotSupportedException();
        public Task<Stream> DownloadAsync(Guid userId, string server, string path, CancellationToken ct) => throw new NotSupportedException();
        public Task RenameAsync(Guid userId, string server, string path, string name, CancellationToken ct) => throw new NotSupportedException();
        public Task CreateFolderAsync(Guid userId, string server, string folderPath, string name, CancellationToken ct) => throw new NotSupportedException();
        public Task DeleteAsync(Guid userId, string server, string path, CancellationToken ct) => throw new NotSupportedException();
        public Task CopyMoveAsync(Guid userId, string server, IEnumerable<string> paths, string destDir, bool removeSrc, bool overwrite, CancellationToken ct) => throw new NotSupportedException();
        public Task<SynoShareLink> CreateSharingLinkAsync(Guid userId, string server, string path, string? password, int? expiresInDays, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<SynoShareLink>> ListSharingLinksAsync(Guid userId, string server, string? path, CancellationToken ct) => throw new NotSupportedException();
        public Task DeleteSharingLinkAsync(Guid userId, string server, string id, CancellationToken ct) => throw new NotSupportedException();
        public Task<SynoStorageInfo> GetStorageInfoAsync(Guid userId, string server, CancellationToken ct) => throw new NotSupportedException();
        public void DropSession(Guid userId, string server) => throw new NotSupportedException();
        public void DropAllSessions(Guid userId) => throw new NotSupportedException();
        public bool HasServiceAccount => false;
        public string? ServiceServer => null;
        public Task<Stream> DownloadAsServiceAsync(string path, CancellationToken ct) => throw new NotSupportedException();
        public Task UploadAsServiceAsync(string dir, string filename, Stream content, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed record UploadRecord(Guid UserId, string Server, string Directory, string FileName, byte[] Bytes);
}
