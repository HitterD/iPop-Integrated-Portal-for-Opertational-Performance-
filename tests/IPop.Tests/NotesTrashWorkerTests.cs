using IPop.Application.Attachments;
using IPop.Application.Notes;
using IPop.Infrastructure.Notes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IPop.Tests;

/// Trash empties itself after 30 days: the page, its sub-pages, versions, comments and the files only they used. A file
/// that another page still shows is never touched.
public class NotesTrashWorkerTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    private sealed class RecordingStore : IAttachmentStore
    {
        public List<Guid> Deleted { get; } = [];
        public Task<StoredAttachment> PutAsync(string area, string fileName, string contentType, Stream content, Guid uploaderId, CancellationToken ct) => throw new NotSupportedException();
        public Task<(StoredAttachment Meta, Stream Content)?> OpenAsync(Guid id, CancellationToken ct) => Task.FromResult<(StoredAttachment, Stream)?>(null);
        public Task DeleteAsync(Guid id, CancellationToken ct) { Deleted.Add(id); return Task.CompletedTask; }
    }

    private static ServiceProvider Provider(TestDb t, RecordingStore store)
    {
        var services = new ServiceCollection();
        services.AddScoped<INoteService>(_ => new NoteService(t.Verify()));
        services.AddScoped(_ => t.Verify());
        services.AddSingleton<IAttachmentStore>(store);
        return services.BuildServiceProvider();
    }

    private static async Task<Guid> PageAsync(TestDb t, Guid owner, Guid? parent = null)
        => (await new NoteService(t.Verify()).CreateAsync(owner, new CreateNoteRequest(parent, "P", null, null, null, null), Ct)).Data!.Id;

    private static async Task<Guid> AttachAsync(TestDb t, Guid page, Guid owner, string path)
    {
        var id = Guid.NewGuid();
        t.Db.Attachments.Add(new IPop.Core.Entities.Attachment { Id = id, Area = "notes", FileName = "f", ContentType = "image/png", Size = 5, Sha256 = "x", StoragePath = path, UploaderId = owner, CreatedAt = DateTime.UtcNow });
        await t.Db.SaveChangesAsync();
        await new NoteService(t.Verify()).LinkAttachmentAsync(page, id, Ct);
        return id;
    }

    private static async Task TrashedAsync(TestDb t, Guid owner, Guid page, int daysAgo)
    {
        await new NoteService(t.Verify()).TrashAsync(owner, page, Ct);
        var when = DateTime.UtcNow.AddDays(-daysAgo);
        await t.Verify().NotePages.Where(p => p.DeletedAt != null && (p.Id == page || p.ParentId == page)).ExecuteUpdateAsync(s => s.SetProperty(p => p.DeletedAt, when));
    }

    [Fact]
    public async Task A_page_trashed_more_than_30_days_ago_is_purged_with_its_sub_pages_and_files_but_a_recent_one_stays()
    {
        using var t = new TestDb();
        var a = t.AddUser("a");
        var old = await PageAsync(t, a.Id);
        var oldChild = await PageAsync(t, a.Id, old);
        var recent = await PageAsync(t, a.Id);
        var oldFile = await AttachAsync(t, oldChild, a.Id, "/nas/old.png");
        var recentFile = await AttachAsync(t, recent, a.Id, "/nas/recent.png");
        await TrashedAsync(t, a.Id, old, 45);
        await TrashedAsync(t, a.Id, recent, 5);

        var store = new RecordingStore();
        await using var sp = Provider(t, store);
        var (pages, files) = await NotesTrashWorker.RunOnceAsync(sp, NotesTrashWorker.Retention, 100, NullLogger.Instance, Ct);

        Assert.Equal(1, files);
        Assert.Equal([oldFile], store.Deleted);
        Assert.False(await t.Verify().NotePages.AnyAsync(p => p.Id == old || p.Id == oldChild));
        Assert.True(await t.Verify().NotePages.AnyAsync(p => p.Id == recent));
        Assert.DoesNotContain(recentFile, store.Deleted);
        Assert.True(pages >= 1);
    }

    [Fact]
    public async Task A_file_still_shown_by_another_page_is_kept()
    {
        using var t = new TestDb();
        var a = t.AddUser("a");
        var gone = await PageAsync(t, a.Id);
        var stays = await PageAsync(t, a.Id);
        var shared = await AttachAsync(t, gone, a.Id, "/nas/shared.png");
        await new NoteService(t.Verify()).LinkAttachmentAsync(stays, shared, Ct);      // the same image is also on a live page
        await TrashedAsync(t, a.Id, gone, 60);

        var store = new RecordingStore();
        await using var sp = Provider(t, store);
        await NotesTrashWorker.RunOnceAsync(sp, NotesTrashWorker.Retention, 100, NullLogger.Instance, Ct);

        Assert.Empty(store.Deleted);
        Assert.True(await t.Verify().NoteAttachments.AnyAsync(x => x.PageId == stays && x.AttachmentId == shared));
    }

    [Fact]
    public async Task A_duplicated_pages_file_is_not_deleted_while_the_copy_still_uses_the_same_bytes()
    {
        using var t = new TestDb();
        var a = t.AddUser("a");
        var original = await PageAsync(t, a.Id);
        await AttachAsync(t, original, a.Id, "/nas/once.png");
        var copy = (await new NoteService(t.Verify()).DuplicateAsync(a.Id, original, Ct)).Data!.Id;   // clones the row, not the bytes
        await TrashedAsync(t, a.Id, original, 90);

        var store = new RecordingStore();
        await using var sp = Provider(t, store);
        await NotesTrashWorker.RunOnceAsync(sp, NotesTrashWorker.Retention, 100, NullLogger.Instance, Ct);

        Assert.Empty(store.Deleted);                                       // the copy still needs /nas/once.png
        Assert.True(await t.Verify().NotePages.AnyAsync(p => p.Id == copy));
    }

    [Fact]
    public async Task Nothing_to_do_is_cheap_and_a_second_run_finds_nothing()
    {
        using var t = new TestDb();
        var a = t.AddUser("a");
        var p = await PageAsync(t, a.Id);
        await TrashedAsync(t, a.Id, p, 31);
        var store = new RecordingStore();
        await using var sp = Provider(t, store);
        await NotesTrashWorker.RunOnceAsync(sp, NotesTrashWorker.Retention, 100, NullLogger.Instance, Ct);
        var (pages, files) = await NotesTrashWorker.RunOnceAsync(sp, NotesTrashWorker.Retention, 100, NullLogger.Instance, Ct);
        Assert.Equal((0, 0), (pages, files));
    }
}
