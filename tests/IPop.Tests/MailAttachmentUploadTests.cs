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

    [Fact]
    public async Task Shared_upload_uses_service_account_and_keeps_orphan_authorization_scoped()
    {
        var owner = Guid.NewGuid();
        var other = Guid.NewGuid();
        var storage = new RecordingFileAccessService();
        var bytes = Encoding.UTF8.GetBytes("recipient can read this");
        var path = $"/sja/SJA SPJ/ICT/mail/drafts/{owner:N}/document.pdf";

        await MailEndpoints.UploadMailAsServiceAsync(storage, $"/sja/SJA SPJ/ICT/mail/drafts/{owner:N}",
            "document.pdf", new MemoryStream(bytes), default);

        var upload = Assert.Single(storage.ServiceUploads);
        Assert.Equal(bytes, upload.Bytes);
        Assert.Equal("/sja/SJA SPJ/ICT/mail/drafts/" + owner.ToString("N"), upload.Directory);
        Assert.True(MailEndpoints.IsSharedMailPath(path));
        Assert.True(MailEndpoints.IsOwnMailDraftPath(path, owner));
        Assert.False(MailEndpoints.IsOwnMailDraftPath(path, other));
        Assert.False(MailEndpoints.IsSharedMailPath($"/home/mail/drafts/{owner:N}/document.pdf"));
        Assert.False(MailEndpoints.IsOwnMailDraftPath($"/foo/drafts/{owner:N}-other/document.pdf", owner));
    }

    [Fact]
    public void Attachments_allow_500_MB_per_file_and_pictures_10_MB()
    {
        Assert.Equal(500L * 1024 * 1024, MailEndpoints.MaxUploadFileBytes);
        Assert.Equal(MailEndpoints.MaxUploadFileBytes, IPop.Web.Services.MailUploadBridge.MaxFileBytes);
        Assert.Equal(MailEndpoints.MaxUploadFiles, IPop.Web.Services.MailUploadBridge.MaxFiles);
        Assert.True(MailEndpoints.MaxUploadRequestBytes >= MailEndpoints.MaxUploadFiles * MailEndpoints.MaxUploadFileBytes);
        Assert.Equal(10 * 1024 * 1024, IPop.Infrastructure.Mail.MailHtml.MaxImageBytes);
        // A 10 MB picture as a data: URI must fit the compose request the body travels in.
        Assert.True(MailEndpoints.MaxComposeRequestBytes > IPop.Infrastructure.Mail.MailHtml.MaxDataImageChars);
    }

    [Fact]
    public void Sanitizer_keeps_a_10_MB_picture_and_drops_a_bigger_one()
    {
        static string Img(int bytes) => $"<p><img src=\"data:image/png;base64,{Convert.ToBase64String(new byte[bytes])}\"></p>";
        Assert.Contains("data:image/png", IPop.Infrastructure.Mail.MailHtml.Sanitize(Img(9 * 1024 * 1024)));
        Assert.DoesNotContain("data:image/png", IPop.Infrastructure.Mail.MailHtml.Sanitize(Img(11 * 1024 * 1024)));
    }

    private sealed class RecordingFileAccessService : IFileAccessService
    {
        public List<UploadRecord> Uploads { get; } = [];
        public List<ServiceUploadRecord> ServiceUploads { get; } = [];

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
        public Task<IReadOnlyList<SiteConnectResult>> ConnectAllAsync(Guid userId, string username, string password, CancellationToken ct) => throw new NotSupportedException();
    public void DropSession(Guid userId, string server) => throw new NotSupportedException();
        public void DropAllSessions(Guid userId) => throw new NotSupportedException();
        public bool HasServiceAccount => false;
        public string? ServiceServer => null;
        public Task<Stream> DownloadAsServiceAsync(string path, CancellationToken ct) => throw new NotSupportedException();
        public async Task UploadAsServiceAsync(string dir, string filename, Stream content, CancellationToken ct)
        {
            await using var copy = new MemoryStream();
            await content.CopyToAsync(copy, ct);
            ServiceUploads.Add(new ServiceUploadRecord(dir, filename, copy.ToArray()));
        }
    }

    private sealed record ServiceUploadRecord(string Directory, string FileName, byte[] Bytes);
    private sealed record UploadRecord(Guid UserId, string Server, string Directory, string FileName, byte[] Bytes);
}
