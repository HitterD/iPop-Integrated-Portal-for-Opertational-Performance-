using System.Security.Cryptography;
using System.Text;
using IPop.Application.Attachments;
using IPop.Application.Files;
using IPop.Infrastructure.Attachments;
using Microsoft.EntityFrameworkCore;
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
