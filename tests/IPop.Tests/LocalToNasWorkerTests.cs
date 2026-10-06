using IPop.Application.Files;
using IPop.Application.Notifications;
using IPop.Core.Entities;
using IPop.Infrastructure.Files;
using IPop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IPop.Tests;

/// Files kept on the server while the NAS was down move to the NAS and the row follows them.
public class LocalToNasWorkerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ipop-l2n-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    private sealed class NasFake(bool up) : IFileAccessService
    {
        public bool Up { get; set; } = up;
        public List<(string Dir, string Name, byte[] Bytes)> Uploads { get; } = [];
        public bool HasServiceAccount => true;
        public string? ServiceServer => "ict";
        public async Task UploadAsServiceAsync(string dir, string filename, Stream content, CancellationToken ct)
        {
            if (!Up) throw new SynoException("NAS unreachable", authFailure: false);
            await using var ms = new MemoryStream(); await content.CopyToAsync(ms, ct);
            Uploads.Add((dir, filename, ms.ToArray()));
        }
        public Task<Stream> DownloadAsServiceAsync(string path, CancellationToken ct) => throw new NotSupportedException();
        public Task DeleteAsServiceAsync(string path, CancellationToken ct) => throw new NotSupportedException();
        public Task UploadAsync(Guid userId, string server, string dir, string filename, Stream content, CancellationToken ct) => throw new NotSupportedException();
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
    }

    private LocalToNasWorker Worker(TestDb t, NasFake nas)
    {
        var sc = new ServiceCollection();
        sc.AddScoped<IPopDbContext>(_ => t.Verify());
        sc.AddSingleton<IFileAccessService>(nas);
        sc.AddSingleton<INotificationGatewayService>(_ => null!);
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["Synology:ChatFolder"] = "/sja/ICT/chat", ["Synology:MailFolder"] = "/sja/ICT/mail" }).Build();
        return new LocalToNasWorker(sc.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(), cfg, NullLogger<LocalToNasWorker>.Instance, _root);
    }

    private string LocalFile(string rel, string text)
    {
        var full = Path.Combine(_root, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text);
        return rel;
    }

    [Fact]
    public async Task Local_chat_and_mail_files_move_to_the_nas_and_the_rows_follow()
    {
        using var t = new TestDb();
        var u = t.AddUser("L2N");
        var room = Guid.NewGuid();
        var chatRel = LocalFile($"chat-files/{room:N}/abc_photo.png", "png-bytes");
        var mailRel = LocalFile($"mail-files/{u.Id:N}/def_report.pdf", "pdf-bytes");
        var chat = new ChatAttachment { Id = Guid.NewGuid(), FileName = "photo.png", MimeType = "image/png", SynologyServer = "local", SynologyPath = chatRel, ThumbnailPath = chatRel, CreatedAt = DateTime.UtcNow };
        var mail = new MailAttachment { Id = Guid.NewGuid(), UploaderId = u.Id, FileName = "report.pdf", MimeType = "application/pdf", SynologyServer = "local", SynologyPath = mailRel, CreatedAt = DateTime.UtcNow };
        t.Db.ChatAttachments.Add(chat); t.Db.MailAttachments.Add(mail); t.Db.SaveChanges();
        var nas = new NasFake(up: true);

        var (moved, waiting) = await Worker(t, nas).RunOnceAsync(default);

        Assert.Equal((2, 0), (moved, waiting));
        await using var v = t.Verify();
        var c = await v.ChatAttachments.SingleAsync(x => x.Id == chat.Id);
        Assert.Equal(("ict", $"/sja/ICT/chat/{room}/abc_photo.png", $"/sja/ICT/chat/{room}/abc_photo.png"), (c.SynologyServer, c.SynologyPath, c.ThumbnailPath));
        var m = await v.MailAttachments.SingleAsync(x => x.Id == mail.Id);
        Assert.Equal(("ict", $"/sja/ICT/mail/drafts/{u.Id:N}/def_report.pdf"), (m.SynologyServer, m.SynologyPath));
        Assert.Contains(nas.Uploads, x => x.Name == "abc_photo.png" && System.Text.Encoding.UTF8.GetString(x.Bytes) == "png-bytes");
        Assert.False(File.Exists(Path.Combine(_root, chatRel)));
        Assert.False(File.Exists(Path.Combine(_root, mailRel)));
    }

    [Fact]
    public async Task While_the_nas_is_down_nothing_changes_and_the_file_stays()
    {
        using var t = new TestDb();
        var room = Guid.NewGuid();
        var rel = LocalFile($"chat-files/{room:N}/x_note.txt", "hello");
        t.Db.ChatAttachments.Add(new ChatAttachment { Id = Guid.NewGuid(), FileName = "note.txt", MimeType = "text/plain", SynologyServer = "local", SynologyPath = rel, CreatedAt = DateTime.UtcNow });
        t.Db.SaveChanges();
        var nas = new NasFake(up: false);
        var worker = Worker(t, nas);

        var (moved, waiting) = await worker.RunOnceAsync(default);

        Assert.Equal((0, 1), (moved, waiting));
        Assert.True(File.Exists(Path.Combine(_root, rel)));
        await using (var v = t.Verify()) Assert.Equal("local", (await v.ChatAttachments.SingleAsync()).SynologyServer);

        nas.Up = true;
        Assert.Equal((1, 0), await worker.RunOnceAsync(default));
    }
}
