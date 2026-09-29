using System.Text;
using IPop.Application.Files;
using IPop.Infrastructure.Files;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IPop.Tests;

public class FileAccessTests
{
    private sealed class StubGateway : ISynologyGateway
    {
        public Func<string, string, string, CancellationToken, Task<string>>? OnAuth;
        public Func<string, string, CancellationToken, Task<IReadOnlyList<string>>>? OnShares;
        public Func<string, string, string, CancellationToken, Task<IReadOnlyList<SynoItem>>>? OnList;
        public int AuthCalls;
        public Task<string> AuthAsync(string server, string u, string p, CancellationToken ct)
        { AuthCalls++; return OnAuth is not null ? OnAuth(server, u, p, ct) : Task.FromResult("sid-" + u); }
        public Task<IReadOnlyList<string>> ListSharesAsync(string server, string sid, CancellationToken ct)
            => OnShares is not null ? OnShares(server, sid, ct) : Task.FromResult<IReadOnlyList<string>>(["/home"]);
        public Task<IReadOnlyList<SynoItem>> ListFolderAsync(string server, string sid, string path, CancellationToken ct)
            => OnList is not null ? OnList(server, sid, path, ct) : Task.FromResult<IReadOnlyList<SynoItem>>([]);
        public Task<Stream> DownloadAsync(string server, string sid, string path, CancellationToken ct)
            => Task.FromResult<Stream>(new MemoryStream(Encoding.UTF8.GetBytes("hello")));
        public Task UploadAsync(string server, string sid, string dir, string fn, Stream c, CancellationToken ct) => Task.CompletedTask;
        public Task DeleteAsync(string server, string sid, string path, CancellationToken ct) => Task.CompletedTask;
        public Task RenameAsync(string server, string sid, string path, string name, CancellationToken ct) => Task.CompletedTask;
        public Task CreateFolderAsync(string server, string sid, string folderPath, string name, CancellationToken ct) => Task.CompletedTask;
        public Task CopyMoveAsync(string server, string sid, IEnumerable<string> paths, string destDir, bool removeSrc, bool overwrite, CancellationToken ct) => Task.CompletedTask;
        public Task<SynoShareLink> CreateSharingLinkAsync(string server, string sid, string path, string? password, int? expiresInDays, CancellationToken ct)
            => Task.FromResult(new SynoShareLink("link1", "https://sharing/link1", path, null, false));
        public Task<IReadOnlyList<SynoShareLink>> ListSharingLinksAsync(string server, string sid, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<SynoShareLink>>([]);
        public Task DeleteSharingLinkAsync(string server, string sid, string id, CancellationToken ct) => Task.CompletedTask;
        public Task<SynoStorageInfo> GetStorageInfoAsync(string server, string sid, CancellationToken ct)
            => Task.FromResult(new SynoStorageInfo(server, 1000, 5000, 4000));
    }

    private static (TestDb tdb, FileAccessService svc, StubGateway gw) Make()
    {
        var tdb = new TestDb();
        var gw = new StubGateway();
        var protector = new EphemeralDataProtectionProvider();
        var cfg = new ConfigurationBuilder().Build();
        var svc = new FileAccessService(tdb.Db, gw, protector, cfg, NullLogger<FileAccessService>.Instance);
        return (tdb, svc, gw);
    }

    [Fact]
    public async Task SetCredential_ValidatesAndStores()
    {
        var m = Make();
        using var tdb = m.tdb; var svc = m.svc; var gw = m.gw;
        var user = tdb.AddUser("90001");
        gw.OnAuth = (_, _, _, _) => Task.FromResult("sid-ok");
        var (ok, _) = await svc.SetCredentialAsync(user.Id, "spj", "alice", "secret", default);
        Assert.True(ok);
        var (found, dto) = await svc.GetCredentialAsync(user.Id, "spj", default);
        Assert.True(found); Assert.False(dto!.IsInvalid); Assert.Equal("alice", dto.Username);
    }

    [Fact]
    public async Task SetCredential_RejectsBadPassword()
    {
        var m = Make();
        using var tdb = m.tdb; var svc = m.svc; var gw = m.gw;
        var user = tdb.AddUser("90002");
        gw.OnAuth = (_, _, _, _) => throw new SynoException("bad", authFailure: true);
        var (ok, _) = await svc.SetCredentialAsync(user.Id, "spj", "alice", "wrong", default);
        Assert.False(ok);
        var (found, _) = await svc.GetCredentialAsync(user.Id, "spj", default);
        Assert.False(found);
    }

    [Fact]
    public async Task AuthFailure_MarksInvalid_AndDoesNotReplay()
    {
        var m = Make();
        using var tdb = m.tdb; var svc = m.svc; var gw = m.gw;
        var user = tdb.AddUser("90003");
        gw.OnAuth = (_, _, _, _) => Task.FromResult("sid-ok");
        await svc.SetCredentialAsync(user.Id, "spj", "bob", "correct", default);
        svc.DropSession(user.Id, "spj");
        gw.OnAuth = (_, _, _, _) => throw new SynoException("bad", authFailure: true);
        await Assert.ThrowsAsync<SynoException>(() => svc.ListSharesAsync(user.Id, "spj", default));
        gw.OnAuth = (_, _, _, _) => Task.FromResult("sid-new");
        var ex = await Assert.ThrowsAsync<SynoException>(() => svc.ListSharesAsync(user.Id, "spj", default));
        Assert.True(ex.IsAuthFailure);
        await using var v = tdb.Verify();
        Assert.True((await v.SynologyCredentials.AsNoTracking().FirstAsync(x => x.UserId == user.Id)).IsInvalid);
    }

    [Fact]
    public async Task SessionExpired_RetriesOnce()
    {
        var m = Make();
        using var tdb = m.tdb; var svc = m.svc; var gw = m.gw;
        var user = tdb.AddUser("90004");
        gw.OnAuth = (_, _, _, _) => Task.FromResult("sid-fresh");
        await svc.SetCredentialAsync(user.Id, "spj", "carol", "pw", default);
        var calls = 0;
        gw.OnShares = (_, _, _) => { calls++; return calls == 1 ? throw new SynoException("expired", sessionExpired: true) : Task.FromResult<IReadOnlyList<string>>(["/shared"]); };
        svc.DropSession(user.Id, "spj");
        var shares = await svc.ListSharesAsync(user.Id, "spj", default);
        Assert.Single(shares);
        Assert.True(gw.AuthCalls >= 2);
    }

    [Fact]
    public async Task ClearCredential_RemovesRow()
    {
        var m = Make();
        using var tdb = m.tdb; var svc = m.svc; var gw = m.gw;
        var user = tdb.AddUser("90005");
        gw.OnAuth = (_, _, _, _) => Task.FromResult("sid-ok");
        await svc.SetCredentialAsync(user.Id, "spj", "dave", "pw", default);
        await svc.ClearCredentialAsync(user.Id, "spj", default);
        var (found, _) = await svc.GetCredentialAsync(user.Id, "spj", default);
        Assert.False(found);
    }

    [Fact]
    public async Task TwoServers_AreIsolated()
    {
        var m = Make();
        using var tdb = m.tdb; var svc = m.svc; var gw = m.gw;
        var user = tdb.AddUser("90006");
        gw.OnAuth = (_, _, _, _) => Task.FromResult("sid-ok");
        await svc.SetCredentialAsync(user.Id, "spj", "alice", "pw1", default);
        await svc.SetCredentialAsync(user.Id, "krw", "alice", "pw2", default);
        await svc.ClearCredentialAsync(user.Id, "spj", default);
        var (foundSpj, _) = await svc.GetCredentialAsync(user.Id, "spj", default);
        var (foundKrw, _) = await svc.GetCredentialAsync(user.Id, "krw", default);
        Assert.False(foundSpj);
        Assert.True(foundKrw);
    }

    [Fact]
    public async Task UploadAsync_FormatsQueryAndMultipartAccordingToSynologySpec()
    {
        string? capturedUri = null;
        string? capturedBody = null;
        var handler = new DelegateHttpMessageHandler(async req =>
        {
            capturedUri = req.RequestUri?.ToString();
            capturedBody = req.Content is not null ? await req.Content.ReadAsStringAsync() : null;
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("{\"success\":true,\"data\":{\"file\":\"doc.pdf\"}}", Encoding.UTF8, "application/json")
            };
        });
        var http = new HttpClient(handler);
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Synology:Servers:spj"] = "https://192.168.2.16:30001"
        }).Build();

        var gateway = new HttpSynologyGateway(http, cfg);
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("sample pdf bytes"));

        await gateway.UploadAsync("spj", "test-sid-123", "/home/chat/room1", "doc.pdf", stream, default);

        Assert.NotNull(capturedUri);
        Assert.Contains("api=SYNO.FileStation.Upload", capturedUri);
        Assert.Contains("version=2", capturedUri);
        Assert.Contains("method=upload", capturedUri);
        Assert.Contains("_sid=test-sid-123", capturedUri);

        Assert.NotNull(capturedBody);
        Assert.Contains("name=\"path\"", capturedBody);
        Assert.Contains("/home/chat/room1", capturedBody);
        Assert.Contains("name=\"create_parents\"", capturedBody);
        Assert.Contains("true", capturedBody);
        Assert.Contains("name=\"overwrite\"", capturedBody);
        Assert.Contains("name=\"file\"; filename=\"doc.pdf\"", capturedBody);
        Assert.Contains("sample pdf bytes", capturedBody);
    }

    [Fact]
    public async Task WordDocumentService_ConvertToPdf_ProducesValidPdfBytes()
    {
        var factory = new IPop.Infrastructure.Documents.DevExpressDocumentFactory();
        var service = new IPop.Infrastructure.Documents.WordDocumentService(factory);
        var docxBytes = await service.CreateSampleDocumentAsync("Test Doc", "This is content");

        using var ms = new MemoryStream(docxBytes);
        var pdfBytes = await service.ConvertToPdfAsync(ms);

        Assert.NotNull(pdfBytes);
        Assert.True(pdfBytes.Length > 0);
        // PDF header magic bytes %PDF-
        Assert.Equal((byte)'%', pdfBytes[0]);
        Assert.Equal((byte)'P', pdfBytes[1]);
        Assert.Equal((byte)'D', pdfBytes[2]);
        Assert.Equal((byte)'F', pdfBytes[3]);
    }

    [Fact]
    public async Task SessionCache_OutlivesTheScopedService()
    {
        // FileAccessService is scoped: each request gets a new instance. The shared cache must
        // carry the NAS session across them, or every folder open costs a fresh DSM login.
        using var tdb = new TestDb();
        var gw = new StubGateway();
        var protector = new EphemeralDataProtectionProvider();
        var cfg = new ConfigurationBuilder().Build();
        var cache = new SynologySessionCache();
        FileAccessService NewScope() => new(tdb.Db, gw, protector, cfg, NullLogger<FileAccessService>.Instance, cache);

        var user = tdb.AddUser("90010");
        await NewScope().SetCredentialAsync(user.Id, "spj", "erin", "pw", default);
        var loginsAfterConnect = gw.AuthCalls;

        await NewScope().ListFolderAsync(user.Id, "spj", "/share", default);
        await NewScope().ListSharesAsync(user.Id, "spj", default);
        await NewScope().GetStorageInfoAsync(user.Id, "spj", default);

        Assert.Equal(loginsAfterConnect, gw.AuthCalls);
    }

    [Fact]
    public async Task ConcurrentRequests_OnAColdSession_LogInOnce()
    {
        using var tdb = new TestDb();
        var gw = new StubGateway();
        var protector = new EphemeralDataProtectionProvider();
        var cfg = new ConfigurationBuilder().Build();
        var cache = new SynologySessionCache();
        FileAccessService NewScope() => new(tdb.Db, gw, protector, cfg, NullLogger<FileAccessService>.Instance, cache);

        var user = tdb.AddUser("90011");
        await NewScope().SetCredentialAsync(user.Id, "spj", "fay", "pw", default);
        cache.Remove(user.Id, "spj");
        gw.AuthCalls = 0;
        gw.OnAuth = async (_, u, _, _) => { await Task.Delay(50); return "sid-" + u; };

        // The page fires listing and storage together; they must share one login.
        // (The per-session lock also serialises the credential read on the shared test DbContext.)
        await Task.WhenAll(
            NewScope().ListFolderAsync(user.Id, "spj", "/share", default),
            NewScope().ListSharesAsync(user.Id, "spj", default),
            NewScope().GetStorageInfoAsync(user.Id, "spj", default));

        Assert.Equal(1, gw.AuthCalls);
    }

    [Fact]
    public async Task ConnectAll_StoresEverywhereAccepted_AndReportsTheRest()
    {
        var m = Make();
        using var tdb = m.tdb; var svc = m.svc; var gw = m.gw;
        var user = tdb.AddUser("90012");
        gw.OnAuth = (server, _, _, _) => server switch
        {
            "jtb" => throw new SynoException("bad", authFailure: true),
            "smg" => throw new HttpRequestException("timeout"),
            _ => Task.FromResult("sid-" + server),
        };

        var results = await svc.ConnectAllAsync(user.Id, "gina", "pw", default);

        Assert.Equal("connected", results.Single(r => r.Server == "spj").Status);
        Assert.Equal("connected", results.Single(r => r.Server == "krw").Status);
        Assert.Equal("rejected", results.Single(r => r.Server == "jtb").Status);
        Assert.Equal("unreachable", results.Single(r => r.Server == "smg").Status);
        var stored = (await svc.ListCredentialsAsync(user.Id, default)).Select(c => c.Server).OrderBy(s => s);
        Assert.Equal(["krw", "spj"], stored);
    }

    [Fact]
    public async Task ConnectAll_RejectedSite_KeepsItsOwnCredential()
    {
        var m = Make();
        using var tdb = m.tdb; var svc = m.svc; var gw = m.gw;
        var user = tdb.AddUser("90013");
        await svc.SetCredentialAsync(user.Id, "jtb", "jtb-local", "other", default);
        gw.OnAuth = (server, u, _, _) => server == "jtb" && u == "hana" ? throw new SynoException("bad", authFailure: true) : Task.FromResult("sid");

        await svc.ConnectAllAsync(user.Id, "hana", "pw", default);

        var (_, jtb) = await svc.GetCredentialAsync(user.Id, "jtb", default);
        Assert.Equal("jtb-local", jtb!.Username);
        Assert.False(jtb.IsInvalid);
    }

    private sealed class DelegateHttpMessageHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => send(request);
    }
}
