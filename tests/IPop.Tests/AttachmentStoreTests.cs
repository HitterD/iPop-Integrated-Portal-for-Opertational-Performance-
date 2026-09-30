using System.Security.Cryptography;
using System.Text;
using IPop.Application.Attachments;
using IPop.Application.Files;
using IPop.Infrastructure.Attachments;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace IPop.Tests;

public class AttachmentStoreTests
{
    private sealed class FakeGateway : ISynologyGateway
    {
        public List<(string User, string Pass)> Auths { get; } = [];
        public List<(string Sid, string Dir, string Name, byte[] Bytes)> Uploads { get; } = [];
        public List<string> Deletes { get; } = [];
        public int ExpireNextCalls;

        public Task<string> AuthAsync(string server, string u, string p, CancellationToken ct)
        { Auths.Add((u, p)); return Task.FromResult($"sid{Auths.Count}"); }
        public Task<IReadOnlyList<string>> ListSharesAsync(string server, string sid, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<SynoItem>> ListFolderAsync(string server, string sid, string path, CancellationToken ct) => throw new NotSupportedException();
        public Task<Stream> DownloadAsync(string server, string sid, string path, CancellationToken ct)
        {
            Expire();
            var up = Uploads.Single(u => $"{u.Dir}/{u.Name}" == path);
            return Task.FromResult<Stream>(new MemoryStream(up.Bytes));
        }
        public async Task UploadAsync(string server, string sid, string dir, string fn, Stream c, CancellationToken ct)
        {
            Expire();
            using var ms = new MemoryStream();
            await c.CopyToAsync(ms, ct);
            Uploads.Add((sid, dir, fn, ms.ToArray()));
        }
        public Task DeleteAsync(string server, string sid, string path, CancellationToken ct)
        { Expire(); Deletes.Add(path); return Task.CompletedTask; }
        public Task RenameAsync(string server, string sid, string path, string name, CancellationToken ct) => Task.CompletedTask;
        public Task CreateFolderAsync(string server, string sid, string folderPath, string name, CancellationToken ct) => Task.CompletedTask;
        public Task CopyMoveAsync(string server, string sid, IEnumerable<string> paths, string destDir, bool removeSrc, bool overwrite, CancellationToken ct) => Task.CompletedTask;
        public Task<SynoShareLink> CreateSharingLinkAsync(string server, string sid, string path, string? password, int? expiresInDays, CancellationToken ct)
            => Task.FromResult(new SynoShareLink("id", "url", path, null, false));
        public Task<IReadOnlyList<SynoShareLink>> ListSharingLinksAsync(string server, string sid, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<SynoShareLink>>([]);
        public Task DeleteSharingLinkAsync(string server, string sid, string id, CancellationToken ct) => Task.CompletedTask;
        public Task<SynoStorageInfo> GetStorageInfoAsync(string server, string sid, CancellationToken ct)
            => Task.FromResult(new SynoStorageInfo(server, 0, 0, 0));

        private void Expire()
        {
            if (ExpireNextCalls <= 0) return;
            ExpireNextCalls--;
            throw new SynoException("Session expired", 119, sessionExpired: true);
        }
    }

    private static readonly AttachmentOptions Opt = new()
    {
        Server = "spj", Share = "ipop", Username = "svc-ipop", Password = "svc-pass", MaxBytes = 1024,
    };

    private static (SynologyAttachmentStore store, FakeGateway gw) Make(TestDb t, AttachmentSession? session = null)
    {
        var gw = new FakeGateway();
        return (new SynologyAttachmentStore(t.Db, gw, Opt, session ?? new AttachmentSession()), gw);
    }

    private static MemoryStream Text(string s) => new(Encoding.UTF8.GetBytes(s));

    [Fact]
    public async Task Put_UsesIdForPath_NotUserFileName()
    {
        using var t = new TestDb();
        var user = t.AddUser("90001");
        var (store, gw) = Make(t);

        var a = await store.PutAsync("chat", "../../etc/passwd evil.PDF", "application/pdf", Text("%PDF-1.4"), user.Id, default);

        var up = gw.Uploads.Single();
        Assert.Equal($"{a.Id:N}.pdf", up.Name);
        Assert.StartsWith($"/ipop/ipop-attachments/chat/{DateTime.UtcNow:yyyy}/", up.Dir);
        Assert.DoesNotContain("..", up.Dir + up.Name);
        Assert.Equal("passwd evil.PDF", a.FileName);
    }

    [Fact]
    public async Task Put_WritesMetadataWithSha256()
    {
        using var t = new TestDb();
        var user = t.AddUser("90001");
        var (store, _) = Make(t);

        var a = await store.PutAsync("news", "memo.txt", "text/plain", Text("hello"), user.Id, default);

        var row = await t.Verify().Attachments.SingleAsync();
        Assert.Equal(a.Id, row.Id);
        Assert.Equal(5, row.Size);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("hello"))), row.Sha256);
        Assert.Equal(user.Id, row.UploaderId);
    }

    [Fact]
    public async Task Put_TooLarge_Rejected_NothingStored()
    {
        using var t = new TestDb();
        var user = t.AddUser("90001");
        var (store, gw) = Make(t);

        var ex = await Assert.ThrowsAsync<AttachmentRejectedException>(() =>
            store.PutAsync("chat", "big.bin", "application/octet-stream", new MemoryStream(new byte[2048]), user.Id, default));

        Assert.Equal("too_large", ex.Code);
        Assert.Empty(gw.Uploads);
        Assert.Empty(await t.Verify().Attachments.ToListAsync());
    }

    [Theory]
    [InlineData("setup.exe", "hi")]
    [InlineData("report.pdf", "MZ\u0090\0")]   // renamed PE
    public async Task Put_Executable_Rejected(string name, string body)
    {
        using var t = new TestDb();
        var user = t.AddUser("90001");
        var (store, gw) = Make(t);

        var ex = await Assert.ThrowsAsync<AttachmentRejectedException>(() =>
            store.PutAsync("chat", name, "application/octet-stream", Text(body), user.Id, default));

        Assert.Equal("blocked_type", ex.Code);
        Assert.Empty(gw.Uploads);
    }

    [Fact]
    public async Task Put_UsesServiceAccount_AndCachesSession()
    {
        using var t = new TestDb();
        var user = t.AddUser("90001");
        var (store, gw) = Make(t);

        await store.PutAsync("chat", "a.txt", "text/plain", Text("a"), user.Id, default);
        await store.PutAsync("chat", "b.txt", "text/plain", Text("b"), user.Id, default);

        Assert.Equal([("svc-ipop", "svc-pass")], gw.Auths);
        Assert.All(gw.Uploads, u => Assert.Equal("sid1", u.Sid));
    }

    [Fact]
    public async Task ExpiredSession_ReauthsOnce_ThenSucceeds()
    {
        using var t = new TestDb();
        var user = t.AddUser("90001");
        var (store, gw) = Make(t);
        gw.ExpireNextCalls = 1;

        await store.PutAsync("chat", "a.txt", "text/plain", Text("a"), user.Id, default);

        Assert.Equal(2, gw.Auths.Count);
        Assert.Equal("sid2", gw.Uploads.Single().Sid);
    }

    [Fact]
    public async Task Open_And_Delete_RoundTrip()
    {
        using var t = new TestDb();
        var user = t.AddUser("90001");
        var (store, gw) = Make(t);
        var a = await store.PutAsync("mail", "note.txt", "text/plain", Text("body"), user.Id, default);

        var opened = await store.OpenAsync(a.Id, default);
        Assert.NotNull(opened);
        using (var r = new StreamReader(opened.Value.Content)) Assert.Equal("body", await r.ReadToEndAsync());

        await store.DeleteAsync(a.Id, default);
        Assert.Single(gw.Deletes);
        Assert.NotNull((await t.Verify().Attachments.SingleAsync()).DeletedAt);
        Assert.Null(await store.OpenAsync(a.Id, default));
    }

    private static Microsoft.Extensions.Configuration.IConfiguration Cfg(Dictionary<string, string?> values) =>
        new Microsoft.Extensions.Configuration.ConfigurationBuilder().AddInMemoryCollection(values).Build();

    [Fact]
    public void Bind_WithoutAttachmentsSection_UsesSynologyServiceAccount_AndSharedSiblingFolder()
    {
        var opt = AttachmentOptions.Bind(Cfg(new()
        {
            ["Synology:ServiceAccount:Server"] = "ICT",
            ["Synology:ServiceAccount:Username"] = "svc",
            ["Synology:ServiceAccount:Password"] = "pw",
            ["Synology:ChatFolder"] = "/sja/SJA SPJ/ICT/chat",
        }));

        Assert.Equal("ict", opt.Server);
        Assert.Equal("svc", opt.Username);
        Assert.True(opt.HasServiceAccount);
        Assert.Equal("/sja/SJA SPJ/ICT/news", opt.FolderFor("news"));
        Assert.Equal(AttachmentOptions.DefaultMaxBytes, opt.MaxBytes);
    }

    [Fact]
    public void Bind_NewsFolderOverride_Wins_ButHomeIsRejected()
    {
        var opt = AttachmentOptions.Bind(Cfg(new()
        {
            ["Synology:ChatFolder"] = "/home/chat",
            ["Synology:MailFolder"] = "/shared/ICT/mail",
            ["Synology:NewsFolder"] = "/shared/ICT/memos/",
            ["Synology:ProjectsFolder"] = "/home/projects",
        }));

        Assert.Equal("/shared/ICT/memos", opt.FolderFor("news"));
        Assert.Equal("/shared/ICT/projects", opt.FolderFor("projects")); // /home override ignored
        Assert.False(opt.HasServiceAccount);
    }

    [Fact]
    public async Task NoServiceAccount_WithLocalRoot_StoresOnDisk_AndRoundTrips()
    {
        using var t = new TestDb();
        var user = t.AddUser("90001");
        var root = Path.Combine(Path.GetTempPath(), "ipop-att-" + Guid.NewGuid().ToString("N"));
        try
        {
            var opt = new AttachmentOptions { LocalRoot = root, MaxBytes = 1024 };
            var gw = new FakeGateway();
            var store = new SynologyAttachmentStore(t.Db, gw, opt, new AttachmentSession());

            var a = await store.PutAsync("news", "memo.txt", "text/plain", Text("local body"), user.Id, default);

            Assert.Empty(gw.Auths);
            var row = await t.Verify().Attachments.SingleAsync();
            Assert.StartsWith("local:news/", row.StoragePath);
            var opened = await store.OpenAsync(a.Id, default);
            using (var r = new StreamReader(opened!.Value.Content)) Assert.Equal("local body", await r.ReadToEndAsync());

            await store.DeleteAsync(a.Id, default);
            Assert.Empty(Directory.GetFiles(root, "*", SearchOption.AllDirectories));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task NoServiceAccount_NoLocalRoot_ThrowsSynoException()
    {
        using var t = new TestDb();
        var user = t.AddUser("90001");
        var store = new SynologyAttachmentStore(t.Db, new FakeGateway(), new AttachmentOptions(), new AttachmentSession());
        await Assert.ThrowsAsync<SynoException>(() => store.PutAsync("news", "a.txt", "text/plain", Text("a"), user.Id, default));
    }

    [Fact]
    public async Task Put_UsesConfiguredAreaFolder()
    {
        using var t = new TestDb();
        var user = t.AddUser("90001");
        var opt = new AttachmentOptions { Username = "svc", Password = "pw", MaxBytes = 1024 };
        opt.Folders["news"] = "/sja/SJA SPJ/ICT/news";
        var gw = new FakeGateway();
        await new SynologyAttachmentStore(t.Db, gw, opt, new AttachmentSession())
            .PutAsync("news", "a.txt", "text/plain", Text("a"), user.Id, default);
        Assert.StartsWith("/sja/SJA SPJ/ICT/news/", gw.Uploads.Single().Dir);
    }

    [Fact]
    public async Task UnknownArea_Throws()
    {
        using var t = new TestDb();
        var user = t.AddUser("90001");
        var (store, _) = Make(t);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            store.PutAsync("home", "a.txt", "text/plain", Text("a"), user.Id, default));
    }
}
