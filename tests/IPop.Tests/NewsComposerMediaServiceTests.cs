using System.Security.Cryptography;
using IPop.Application.Attachments;
using IPop.Application.News;
using IPop.Core.Entities;
using IPop.Infrastructure.News;
using IPop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace IPop.Tests;

/// <summary>
/// Composer media: upload validation, draft-scoped authorization, ticket-context re-checks and the
/// conservative 24h cleanup. Uses an in-memory attachment store so no NAS is required; the video path
/// exercises a real local-disk store directory.
/// </summary>
public class NewsComposerMediaServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ipop-composer-media-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private NewsComposerMediaService Svc(TestDb t, FakeStore? store = null)
    {
        var db = t.Verify();
        return new NewsComposerMediaService(db, new NewsService(db), store ?? new FakeStore(t.Db),
            new NewsComposerMediaOptions { LocalRoot = _root });
    }

    // ── Fixtures ────────────────────────────────────────────────────────

    private static NewsComposerDraft AddDraft(TestDb t, Guid ownerId, Guid? postId = null, string state = "active")
    {
        var d = new NewsComposerDraft
        {
            Id = Guid.NewGuid(), OwnerId = ownerId, PostId = postId, State = state,
            BodyFormat = "plain", CoverMode = "auto", Version = 0,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        t.Db.NewsComposerDrafts.Add(d);
        t.Db.SaveChanges();
        return d;
    }

    private static NewsComposerMedia AddMedia(TestDb t, Guid ownerId, string kind, Guid? uploadDraftId = null, DateTime? createdAt = null)
    {
        var m = new NewsComposerMedia
        {
            Id = Guid.NewGuid(), OwnerId = ownerId, Kind = kind, UploadDraftId = uploadDraftId,
            FileName = "a" + kind, ContentType = kind == "video" ? "video/mp4" : "image/png",
            Size = 10, Sha256 = "00", CreatedAt = createdAt ?? DateTime.UtcNow,
        };
        t.Db.NewsComposerMedia.Add(m);
        t.Db.SaveChanges();
        return m;
    }

    private static string TempImage(string ext = ".png", int size = 64)
    {
        var path = Path.Combine(Path.GetTempPath(), $"ipop-test-{Guid.NewGuid():N}{ext}");
        var bytes = new byte[size];
        new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(bytes, 0);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static string TempVideo(string ext = ".mp4", int size = 4096)
    {
        var path = Path.Combine(Path.GetTempPath(), $"ipop-test-{Guid.NewGuid():N}{ext}");
        var bytes = new byte[size];
        "\0\0\0\u0018ftypmp42"u8.ToArray().CopyTo(bytes, 0);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static NewsComposerMediaUpload Upload(Guid draftId, string kind, string path, string fileName) =>
        new(draftId, kind, path, fileName, null);

    // ── Upload ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Image_upload_creates_media_with_upload_scope_and_no_content_ref()
    {
        using var t = new TestDb();
        var author = t.AddAdmin("AUTH");
        var draft = AddDraft(t, author.Id);
        var store = new FakeStore(t.Db);

        var tmp = TempImage();
        var res = await Svc(t, store).UploadAsync(author.Id, Upload(draft.Id, "image", tmp, "photo.png"), default);
        Assert.True(res.Success, res.Error);
        var m = res.Media!;
        Assert.Equal("image", m.Kind);
        Assert.Equal("photo.png", m.FileName);
        Assert.Equal(draft.Id, m.ContextId);
        Assert.Equal("draft", m.ContextKind);

        var row = await t.Verify().NewsComposerMedia.AsNoTracking().FirstAsync(x => x.Id == m.MediaId);
        Assert.Equal(draft.Id, row.UploadDraftId);
        Assert.NotNull(row.AttachmentId);
        Assert.Equal("news", (await t.Verify().Attachments.AsNoTracking().FirstAsync(a => a.Id == row.AttachmentId)).Area);

        // The upload must NOT write the CAS-owned ref rows: an upload racing an autosave would
        // otherwise clobber the ref replacement.
        Assert.False(await t.Verify().NewsComposerDraftMedia.AnyAsync(r => r.DraftId == draft.Id));
    }

    [Fact]
    public async Task Image_over_5mb_and_spoofed_signature_are_rejected()
    {
        using var t = new TestDb();
        var author = t.AddAdmin("AUTH");
        var draft = AddDraft(t, author.Id);

        var big = TempImage(size: (int)NewsComposerMediaLimits.MaxImageBytes + 1);
        Assert.Equal("too_large", (await Svc(t).UploadAsync(author.Id, Upload(draft.Id, "image", big, "big.png"), default)).Error);
        File.Delete(big);

        // A text file renamed to .png must fail the signature check.
        var fake = Path.Combine(Path.GetTempPath(), $"ipop-test-{Guid.NewGuid():N}.png");
        File.WriteAllText(fake, "this is definitely not a png");
        Assert.Equal("blocked_type", (await Svc(t).UploadAsync(author.Id, Upload(draft.Id, "image", fake, "fake.png"), default)).Error);
        File.Delete(fake);

        // An unapproved extension never reaches the store.
        var ok = TempImage();
        Assert.Equal("blocked_type", (await Svc(t).UploadAsync(author.Id, Upload(draft.Id, "file", ok, "run.exe"), default)).Error);
        File.Delete(ok);

        Assert.Empty(await t.Verify().NewsComposerMedia.ToListAsync());
    }

    [Fact]
    public async Task Upload_requires_draft_ownership_and_an_authorized_edit_context()
    {
        using var t = new TestDb();
        var author = t.AddHr("HR1");
        var stranger = t.AddHr("HR2");
        var draft = AddDraft(t, author.Id);

        var tmp = TempImage();
        // A stranger naming someone else's draft gets "not_found" (drafts are private).
        Assert.Equal("not_found", (await Svc(t).UploadAsync(stranger.Id, Upload(draft.Id, "image", tmp, "x.png"), default)).Error);
        // A draft that is not an active edit context is refused.
        var discarded = AddDraft(t, author.Id, state: "discarded");
        Assert.Equal("not_active", (await Svc(t).UploadAsync(author.Id, Upload(discarded.Id, "image", tmp, "x.png"), default)).Error);
        // An inactive user is refused before anything else.
        var inactive = t.AddAdmin("DEAD"); inactive.IsActive = false; t.Db.SaveChanges();
        var draft2 = AddDraft(t, inactive.Id);
        Assert.Equal("forbidden", (await Svc(t).UploadAsync(inactive.Id, Upload(draft2.Id, "image", tmp, "x.png"), default)).Error);
        File.Delete(tmp);
    }

    [Fact]
    public async Task Video_upload_stores_locally_and_allows_only_one_per_draft()
    {
        using var t = new TestDb();
        var author = t.AddAdmin("AUTH");
        var draft = AddDraft(t, author.Id);

        var first = await Svc(t).UploadAsync(author.Id, Upload(draft.Id, "video", TempVideo(), "clip.mp4"), default);
        Assert.True(first.Success, first.Error);
        var row = await t.Verify().NewsComposerMedia.AsNoTracking().FirstAsync(m => m.Id == first.Media!.MediaId);
        Assert.NotNull(row.LocalPath);
        Assert.True(File.Exists(Path.Combine(_root, row.LocalPath!)));

        // A second video in the same draft is refused before the 100 MB body is stored.
        var second = await Svc(t).UploadAsync(author.Id, Upload(draft.Id, "video", TempVideo(), "clip2.mp4"), default);
        Assert.Equal("video_limit", second.Error);

        // A non-video signature named .mp4 is rejected.
        var fake = Path.Combine(Path.GetTempPath(), $"ipop-test-{Guid.NewGuid():N}.mp4");
        File.WriteAllText(fake, "nope");
        var other = AddDraft(t, author.Id);
        Assert.Equal("blocked_type", (await Svc(t).UploadAsync(author.Id, Upload(other.Id, "video", fake, "bad.mp4"), default)).Error);
        File.Delete(fake);
    }

    // ── Read authorization + ticket context re-check ────────────────────

    [Fact]
    public async Task Open_rechecks_context_ownership_active_user_and_draft_state()
    {
        using var t = new TestDb();
        var author = t.AddAdmin("AUTH");
        var stranger = t.AddAdmin("OTHER");
        var draft = AddDraft(t, author.Id);
        var store = new FakeStore(t.Db);
        var res = await Svc(t, store).UploadAsync(author.Id, Upload(draft.Id, "image", TempImage(), "p.png"), default);
        var mediaId = res.Media!.MediaId;

        // The uploader may read it inside the draft it was uploaded into.
        Assert.NotNull(await Svc(t, store).OpenAsync(mediaId, author.Id, "draft", draft.Id, false, default));
        // A stranger may not, even with a well-formed ticket.
        Assert.Null(await Svc(t, store).OpenAsync(mediaId, stranger.Id, "draft", draft.Id, false, default));
        // A stale/other context id does not authorize it.
        Assert.Null(await Svc(t, store).OpenAsync(mediaId, author.Id, "draft", Guid.NewGuid(), false, default));

        // Discarding the draft closes access immediately, without touching the ticket.
        draft.State = "discarded"; t.Db.SaveChanges();
        Assert.Null(await Svc(t, store).OpenAsync(mediaId, author.Id, "draft", draft.Id, false, default));

        // A deactivated user loses access even for their own asset.
        draft.State = "active"; t.Db.SaveChanges();
        var u = t.Db.Users.First(x => x.Id == author.Id); u.IsActive = false; t.Db.SaveChanges();
        Assert.Null(await Svc(t, store).OpenAsync(mediaId, author.Id, "draft", draft.Id, false, default));
    }

    [Fact]
    public async Task Open_for_a_post_requires_visibility_and_a_live_post_reference()
    {
        using var t = new TestDb();
        var author = t.AddAdmin("AUTH");
        var member = t.AddUser("M1");
        var outsider = t.AddUser("OUT");
        var dept = t.EnsureDepartment("D1", "Dept 1");
        t.AssignDepartment(member.Id, dept.Id);

        var post = new NewsPost
        {
            Id = Guid.NewGuid(), Type = "news", Title = "T", Body = "B", Status = "published",
            AuthorId = author.Id, PublishedAt = DateTime.UtcNow, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        t.Db.NewsPosts.Add(post);
        // The post is public: no targets means "all", which both readers satisfy.
        t.Db.SaveChanges();

        var media = AddMedia(t, author.Id, "image");
        var store = new FakeStore(t.Db);
        var stored = await store.PutAsync(AttachmentAreas.News, "p.png", "image/png", new MemoryStream([1, 2, 3]), author.Id, default);
        media.AttachmentId = stored.Id;
        t.Db.NewsPostMedia.Add(new NewsPostMedia { NewsPostId = post.Id, MediaId = media.Id, Role = "inline", SortOrder = 0 });
        t.Db.SaveChanges();

        // Both readers may see the public post's media; an unknown context does not authorize it.
        Assert.NotNull(await Svc(t, store).OpenAsync(media.Id, member.Id, "post", post.Id, false, default));
        Assert.NotNull(await Svc(t, store).OpenAsync(media.Id, outsider.Id, "post", post.Id, false, default));
        Assert.Null(await Svc(t, store).OpenAsync(media.Id, member.Id, "post", Guid.NewGuid(), false, default));

        // Unpublishing the post closes access on the next request (no caching of the decision).
        post.Status = "draft";
        t.Db.SaveChanges();
        Assert.Null(await Svc(t, store).OpenAsync(media.Id, member.Id, "post", post.Id, false, default));
    }

    [Fact]
    public async Task Draft_manifest_lists_uploaded_and_referenced_media_but_not_other_drafts()
    {
        using var t = new TestDb();
        var author = t.AddAdmin("AUTH");
        var stranger = t.AddAdmin("OTHER");
        var draft = AddDraft(t, author.Id);
        var store = new FakeStore(t.Db);

        var uploaded = await Svc(t, store).UploadAsync(author.Id, Upload(draft.Id, "image", TempImage(), "a.png"), default);
        // Referenced-only asset (no UploadDraftId) still shows.
        var referenced = AddMedia(t, author.Id, "image");
        t.Db.NewsComposerDraftMedia.Add(new NewsComposerDraftMedia { DraftId = draft.Id, MediaId = referenced.Id, Role = "inline", SortOrder = 0 });
        // A different owner's draft upload must never leak in.
        var otherDraft = AddDraft(t, stranger.Id);
        await Svc(t, store).UploadAsync(stranger.Id, Upload(otherDraft.Id, "image", TempImage(), "b.png"), default);
        // This author's upload into a *different* draft is not part of this draft.
        var otherOwnDraft = AddDraft(t, author.Id);
        await Svc(t, store).UploadAsync(author.Id, Upload(otherOwnDraft.Id, "image", TempImage(), "c.png"), default);

        var list = await Svc(t, store).ListDraftMediaAsync(author.Id, draft.Id, default);
        Assert.NotNull(list);
        var ids = list!.Select(m => m.MediaId).ToHashSet();
        Assert.Contains(uploaded.Media!.MediaId, ids);
        Assert.Contains(referenced.Id, ids);
        Assert.Equal(2, list.Count);
        Assert.Equal("draft", list[0].ContextKind);
        Assert.Equal(draft.Id, list[0].ContextId);
    }

    // ── Cleanup ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Cleanup_deletes_only_old_unreferenced_media()
    {
        using var t = new TestDb();
        var author = t.AddAdmin("AUTH");
        var old = DateTime.UtcNow.AddHours(-25);
        var recent = DateTime.UtcNow.AddMinutes(-5);

        var orphanOld = AddMedia(t, author.Id, "image", createdAt: old);
        var orphanRecent = AddMedia(t, author.Id, "image", createdAt: recent);

        // Held by an active draft.
        var draft = AddDraft(t, author.Id);
        var heldByDraft = AddMedia(t, author.Id, "image", createdAt: old);
        t.Db.NewsComposerDraftMedia.Add(new NewsComposerDraftMedia { DraftId = draft.Id, MediaId = heldByDraft.Id, Role = "inline", SortOrder = 0 });

        // Held by a *scheduled* (non-active, non-discarded) revision: must be preserved.
        var scheduled = AddDraft(t, author.Id, state: "scheduled");
        var heldByScheduled = AddMedia(t, author.Id, "image", createdAt: old);
        t.Db.NewsComposerDraftMedia.Add(new NewsComposerDraftMedia { DraftId = scheduled.Id, MediaId = heldByScheduled.Id, Role = "inline", SortOrder = 0 });

        // A *promoted* draft is terminal: its media lives on through the post rows, not the draft, so a
        // detached asset must not be pinned by the draft forever.
        var promoted = AddDraft(t, author.Id, state: "promoted");
        var heldByPromoted = AddMedia(t, author.Id, "image", createdAt: old);
        t.Db.NewsComposerDraftMedia.Add(new NewsComposerDraftMedia { DraftId = promoted.Id, MediaId = heldByPromoted.Id, Role = "inline", SortOrder = 0 });

        // A stale upload scope must NOT pin an asset forever: upload scope is a transient window
        // (bounded by CreatedAt), not a permanent lock.
        var staleUploadScope = AddMedia(t, author.Id, "image", uploadDraftId: draft.Id, createdAt: old);

        // Held by a live post (inline ref and cover).
        var post = new NewsPost
        {
            Id = Guid.NewGuid(), Type = "news", Title = "T", Body = "B", Status = "published",
            AuthorId = author.Id, PublishedAt = DateTime.UtcNow, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        t.Db.NewsPosts.Add(post);
        var heldByPost = AddMedia(t, author.Id, "image", createdAt: old);
        t.Db.NewsPostMedia.Add(new NewsPostMedia { NewsPostId = post.Id, MediaId = heldByPost.Id, Role = "inline", SortOrder = 0 });
        var heldAsCover = AddMedia(t, author.Id, "image", createdAt: old);
        post.CoverMediaId = heldAsCover.Id;
        t.Db.SaveChanges();

        // Held only by a *discarded* draft: eligible after the grace window.
        var discarded = AddDraft(t, author.Id, state: "discarded");
        var heldByDiscarded = AddMedia(t, author.Id, "image", createdAt: old);
        t.Db.NewsComposerDraftMedia.Add(new NewsComposerDraftMedia { DraftId = discarded.Id, MediaId = heldByDiscarded.Id, Role = "inline", SortOrder = 0 });
        t.Db.SaveChanges();

        var result = await Svc(t).CleanupAsync(NewsComposerMediaCleanupRequest.Default, default);
        Assert.Equal(0, result.Failed);

        var live = await t.Verify().NewsComposerMedia.AsNoTracking().Where(m => m.DeletedAt == null).Select(m => m.Id).ToListAsync();
        Assert.DoesNotContain(orphanOld.Id, live);
        Assert.Contains(orphanRecent.Id, live);
        Assert.Contains(heldByDraft.Id, live);
        Assert.Contains(heldByScheduled.Id, live);
        Assert.Contains(heldByPost.Id, live);
        Assert.Contains(heldAsCover.Id, live);
        Assert.DoesNotContain(heldByDiscarded.Id, live);
        Assert.DoesNotContain(heldByPromoted.Id, live);
        Assert.DoesNotContain(staleUploadScope.Id, live);
    }

    [Fact]
    public async Task Cleanup_does_not_starve_orphans_behind_older_referenced_assets()
    {
        using var t = new TestDb();
        var author = t.AddAdmin("AUTH");
        var draft = AddDraft(t, author.Id);

        // Older referenced assets must not consume the batch and hide the orphan behind them.
        for (var i = 0; i < 5; i++)
        {
            var held = AddMedia(t, author.Id, "image", createdAt: DateTime.UtcNow.AddHours(-100 + i));
            t.Db.NewsComposerDraftMedia.Add(new NewsComposerDraftMedia { DraftId = draft.Id, MediaId = held.Id, Role = "inline", SortOrder = i });
        }
        var orphan = AddMedia(t, author.Id, "image", createdAt: DateTime.UtcNow.AddHours(-50));
        t.Db.SaveChanges();

        var result = await Svc(t).CleanupAsync(new NewsComposerMediaCleanupRequest(NewsComposerMediaLimits.UnreferencedTtl, BatchSize: 1), default);
        Assert.Equal(1, result.Scanned);
        Assert.Equal(1, result.Deleted);
        Assert.False(await t.Verify().NewsComposerMedia.AsNoTracking().AnyAsync(m => m.Id == orphan.Id && m.DeletedAt == null));
    }

    [Fact]
    public async Task Cleanup_dry_run_reports_without_deleting()
    {
        using var t = new TestDb();
        var author = t.AddAdmin("AUTH");
        var orphan = AddMedia(t, author.Id, "image", createdAt: DateTime.UtcNow.AddHours(-25));

        var result = await Svc(t).CleanupAsync(new NewsComposerMediaCleanupRequest(NewsComposerMediaLimits.UnreferencedTtl, DryRun: true), default);
        Assert.Equal(1, result.Deleted);
        Assert.True(await t.Verify().NewsComposerMedia.AsNoTracking().AnyAsync(m => m.Id == orphan.Id && m.DeletedAt == null));
    }

    [Fact]
    public async Task Cleanup_frees_the_stored_attachment_but_keeps_one_a_live_post_still_links()
    {
        using var t = new TestDb();
        var author = t.AddAdmin("AUTH");
        var store = new FakeStore(t.Db);

        // Media whose shared attachment is also linked by a live post's regular attachment link.
        var post = new NewsPost
        {
            Id = Guid.NewGuid(), Type = "news", Title = "T", Body = "B", Status = "published",
            AuthorId = author.Id, PublishedAt = DateTime.UtcNow, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        t.Db.NewsPosts.Add(post);
        var linked = AddMedia(t, author.Id, "file", createdAt: DateTime.UtcNow.AddHours(-25));
        var stored = await store.PutAsync(AttachmentAreas.News, "f.pdf", "application/pdf", new MemoryStream([1, 2, 3]), author.Id, default);
        linked.AttachmentId = stored.Id;
        t.Db.NewsAttachments.Add(new NewsAttachment { NewsPostId = post.Id, AttachmentId = stored.Id, SortOrder = 0 });
        t.Db.SaveChanges();

        // Media whose attachment is only its own.
        var sole = AddMedia(t, author.Id, "file", createdAt: DateTime.UtcNow.AddHours(-25));
        var soleStored = await store.PutAsync(AttachmentAreas.News, "g.pdf", "application/pdf", new MemoryStream([4, 5, 6]), author.Id, default);
        sole.AttachmentId = soleStored.Id;
        t.Db.SaveChanges();

        await Svc(t, store).CleanupAsync(NewsComposerMediaCleanupRequest.Default, default);

        // The shared blob a live post still links is left alone; the sole one is freed.
        Assert.DoesNotContain(stored.Id, store.Deleted);
        Assert.Contains(soleStored.Id, store.Deleted);
        Assert.False(await t.Verify().NewsComposerMedia.AsNoTracking().AnyAsync(m => m.Id == linked.Id && m.DeletedAt == null));
        Assert.False(await t.Verify().NewsComposerMedia.AsNoTracking().AnyAsync(m => m.Id == sole.Id && m.DeletedAt == null));
    }

    [Fact]
    public async Task Cleanup_keeps_the_shared_blob_a_regular_draft_attachment_still_links()
    {
        using var t = new TestDb();
        var author = t.AddAdmin("AUTH");
        var store = new FakeStore(t.Db);
        var draft = AddDraft(t, author.Id);

        // A regular composer attachment (NewsComposerDraftAttachment, not media) shares the same store.
        var shared = await store.PutAsync(AttachmentAreas.News, "spec.pdf", "application/pdf", new MemoryStream([9, 9, 9]), author.Id, default);
        t.Db.NewsComposerDraftAttachments.Add(new NewsComposerDraftAttachment { DraftId = draft.Id, AttachmentId = shared.Id, SortOrder = 0 });

        // A composer media row pointing at the same blob is unreferenced and old.
        var media = AddMedia(t, author.Id, "file", createdAt: DateTime.UtcNow.AddHours(-25));
        media.AttachmentId = shared.Id;
        t.Db.SaveChanges();

        await Svc(t, store).CleanupAsync(NewsComposerMediaCleanupRequest.Default, default);

        // The media row is tombstoned, but the bytes survive because the draft attachment still needs them.
        Assert.False(await t.Verify().NewsComposerMedia.AsNoTracking().AnyAsync(m => m.Id == media.Id && m.DeletedAt == null));
        Assert.DoesNotContain(shared.Id, store.Deleted);
    }

    [Fact]
    public async Task Cleanup_never_tombstones_media_a_draft_references_by_the_time_it_runs()
    {
        using var t = new TestDb();
        var author = t.AddAdmin("AUTH");
        var old = DateTime.UtcNow.AddHours(-25);
        var draft = AddDraft(t, author.Id);

        // A referenced asset is never eligible, however old it is.
        var media = AddMedia(t, author.Id, "image", createdAt: old);
        t.Db.NewsComposerDraftMedia.Add(new NewsComposerDraftMedia { DraftId = draft.Id, MediaId = media.Id, Role = "inline", SortOrder = 0 });
        t.Db.SaveChanges();

        // The eligible predicate excludes referenced assets before the batch is taken, so this one is
        // not even scanned — and certainly not freed.
        var result = await Svc(t).CleanupAsync(NewsComposerMediaCleanupRequest.Default, default);
        Assert.Equal(0, result.Scanned);
        Assert.True(await t.Verify().NewsComposerMedia.AsNoTracking().AnyAsync(m => m.Id == media.Id && m.DeletedAt == null));

        // Once the draft is discarded (and no other reference holds it), the next pass frees it.
        draft.State = "discarded";
        t.Db.SaveChanges();
        await Svc(t).CleanupAsync(NewsComposerMediaCleanupRequest.Default, default);
        Assert.False(await t.Verify().NewsComposerMedia.AsNoTracking().AnyAsync(m => m.Id == media.Id && m.DeletedAt == null));
    }

    // ── Bare "news" attachments (legacy POST /api/news/attachments) ─────

    [Fact]
    public async Task Cleanup_reclaims_an_unlinked_legacy_news_attachment()
    {
        using var t = new TestDb();
        var author = t.AddAdmin("AUTH");
        var store = new FakeStore(t.Db);

        // A legacy upload: Attachment row in area "news", no NewsComposerMedia row, never linked.
        var stored = await store.PutAsync(AttachmentAreas.News, "orphan.pdf", "application/pdf", new MemoryStream([1, 2, 3]), author.Id, default);
        var row = await t.Db.Attachments.FirstAsync(a => a.Id == stored.Id);
        row.CreatedAt = DateTime.UtcNow.AddHours(-25);
        t.Db.SaveChanges();

        var result = await Svc(t, store).CleanupAsync(NewsComposerMediaCleanupRequest.Default, default);
        Assert.Equal(1, result.Deleted);
        Assert.True(store.Deleted.Contains(stored.Id));
        Assert.NotNull((await t.Verify().Attachments.AsNoTracking().FirstAsync(a => a.Id == stored.Id)).DeletedAt);
    }

    [Fact]
    public async Task Cleanup_retains_legacy_news_attachments_that_are_still_referenced()
    {
        using var t = new TestDb();
        var author = t.AddAdmin("AUTH");
        var store = new FakeStore(t.Db);
        var old = DateTime.UtcNow.AddHours(-25);

        var post = new NewsPost
        {
            Id = Guid.NewGuid(), Type = "news", Title = "T", Body = "B", Status = "published",
            AuthorId = author.Id, PublishedAt = DateTime.UtcNow, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        t.Db.NewsPosts.Add(post);
        var draft = AddDraft(t, author.Id);

        // Linked to a live post.
        var onPost = await store.PutAsync(AttachmentAreas.News, "p.pdf", "application/pdf", new MemoryStream([1]), author.Id, default);
        t.Db.NewsAttachments.Add(new NewsAttachment { NewsPostId = post.Id, AttachmentId = onPost.Id, SortOrder = 0 });

        // Linked to a draft (regular attachment, not media).
        var onDraft = await store.PutAsync(AttachmentAreas.News, "d.pdf", "application/pdf", new MemoryStream([2]), author.Id, default);
        t.Db.NewsComposerDraftAttachments.Add(new NewsComposerDraftAttachment { DraftId = draft.Id, AttachmentId = onDraft.Id, SortOrder = 0 });

        // Linked by a composer media row that is itself live (referenced by a draft). An *unreferenced*
        // media row does not protect its blob — that blob is genuinely orphaned — so this one is
        // referenced by a draft to keep both the media and its attachment alive.
        var viaMedia = await store.PutAsync(AttachmentAreas.News, "m.png", "image/png", new MemoryStream([3]), author.Id, default);
        var media = AddMedia(t, author.Id, "image", createdAt: old);
        media.AttachmentId = viaMedia.Id;
        t.Db.NewsComposerDraftMedia.Add(new NewsComposerDraftMedia { DraftId = draft.Id, MediaId = media.Id, Role = "inline", SortOrder = 0 });

        // Too recent to be eligible.
        var recent = await store.PutAsync(AttachmentAreas.News, "new.pdf", "application/pdf", new MemoryStream([4]), author.Id, default);

        // A non-news area is out of scope entirely.
        var mail = await store.PutAsync(AttachmentAreas.Mail, "mail.pdf", "application/pdf", new MemoryStream([5]), author.Id, default);

        // Age everything except the deliberately-recent row.
        foreach (var id in new[] { onPost.Id, onDraft.Id, viaMedia.Id, mail.Id })
        {
            var a = await t.Db.Attachments.FirstAsync(x => x.Id == id);
            a.CreatedAt = old;
        }
        t.Db.SaveChanges();

        await Svc(t, store).CleanupAsync(NewsComposerMediaCleanupRequest.Default, default);

        foreach (var id in new[] { onPost.Id, onDraft.Id, viaMedia.Id, recent.Id, mail.Id })
        {
            Assert.DoesNotContain(id, store.Deleted);
            Assert.Null((await t.Verify().Attachments.AsNoTracking().FirstAsync(a => a.Id == id)).DeletedAt);
        }
    }

    [Fact]
    public async Task Cleanup_leaves_already_soft_deleted_legacy_rows_alone()
    {
        using var t = new TestDb();
        var author = t.AddAdmin("AUTH");
        var store = new FakeStore(t.Db);

        // ReplaceAttachmentsAsync already soft-deleted this row and its file is gone; retrying the
        // physical delete is the known legacy gap and must not be widened here.
        var stored = await store.PutAsync(AttachmentAreas.News, "gone.pdf", "application/pdf", new MemoryStream([1]), author.Id, default);
        var row = await t.Db.Attachments.FirstAsync(a => a.Id == stored.Id);
        row.CreatedAt = DateTime.UtcNow.AddHours(-25);
        row.DeletedAt = DateTime.UtcNow.AddHours(-2);
        t.Db.SaveChanges();
        store.Deleted.Add(stored.Id); // pretend the file is already gone

        var result = await Svc(t, store).CleanupAsync(NewsComposerMediaCleanupRequest.Default, default);
        Assert.Equal(0, result.Scanned);
    }

    [Fact]
    public async Task Cleanup_bare_attachments_can_be_opted_out()
    {
        using var t = new TestDb();
        var author = t.AddAdmin("AUTH");
        var store = new FakeStore(t.Db);

        var stored = await store.PutAsync(AttachmentAreas.News, "orphan.pdf", "application/pdf", new MemoryStream([1]), author.Id, default);
        var row = await t.Db.Attachments.FirstAsync(a => a.Id == stored.Id);
        row.CreatedAt = DateTime.UtcNow.AddHours(-25);
        t.Db.SaveChanges();

        var result = await Svc(t, store).CleanupAsync(
            new NewsComposerMediaCleanupRequest(NewsComposerMediaLimits.UnreferencedTtl, IncludeBareAttachments: false), default);
        Assert.Equal(0, result.Scanned);
        Assert.DoesNotContain(stored.Id, store.Deleted);
    }

    // ── Fake store ──────────────────────────────────────────────────────

    private sealed class FakeStore(IPopDbContext? db = null) : IAttachmentStore
    {
        public Dictionary<Guid, byte[]> Blobs { get; } = [];
        public Dictionary<Guid, StoredAttachment> Meta { get; } = [];
        public HashSet<Guid> Deleted { get; } = [];

        public async Task<StoredAttachment> PutAsync(string area, string fileName, string contentType, Stream content, Guid uploaderId, CancellationToken ct)
        {
            using var ms = new MemoryStream();
            await content.CopyToAsync(ms, ct);
            if (ms.Length > 25L * 1024 * 1024) throw new AttachmentRejectedException("too_large", "too large");
            var id = Guid.NewGuid();
            var m = new StoredAttachment(id, fileName, contentType, ms.Length, Convert.ToHexStringLower(SHA256.HashData(ms.ToArray())));
            Blobs[id] = ms.ToArray();
            Meta[id] = m;
            // Mirror the real store: the row exists so the media -> attachment FK holds.
            db?.Attachments.Add(new Attachment
            {
                Id = id, Area = area, FileName = fileName, ContentType = contentType,
                Size = m.Size, Sha256 = m.Sha256, StoragePath = "local:fake/" + id.ToString("N"),
                UploaderId = uploaderId, CreatedAt = DateTime.UtcNow,
            });
            if (db is not null) await db.SaveChangesAsync(ct);
            return m;
        }

        public Task<(StoredAttachment Meta, Stream Content)?> OpenAsync(Guid id, CancellationToken ct) =>
            Task.FromResult(Meta.TryGetValue(id, out var m) && !Deleted.Contains(id)
                ? ((StoredAttachment, Stream)?)(m, new MemoryStream(Blobs[id]))
                : null);

        public Task DeleteAsync(Guid id, CancellationToken ct) { Deleted.Add(id); return Task.CompletedTask; }
    }
}