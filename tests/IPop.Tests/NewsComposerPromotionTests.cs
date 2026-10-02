using IPop.Application.News;
using IPop.Core.Entities;
using IPop.Infrastructure.News;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace IPop.Tests;

/// Promotion: the only step that changes what readers see. Publishing is HR/Admin only, promotion is
/// atomic under a version compare-and-swap, and a scheduled draft stays frozen until it is due.
public class NewsComposerPromotionTests
{
    private static NewsComposerService Composer(TestDb t, INewsPublicationHooks? hooks = null)
        => new(t.Verify(), new NewsService(t.Verify()), hooks);

    private sealed class CountingHooks : INewsPublicationHooks
    {
        public int Calls { get; private set; }
        public bool LastNotify { get; private set; }
        public Guid LastPostId { get; private set; }
        public Task AfterPublishedAsync(Guid newsPostId, bool notify, CancellationToken ct)
        {
            Calls++; LastNotify = notify; LastPostId = newsPostId;
            return Task.CompletedTask;
        }
    }

    private static async Task<Guid> CreateCompleteDraftAsync(TestDb t, Guid ownerId, string title = "Town hall",
        DateTime? publishAt = null, bool requiresAck = false)
    {
        var svc = Composer(t);
        var created = await svc.CreateDraftAsync(ownerId, new CreateNewsDraftRequest(), default);
        var saved = await svc.SaveDraftAsync(ownerId, created.Draft!.Id, new SaveNewsDraftRequest(0, new NewsDraftSnapshotDto(
            Type: "announcement", Title: title, Body: "<p>Body text</p>", BodyFormat: "html",
            RequiresAcknowledgement: requiresAck, PublishAt: publishAt)), default);
        Assert.True(saved.Success, saved.Error);
        return created.Draft.Id;
    }

    // ── Publish ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Promote_publishes_a_new_post_and_returns_its_id()
    {
        using var t = new TestDb();
        var hr = t.AddHr("HR1");
        var draftId = await CreateCompleteDraftAsync(t, hr.Id);

        var result = await Composer(t).PromoteDraftAsync(hr.Id, draftId,
            new PromoteNewsDraftRequest(1), default);

        Assert.True(result.Success, result.Error);
        Assert.Equal("published", result.News!.Status);
        Assert.NotEqual(Guid.Empty, result.News.Id);

        var post = await t.Verify().NewsPosts.AsNoTracking().SingleAsync();
        Assert.Equal(result.News.Id, post.Id);
        Assert.Equal("Town hall", post.Title);
        Assert.Equal("html", post.BodyFormat);

        // The draft is spent and points at the post it created.
        var draft = await t.Verify().NewsComposerDrafts.AsNoTracking().SingleAsync();
        Assert.Equal("promoted", draft.State);
        Assert.Equal(post.Id, draft.PostId);
    }

    [Fact]
    public async Task Promote_requires_publish_rights_even_for_the_draft_owner()
    {
        using var t = new TestDb();
        var author = t.AddUser("AUTHOR");
        var post = new NewsPost
        {
            Id = Guid.NewGuid(), Type = "news", Title = "Existing", Body = "b", Status = "published",
            AuthorId = author.Id, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow, PublishedAt = DateTime.UtcNow,
        };
        t.Db.NewsPosts.Add(post);
        await t.Db.SaveChangesAsync();

        // The author may open a revision draft, but not publish it without news.publish.
        var draft = await Composer(t).CreateDraftAsync(author.Id, new CreateNewsDraftRequest(post.Id), default);
        var saved = await Composer(t).SaveDraftAsync(author.Id, draft.Draft!.Id, new SaveNewsDraftRequest(0,
            new NewsDraftSnapshotDto(Type: "news", Title: "Rewritten", Body: "<p>x</p>", BodyFormat: "html")), default);
        Assert.True(saved.Success, saved.Error);

        var denied = await Composer(t).PromoteDraftAsync(author.Id, draft.Draft.Id,
            new PromoteNewsDraftRequest(saved.Draft!.Version), default);

        Assert.False(denied.Success);
        Assert.Equal("forbidden", denied.Error);
        Assert.Equal("Existing", (await t.Verify().NewsPosts.AsNoTracking().SingleAsync()).Title);
        Assert.Equal("active", (await t.Verify().NewsComposerDrafts.AsNoTracking().SingleAsync()).State);
    }

    [Fact]
    public async Task Promote_refuses_incomplete_content()
    {
        using var t = new TestDb();
        var hr = t.AddHr("HR1");
        var svc = Composer(t);
        var draft = await svc.CreateDraftAsync(hr.Id, new CreateNewsDraftRequest(), default);
        await svc.SaveDraftAsync(hr.Id, draft.Draft!.Id, new SaveNewsDraftRequest(0, new NewsDraftSnapshotDto(Title: "Only title")), default);

        var result = await svc.PromoteDraftAsync(hr.Id, draft.Draft.Id, new PromoteNewsDraftRequest(1), default);

        Assert.False(result.Success);
        Assert.Equal("body_required", result.Error);
        Assert.False(await t.Verify().NewsPosts.AnyAsync());
    }

    [Fact]
    public async Task Stale_promote_is_refused_and_publishes_nothing()
    {
        using var t = new TestDb();
        var hr = t.AddHr("HR1");
        var draftId = await CreateCompleteDraftAsync(t, hr.Id);

        // The client holds version 0 but the draft is at 1.
        var stale = await Composer(t).PromoteDraftAsync(hr.Id, draftId, new PromoteNewsDraftRequest(0), default);

        Assert.False(stale.Success);
        Assert.Equal("version_conflict", stale.Error);
        Assert.False(await t.Verify().NewsPosts.AnyAsync());
        Assert.Equal("active", (await t.Verify().NewsComposerDrafts.AsNoTracking().SingleAsync()).State);
    }

    [Fact]
    public async Task Double_promote_publishes_exactly_once()
    {
        using var t = new TestDb();
        var hr = t.AddHr("HR1");
        var draftId = await CreateCompleteDraftAsync(t, hr.Id);

        var first = await Composer(t).PromoteDraftAsync(hr.Id, draftId, new PromoteNewsDraftRequest(1), default);
        var second = await Composer(t).PromoteDraftAsync(hr.Id, draftId, new PromoteNewsDraftRequest(1), default);

        Assert.True(first.Success, first.Error);
        Assert.False(second.Success);                            // already promoted -> not active
        Assert.Single(await t.Verify().NewsPosts.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task After_commit_hook_runs_once_and_notifies_for_a_real_publication()
    {
        using var t = new TestDb();
        var hr = t.AddHr("HR1");
        var draftId = await CreateCompleteDraftAsync(t, hr.Id);
        var hooks = new CountingHooks();

        var promoted = await Composer(t, hooks).PromoteDraftAsync(hr.Id, draftId, new PromoteNewsDraftRequest(1), default);
        Assert.True(promoted.Success, promoted.Error);
        Assert.Equal(1, hooks.Calls);
        Assert.True(hooks.LastNotify);            // first publish: the audience must be told
        Assert.Equal(promoted.News!.Id, hooks.LastPostId);

        // A retry must not fire the hook again (the draft is no longer active).
        await Composer(t, hooks).PromoteDraftAsync(hr.Id, draftId, new PromoteNewsDraftRequest(2), default);
        Assert.Equal(1, hooks.Calls);
    }

    [Fact]
    public async Task Ordinary_content_update_does_not_notify_but_a_reset_does()
    {
        using var t = new TestDb();
        var hr = t.AddHr("HR1");
        var draftId = await CreateCompleteDraftAsync(t, hr.Id);
        var hooks = new CountingHooks();
        var published = await Composer(t, hooks).PromoteDraftAsync(hr.Id, draftId, new PromoteNewsDraftRequest(1), default);
        var postId = published.News!.Id;
        Assert.True(hooks.LastNotify);            // first publish notifies
        Assert.Equal(1, hooks.Calls);

        // Plain edit of the live post: refresh the feed, do not re-notify the audience.
        var edit = await Composer(t).CreateDraftAsync(hr.Id, new CreateNewsDraftRequest(postId), default);
        var editSaved = await Composer(t).SaveDraftAsync(hr.Id, edit.Draft!.Id, new SaveNewsDraftRequest(0,
            new NewsDraftSnapshotDto(Type: "announcement", Title: "Tweaked", Body: "<p>t</p>", BodyFormat: "html")), default);
        await Composer(t, hooks).PromoteDraftAsync(hr.Id, edit.Draft.Id, new PromoteNewsDraftRequest(editSaved.Draft!.Version), default);
        Assert.Equal(2, hooks.Calls);
        Assert.False(hooks.LastNotify);           // ordinary edit: no notification

        // Explicit acknowledgement reset: the audience must confirm the new content.
        var reset = await Composer(t).CreateDraftAsync(hr.Id, new CreateNewsDraftRequest(postId), default);
        var resetSaved = await Composer(t).SaveDraftAsync(hr.Id, reset.Draft!.Id, new SaveNewsDraftRequest(0,
            new NewsDraftSnapshotDto(Type: "announcement", Title: "Changed", Body: "<p>c</p>", BodyFormat: "html")), default);
        await Composer(t, hooks).PromoteDraftAsync(hr.Id, reset.Draft.Id,
            new PromoteNewsDraftRequest(resetSaved.Draft!.Version, ResetAcknowledgements: true), default);
        Assert.Equal(3, hooks.Calls);
        Assert.True(hooks.LastNotify);            // reset: notify again
    }

    // ── Revision of a live post ──────────────────────────────────────────

    [Fact]
    public async Task Promote_updates_an_existing_post_and_bumps_its_content_version()
    {
        using var t = new TestDb();
        var hr = t.AddHr("HR1");
        var draftId = await CreateCompleteDraftAsync(t, hr.Id, title: "First");
        var first = await Composer(t).PromoteDraftAsync(hr.Id, draftId, new PromoteNewsDraftRequest(1), default);
        var postId = first.News!.Id;
        var versionAfterPublish = (await t.Verify().NewsPosts.AsNoTracking().SingleAsync()).ContentVersion;

        var revision = await Composer(t).CreateDraftAsync(hr.Id, new CreateNewsDraftRequest(postId), default);
        var saved = await Composer(t).SaveDraftAsync(hr.Id, revision.Draft!.Id, new SaveNewsDraftRequest(0,
            new NewsDraftSnapshotDto(Type: "announcement", Title: "Second", Body: "<p>new body</p>", BodyFormat: "html")), default);
        Assert.True(saved.Success, saved.Error);
        Assert.Equal(versionAfterPublish, saved.Draft!.BaseContentVersion);

        var promoted = await Composer(t).PromoteDraftAsync(hr.Id, revision.Draft.Id,
            new PromoteNewsDraftRequest(saved.Draft.Version, ExpectedPostContentVersion: versionAfterPublish), default);

        Assert.True(promoted.Success, promoted.Error);
        var post = await t.Verify().NewsPosts.AsNoTracking().SingleAsync();
        Assert.Equal("Second", post.Title);
        Assert.Equal(postId, post.Id);
        Assert.True(post.ContentVersion > versionAfterPublish);
    }

    [Fact]
    public async Task Revision_promote_is_refused_when_the_live_post_moved_on()
    {
        using var t = new TestDb();
        var hr = t.AddHr("HR1");
        var draftId = await CreateCompleteDraftAsync(t, hr.Id);
        var published = await Composer(t).PromoteDraftAsync(hr.Id, draftId, new PromoteNewsDraftRequest(1), default);
        var postId = published.News!.Id;

        var revision = await Composer(t).CreateDraftAsync(hr.Id, new CreateNewsDraftRequest(postId), default);

        // Someone edits the live post meanwhile.
        await using (var v = t.Verify())
        {
            var live = await v.NewsPosts.FirstAsync(p => p.Id == postId);
            live.ContentVersion += 1;
            live.Title = "Edited elsewhere";
            await v.SaveChangesAsync();
        }

        var promoted = await Composer(t).PromoteDraftAsync(hr.Id, revision.Draft!.Id,
            new PromoteNewsDraftRequest(revision.Draft.Version), default);

        Assert.False(promoted.Success);
        Assert.Equal("post_content_conflict", promoted.Error);
        Assert.Equal("Edited elsewhere", (await t.Verify().NewsPosts.AsNoTracking().SingleAsync()).Title);
    }

    // ── Acknowledgements ─────────────────────────────────────────────────

    [Fact]
    public async Task Acknowledgements_are_retained_by_default_and_reset_only_when_asked()
    {
        using var t = new TestDb();
        var hr = t.AddHr("HR1");
        var reader = t.AddUser("READER");
        var draftId = await CreateCompleteDraftAsync(t, hr.Id, requiresAck: true);
        var published = await Composer(t).PromoteDraftAsync(hr.Id, draftId, new PromoteNewsDraftRequest(1), default);
        var postId = published.News!.Id;

        // An html post must be acknowledged against the exact content version the reader saw.
        await new NewsService(t.Verify()).AcknowledgeAsync(reader.Id, postId, default, published.News.ContentVersion);
        Assert.True(await t.Verify().NewsAcknowledgements.AnyAsync(a => a.NewsPostId == postId));

        // An ordinary revision keeps the existing acknowledgements.
        var keep = await Composer(t).CreateDraftAsync(hr.Id, new CreateNewsDraftRequest(postId), default);
        var keepSaved = await Composer(t).SaveDraftAsync(hr.Id, keep.Draft!.Id, new SaveNewsDraftRequest(0,
            new NewsDraftSnapshotDto(Type: "announcement", Title: "Revised", Body: "<p>r</p>", BodyFormat: "html")), default);
        var kept = await Composer(t).PromoteDraftAsync(hr.Id, keep.Draft.Id,
            new PromoteNewsDraftRequest(keepSaved.Draft!.Version), default);
        Assert.True(kept.Success, kept.Error);
        Assert.True(await t.Verify().NewsAcknowledgements.AnyAsync(a => a.NewsPostId == postId));

        // An explicit reset on a published post clears them.
        var reset = await Composer(t).CreateDraftAsync(hr.Id, new CreateNewsDraftRequest(postId), default);
        var resetSaved = await Composer(t).SaveDraftAsync(hr.Id, reset.Draft!.Id, new SaveNewsDraftRequest(0,
            new NewsDraftSnapshotDto(Type: "announcement", Title: "Revised again", Body: "<p>r2</p>", BodyFormat: "html")), default);
        var resetResult = await Composer(t).PromoteDraftAsync(hr.Id, reset.Draft.Id,
            new PromoteNewsDraftRequest(resetSaved.Draft!.Version, ResetAcknowledgements: true), default);
        Assert.True(resetResult.Success, resetResult.Error);
        Assert.False(await t.Verify().NewsAcknowledgements.AnyAsync(a => a.NewsPostId == postId));
    }

    // ── Schedule ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Scheduled_promotion_freezes_the_draft_and_leaves_the_post_hidden()
    {
        using var t = new TestDb();
        var hr = t.AddHr("HR1");
        var reader = t.AddUser("READER");
        var when = DateTime.UtcNow.AddDays(3);
        var draftId = await CreateCompleteDraftAsync(t, hr.Id, publishAt: when);

        var result = await Composer(t).PromoteDraftAsync(hr.Id, draftId, new PromoteNewsDraftRequest(1, Mode: "schedule"), default);
        Assert.True(result.Success, result.Error);
        // Nothing was published: the freeze keeps the content private until it is due.
        Assert.Null(result.News);
        Assert.False(await t.Verify().NewsPosts.AnyAsync());
        Assert.Null(await new NewsService(t.Verify()).GetAsync(Guid.NewGuid(), reader.Id, default));
        // and cancel it — it must not silently disappear.
        var frozen = await Composer(t).GetDraftAsync(hr.Id, draftId, default);
        Assert.False(frozen.Success);
        var listed = await Composer(t).ListDraftsAsync(hr.Id, default);
        var item = Assert.Single(listed.Items);
        Assert.Equal(draftId, item.Id);
        Assert.Equal("scheduled", item.State);
        Assert.Equal(when, item.Snapshot.PublishAt);
        Assert.Equal("scheduled", (await t.Verify().NewsComposerDrafts.AsNoTracking().SingleAsync()).State);
    }

    [Fact]
    public async Task A_scheduled_draft_can_be_resumed_for_editing()
    {
        using var t = new TestDb();
        var hr = t.AddHr("HR1");
        var when = DateTime.UtcNow.AddDays(3);
        var draftId = await CreateCompleteDraftAsync(t, hr.Id, publishAt: when);
        await Composer(t).PromoteDraftAsync(hr.Id, draftId, new PromoteNewsDraftRequest(1, Mode: "schedule"), default);

        var resumed = await Composer(t).ResumeScheduledDraftAsync(hr.Id, draftId, default);

        Assert.True(resumed.Success, resumed.Error);
        Assert.Equal("active", resumed.Draft!.State);
        Assert.Null(resumed.Draft.Snapshot.PublishAt);   // the author re-chooses the time
        // Editable again.
        Assert.True((await Composer(t).GetDraftAsync(hr.Id, draftId, default)).Success);
        Assert.False(await t.Verify().NewsPosts.AnyAsync());
    }

    [Fact]
    public async Task Another_user_cannot_resume_someone_elses_schedule()
    {
        using var t = new TestDb();
        var hr = t.AddHr("HR1");
        var otherHr = t.AddHr("HR2");
        var draftId = await CreateCompleteDraftAsync(t, hr.Id, publishAt: DateTime.UtcNow.AddDays(3));
        await Composer(t).PromoteDraftAsync(hr.Id, draftId, new PromoteNewsDraftRequest(1, Mode: "schedule"), default);

        var denied = await Composer(t).ResumeScheduledDraftAsync(otherHr.Id, draftId, default);

        Assert.False(denied.Success);
        Assert.Equal("forbidden", denied.Error);
        Assert.Equal("scheduled", (await t.Verify().NewsComposerDrafts.AsNoTracking().SingleAsync()).State);
    }

    [Fact]
    public async Task A_scheduled_draft_can_be_cancelled_by_discard()
    {
        using var t = new TestDb();
        var hr = t.AddHr("HR1");
        var draftId = await CreateCompleteDraftAsync(t, hr.Id, publishAt: DateTime.UtcNow.AddDays(3));
        await Composer(t).PromoteDraftAsync(hr.Id, draftId, new PromoteNewsDraftRequest(1, Mode: "schedule"), default);

        var cancelled = await Composer(t).DiscardDraftAsync(hr.Id, draftId, default);

        Assert.True(cancelled.Success, cancelled.Error);
        Assert.Equal("discarded", cancelled.Draft!.State);
        // The freeze never touched the live post, so cancelling has nothing to unwind.
        Assert.False(await t.Verify().NewsPosts.AnyAsync());
        Assert.Null(await Composer(t).ApplyScheduledDraftAsync(draftId, default));
    }

    [Fact]
    public async Task Scheduled_promotion_needs_a_future_time()
    {
        using var t = new TestDb();
        var hr = t.AddHr("HR1");
        var draftId = await CreateCompleteDraftAsync(t, hr.Id);

        var result = await Composer(t).PromoteDraftAsync(hr.Id, draftId, new PromoteNewsDraftRequest(1, Mode: "schedule"), default);

        Assert.False(result.Success);
        Assert.Equal("schedule_requires_future_time", result.Error);
        Assert.False(await t.Verify().NewsPosts.AnyAsync());
    }

    [Fact]
    public async Task Apply_scheduled_promotes_only_when_due()
    {
        using var t = new TestDb();
        var hr = t.AddHr("HR1");
        var when = DateTime.UtcNow.AddDays(3);
        var draftId = await CreateCompleteDraftAsync(t, hr.Id, publishAt: when);
        await Composer(t).PromoteDraftAsync(hr.Id, draftId, new PromoteNewsDraftRequest(1, Mode: "schedule"), default);

        // Not due yet.
        Assert.Null(await Composer(t).ApplyScheduledDraftAsync(draftId, default));
        Assert.Equal("scheduled", (await t.Verify().NewsComposerDrafts.AsNoTracking().SingleAsync()).State);

        // Due: the worker promotes it.
        await using (var v = t.Verify())
        {
            var d = await v.NewsComposerDrafts.FirstAsync(x => x.Id == draftId);
            d.PublishAt = DateTime.UtcNow.AddMinutes(-1);
            await v.SaveChangesAsync();
        }
        var applied = await Composer(t).ApplyScheduledDraftAsync(draftId, default);
        Assert.NotNull(applied);
        Assert.True(applied!.Success, applied.Error);
        Assert.Equal("promoted", (await t.Verify().NewsComposerDrafts.AsNoTracking().SingleAsync()).State);

        // The post now exists and is live.
        var post = await t.Verify().NewsPosts.AsNoTracking().SingleAsync();
        Assert.Equal("published", post.Status);
        Assert.NotNull(post.PublishedAt);
    }

    [Fact]
    public async Task Apply_scheduled_refuses_when_the_owner_is_no_longer_entitled()
    {
        using var t = new TestDb();
        var author = t.AddUser("AUTHOR");
        var post = new NewsPost
        {
            Id = Guid.NewGuid(), Type = "news", Title = "Existing", Body = "b", Status = "published",
            AuthorId = author.Id, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow, PublishedAt = DateTime.UtcNow,
        };
        t.Db.NewsPosts.Add(post);
        await t.Db.SaveChangesAsync();

        // An HR user schedules a revision of the author's post, then loses the right.
        var hr = t.AddHr("HR1");
        var svc = Composer(t);
        var draft = await svc.CreateDraftAsync(hr.Id, new CreateNewsDraftRequest(post.Id), default);
        var saved = await svc.SaveDraftAsync(hr.Id, draft.Draft!.Id, new SaveNewsDraftRequest(0, new NewsDraftSnapshotDto(
            Type: "news", Title: "Later", Body: "<p>x</p>", BodyFormat: "html",
            PublishAt: DateTime.UtcNow.AddDays(-1))), default);
        Assert.True(saved.Success, saved.Error);
        // Schedule into the past is rejected, so drive the freeze directly.
        var frozen = await t.Verify().NewsComposerDrafts.FirstAsync(d => d.Id == draft.Draft.Id);
        frozen.State = "scheduled";
        frozen.PublishAt = DateTime.UtcNow.AddMinutes(-1);
        await t.Verify().SaveChangesAsync();

        await using (var v = t.Verify())
        {
            var row = await v.Users.FirstAsync(u => u.Id == hr.Id);
            row.IsActive = false;
            await v.SaveChangesAsync();
        }

        Assert.Null(await Composer(t).ApplyScheduledDraftAsync(draft.Draft.Id, default));
        Assert.Equal("Existing", (await t.Verify().NewsPosts.AsNoTracking().FirstAsync(p => p.Id == post.Id)).Title);
    }

    // ── References move to the live post ─────────────────────────────────

    [Fact]
    public async Task Promotion_copies_draft_references_onto_the_post()
    {
        using var t = new TestDb();
        var hr = t.AddHr("HR1");
        var media = new NewsComposerMedia
        {
            Id = Guid.NewGuid(), OwnerId = hr.Id, Kind = "image", FileName = "cover.png",
            ContentType = "image/png", Size = 10, Sha256 = new string('c', 64), CreatedAt = DateTime.UtcNow,
        };
        var file = new Attachment
        {
            Id = Guid.NewGuid(), Area = "news", FileName = "doc.pdf", ContentType = "application/pdf",
            Size = 10, Sha256 = new string('d', 64), StoragePath = "news/x.pdf",
            UploaderId = hr.Id, CreatedAt = DateTime.UtcNow,
        };
        t.Db.NewsComposerMedia.Add(media);
        t.Db.Attachments.Add(file);
        await t.Db.SaveChangesAsync();

        var svc = Composer(t);
        var draft = await svc.CreateDraftAsync(hr.Id, new CreateNewsDraftRequest(), default);
        var bodyId = Guid.NewGuid();
        var withRefs = await svc.SaveDraftAsync(hr.Id, draft.Draft!.Id, new SaveNewsDraftRequest(0, new NewsDraftSnapshotDto(
            Type: "news", Title: "With media", BodyFormat: "html",
            Body: $"<p>x</p><img data-news-media=\"{bodyId}\">",
            Media: [new NewsDraftMediaRefDto(bodyId, "inline")],
            AttachmentIds: [file.Id])), default);
        Assert.False(withRefs.Success);   // body/ref agreement: the id is not a real owned asset
        Assert.Equal("invalid_media", withRefs.Error);

        var ok = await svc.SaveDraftAsync(hr.Id, draft.Draft.Id, new SaveNewsDraftRequest(0, new NewsDraftSnapshotDto(
            Type: "news", Title: "With media", BodyFormat: "html",
            Body: $"<p>x</p><img data-news-media=\"{media.Id}\">",
            Media: [new NewsDraftMediaRefDto(media.Id, "inline")],
            CoverMediaId: media.Id, CoverMode: "manual",
            AttachmentIds: [file.Id])), default);
        Assert.True(ok.Success, ok.Error);

        var promoted = await svc.PromoteDraftAsync(hr.Id, draft.Draft.Id, new PromoteNewsDraftRequest(ok.Draft!.Version), default);
        Assert.True(promoted.Success, promoted.Error);

        var postId = promoted.News!.Id;
        // One asset = one reference row (the join key is (draft/media, asset)); the cover role wins
        // because the cover is what the feed needs, and the body still resolves the asset by id.
        var postMedia = await t.Verify().NewsPostMedia.AsNoTracking().Where(m => m.NewsPostId == postId).ToListAsync();
        var row = Assert.Single(postMedia);
        Assert.Equal(media.Id, row.MediaId);
        Assert.Equal("cover", row.Role);
        Assert.True(await t.Verify().NewsAttachments.AsNoTracking().AnyAsync(a => a.NewsPostId == postId && a.AttachmentId == file.Id));
    }

    // ── Cover resolution ─────────────────────────────────────────────────

    private static NewsComposerMedia Image(TestDb t, Guid ownerId, string name)
    {
        var m = new NewsComposerMedia
        {
            Id = Guid.NewGuid(), OwnerId = ownerId, Kind = "image", FileName = name,
            ContentType = "image/png", Size = 10, Sha256 = new string('e', 64), CreatedAt = DateTime.UtcNow,
        };
        t.Db.NewsComposerMedia.Add(m);
        t.Db.SaveChanges();
        return m;
    }

    private static NewsComposerMedia Video(TestDb t, Guid ownerId)
    {
        var m = new NewsComposerMedia
        {
            Id = Guid.NewGuid(), OwnerId = ownerId, Kind = "video", FileName = "clip.mp4",
            ContentType = "video/mp4", Size = 10, Sha256 = new string('f', 64), CreatedAt = DateTime.UtcNow,
        };
        t.Db.NewsComposerMedia.Add(m);
        t.Db.SaveChanges();
        return m;
    }

    [Fact]
    public async Task Auto_cover_derives_the_first_body_image_and_never_a_video()
    {
        using var t = new TestDb();
        var hr = t.AddHr("HR1");
        var video = Video(t, hr.Id);
        var second = Image(t, hr.Id, "second.png");
        var first = Image(t, hr.Id, "first.png");

        var svc = Composer(t);
        var draft = await svc.CreateDraftAsync(hr.Id, new CreateNewsDraftRequest(), default);
        // Body order: video, then first.png, then second.png. The video must be skipped.
        var body = $"""
            <p>x</p>
            <div data-news-media="{video.Id}"></div>
            <img data-news-media="{first.Id}">
            <img data-news-media="{second.Id}">
            """;
        var saved = await svc.SaveDraftAsync(hr.Id, draft.Draft!.Id, new SaveNewsDraftRequest(0, new NewsDraftSnapshotDto(
            Type: "news", Title: "With images", Body: body, BodyFormat: "html", CoverMode: "auto",
            Media: [
                new NewsDraftMediaRefDto(video.Id, "inline", 0),
                new NewsDraftMediaRefDto(first.Id, "inline", 1),
                new NewsDraftMediaRefDto(second.Id, "inline", 2),
            ])), default);
        Assert.True(saved.Success, saved.Error);

        var promoted = await svc.PromoteDraftAsync(hr.Id, draft.Draft.Id, new PromoteNewsDraftRequest(saved.Draft!.Version), default);
        Assert.True(promoted.Success, promoted.Error);

        var post = await t.Verify().NewsPosts.AsNoTracking().SingleAsync();
        Assert.Equal(first.Id, post.CoverMediaId);      // first IMAGE in body order, not the video
        Assert.Equal("auto", post.CoverMode);
    }

    [Fact]
    public async Task Auto_cover_is_null_when_the_body_has_no_image()
    {
        using var t = new TestDb();
        var hr = t.AddHr("HR1");
        var video = Video(t, hr.Id);
        var svc = Composer(t);
        var draft = await svc.CreateDraftAsync(hr.Id, new CreateNewsDraftRequest(), default);
        var saved = await svc.SaveDraftAsync(hr.Id, draft.Draft!.Id, new SaveNewsDraftRequest(0, new NewsDraftSnapshotDto(
            Type: "news", Title: "Video only", BodyFormat: "html",
            Body: $"""<p>x</p><div data-news-media="{video.Id}"></div>""",
            CoverMode: "auto", Media: [new NewsDraftMediaRefDto(video.Id, "inline")])), default);
        Assert.True(saved.Success, saved.Error);

        var promoted = await svc.PromoteDraftAsync(hr.Id, draft.Draft.Id, new PromoteNewsDraftRequest(saved.Draft!.Version), default);
        Assert.True(promoted.Success, promoted.Error);
        Assert.Null((await t.Verify().NewsPosts.AsNoTracking().SingleAsync()).CoverMediaId);
    }

    [Fact]
    public async Task None_cover_is_respected_and_never_re_derived()
    {
        using var t = new TestDb();
        var hr = t.AddHr("HR1");
        var image = Image(t, hr.Id, "pic.png");
        var svc = Composer(t);
        var draft = await svc.CreateDraftAsync(hr.Id, new CreateNewsDraftRequest(), default);
        var saved = await svc.SaveDraftAsync(hr.Id, draft.Draft!.Id, new SaveNewsDraftRequest(0, new NewsDraftSnapshotDto(
            Type: "news", Title: "No cover", BodyFormat: "html",
            Body: $"""<p>x</p><img data-news-media="{image.Id}">""",
            CoverMode: "none", Media: [new NewsDraftMediaRefDto(image.Id, "inline")])), default);
        Assert.True(saved.Success, saved.Error);

        var promoted = await svc.PromoteDraftAsync(hr.Id, draft.Draft.Id, new PromoteNewsDraftRequest(saved.Draft!.Version), default);
        Assert.True(promoted.Success, promoted.Error);
        var post = await t.Verify().NewsPosts.AsNoTracking().SingleAsync();
        Assert.Null(post.CoverMediaId);          // explicitly removed: auto must not bring it back
        Assert.Equal("none", post.CoverMode);
    }

    [Fact]
    public async Task Manual_cover_must_be_a_referenced_image()
    {
        using var t = new TestDb();
        var hr = t.AddHr("HR1");
        var image = Image(t, hr.Id, "pic.png");
        var video = Video(t, hr.Id);
        var svc = Composer(t);
        var draft = await svc.CreateDraftAsync(hr.Id, new CreateNewsDraftRequest(), default);
        var saved = await svc.SaveDraftAsync(hr.Id, draft.Draft!.Id, new SaveNewsDraftRequest(0, new NewsDraftSnapshotDto(
            Type: "news", Title: "Manual", BodyFormat: "html",
            Body: $"""<p>x</p><img data-news-media="{image.Id}">""",
            CoverMode: "manual", CoverMediaId: video.Id,          // a video is never a cover
            Media: [new NewsDraftMediaRefDto(image.Id, "inline"), new NewsDraftMediaRefDto(video.Id, "cover")])), default);
        Assert.True(saved.Success, saved.Error);

        var promoted = await svc.PromoteDraftAsync(hr.Id, draft.Draft.Id, new PromoteNewsDraftRequest(saved.Draft!.Version), default);

        Assert.False(promoted.Success);
        Assert.Equal("invalid_cover", promoted.Error);
        Assert.False(await t.Verify().NewsPosts.AnyAsync());
    }

    [Fact]
    public async Task Manual_cover_needs_an_id()
    {
        using var t = new TestDb();
        var hr = t.AddHr("HR1");
        var svc = Composer(t);
        var draft = await svc.CreateDraftAsync(hr.Id, new CreateNewsDraftRequest(), default);
        var saved = await svc.SaveDraftAsync(hr.Id, draft.Draft!.Id, new SaveNewsDraftRequest(0, new NewsDraftSnapshotDto(
            Type: "news", Title: "Manual no id", Body: "<p>x</p>", BodyFormat: "html", CoverMode: "manual")), default);
        Assert.True(saved.Success, saved.Error);

        var promoted = await svc.PromoteDraftAsync(hr.Id, draft.Draft.Id, new PromoteNewsDraftRequest(saved.Draft!.Version), default);

        Assert.False(promoted.Success);
        Assert.Equal("cover_required", promoted.Error);
    }
}