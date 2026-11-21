using IPop.Application.Notes;
using IPop.Core.Entities;
using IPop.Infrastructure.Notes;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace IPop.Tests;

/// Named versions (kept forever), the owner's own templates, and the "who opened this page" list.
public class NotePageExtrasTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static NoteService Svc(TestDb t) => new(t.Verify());

    private static async Task<Guid> PageWithContentAsync(TestDb t, Guid owner, string title = "Plan")
    {
        var id = (await Svc(t).CreateAsync(owner, new CreateNoteRequest(null, title, "📋", null, null, null), Ct)).Data!.Id;
        var r = await Svc(t).SnapshotAsync(owner, id, new NoteSnapshotRequest("AQ==", 0, [], "hello", 0, 0), Ct);
        Assert.True(r.Success, r.Error);
        return id;
    }

    // ── named versions ──────────────────────────────────────────────────────

    [Fact]
    public async Task A_version_can_be_named_renamed_and_unnamed_and_the_page_can_save_a_named_version_now()
    {
        using var t = new TestDb();
        var a = t.AddUser("a");
        var page = await PageWithContentAsync(t, a.Id);

        var saved = await Svc(t).SaveNamedVersionAsync(a.Id, page, "  Before the audit  ", Ct);
        Assert.True(saved.Success, saved.Error);
        Assert.Equal("Before the audit", saved.Data!.Name);

        var list = (await Svc(t).GetVersionsAsync(a.Id, page, Ct)).Data!;
        Assert.Contains(list, v => v.Name == "Before the audit");

        var renamed = await Svc(t).NameVersionAsync(a.Id, page, saved.Data.Id, "Audit v2", Ct);
        Assert.Equal("Audit v2", renamed.Data!.Name);
        var cleared = await Svc(t).NameVersionAsync(a.Id, page, saved.Data.Id, "   ", Ct);
        Assert.Null(cleared.Data!.Name);
    }

    [Fact]
    public async Task Naming_needs_a_name_edit_access_and_a_real_version_of_that_page()
    {
        using var t = new TestDb();
        var a = t.AddUser("a");
        var b = t.AddUser("b");
        var page = await PageWithContentAsync(t, a.Id);
        var other = await PageWithContentAsync(t, a.Id, "Other");
        Assert.Equal("name_required", (await Svc(t).SaveNamedVersionAsync(a.Id, page, " ", Ct)).Error);
        Assert.Equal("not_found", (await Svc(t).SaveNamedVersionAsync(b.Id, page, "x", Ct)).Error);          // no access at all
        await Svc(t).AddShareAsync(a.Id, page, new AddNoteShareRequest(b.Id, null, NoteLevels.View), Ct);
        Assert.Equal("forbidden", (await Svc(t).SaveNamedVersionAsync(b.Id, page, "x", Ct)).Error);          // can only view
        var v = (await Svc(t).SaveNamedVersionAsync(a.Id, page, "ok", Ct)).Data!;
        Assert.Equal("not_found", (await Svc(t).NameVersionAsync(a.Id, other, v.Id, "steal", Ct)).Error);   // version of another page
    }

    [Fact]
    public async Task Named_versions_survive_the_thinning_that_removes_old_unnamed_ones_and_the_count_is_capped()
    {
        var now = new DateTime(2026, 12, 1, 12, 0, 0, DateTimeKind.Utc);
        var old = Enumerable.Range(0, 6).Select(i => new NoteService.VersionStamp(Guid.NewGuid(), now.AddDays(-30).AddMinutes(i * 10), Named: i == 2)).ToList();
        var stale = NoteService.StaleVersions(old, now).ToHashSet();
        Assert.DoesNotContain(old[2].Id, stale);                 // named: kept
        Assert.True(stale.Count >= 4);                           // same-day unnamed ones: thinned to one

        using var t = new TestDb();
        var a = t.AddUser("a");
        var page = await PageWithContentAsync(t, a.Id);
        for (var i = 0; i < 50; i++) Assert.True((await Svc(t).SaveNamedVersionAsync(a.Id, page, $"v{i}", Ct)).Success);
        Assert.Equal("too_many_named", (await Svc(t).SaveNamedVersionAsync(a.Id, page, "one too many", Ct)).Error);
    }

    // ── templates ───────────────────────────────────────────────────────────

    [Fact]
    public async Task A_page_saved_as_a_template_starts_new_pages_with_the_same_content_and_stays_private_to_its_owner()
    {
        using var t = new TestDb();
        var a = t.AddUser("a");
        var b = t.AddUser("b");
        var page = await PageWithContentAsync(t, a.Id, "Weekly report");
        var tpl = await Svc(t).SaveTemplateAsync(a.Id, page, null, Ct);
        Assert.True(tpl.Success, tpl.Error);
        Assert.Equal("Weekly report", tpl.Data!.Name);                 // defaults to the page title

        var created = await Svc(t).CreateFromTemplateAsync(a.Id, tpl.Data.Id, new NewFromTemplateRequest(null, null), Ct);
        Assert.True(created.Success, created.Error);
        Assert.Equal("Weekly report", created.Data!.Title);
        Assert.NotEqual(page, created.Data.Id);
        var original = await t.Verify().NotePages.AsNoTracking().FirstAsync(p => p.Id == page);
        var copy = await t.Verify().NotePages.AsNoTracking().FirstAsync(p => p.Id == created.Data.Id);
        Assert.Equal(original.YState, copy.YState);
        Assert.Equal(a.Id, copy.OwnerId);

        // someone else cannot see, use or delete it
        Assert.Empty(await Svc(t).GetTemplatesAsync(b.Id, Ct));
        Assert.Equal("not_found", (await Svc(t).CreateFromTemplateAsync(b.Id, tpl.Data.Id, new NewFromTemplateRequest(null, null), Ct)).Error);
        Assert.Equal("not_found", (await Svc(t).DeleteTemplateAsync(b.Id, tpl.Data.Id, Ct)).Error);
        Assert.Single(await Svc(t).GetTemplatesAsync(a.Id, Ct));
        Assert.True((await Svc(t).DeleteTemplateAsync(a.Id, tpl.Data.Id, Ct)).Success);
        Assert.Empty(await Svc(t).GetTemplatesAsync(a.Id, Ct));
    }

    [Fact]
    public async Task Templates_need_edit_access_on_the_source_and_on_the_parent_and_an_empty_page_is_refused()
    {
        using var t = new TestDb();
        var a = t.AddUser("a");
        var b = t.AddUser("b");
        var page = await PageWithContentAsync(t, a.Id);
        await Svc(t).AddShareAsync(a.Id, page, new AddNoteShareRequest(b.Id, null, NoteLevels.View), Ct);
        Assert.Equal("forbidden", (await Svc(t).SaveTemplateAsync(b.Id, page, "mine", Ct)).Error);     // can read, cannot copy durably

        var empty = (await Svc(t).CreateAsync(a.Id, new CreateNoteRequest(null, "Empty", null, null, null, null), Ct)).Data!.Id;
        Assert.Equal("empty_page", (await Svc(t).SaveTemplateAsync(a.Id, empty, null, Ct)).Error);

        var tpl = (await Svc(t).SaveTemplateAsync(a.Id, page, "T", Ct)).Data!;
        var theirs = await PageWithContentAsync(t, b.Id, "B page");
        var foreign = (await Svc(t).CreateAsync(b.Id, new CreateNoteRequest(null, "B root", null, null, null, null), Ct)).Data!.Id;
        Assert.Equal("not_found", (await Svc(t).CreateFromTemplateAsync(a.Id, tpl.Id, new NewFromTemplateRequest(foreign, null), Ct)).Error);
        Assert.True(theirs != Guid.Empty);
    }

    [Fact]
    public async Task A_user_can_keep_at_most_30_templates()
    {
        using var t = new TestDb();
        var a = t.AddUser("a");
        var page = await PageWithContentAsync(t, a.Id);
        for (var i = 0; i < 30; i++) Assert.True((await Svc(t).SaveTemplateAsync(a.Id, page, $"t{i}", Ct)).Success);
        Assert.Equal("too_many_templates", (await Svc(t).SaveTemplateAsync(a.Id, page, "31st", Ct)).Error);
    }

    [Fact]
    public async Task Files_inside_a_template_are_cloned_for_the_new_page_so_deleting_the_source_does_not_break_it()
    {
        using var t = new TestDb();
        var a = t.AddUser("a");
        var page = (await Svc(t).CreateAsync(a.Id, new CreateNoteRequest(null, "With image", null, null, null, null), Ct)).Data!.Id;
        var fileId = Guid.NewGuid();
        t.Db.Attachments.Add(new Attachment { Id = fileId, Area = "notes", FileName = "pic.png", ContentType = "image/png", Size = 5, Sha256 = "x", StoragePath = "/nas/pic.png", UploaderId = a.Id, CreatedAt = DateTime.UtcNow });
        await t.Db.SaveChangesAsync();
        var state = Convert.ToBase64String(System.Text.Encoding.ASCII.GetBytes($"xx{fileId:D}yy"));
        Assert.True((await Svc(t).SnapshotAsync(a.Id, page, new NoteSnapshotRequest(state, 0, [], "x", 0, 0), Ct)).Success);

        var tpl = (await Svc(t).SaveTemplateAsync(a.Id, page, null, Ct)).Data!;
        var created = (await Svc(t).CreateFromTemplateAsync(a.Id, tpl.Id, new NewFromTemplateRequest(null, null), Ct)).Data!;

        var copyState = (await t.Verify().NotePages.AsNoTracking().FirstAsync(p => p.Id == created.Id)).YState!;
        var text = System.Text.Encoding.ASCII.GetString(copyState);
        Assert.DoesNotContain(fileId.ToString("D"), text);                       // points at its own clone
        var cloneId = Guid.Parse(text.Substring(2, 36));
        Assert.True(await t.Verify().NoteAttachments.AnyAsync(x => x.PageId == created.Id && x.AttachmentId == cloneId));
        Assert.Equal("/nas/pic.png", (await t.Verify().Attachments.AsNoTracking().FirstAsync(x => x.Id == cloneId)).StoragePath);
    }

    // ── views ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Opening_a_page_counts_one_row_per_person_and_only_owner_and_editors_can_read_the_list()
    {
        using var t = new TestDb();
        var a = t.AddUser("a");
        var b = t.AddUser("b");
        var c = t.AddUser("c");
        var page = await PageWithContentAsync(t, a.Id);
        await Svc(t).AddShareAsync(a.Id, page, new AddNoteShareRequest(b.Id, null, NoteLevels.View), Ct);
        await Svc(t).AddShareAsync(a.Id, page, new AddNoteShareRequest(c.Id, null, NoteLevels.Edit), Ct);

        await Svc(t).GetPageAsync(b.Id, page, Ct);
        await Svc(t).GetPageAsync(b.Id, page, Ct);
        await Svc(t).GetPageAsync(b.Id, page, Ct);
        await Svc(t).GetPageAsync(c.Id, page, Ct);

        var seen = (await Svc(t).GetViewersAsync(a.Id, page, Ct)).Data!;
        Assert.Equal(2, seen.Count);
        Assert.Equal(3, seen.Single(v => v.UserId == b.Id).Count);       // one row, counted three times
        Assert.DoesNotContain(seen, v => v.UserId == a.Id);              // you never see yourself in the list

        Assert.True((await Svc(t).GetViewersAsync(c.Id, page, Ct)).Success);                  // an editor may
        Assert.Equal("forbidden", (await Svc(t).GetViewersAsync(b.Id, page, Ct)).Error);      // a viewer may not
        var stranger = t.AddUser("z");
        Assert.Equal("not_found", (await Svc(t).GetViewersAsync(stranger.Id, page, Ct)).Error);
    }
}
