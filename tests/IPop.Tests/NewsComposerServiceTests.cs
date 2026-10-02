using IPop.Application.News;
using IPop.Core.Entities;
using IPop.Infrastructure.News;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace IPop.Tests;

/// Private News composer drafts: ownership, versioned autosave, incomplete content, and the
/// guarantee that autosave can never change what readers see.
public class NewsComposerServiceTests
{
    private static NewsComposerService Composer(TestDb t) => new(t.Verify(), new NewsService(t.Verify()));

    private static Attachment AddAttachment(TestDb t, Guid uploaderId, string area = "news", string name = "f.pdf")
    {
        var a = new Attachment
        {
            Id = Guid.NewGuid(), Area = area, FileName = name, ContentType = "application/pdf",
            Size = 10, Sha256 = new string('a', 64), StoragePath = $"{area}/{Guid.NewGuid():N}.pdf",
            UploaderId = uploaderId, CreatedAt = DateTime.UtcNow,
        };
        t.Db.Attachments.Add(a);
        t.Db.SaveChanges();
        return a;
    }

    private static NewsComposerMedia AddMedia(TestDb t, Guid ownerId, string kind = "image")
    {
        var m = new NewsComposerMedia
        {
            Id = Guid.NewGuid(), OwnerId = ownerId, Kind = kind, FileName = "pic.png",
            ContentType = "image/png", Size = 12, Sha256 = new string('b', 64), CreatedAt = DateTime.UtcNow,
        };
        t.Db.NewsComposerMedia.Add(m);
        t.Db.SaveChanges();
        return m;
    }

    private static async Task<NewsPost> AddLivePostAsync(TestDb t, Guid authorId, string status = "published", string body = "live body")
    {
        var now = DateTime.UtcNow;
        var post = new NewsPost
        {
            Id = Guid.NewGuid(), Type = "news", Title = "Live post", Body = body, Status = status,
            AuthorId = authorId, CreatedAt = now, UpdatedAt = now,
            PublishedAt = status == "published" ? now : null,
        };
        t.Db.NewsPosts.Add(post);
        await t.Db.SaveChangesAsync();
        return post;
    }

    // ── Creation + authorization ──────────────────────────────────────────

    [Fact]
    public async Task New_draft_requires_hr_or_admin()
    {
        using var t = new TestDb();
        var employee = t.AddUser("EMP1");

        var denied = await Composer(t).CreateDraftAsync(employee.Id, new CreateNewsDraftRequest(), default);
        Assert.False(denied.Success);
        Assert.Equal("forbidden", denied.Error);
        Assert.False(await t.Verify().NewsComposerDrafts.AnyAsync());
    }

    [Fact]
    public async Task New_draft_never_creates_a_public_post_row()
    {
        using var t = new TestDb();
        var hr = t.AddHr("HR1");

        var created = await Composer(t).CreateDraftAsync(hr.Id, new CreateNewsDraftRequest(), default);
        Assert.True(created.Success, created.Error);
        Assert.Null(created.Draft!.PostId);
        Assert.Equal(0, created.Draft.Version);
        Assert.Equal("active", created.Draft.State);
        Assert.False(await t.Verify().NewsPosts.AnyAsync());
    }

    [Fact]
    public async Task Multiple_active_drafts_per_owner_are_allowed()
    {
        using var t = new TestDb();
        var hr = t.AddHr("HR1");
        var svc = Composer(t);

        var a = await svc.CreateDraftAsync(hr.Id, new CreateNewsDraftRequest(), default);
        var b = await svc.CreateDraftAsync(hr.Id, new CreateNewsDraftRequest(), default);
        Assert.True(a.Success && b.Success);
        Assert.NotEqual(a.Draft!.Id, b.Draft!.Id);

        var list = await svc.ListDraftsAsync(hr.Id, default);
        Assert.Equal(2, list.Items.Count);
    }

    [Fact]
    public async Task Revision_draft_is_seeded_from_the_live_post_and_requires_edit_rights()
    {
        using var t = new TestDb();
        var hr = t.AddHr("HR1");
        var other = t.AddUser("OTHER");
        var post = await AddLivePostAsync(t, hr.Id, body: "original body");

        // A non-author without news.publish cannot open a revision of someone else's post.
        var denied = await Composer(t).CreateDraftAsync(other.Id, new CreateNewsDraftRequest(post.Id), default);
        Assert.False(denied.Success);
        Assert.Equal("forbidden", denied.Error);

        var created = await Composer(t).CreateDraftAsync(hr.Id, new CreateNewsDraftRequest(post.Id), default);
        Assert.True(created.Success, created.Error);
        Assert.Equal(post.Id, created.Draft!.PostId);
        Assert.Equal("original body", created.Draft.Snapshot.Body);
        Assert.Equal(post.ContentVersion, created.Draft.BaseContentVersion);
    }

    [Fact]
    public async Task Revision_draft_is_allowed_for_the_post_author_without_manage_rights()
    {
        using var t = new TestDb();
        var author = t.AddUser("AUTHOR");
        var post = await AddLivePostAsync(t, author.Id);

        var created = await Composer(t).CreateDraftAsync(author.Id, new CreateNewsDraftRequest(post.Id), default);
        Assert.True(created.Success, created.Error);
        Assert.Equal(post.Id, created.Draft!.PostId);
    }

    [Fact]
    public async Task Draft_is_private_to_its_owner()
    {
        using var t = new TestDb();
        var hr = t.AddHr("HR1");
        var otherHr = t.AddHr("HR2");
        var created = await Composer(t).CreateDraftAsync(hr.Id, new CreateNewsDraftRequest(), default);

        var peek = await Composer(t).GetDraftAsync(otherHr.Id, created.Draft!.Id, default);
        Assert.False(peek.Success);
        Assert.Equal("not_found", peek.Error);
        Assert.Empty((await Composer(t).ListDraftsAsync(otherHr.Id, default)).Items);
    }

    // ── Autosave: incomplete content, CAS, live isolation ─────────────────

    [Fact]
    public async Task Autosave_accepts_incomplete_content()
    {
        using var t = new TestDb();
        var hr = t.AddHr("HR1");
        var svc = Composer(t);
        var created = await svc.CreateDraftAsync(hr.Id, new CreateNewsDraftRequest(), default);

        var saved = await svc.SaveDraftAsync(hr.Id, created.Draft!.Id,
            new SaveNewsDraftRequest(0, new NewsDraftSnapshotDto(Title: "Only a title so far")), default);

        Assert.True(saved.Success, saved.Error);
        Assert.Equal(1, saved.Draft!.Version);
        Assert.Equal("Only a title so far", saved.Draft.Snapshot.Title);
        Assert.Null(saved.Draft.Snapshot.Body);
    }

    [Fact]
    public async Task Stale_autosave_is_refused_by_the_version_predicate()
    {
        using var t = new TestDb();
        var hr = t.AddHr("HR1");
        var svc = Composer(t);
        var created = await svc.CreateDraftAsync(hr.Id, new CreateNewsDraftRequest(), default);
        var id = created.Draft!.Id;

        var first = await svc.SaveDraftAsync(hr.Id, id, new SaveNewsDraftRequest(0, new NewsDraftSnapshotDto(Title: "tab one")), default);
        Assert.True(first.Success, first.Error);

        // A second tab still holding version 0 must not overwrite the newer snapshot.
        var stale = await svc.SaveDraftAsync(hr.Id, id, new SaveNewsDraftRequest(0, new NewsDraftSnapshotDto(Title: "tab two")), default);
        Assert.False(stale.Success);
        Assert.Equal("version_conflict", stale.Error);

        var current = await svc.GetDraftAsync(hr.Id, id, default);
        Assert.Equal("tab one", current.Draft!.Snapshot.Title);
        Assert.Equal(1, current.Draft.Version);
    }

    [Fact]
    public async Task Autosave_never_changes_the_live_post()
    {
        using var t = new TestDb();
        var hr = t.AddHr("HR1");
        var reader = t.AddUser("READER");
        var post = await AddLivePostAsync(t, hr.Id, body: "readers see this");
        var svc = Composer(t);
        var draft = await svc.CreateDraftAsync(hr.Id, new CreateNewsDraftRequest(post.Id), default);

        var saved = await svc.SaveDraftAsync(hr.Id, draft.Draft!.Id, new SaveNewsDraftRequest(
            draft.Draft.Version,
            new NewsDraftSnapshotDto(Title: "unfinished rewrite", Body: "<p>never published</p>", BodyFormat: "html")), default);
        Assert.True(saved.Success, saved.Error);

        var live = await t.Verify().NewsPosts.AsNoTracking().FirstAsync(p => p.Id == post.Id);
        Assert.Equal("readers see this", live.Body);
        Assert.Equal("Live post", live.Title);
        Assert.Equal(post.ContentVersion, live.ContentVersion);

        // The reader still sees the old content, and the draft is nowhere in their feed.
        var readerView = await new NewsService(t.Verify()).GetAsync(post.Id, reader.Id, default);
        Assert.Equal("readers see this", readerView!.Body);
    }

    [Fact]
    public async Task Revision_save_is_refused_when_the_live_post_moved_on()
    {
        using var t = new TestDb();
        var hr = t.AddHr("HR1");
        var post = await AddLivePostAsync(t, hr.Id);
        var svc = Composer(t);
        var draft = await svc.CreateDraftAsync(hr.Id, new CreateNewsDraftRequest(post.Id), default);

        // Someone edits the live post meanwhile (legacy path bumps ContentVersion).
        await using (var v = t.Verify())
        {
            var moved = await v.NewsPosts.FirstAsync(p => p.Id == post.Id);
            moved.ContentVersion += 1;
            await v.SaveChangesAsync();
        }

        var saved = await svc.SaveDraftAsync(hr.Id, draft.Draft!.Id, new SaveNewsDraftRequest(
            draft.Draft.Version, new NewsDraftSnapshotDto(Title: "stale revision"),
            ExpectedPostContentVersion: draft.Draft.BaseContentVersion), default);

        Assert.False(saved.Success);
        Assert.Equal("post_content_conflict", saved.Error);
    }

    // ── Reference validation ──────────────────────────────────────────────

    [Fact]
    public async Task Draft_cannot_reference_media_owned_by_someone_else()
    {
        using var t = new TestDb();
        var hr = t.AddHr("HR1");
        var other = t.AddUser("OTHER");
        var foreign = AddMedia(t, other.Id);
        var svc = Composer(t);
        var draft = await svc.CreateDraftAsync(hr.Id, new CreateNewsDraftRequest(), default);

        var saved = await svc.SaveDraftAsync(hr.Id, draft.Draft!.Id, new SaveNewsDraftRequest(0,
            new NewsDraftSnapshotDto(Media: [new NewsDraftMediaRefDto(foreign.Id)])), default);
        Assert.False(saved.Success);
        Assert.Equal("invalid_media", saved.Error);
    }

    [Fact]
    public async Task Draft_cannot_reference_an_unrelated_upload()
    {
        using var t = new TestDb();
        var hr = t.AddHr("HR1");
        var other = t.AddUser("OTHER");
        var foreign = AddAttachment(t, other.Id);
        var svc = Composer(t);
        var draft = await svc.CreateDraftAsync(hr.Id, new CreateNewsDraftRequest(), default);

        var saved = await svc.SaveDraftAsync(hr.Id, draft.Draft!.Id, new SaveNewsDraftRequest(0,
            new NewsDraftSnapshotDto(AttachmentIds: [foreign.Id])), default);

        Assert.False(saved.Success);
        Assert.Equal("invalid_attachment", saved.Error);
    }

    [Fact]
    public async Task Draft_accepts_its_own_upload_and_own_media()
    {
        using var t = new TestDb();
        var hr = t.AddHr("HR1");
        var mine = AddAttachment(t, hr.Id);
        var media = AddMedia(t, hr.Id);
        var svc = Composer(t);
        var draft = await svc.CreateDraftAsync(hr.Id, new CreateNewsDraftRequest(), default);

        var saved = await svc.SaveDraftAsync(hr.Id, draft.Draft!.Id, new SaveNewsDraftRequest(0,
            new NewsDraftSnapshotDto(
                AttachmentIds: [mine.Id],
                Media: [new NewsDraftMediaRefDto(media.Id, "cover")],
                CoverMediaId: media.Id, CoverMode: "manual")), default);

        Assert.True(saved.Success, saved.Error);
        var snapshot = saved.Draft!.Snapshot;
        Assert.Equal([mine.Id], snapshot.AttachmentIds);
        Assert.Equal(media.Id, snapshot.CoverMediaId);
        Assert.Equal("manual", snapshot.CoverMode);
        Assert.Equal("cover", Assert.Single(snapshot.Media!).Role);
    }

    // ── Discard + resume ─────────────────────────────────────────────────

    [Fact]
    public async Task Discard_ends_the_draft_without_touching_the_post()
    {
        using var t = new TestDb();
        var hr = t.AddHr("HR1");
        var post = await AddLivePostAsync(t, hr.Id);
        var svc = Composer(t);
        var draft = await svc.CreateDraftAsync(hr.Id, new CreateNewsDraftRequest(post.Id), default);

        var discarded = await svc.DiscardDraftAsync(hr.Id, draft.Draft!.Id, default);
        Assert.True(discarded.Success, discarded.Error);
        Assert.Equal("discarded", discarded.Draft!.State);

        Assert.False((await svc.GetDraftAsync(hr.Id, draft.Draft.Id, default)).Success);
        Assert.Empty((await svc.ListDraftsAsync(hr.Id, default)).Items);

        var live = await t.Verify().NewsPosts.AsNoTracking().FirstAsync(p => p.Id == post.Id);
        Assert.Equal("Live post", live.Title);
    }

    [Fact]
    public async Task Another_user_cannot_discard_someone_elses_draft()
    {
        using var t = new TestDb();
        var hr = t.AddHr("HR1");
        var otherHr = t.AddHr("HR2");
        var draft = await Composer(t).CreateDraftAsync(hr.Id, new CreateNewsDraftRequest(), default);

        var denied = await Composer(t).DiscardDraftAsync(otherHr.Id, draft.Draft!.Id, default);
        Assert.False(denied.Success);
        Assert.Equal("forbidden", denied.Error);
        Assert.True((await Composer(t).GetDraftAsync(hr.Id, draft.Draft.Id, default)).Success);
    }

    [Fact]
    public async Task Inactive_owner_cannot_keep_editing_a_revision_draft()
    {
        using var t = new TestDb();
        var author = t.AddUser("AUTHOR");
        var post = await AddLivePostAsync(t, author.Id);
        var draft = await Composer(t).CreateDraftAsync(author.Id, new CreateNewsDraftRequest(post.Id), default);
        Assert.True(draft.Success, draft.Error);

        // The author is deactivated: their draft must not remain a way back in.
        await using (var v = t.Verify())
        {
            var row = await v.Users.FirstAsync(u => u.Id == author.Id);
            row.IsActive = false;
            await v.SaveChangesAsync();
        }

        var saved = await Composer(t).SaveDraftAsync(author.Id, draft.Draft!.Id,
            new SaveNewsDraftRequest(0, new NewsDraftSnapshotDto(Title: "sneaky")), default);
        Assert.False(saved.Success);
        Assert.Equal("forbidden", saved.Error);
    }

    // ── Preview ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Preview_returns_sanitized_html_and_plain_text()
    {
        using var t = new TestDb();
        var hr = t.AddHr("HR1");
        var svc = Composer(t);
        var draft = await svc.CreateDraftAsync(hr.Id, new CreateNewsDraftRequest(), default);
        await svc.SaveDraftAsync(hr.Id, draft.Draft!.Id, new SaveNewsDraftRequest(0, new NewsDraftSnapshotDto(
            Title: "Preview me",
            Body: "<p>Hello <b>world</b></p><script>alert(1)</script>",
            BodyFormat: "html")), default);

        var preview = await svc.PreviewAsync(hr.Id, draft.Draft.Id, default);
        Assert.NotNull(preview);
        Assert.DoesNotContain("<script", preview!.Html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("<b>world</b>", preview.Html);
        Assert.Equal("Hello world", preview.PlainText);
    }

    [Fact]
    public async Task Preview_of_a_plain_draft_escapes_markup()
    {
        using var t = new TestDb();
        var hr = t.AddHr("HR1");
        var svc = Composer(t);
        var draft = await svc.CreateDraftAsync(hr.Id, new CreateNewsDraftRequest(), default);
        await svc.SaveDraftAsync(hr.Id, draft.Draft!.Id, new SaveNewsDraftRequest(0, new NewsDraftSnapshotDto(
            Body: "<img src=x onerror=alert(1)>", BodyFormat: "plain")), default);

        var preview = await svc.PreviewAsync(hr.Id, draft.Draft.Id, default);
        Assert.NotNull(preview);
        Assert.DoesNotContain("<img", preview!.Html);
        Assert.Contains("&lt;img", preview.Html);
    }

    [Fact]
    public async Task Preview_of_someone_elses_draft_is_null()
    {
        using var t = new TestDb();
        var hr = t.AddHr("HR1");
        var otherHr = t.AddHr("HR2");
        var draft = await Composer(t).CreateDraftAsync(hr.Id, new CreateNewsDraftRequest(), default);

        Assert.Null(await Composer(t).PreviewAsync(otherHr.Id, draft.Draft!.Id, default));
    }

    // ── Snapshot round-trip ──────────────────────────────────────────────

    [Fact]
    public async Task Snapshot_round_trips_audience_and_scheduling()
    {
        using var t = new TestDb();
        var hr = t.AddHr("HR1");
        var dept = t.EnsureDepartment("IT", "IT");
        var due = DateTime.UtcNow.AddDays(3);
        var when = DateTime.UtcNow.AddDays(7);
        var svc = Composer(t);
        var draft = await svc.CreateDraftAsync(hr.Id, new CreateNewsDraftRequest(), default);

        var saved = await svc.SaveDraftAsync(hr.Id, draft.Draft!.Id, new SaveNewsDraftRequest(0, new NewsDraftSnapshotDto(
            Type: "memo", Title: "Memo", Body: "<p>body</p>", BodyFormat: "html",
            RequiresAcknowledgement: true, AckDueAt: due, IsPinned: true, PublishAt: when,
            Targets: [new NewsTargetInput("department", dept.Code)])), default);
        Assert.True(saved.Success, saved.Error);

        var resumed = await svc.GetDraftAsync(hr.Id, draft.Draft.Id, default);
        var snap = resumed.Draft!.Snapshot;
        Assert.Equal("memo", snap.Type);
        Assert.True(snap.RequiresAcknowledgement);
        Assert.True(snap.IsPinned);
        Assert.Equal(when, snap.PublishAt);
        var target = Assert.Single(snap.Targets!);
        Assert.Equal("department", target.Kind);
        Assert.Equal(dept.Code, target.Value);
    }

    [Fact]
    public async Task Cover_mode_survives_removal()
    {
        using var t = new TestDb();
        var hr = t.AddHr("HR1");
        var media = AddMedia(t, hr.Id);
        var svc = Composer(t);
        var draft = await svc.CreateDraftAsync(hr.Id, new CreateNewsDraftRequest(), default);
        var id = draft.Draft!.Id;

        var chosen = await svc.SaveDraftAsync(hr.Id, id, new SaveNewsDraftRequest(0, new NewsDraftSnapshotDto(
            CoverMediaId: media.Id, CoverMode: "manual", Media: [new NewsDraftMediaRefDto(media.Id, "cover")])), default);
        Assert.True(chosen.Success, chosen.Error);
        Assert.Equal("manual", chosen.Draft!.Snapshot.CoverMode);

        // Explicitly removed: the mode must stay "none" so an auto cover cannot reappear.
        var removed = await svc.SaveDraftAsync(hr.Id, id, new SaveNewsDraftRequest(1, new NewsDraftSnapshotDto(
            CoverMediaId: null, CoverMode: "none")), default);
        Assert.True(removed.Success, removed.Error);
        Assert.Equal("none", removed.Draft!.Snapshot.CoverMode);
        Assert.Null(removed.Draft.Snapshot.CoverMediaId);
    }
}