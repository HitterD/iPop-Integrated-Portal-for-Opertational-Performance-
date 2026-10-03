using System.Text;
using IPop.Application.Chat;
using IPop.Application.Links;
using IPop.Application.News;
using IPop.Infrastructure.Chat;
using IPop.Infrastructure.Links;
using IPop.Infrastructure.News;
using IPop.Infrastructure.Projects;
using IPop.Infrastructure.Rbac;
using IPop.Core.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using static IPop.Tests.LinkPreviewFetchTests;

namespace IPop.Tests;

public class LinkPreviewServiceTests
{
    // ── OpenGraph parsing ───────────────────────────────────────────────

    private static readonly Uri Page = new("https://news.example.com/2026/10/story.html");

    [Fact]
    public void Og_tags_win_and_relative_image_resolves()
    {
        const string html = """
            <html><head>
              <title>Fallback title</title>
              <meta property="og:title" content="Kapal &amp; Pelabuhan &quot;Baru&quot;">
              <meta property="og:description" content="  Deskripsi   panjang
                 dengan spasi  ">
              <meta property="og:site_name" content="Example News">
              <meta property="og:image" content="/img/cover.jpg">
              <meta name="description" content="meta description">
            </head><body></body></html>
            """;
        var og = OpenGraphParser.Parse(html, Page);
        Assert.Equal("Kapal & Pelabuhan \"Baru\"", og.Title);
        Assert.Equal("Deskripsi panjang dengan spasi", og.Description);
        Assert.Equal("Example News", og.SiteName);
        Assert.Equal("https://news.example.com/img/cover.jpg", og.Image!.AbsoluteUri);
    }

    [Fact]
    public void Twitter_then_title_and_meta_description_fallbacks()
    {
        var tw = OpenGraphParser.Parse("""<head><meta name="twitter:title" content="TW"><meta name="twitter:image" content="pic.png"><title>T</title></head>""", Page);
        Assert.Equal("TW", tw.Title);
        Assert.Equal("https://news.example.com/2026/10/pic.png", tw.Image!.AbsoluteUri);

        var plain = OpenGraphParser.Parse("<head><title> Just a title </title><meta name=\"description\" content=\"Desc\"></head>", Page);
        Assert.Equal("Just a title", plain.Title);
        Assert.Equal("Desc", plain.Description);
        Assert.Null(plain.Image);
        Assert.True(plain.IsUseful);

        Assert.False(OpenGraphParser.Parse("<body>no meta</body>", Page).IsUseful);
    }

    [Fact]
    public void Long_values_are_trimmed_and_bad_image_schemes_dropped()
    {
        var og = OpenGraphParser.Parse(
            $"<meta property=\"og:title\" content=\"{new string('a', 500)}\"><meta property=\"og:description\" content=\"{new string('b', 900)}\"><meta property=\"og:image\" content=\"javascript:alert(1)\">", Page);
        Assert.True(og.Title!.Length <= OpenGraphParser.MaxTitle);
        Assert.True(og.Description!.Length <= OpenGraphParser.MaxDescription);
        Assert.Null(og.Image);
    }

    [Fact]
    public void Charset_from_meta_is_respected()
    {
        var latin1 = Encoding.Latin1.GetBytes("<html><head><meta charset=\"iso-8859-1\"><title>Café São</title></head></html>");
        Assert.Equal("Café São", OpenGraphParser.Parse(latin1, null, Page).Title);

        var win = Encoding.Latin1.GetBytes("<title>Naïve</title>");
        Assert.Equal("Naïve", OpenGraphParser.Parse(win, "windows-1252", Page).Title);
    }

    // ── Image signature ─────────────────────────────────────────────────

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static (string u, long exp, string sig) ParseSigned(string url)
    {
        var q = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(new Uri("https://x" + url).Query);
        return (q["u"]!, long.Parse(q["exp"]!), q["sig"]!);
    }

    [Fact]
    public void Signed_image_url_verifies_and_expires()
    {
        var clock = new FixedTime(DateTimeOffset.Parse("2026-10-03T10:15:00Z"));
        var signer = new LinkPreviewImageSigner("test-secret-0123456789-0123456789", clock);
        var url = signer.CreateUrl(new Uri("https://cdn.example.com/a.jpg?x=1"), TimeSpan.FromHours(12));
        Assert.StartsWith(LinkPreviewImageSigner.Path + "?u=", url);
        var (u, exp, sig) = ParseSigned(url);

        Assert.True(signer.Verify(u, exp, sig));
        Assert.False(signer.Verify("https://cdn.example.com/b.jpg", exp, sig));   // other target
        Assert.False(signer.Verify(u, exp + 3600, sig));                          // extended expiry
        Assert.False(signer.Verify(u, exp, sig[..^2] + "AA"));                    // tampered
        Assert.False(signer.Verify(u, exp, "not base64 !!"));
        Assert.False(new LinkPreviewImageSigner("another-secret-xxxxxxxxxxxxxxxxxxxx", clock).Verify(u, exp, sig));

        clock.Now = DateTimeOffset.FromUnixTimeSeconds(exp + 1);
        Assert.False(signer.Verify(u, exp, sig));
    }

    // ── Service: external + internal ────────────────────────────────────

    private static LinkPreviewService NewService(TestDb t, SafeHttpFetcher fetcher, LinkPreviewCache? cache = null)
    {
        var db = t.Verify();
        var resolver = new InternalLinkResolver(new NewsService(db), new ProjectService(db), new PermissionEvaluator(db));
        return new LinkPreviewService(cache ?? new LinkPreviewCache(), fetcher, new LinkPreviewOptions(),
            new LinkPreviewImageSigner("test-secret-0123456789-0123456789"), resolver, NullLogger<LinkPreviewService>.Instance);
    }

    [Fact]
    public async Task External_preview_is_fetched_once_then_cached_and_image_is_signed()
    {
        using var t = new TestDb();
        var dns = new FakeDns(new() { ["example.com"] = ["93.184.216.34"] });
        var transport = new FakeTransport(_ => Html("<meta property=\"og:title\" content=\"Hello\"><meta property=\"og:image\" content=\"https://example.com/i.png\">"));
        using var fetcher = new SafeHttpFetcher(dns, transport);
        var svc = NewService(t, fetcher);

        var p = await svc.GetAsync("https://Example.com/page#frag", Guid.NewGuid(), default);
        Assert.NotNull(p);
        Assert.Equal("external", p!.Kind);
        Assert.Equal("Hello", p.Title);
        Assert.Equal("example.com", p.SiteName);
        Assert.Equal("https://example.com/page", p.Url);
        Assert.StartsWith("/api/link-preview/image?u=https%3A%2F%2Fexample.com%2Fi.png&exp=", p.Image);
        Assert.Null(p.Internal);

        await svc.GetAsync("https://example.com/page", Guid.NewGuid(), default);
        Assert.Single(transport.Requests);
    }

    [Fact]
    public async Task Blocked_or_empty_targets_yield_null_and_are_negatively_cached()
    {
        using var t = new TestDb();
        var dns = new FakeDns(new() { ["intranet.example"] = ["10.0.0.9"], ["empty.example"] = ["93.184.216.34"] });
        var transport = new FakeTransport(_ => Html("<body>nothing</body>"));
        using var fetcher = new SafeHttpFetcher(dns, transport);
        var svc = NewService(t, fetcher);

        Assert.Null(await svc.GetAsync("http://intranet.example/", Guid.NewGuid(), default));
        Assert.Null(await svc.GetAsync("https://empty.example/", Guid.NewGuid(), default));
        Assert.Null(await svc.GetAsync("https://empty.example/", Guid.NewGuid(), default));
        Assert.Single(transport.Requests);
        Assert.Null(await svc.GetAsync("javascript:alert(1)", Guid.NewGuid(), default));
        Assert.Null(await svc.GetAsync("not a url", Guid.NewGuid(), default));
    }

    private static void GrantRead(TestDb t, Guid userId, params string[] codes)
    {
        var role = t.EnsureRole("Employee");
        foreach (var code in codes)
        {
            var perm = t.Db.Permissions.FirstOrDefault(p => p.Code == code);
            if (perm is null) { perm = new Permission { Id = Guid.NewGuid(), Code = code, Description = code }; t.Db.Permissions.Add(perm); }
            if (!t.Db.RolePermissions.Any(rp => rp.RoleId == role.Id && rp.PermissionId == perm.Id))
                t.Db.RolePermissions.Add(new RolePermission { RoleId = role.Id, PermissionId = perm.Id });
        }
        t.Db.SaveChanges();
        t.AssignRole(userId, "Employee");
    }

    [Fact]
    public async Task Internal_links_return_native_data_only_to_authorized_viewers_and_are_never_fetched()
    {
        using var t = new TestDb();
        var owner = t.AddUser("OWN");
        var member = t.AddUser("MEM");
        var outsider = t.AddUser("OUT");
        GrantRead(t, member.Id, "projects.read", "news.read");
        GrantRead(t, outsider.Id, "projects.read", "news.read");
        GrantRead(t, owner.Id, "projects.read", "news.read");
        var project = t.AddProject("Secret Project", owner.Id);
        t.AddProjectMember(project.Id, member.Id);
        var task = new ProjectTask
        {
            Id = Guid.NewGuid(), ProjectId = project.Id, GroupId = t.Db.ProjectGroups.First(g => g.ProjectId == project.Id).Id,
            Title = "Hidden task", Status = "doing", CreatedById = owner.Id, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        t.Db.ProjectTasks.Add(task);
        t.Db.SaveChanges();

        var transport = new FakeTransport(_ => throw new InvalidOperationException("internal links must not be fetched"));
        using var fetcher = new SafeHttpFetcher(new FakeDns([]), transport);
        var cache = new LinkPreviewCache();

        var projUrl = $"https://ipop.santos.co.id/projects/{project.Id}";
        var taskUrl = $"https://ipop.santos.co.id/projects/{project.Id}?task={task.Id}";

        var m = await NewService(t, fetcher, cache).GetAsync(projUrl, member.Id, default);
        Assert.Equal("internal", m!.Kind);
        Assert.Equal("project", m.Internal!.Type);
        Assert.Equal("Secret Project", m.Internal.Title);
        Assert.Equal(project.Id, m.Internal.Id);

        var mt = await NewService(t, fetcher, cache).GetAsync(taskUrl, member.Id, default);
        Assert.Equal("task", mt!.Internal!.Type);
        Assert.Equal("Hidden task", mt.Internal.Title);

        // Same URLs, same shared cache — the outsider must not see the member's card.
        foreach (var url in new[] { projUrl, taskUrl })
        {
            var o = await NewService(t, fetcher, cache).GetAsync(url, outsider.Id, default);
            Assert.Equal("internal", o!.Kind);
            Assert.Equal("page", o.Internal!.Type);
            Assert.Null(o.Internal.Id);
            Assert.Null(o.Internal.Title);
            Assert.Null(o.Internal.Subtitle);
            Assert.Null(o.Title);
            Assert.DoesNotContain("Secret", o.Description ?? "");
        }

        // App-relative paths count as internal too; unknown routes are bare pages.
        var rel = await NewService(t, fetcher, cache).GetAsync("/settings", member.Id, default);
        Assert.Equal("page", rel!.Internal!.Type);
        Assert.Equal("/settings", rel.Internal.Path);
        Assert.Empty(transport.Requests);
    }

    [Fact]
    public async Task Internal_news_respects_visibility()
    {
        using var t = new TestDb();
        var hr = t.AddHr("HR1");
        var reader = t.AddUser("R1");
        var other = t.AddUser("R2");
        GrantRead(t, reader.Id, "news.read");
        GrantRead(t, other.Id, "news.read");
        var created = await new NewsService(t.Verify()).CreateAsync(hr.Id, new CreateNewsRequest(
            Type: "memo", Title: "Targeted memo", Body: "body",
            Targets: [new NewsTargetInput("user", reader.Nik)]), default);
        Assert.True(created.Success, created.Error);
        Assert.True((await new NewsService(t.Verify()).PublishAsync(hr.Id, created.News!.Id, default)).Success);

        using var fetcher = new SafeHttpFetcher(new FakeDns([]), new FakeTransport(_ => throw new InvalidOperationException()));
        var url = $"https://ipop.santos.co.id/news/{created.News.Id}";

        var seen = await NewService(t, fetcher).GetAsync(url, reader.Id, default);
        Assert.Equal("news", seen!.Internal!.Type);
        Assert.Equal("Targeted memo", seen.Internal.Title);
        Assert.StartsWith("Memo", seen.Internal.Subtitle);

        var hidden = await NewService(t, fetcher).GetAsync(url, other.Id, default);
        Assert.Equal("page", hidden!.Internal!.Type);
        Assert.Null(hidden.Internal.Title);
    }

    // ── Chat: LinkPreviewOff round-trip ─────────────────────────────────

    [Fact]
    public async Task Chat_LinkPreviewOff_round_trips_through_send_and_history()
    {
        using var t = new TestDb();
        var a = t.AddUser("A");
        var b = t.AddUser("B");
        var room = t.AddRoom();
        t.AddMember(room.Id, a.Id);
        t.AddMember(room.Id, b.Id);

        var off = await new ChatService(t.Verify()).SendMessageAsync(a.Id,
            new SendMessageRequest(room.Id, "lihat https://example.com", Guid.NewGuid(), null, "text", LinkPreviewOff: true), default);
        Assert.True(off.Success, off.Error);
        Assert.True(off.Message!.LinkPreviewOff);

        var on = await new ChatService(t.Verify()).SendMessageAsync(a.Id,
            new SendMessageRequest(room.Id, "dan https://example.org", Guid.NewGuid(), null, "text"), default);
        Assert.False(on.Message!.LinkPreviewOff);

        var history = await new ChatService(t.Verify()).GetMessagesAsync(room.Id, b.Id, 1, 50, default);
        Assert.True(history.Items.Single(m => m.Id == off.Message.Id).LinkPreviewOff);
        Assert.False(history.Items.Single(m => m.Id == on.Message.Id).LinkPreviewOff);

        var thread = await new ChatService(t.Verify()).SendThreadReplyAsync(b.Id, on.Message.Id,
            new SendMessageRequest(room.Id, "reply https://example.net", Guid.NewGuid(), null, "text", LinkPreviewOff: true), default);
        Assert.True(thread.Success, thread.Error);
        Assert.True(thread.Message!.LinkPreviewOff);
    }
}
