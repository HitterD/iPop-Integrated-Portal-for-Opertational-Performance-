using System.Text;
using IPop.Application.Attachments;
using IPop.Application.Projects;
using IPop.Core.Entities;
using IPop.Infrastructure.Persistence;
using IPop.Infrastructure.Projects;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace IPop.Tests;

/// Task evidence on the NAS: link/list/delete rules of TaskFileService, the "projects" download
/// policy, and the real completion date (CompletedAt) on every status path.
public sealed class ProjectTaskFileTests
{
    private sealed record Ctx(TestDb T, ProjectService Svc, Guid Owner, Guid Member, Guid Outsider, Guid ProjectId, Guid TaskId) : IDisposable
    {
        public void Dispose() => T.Dispose();
    }

    private static async Task<Ctx> SetupAsync()
    {
        var t = new TestDb();
        var owner = t.AddUser("TF_O");
        var member = t.AddUser("TF_M");
        var outsider = t.AddUser("TF_X");
        var svc = new ProjectService(t.Verify());
        var p = (await svc.CreateProjectAsync(owner.Id, new CreateProjectRequest("Files", null, 2026, owner.Id, null, null, null, null), default)).Project!;
        await svc.AddMemberAsync(owner.Id, p.Id, new AddProjectMemberRequest(member.Id, "member"), default);
        var task = await TaskAsync(svc, owner.Id, p.Id, "Install panel");
        return new Ctx(t, svc, owner.Id, member.Id, outsider.Id, p.Id, task.Id);
    }

    private static async Task<ProjectTaskDto> TaskAsync(ProjectService svc, Guid actor, Guid projectId, string title, string status = "todo") =>
        (await svc.CreateTaskAsync(actor, new CreateTaskRequest(projectId, null, title, null, status, null, null, null, null, null, null), default)).Task!;

    private static UpdateTaskRequest Status(string status) =>
        new(null, null, status, null, null, null, null, null, null, null, null, null, null, null);

    private static Stream Bytes(string s = "hello") => new MemoryStream(Encoding.UTF8.GetBytes(s));

    // ── TaskFileService ─────────────────────────────────────────────────────

    [Fact]
    public async Task Member_upload_links_the_file_logs_activity_and_lists_newest_first()
    {
        using var c = await SetupAsync();
        using var db = c.T.Verify();
        var files = new TaskFileService(db, new FakeStore(db));

        var photo = await files.UploadAsync(c.Member, c.TaskId, "field", "site.jpg", "image/jpeg", Bytes(), default);
        Assert.True(photo.Success);
        Assert.Equal("field", photo.Value!.Kind);
        Assert.Equal($"/api/attachments/{photo.Value.AttachmentId}", photo.Value.Url);
        Assert.Equal(photo.Value.Url, photo.Value.ThumbUrl);
        Assert.Equal("User TF_M", photo.Value.UploadedByName);
        await Task.Delay(5);
        var doc = await files.UploadAsync(c.Owner, c.TaskId, "DELIVERABLE", "report.pdf", "application/pdf", Bytes(), default);
        Assert.Equal("deliverable", doc.Value!.Kind);
        Assert.Null(doc.Value.ThumbUrl);

        var list = await files.ListAsync(c.Member, c.TaskId, default);
        Assert.Equal(["report.pdf", "site.jpg"], list.Value!.Select(f => f.FileName));
        using var check = c.T.Verify();
        Assert.Equal(AttachmentAreas.Projects, (await check.Attachments.SingleAsync(a => a.Id == photo.Value.AttachmentId)).Area);
        Assert.Equal(2, await check.ProjectTaskActivities.CountAsync(a => a.TaskId == c.TaskId && a.Action == "file_added"));
    }

    [Fact]
    public async Task Outsider_cannot_upload_or_list_and_bad_kind_is_rejected_before_storing()
    {
        using var c = await SetupAsync();
        using var db = c.T.Verify();
        var store = new FakeStore(db);
        var files = new TaskFileService(db, store);

        Assert.Equal("forbidden", (await files.UploadAsync(c.Outsider, c.TaskId, "field", "a.jpg", "image/jpeg", Bytes(), default)).Error);
        Assert.Equal("forbidden", (await files.ListAsync(c.Outsider, c.TaskId, default)).Error);
        Assert.Equal("invalid_kind", (await files.UploadAsync(c.Member, c.TaskId, "selfie", "a.jpg", "image/jpeg", Bytes(), default)).Error);
        Assert.Equal("task_not_found", (await files.UploadAsync(c.Member, Guid.NewGuid(), "field", "a.jpg", "image/jpeg", Bytes(), default)).Error);
        Assert.Equal(0, store.Puts);
    }

    [Fact]
    public async Task Store_rejection_propagates_and_leaves_no_link()
    {
        using var c = await SetupAsync();
        using var db = c.T.Verify();
        var files = new TaskFileService(db, new FakeStore(db) { Reject = "too_large" });

        var ex = await Assert.ThrowsAsync<AttachmentRejectedException>(() =>
            files.UploadAsync(c.Member, c.TaskId, "field", "big.jpg", "image/jpeg", Bytes(), default));
        Assert.Equal("too_large", ex.Code);
        Assert.Equal(0, await c.T.Verify().ProjectTaskFiles.CountAsync());
    }

    [Fact]
    public async Task Delete_is_for_the_uploader_or_project_owner_and_hides_the_file()
    {
        using var c = await SetupAsync();
        var third = c.T.AddUser("TF_3");
        await c.Svc.AddMemberAsync(c.Owner, c.ProjectId, new AddProjectMemberRequest(third.Id, "member"), default);
        using var db = c.T.Verify();
        var files = new TaskFileService(db, new FakeStore(db));
        var mine = (await files.UploadAsync(c.Member, c.TaskId, "field", "a.jpg", "image/jpeg", Bytes(), default)).Value!;
        var other = (await files.UploadAsync(c.Member, c.TaskId, "attachment", "b.txt", "text/plain", Bytes(), default)).Value!;

        Assert.Equal("forbidden", (await files.DeleteAsync(third.Id, c.TaskId, mine.Id, default)).Error);
        Assert.True((await files.DeleteAsync(c.Member, c.TaskId, mine.Id, default)).Success);
        Assert.True((await files.DeleteAsync(c.Owner, c.TaskId, other.Id, default)).Success);
        Assert.Equal("file_not_found", (await files.DeleteAsync(c.Owner, c.TaskId, other.Id, default)).Error);

        Assert.Empty((await files.ListAsync(c.Member, c.TaskId, default)).Value!);
        Assert.Equal(2, await c.T.Verify().ProjectTaskActivities.CountAsync(a => a.TaskId == c.TaskId && a.Action == "file_removed"));
    }

    [Fact]
    public async Task Recent_returns_field_and_deliverable_across_tasks_with_task_titles()
    {
        using var c = await SetupAsync();
        var second = await TaskAsync(c.Svc, c.Owner, c.ProjectId, "Commission");
        using var db = c.T.Verify();
        var files = new TaskFileService(db, new FakeStore(db));
        await files.UploadAsync(c.Member, c.TaskId, "field", "1.jpg", "image/jpeg", Bytes(), default);
        await Task.Delay(5);
        await files.UploadAsync(c.Member, second.Id, "attachment", "notes.txt", "text/plain", Bytes(), default);
        await Task.Delay(5);
        await files.UploadAsync(c.Member, second.Id, "deliverable", "handover.pdf", "application/pdf", Bytes(), default);

        var recent = (await files.RecentAsync(c.Member, c.ProjectId, 12, default)).Value!;
        Assert.Equal(["handover.pdf", "1.jpg"], recent.Select(r => r.FileName));
        Assert.Equal(["Commission", "Install panel"], recent.Select(r => r.TaskTitle));
        Assert.Single((await files.RecentAsync(c.Member, c.ProjectId, 1, default)).Value!);
        Assert.Equal("forbidden", (await files.RecentAsync(c.Outsider, c.ProjectId, 12, default)).Error);
        Assert.Equal("project_not_found", (await files.RecentAsync(c.Member, Guid.NewGuid(), 12, default)).Error);
    }

    // ── ProjectsAttachmentPolicy ───────────────────────────────────────────

    [Fact]
    public async Task Policy_lets_members_read_linked_files_and_only_the_uploader_read_unlinked_ones()
    {
        using var c = await SetupAsync();
        using var db = c.T.Verify();
        var store = new FakeStore(db);
        var files = new TaskFileService(db, store);
        var linked = (await files.UploadAsync(c.Member, c.TaskId, "field", "a.jpg", "image/jpeg", Bytes(), default)).Value!;
        var loose = await store.PutAsync(AttachmentAreas.Projects, "loose.jpg", "image/jpeg", Bytes(), c.Member, default);
        await db.SaveChangesAsync();
        var policy = new ProjectsAttachmentPolicy(c.T.Verify());

        Assert.Equal(AttachmentAreas.Projects, policy.Area);
        Assert.True(await policy.CanReadAsync(linked.AttachmentId, c.Owner, default));
        Assert.True(await policy.CanReadAsync(linked.AttachmentId, c.Member, default));
        Assert.False(await policy.CanReadAsync(linked.AttachmentId, c.Outsider, default));

        Assert.True(await policy.CanReadAsync(loose.Id, c.Member, default));
        Assert.False(await policy.CanReadAsync(loose.Id, c.Owner, default));

        await files.DeleteAsync(c.Member, c.TaskId, linked.Id, default);
        Assert.False(await new ProjectsAttachmentPolicy(c.T.Verify()).CanReadAsync(linked.AttachmentId, c.Owner, default));
    }

    // ── CompletedAt ────────────────────────────────────────────────────────

    [Fact]
    public async Task Update_to_done_sets_CompletedAt_and_leaving_done_clears_it()
    {
        using var c = await SetupAsync();
        var before = DateTime.UtcNow.AddSeconds(-1);

        var done = (await c.Svc.UpdateTaskAsync(c.Owner, c.TaskId, Status("done"), default)).Task!;
        Assert.NotNull(done.CompletedAt);
        Assert.True(done.CompletedAt >= before);
        var firstCompleted = done.CompletedAt;

        // Saving again while done keeps the original completion date.
        var again = (await c.Svc.UpdateTaskAsync(c.Owner, c.TaskId, Status("done"), default)).Task!;
        Assert.Equal(firstCompleted, again.CompletedAt);

        var reopened = (await c.Svc.UpdateTaskAsync(c.Owner, c.TaskId, Status("doing"), default)).Task!;
        Assert.Null(reopened.CompletedAt);
        Assert.Null((await c.T.Verify().ProjectTasks.SingleAsync(t => t.Id == c.TaskId)).CompletedAt);
    }

    [Fact]
    public async Task Move_sets_and_clears_CompletedAt_and_lists_expose_it()
    {
        using var c = await SetupAsync();

        var moved = (await c.Svc.MoveTaskAsync(c.Owner, c.TaskId, new TaskMoveRequest("done", null), default)).Task!;
        Assert.NotNull(moved.CompletedAt);
        var board = (await c.Svc.GetKanbanAsync(c.ProjectId, c.Owner, default))!;
        Assert.Equal(moved.CompletedAt, board.Done.Single(t => t.Id == c.TaskId).CompletedAt);
        Assert.Equal(moved.CompletedAt, (await c.Svc.GetTasksAsync(c.ProjectId, null, default)).Single(t => t.Id == c.TaskId).CompletedAt);
        Assert.Equal(moved.CompletedAt, (await c.Svc.GetTaskListAsync(c.ProjectId, c.Owner, null, 1, 50, default)).Items.Single(t => t.Id == c.TaskId).CompletedAt);

        var back = (await c.Svc.MoveTaskAsync(c.Owner, c.TaskId, new TaskMoveRequest("review", null), default)).Task!;
        Assert.Null(back.CompletedAt);
    }

    [Fact]
    public async Task Bulk_status_sets_and_clears_CompletedAt()
    {
        using var c = await SetupAsync();
        var other = await TaskAsync(c.Svc, c.Owner, c.ProjectId, "Second");

        var r = await c.Svc.BulkUpdateTasksAsync(c.Owner, c.ProjectId, new([c.TaskId, other.Id], "status", "done"), default);
        Assert.True(r.Success);
        using (var db = c.T.Verify())
            Assert.All(await db.ProjectTasks.Where(t => t.ProjectId == c.ProjectId).ToListAsync(), t => Assert.NotNull(t.CompletedAt));

        await c.Svc.BulkUpdateTasksAsync(c.Owner, c.ProjectId, new([c.TaskId], "status", "todo"), default);
        using var after = c.T.Verify();
        Assert.Null((await after.ProjectTasks.SingleAsync(t => t.Id == c.TaskId)).CompletedAt);
        Assert.NotNull((await after.ProjectTasks.SingleAsync(t => t.Id == other.Id)).CompletedAt);
    }

    [Fact]
    public async Task Task_created_done_has_CompletedAt_and_others_do_not()
    {
        using var c = await SetupAsync();
        Assert.NotNull((await TaskAsync(c.Svc, c.Owner, c.ProjectId, "Already done", "done")).CompletedAt);
        Assert.Null((await TaskAsync(c.Svc, c.Owner, c.ProjectId, "Open", "doing")).CompletedAt);
    }

    // ── Fake store ─────────────────────────────────────────────────────────

    private sealed class FakeStore(IPopDbContext db) : IAttachmentStore
    {
        public string? Reject { get; init; }
        public int Puts { get; private set; }

        public async Task<StoredAttachment> PutAsync(string area, string fileName, string contentType, Stream content, Guid uploaderId, CancellationToken ct)
        {
            if (Reject is not null) throw new AttachmentRejectedException(Reject, Reject);
            Puts++;
            using var ms = new MemoryStream();
            await content.CopyToAsync(ms, ct);
            var id = Guid.NewGuid();
            // Mirror the real store: the row exists so the link -> attachment FK holds.
            db.Attachments.Add(new Attachment
            {
                Id = id, Area = area, FileName = fileName, ContentType = contentType, Size = ms.Length,
                Sha256 = "hash", StoragePath = "local:fake/" + id.ToString("N"), UploaderId = uploaderId, CreatedAt = DateTime.UtcNow
            });
            return new StoredAttachment(id, fileName, contentType, ms.Length, "hash");
        }

        public Task<(StoredAttachment Meta, Stream Content)?> OpenAsync(Guid id, CancellationToken ct) =>
            Task.FromResult<(StoredAttachment, Stream)?>(null);

        public Task DeleteAsync(Guid id, CancellationToken ct) => Task.CompletedTask;
    }
}
