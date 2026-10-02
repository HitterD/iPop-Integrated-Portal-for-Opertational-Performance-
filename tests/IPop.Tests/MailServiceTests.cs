using IPop.Application.Mail;
using IPop.Core.Entities;
using IPop.Infrastructure.Mail;
using Xunit;

namespace IPop.Tests;

public class MailServiceTests
{
    [Fact]
    public async Task Send_delivers_to_recipient_inbox_and_sender_sent()
    {
        using var t = new TestDb();
        var a = t.AddUser("A");
        var b = t.AddUser("B");

        var svc = new MailService(t.Verify());
        var result = await svc.SendAsync(a.Id, new ComposeMailRequest(
            "Hello", "<p>Hi</p>", [b.Nik], [], []), default);

        Assert.True(result.Success);
        Assert.NotNull(result.MailId);
        Assert.NotEqual(Guid.Empty, result.MailId.Value);

        // Recipient: the mail lands in their inbox, unread, sender resolved.
        var inbox = await new MailService(t.Verify()).GetFolderAsync(b.Id, MailFolder.Inbox, 1, 20, null, default);
        Assert.Equal(1, inbox.TotalCount);
        var item = Assert.Single(inbox.Items);
        Assert.Equal("Hello", item.Subject);
        Assert.Equal(a.Id, item.SenderId);
        Assert.Equal(a.Name, item.SenderName);
        Assert.False(item.IsRead);

        // Sender: their own row lives in Sent.
        var sent = await new MailService(t.Verify()).GetFolderAsync(a.Id, MailFolder.Sent, 1, 20, null, default);
        Assert.Equal(1, sent.TotalCount);
        Assert.Equal("Hello", Assert.Single(sent.Items).Subject);
    }

    [Fact]
    public async Task SaveDraft_lists_in_draft_folder_without_delivering()
    {
        using var t = new TestDb();
        var a = t.AddUser("A");
        var b = t.AddUser("B");

        var svc = new MailService(t.Verify());
        var result = await svc.SaveDraftAsync(a.Id, new ComposeMailRequest(
            "WIP", "<p>body</p>", [b.Nik], [], []), default);

        Assert.True(result.Success);

        var drafts = await new MailService(t.Verify()).GetFolderAsync(a.Id, MailFolder.Draft, 1, 20, null, default);
        Assert.Equal(1, drafts.TotalCount);
        Assert.Equal("WIP", Assert.Single(drafts.Items).Subject);

        // The recipient must NOT see a draft as inbox mail.
        var inbox = await new MailService(t.Verify()).GetFolderAsync(b.Id, MailFolder.Inbox, 1, 20, null, default);
        Assert.Equal(0, inbox.TotalCount);
    }

    [Fact]
    public async Task Counts_reflect_folders_and_unread_badges()
    {
        using var t = new TestDb();
        var a = t.AddUser("A");
        var b = t.AddUser("B");
        var svc = new MailService(t.Verify());

        await svc.SendAsync(a.Id, new ComposeMailRequest("One", "<p>1</p>", [b.Nik], [], []), default);
        await svc.SendAsync(a.Id, new ComposeMailRequest("Two", "<p>2</p>", [b.Nik], [], []), default);
        var mail2 = await svc.SendAsync(a.Id, new ComposeMailRequest("Three", "<p>3</p>", [b.Nik], [], []), default);
        await svc.MarkReadAsync(b.Id, mail2.MailId!.Value, default);
        await svc.ToggleStarAsync(b.Id, mail2.MailId!.Value, default);

        var counts = await svc.GetFolderCountsAsync(b.Id, default);
        Assert.Equal(2, counts.InboxUnread);      // 3 inbox, 1 read
        Assert.Equal(3, counts.InboxTotal);
        Assert.Equal(1, counts.StarredTotal);
        Assert.Equal(0, counts.SentTotal);        // nothing sent BY b
        Assert.Equal(0, counts.DraftTotal);
    }

    [Fact]
    public async Task Search_filters_by_sender_unread_and_starred()
    {
        using var t = new TestDb();
        var a = t.AddUser("A");
        var b = t.AddUser("B");
        var c = t.AddUser("C");
        var svc = new MailService(t.Verify());

        var m1 = await svc.SendAsync(a.Id, new ComposeMailRequest("Quarterly Report", "<p>x</p>", [b.Nik], [], []), default);
        await svc.SendAsync(a.Id, new ComposeMailRequest("Lunch", "<p>y</p>", [b.Nik], [], []), default);
        await svc.SendAsync(c.Id, new ComposeMailRequest("Other", "<p>z</p>", [b.Nik], [], []), default);
        await svc.MarkReadAsync(b.Id, m1.MailId!.Value, default);
        await svc.ToggleStarAsync(b.Id, m1.MailId!.Value, default);

        // Sender filter — cross-provider (equality, translatable on SQLite).
        var bySender = await svc.GetFolderAsync(b.Id, MailFolder.Inbox, 1, 20, new MailSearchRequest(SenderNik: a.Nik), default);
        Assert.Equal(2, bySender.TotalCount);

        // Unread filter.
        var unread = await svc.GetFolderAsync(b.Id, MailFolder.Inbox, 1, 20, new MailSearchRequest(UnreadOnly: true), default);
        Assert.Equal(2, unread.TotalCount);

        // Starred filter.
        var starred = await svc.GetFolderAsync(b.Id, MailFolder.Starred, 1, 20, null, default);
        Assert.Equal(1, starred.TotalCount);
        Assert.Equal("Quarterly Report", Assert.Single(starred.Items).Subject);
    }

    [Fact]
    public async Task Threading_preserves_reply_chain()
    {
        using var t = new TestDb();
        var a = t.AddUser("A");
        var b = t.AddUser("B");
        var svc = new MailService(t.Verify());

        var root = await svc.SendAsync(a.Id, new ComposeMailRequest("Thread root", "<p>1</p>", [b.Nik], [], []), default);
        var reply = await svc.SendAsync(b.Id, new ComposeMailRequest("Re: Thread root", "<p>2</p>", [a.Nik], [], [], InReplyToId: root.MailId), default);

        Assert.True(reply.Success);

        var thread = await svc.GetThreadAsync(b.Id, root.MailId!.Value, 1, 50, default);
        Assert.Equal(2, thread.TotalCount);
        Assert.Equal(2, thread.Items.Count);

        // Thread root carries the count.
        var detail = await new MailService(t.Verify()).GetAsync(b.Id, root.MailId.Value, default);
        Assert.NotNull(detail);
        Assert.True(detail.ThreadCount >= 1);
    }

    [Fact]
    public async Task Bulk_move_and_delete_apply_to_selected_ids()
    {
        using var t = new TestDb();
        var a = t.AddUser("A");
        var b = t.AddUser("B");
        var svc = new MailService(t.Verify());

        var m1 = await svc.SendAsync(a.Id, new ComposeMailRequest("One", "<p>1</p>", [b.Nik], [], []), default);
        var m2 = await svc.SendAsync(a.Id, new ComposeMailRequest("Two", "<p>2</p>", [b.Nik], [], []), default);
        var m3 = await svc.SendAsync(a.Id, new ComposeMailRequest("Three", "<p>3</p>", [b.Nik], [], []), default);

        var moved = await svc.BulkMoveAsync(b.Id, [m1.MailId!.Value, m2.MailId!.Value], MailFolder.Archive, default);
        Assert.Equal(2, moved.Affected);

        var archive = await svc.GetFolderAsync(b.Id, MailFolder.Archive, 1, 20, null, default);
        Assert.Equal(2, archive.TotalCount);

        var deleted = await svc.BulkDeleteAsync(b.Id, [m3.MailId!.Value], default);
        Assert.Equal(1, deleted.Affected);
        var trash = await svc.GetFolderAsync(b.Id, MailFolder.Trash, 1, 20, null, default);
        Assert.Equal(1, trash.TotalCount);
    }

    [Fact]
    public async Task Move_from_trash_requires_explicit_restore()
    {
        using var t = new TestDb();
        var a = t.AddUser("A");
        var b = t.AddUser("B");
        var svc = new MailService(t.Verify());

        var m1 = await svc.SendAsync(a.Id, new ComposeMailRequest("One", "<p>1</p>", [b.Nik], [], []), default);
        await svc.DeleteAsync(b.Id, m1.MailId!.Value, default);

        var blocked = await svc.MoveToFolderAsync(b.Id, m1.MailId!.Value, MailFolder.Inbox, default);
        Assert.False(blocked.Success);
        Assert.Equal("mail_trashed", blocked.Error);

        // Restore = move back to inbox (allowed since target is not trash).
        var restore = await svc.MoveToFolderAsync(b.Id, m1.MailId!.Value, MailFolder.Trash, default); // no-op, still in trash
        Assert.True(restore.Success);
    }

    [Fact]
    public async Task Bcc_recipient_is_hidden_from_to_recipients()
    {
        using var t = new TestDb();
        var a = t.AddUser("A");
        var b = t.AddUser("B");
        var hidden = t.AddUser("C");
        var svc = new MailService(t.Verify());

        await svc.SendAsync(a.Id, new ComposeMailRequest("Secret", "<p>hi</p>", [b.Nik], [], [hidden.Nik]), default);

        var bDetail = await svc.GetAsync(b.Id, t.Verify().Mails.Single().Id, default);
        Assert.NotNull(bDetail);
        // B's view hides the BCC recipient — only To is visible to B.
        Assert.DoesNotContain(bDetail.Recipients, r => r.UserId == hidden.Id);

        // But the hidden recipient still receives it in their inbox.
        var hiddenInbox = await svc.GetFolderAsync(hidden.Id, MailFolder.Inbox, 1, 20, null, default);
        Assert.Equal(1, hiddenInbox.TotalCount);
    }

    [Fact]
    public async Task Send_with_null_cc_bcc_does_not_throw()
    {
        using var t = new TestDb();
        var a = t.AddUser("A");
        var b = t.AddUser("B");

        // System.Text.Json leaves omitted IReadOnlyList<> reference types null (repro'd: web defaults
        // give Cc/Bcc null when missing). Simulate exactly what an API client sends.
        var svc = new MailService(t.Verify());
        var result = await svc.SendAsync(a.Id, new ComposeMailRequest("Hello", "<p>hi</p>", [b.Nik], null!, null!), default);

        Assert.True(result.Success);
        Assert.NotNull(result.MailId);
    }

    [Fact]
    public async Task Send_to_self_does_not_violate_unique_recipient_index()
    {
        using var t = new TestDb();
        var a = t.AddUser("A");

        // Sender lists themselves in To — before the fix this collided with their own "from" row
        // on the unique (MailId, UserId) index and exploded (500 on the API).
        var svc = new MailService(t.Verify());
        var result = await svc.SendAsync(a.Id, new ComposeMailRequest("To me", "<p>note</p>", [a.Nik], [], []), default);

        Assert.True(result.Success);
        Assert.NotNull(result.MailId);

        // Their own row still exists — in Sent — and no second duplicate (mail, user) row.
        var sent = await svc.GetFolderAsync(a.Id, MailFolder.Sent, 1, 20, null, default);
        Assert.Equal(1, sent.TotalCount);
        var rows = t.Verify().MailRecipients.Count(r => r.MailId == result.MailId.Value && r.UserId == a.Id);
        Assert.Equal(1, rows);
    }

    [Fact]
    public async Task Deep_reply_chain_keeps_the_original_root_and_all_messages()
    {
        using var t = new TestDb();
        var a = t.AddUser("A");
        var b = t.AddUser("B");
        var svc = new MailService(t.Verify());

        var root = await svc.SendAsync(a.Id, new ComposeMailRequest("Root", "one", [b.Nik], [], []), default);
        var firstReply = await svc.SendAsync(b.Id, new ComposeMailRequest("Re: Root", "two", [a.Nik], [], [], InReplyToId: root.MailId), default);
        var secondReply = await svc.SendAsync(a.Id, new ComposeMailRequest("Re: Root", "three", [b.Nik], [], [], InReplyToId: firstReply.MailId), default);

        var thread = await svc.GetThreadAsync(a.Id, root.MailId!.Value, 1, 50, default);

        Assert.Equal(3, thread.TotalCount);
        Assert.Equal(3, thread.Items.Count);
        var detail = await svc.GetAsync(a.Id, root.MailId.Value, default);
        Assert.NotNull(detail);
        Assert.Equal(2, detail.ThreadCount);
    }

    [Fact]
    public async Task Search_query_matches_subject_body_and_sender_on_sqlite()
    {
        using var t = new TestDb();
        var sender = t.AddUser("Sender");
        var recipient = t.AddUser("Recipient");
        var svc = new MailService(t.Verify());

        await svc.SendAsync(sender.Id, new ComposeMailRequest("Quarterly report", "body", [recipient.Nik], [], []), default);
        await svc.SendAsync(sender.Id, new ComposeMailRequest("Other", "Quarterly body", [recipient.Nik], [], []), default);

        var result = await svc.GetFolderAsync(recipient.Id, MailFolder.Inbox, 1, 20, new MailSearchRequest(Query: "quarterly"), default);

        Assert.Equal(2, result.TotalCount);
    }

    [Fact]
    public async Task Short_snippet_does_not_get_a_trailing_ellipsis()
    {
        using var t = new TestDb();
        var sender = t.AddUser("Sender");
        var recipient = t.AddUser("Recipient");
        var svc = new MailService(t.Verify());

        await svc.SendAsync(sender.Id, new ComposeMailRequest("Short", "hello", [recipient.Nik], [], []), default);

        var result = await svc.GetFolderAsync(recipient.Id, MailFolder.Inbox, 1, 20, null, default);

        Assert.Equal("hello", Assert.Single(result.Items).Snippet);
    }

    [Fact]
    public async Task Bulk_restore_moves_trashed_messages_back_to_inbox()
    {
        using var t = new TestDb();
        var sender = t.AddUser("Sender");
        var recipient = t.AddUser("Recipient");
        var svc = new MailService(t.Verify());

        var sent = await svc.SendAsync(sender.Id, new ComposeMailRequest("Restore me", "body", [recipient.Nik], [], []), default);
        await svc.DeleteAsync(recipient.Id, sent.MailId!.Value, default);

        var restored = await svc.BulkRestoreAsync(recipient.Id, [sent.MailId.Value], default);

        Assert.Equal(1, restored.Affected);
        var inbox = await svc.GetFolderAsync(recipient.Id, MailFolder.Inbox, 1, 20, null, default);
        Assert.Equal(1, inbox.TotalCount);
    }

    [Fact]
    public async Task Mail_detail_exposes_only_project_links_the_reader_can_access()
    {
        using var t = new TestDb();
        var sender = t.AddUser("Sender");
        var recipient = t.AddUser("Recipient");
        var outsider = t.AddUser("Outsider");
        var project = new Project
        {
            Id = Guid.NewGuid(), Name = "Alpha Project", Year = 2026, OwnerId = recipient.Id,
            Status = "active", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        };
        t.Db.Projects.Add(project);
        t.Db.ProjectMembers.Add(new ProjectMember { ProjectId = project.Id, UserId = recipient.Id, Role = "owner", JoinedAt = DateTime.UtcNow });
        t.Db.SaveChanges();
        var svc = new MailService(t.Verify());
        var sent = await svc.SendAsync(sender.Id, new ComposeMailRequest(
            "Project update", $"@[Alpha Project](p:{project.Id})", [recipient.Nik], [], []), default);

        var linked = await svc.GetAsync(recipient.Id, sent.MailId!.Value, default);
        var hidden = await svc.GetAsync(outsider.Id, sent.MailId.Value, default);

        Assert.NotNull(linked);
        var link = Assert.Single(linked.LinkedProjects);
        Assert.Equal(project.Id, link.ProjectId);
        Assert.Equal("Alpha Project", link.Name);
        Assert.Null(hidden);
    }

    [Fact]
    public async Task ScheduleAsync_creates_mail_in_scheduled_folder_without_delivering_to_recipient()
    {
        using var t = new TestDb();
        var a = t.AddUser("A");
        var b = t.AddUser("B");

        var svc = new MailService(t.Verify());
        var future = DateTime.UtcNow.AddHours(2);
        var result = await svc.SendAsync(a.Id, new ComposeMailRequest(
            "Scheduled Report", "<p>Content</p>", [b.Nik], [], [],
            ScheduledAt: future, Importance: "high"), default);

        Assert.True(result.Success);
        Assert.NotNull(result.MailId);

        // Sender: lands in Scheduled folder, NOT in Sent.
        var scheduled = await svc.GetFolderAsync(a.Id, MailFolder.Scheduled, 1, 20, null, default);
        Assert.Equal(1, scheduled.TotalCount);
        var item = Assert.Single(scheduled.Items);
        Assert.Equal("Scheduled Report", item.Subject);
        Assert.Equal("high", item.Importance);
        Assert.NotNull(item.ScheduledAt);

        var sent = await svc.GetFolderAsync(a.Id, MailFolder.Sent, 1, 20, null, default);
        Assert.Equal(0, sent.TotalCount);

        // Recipient: must NOT see in Inbox yet.
        var inbox = await svc.GetFolderAsync(b.Id, MailFolder.Inbox, 1, 20, null, default);
        Assert.Equal(0, inbox.TotalCount);

        // Folder counts: sender has 1 ScheduledTotal
        var counts = await svc.GetFolderCountsAsync(a.Id, default);
        Assert.Equal(1, counts.ScheduledTotal);
    }

    [Fact]
    public async Task DeliverDueScheduledMails_delivers_ready_mails_and_moves_sender_to_sent()
    {
        using var t = new TestDb();
        var a = t.AddUser("A");
        var b = t.AddUser("B");

        var svc = new MailService(t.Verify());
        // Create scheduled mail with past date (due for delivery)
        var past = DateTime.UtcNow.AddMinutes(-5);
        var result = await svc.SendAsync(a.Id, new ComposeMailRequest(
            "Due Mail", "<p>Ready</p>", [b.Nik], [], [],
            ScheduledAt: past), default);

        Assert.True(result.Success);

        // Trigger scheduled delivery sweep
        var deliveredCount = await svc.DeliverDueScheduledMailsAsync(default);
        Assert.Equal(1, deliveredCount);

        // Now recipient has it in Inbox
        var inbox = await svc.GetFolderAsync(b.Id, MailFolder.Inbox, 1, 20, null, default);
        Assert.Equal(1, inbox.TotalCount);
        Assert.Equal("Due Mail", Assert.Single(inbox.Items).Subject);

        // Sender: moved from Scheduled to Sent
        var scheduled = await svc.GetFolderAsync(a.Id, MailFolder.Scheduled, 1, 20, null, default);
        Assert.Equal(0, scheduled.TotalCount);

        var sent = await svc.GetFolderAsync(a.Id, MailFolder.Sent, 1, 20, null, default);
        Assert.Equal(1, sent.TotalCount);
    }

    [Fact]
    public async Task CancelSchedule_reverts_to_draft_folder()
    {
        using var t = new TestDb();
        var a = t.AddUser("A");
        var b = t.AddUser("B");

        var svc = new MailService(t.Verify());
        var future = DateTime.UtcNow.AddHours(3);
        var result = await svc.SendAsync(a.Id, new ComposeMailRequest(
            "To Cancel", "<p>Text</p>", [b.Nik], [], [], ScheduledAt: future), default);

        Assert.True(result.Success);
        var cancelResult = await svc.CancelScheduleAsync(a.Id, result.MailId!.Value, default);
        Assert.True(cancelResult.Success);

        // Moved from Scheduled to Draft
        var scheduled = await svc.GetFolderAsync(a.Id, MailFolder.Scheduled, 1, 20, null, default);
        Assert.Equal(0, scheduled.TotalCount);

        var drafts = await svc.GetFolderAsync(a.Id, MailFolder.Draft, 1, 20, null, default);
        Assert.Equal(1, drafts.TotalCount);
        Assert.Equal("To Cancel", Assert.Single(drafts.Items).Subject);
    }

    [Fact]
    public async Task TogglePin_pins_and_unpins_recipient_mail()
    {
        using var t = new TestDb();
        var a = t.AddUser("A");
        var b = t.AddUser("B");

        var svc = new MailService(t.Verify());
        var m = await svc.SendAsync(a.Id, new ComposeMailRequest("Normal mail", "<p>hi</p>", [b.Nik], [], []), default);

        // Pin it
        var pinRes = await svc.TogglePinAsync(b.Id, m.MailId!.Value, default);
        Assert.True(pinRes.Success);

        var inbox = await svc.GetFolderAsync(b.Id, MailFolder.Inbox, 1, 20, null, default);
        Assert.True(Assert.Single(inbox.Items).IsPinned);

        // Unpin it
        var unpinRes = await svc.TogglePinAsync(b.Id, m.MailId!.Value, default);
        Assert.True(unpinRes.Success);

        inbox = await svc.GetFolderAsync(b.Id, MailFolder.Inbox, 1, 20, null, default);
        Assert.False(Assert.Single(inbox.Items).IsPinned);
    }

    [Fact]
    public async Task GetFolder_orders_pinned_mails_first()
    {
        using var t = new TestDb();
        var a = t.AddUser("A");
        var b = t.AddUser("B");

        var svc = new MailService(t.Verify());
        var m1 = await svc.SendAsync(a.Id, new ComposeMailRequest("First (older)", "<p>1</p>", [b.Nik], [], []), default);
        var m2 = await svc.SendAsync(a.Id, new ComposeMailRequest("Second (newer)", "<p>2</p>", [b.Nik], [], []), default);

        // Pin the older one (m1)
        await svc.TogglePinAsync(b.Id, m1.MailId!.Value, default);

        var inbox = await svc.GetFolderAsync(b.Id, MailFolder.Inbox, 1, 20, null, default);
        Assert.Equal(2, inbox.TotalCount);
        // First item in list must be the pinned one (m1), even though m2 is newer
        Assert.Equal("First (older)", inbox.Items[0].Subject);
        Assert.True(inbox.Items[0].IsPinned);
        Assert.Equal("Second (newer)", inbox.Items[1].Subject);
        Assert.False(inbox.Items[1].IsPinned);
    }

    // ── Regression: thread discovery from the ROOT message ────────────────────
    // The root of a thread has ThreadRootId == null (only replies carry it). Any caller that
    // decides "does this message have a conversation?" by testing ThreadRootId != null will
    // silently skip every root message, so the first mail in a thread never shows its replies.
    // The reliable signal is ThreadCount; the thread key is (ThreadRootId ?? Id).

    [Fact]
    public async Task Root_message_has_null_ThreadRootId_but_still_reports_its_replies()
    {
        using var t = new TestDb();
        var a = t.AddUser("A");
        var b = t.AddUser("B");
        var svc = new MailService(t.Verify());

        var root = await svc.SendAsync(a.Id, new ComposeMailRequest("Root", "<p>one</p>", [b.Nik], [], []), default);
        await svc.SendAsync(b.Id, new ComposeMailRequest("Re: Root", "<p>two</p>", [a.Nik], [], [], InReplyToId: root.MailId), default);

        var rootDetail = await svc.GetAsync(a.Id, root.MailId!.Value, default);
        Assert.NotNull(rootDetail);

        // The root genuinely has no ThreadRootId — this is what the buggy UI check tripped over.
        Assert.Null(rootDetail.ThreadRootId);
        // ...yet it has a conversation, and ThreadCount is the signal that says so.
        Assert.Equal(1, rootDetail.ThreadCount);

        // Resolving the thread key as (ThreadRootId ?? Id) finds the whole conversation.
        var threadKey = rootDetail.ThreadRootId ?? rootDetail.Id;
        var thread = await svc.GetThreadAsync(a.Id, threadKey, 1, 50, default);
        Assert.Equal(2, thread.TotalCount);
    }

    [Fact]
    public async Task Thread_key_resolves_to_the_same_conversation_from_root_and_from_reply()
    {
        using var t = new TestDb();
        var a = t.AddUser("A");
        var b = t.AddUser("B");
        var svc = new MailService(t.Verify());

        var root = await svc.SendAsync(a.Id, new ComposeMailRequest("Root", "one", [b.Nik], [], []), default);
        var reply = await svc.SendAsync(b.Id, new ComposeMailRequest("Re: Root", "two", [a.Nik], [], [], InReplyToId: root.MailId), default);

        var rootDetail = await svc.GetAsync(a.Id, root.MailId!.Value, default);
        var replyDetail = await svc.GetAsync(a.Id, reply.MailId!.Value, default);
        Assert.NotNull(rootDetail);
        Assert.NotNull(replyDetail);

        var keyFromRoot = rootDetail.ThreadRootId ?? rootDetail.Id;
        var keyFromReply = replyDetail.ThreadRootId ?? replyDetail.Id;
        Assert.Equal(keyFromRoot, keyFromReply);

        // Opening either message must surface the same two-message conversation.
        var fromRoot = await svc.GetThreadAsync(a.Id, keyFromRoot, 1, 50, default);
        var fromReply = await svc.GetThreadAsync(a.Id, keyFromReply, 1, 50, default);
        Assert.Equal(2, fromRoot.TotalCount);
        Assert.Equal(2, fromReply.TotalCount);
    }

    [Fact]
    public async Task Standalone_message_with_no_replies_reports_no_conversation()
    {
        using var t = new TestDb();
        var a = t.AddUser("A");
        var b = t.AddUser("B");
        var svc = new MailService(t.Verify());

        var solo = await svc.SendAsync(a.Id, new ComposeMailRequest("Solo", "only", [b.Nik], [], []), default);

        var detail = await svc.GetAsync(b.Id, solo.MailId!.Value, default);
        Assert.NotNull(detail);
        Assert.Null(detail.ThreadRootId);
        // ThreadCount 0 is what keeps the thread stack hidden for ordinary one-off mail.
        Assert.Equal(0, detail.ThreadCount);
    }

    [Fact]
    public async Task Thread_is_returned_newest_first()
    {
        using var t = new TestDb();
        var a = t.AddUser("A");
        var b = t.AddUser("B");
        var svc = new MailService(t.Verify());

        var root = await svc.SendAsync(a.Id, new ComposeMailRequest("Root", "one", [b.Nik], [], []), default);
        var r1 = await svc.SendAsync(b.Id, new ComposeMailRequest("Re: Root", "two", [a.Nik], [], [], InReplyToId: root.MailId), default);
        var r2 = await svc.SendAsync(a.Id, new ComposeMailRequest("Re: Root", "three", [b.Nik], [], [], InReplyToId: r1.MailId), default);

        var thread = await svc.GetThreadAsync(a.Id, root.MailId!.Value, 1, 50, default);

        Assert.Equal(3, thread.Items.Count);
        Assert.Equal(r2.MailId!.Value, thread.Items[0].Id);
        Assert.Equal(r1.MailId!.Value, thread.Items[1].Id);
        Assert.Equal(root.MailId.Value, thread.Items[2].Id);
    }

    [Fact]
    public async Task Thread_pages_hold_newest_first_then_older_without_overlap()
    {
        using var t = new TestDb();
        var a = t.AddUser("A");
        var b = t.AddUser("B");
        var svc = new MailService(t.Verify());

        var root = await svc.SendAsync(a.Id, new ComposeMailRequest("Root", "m0", [b.Nik], [], []), default);
        var ids = new List<Guid> { root.MailId!.Value };
        for (var i = 1; i < 5; i++)
        {
            var sender = i % 2 == 0 ? a : b;
            var to = i % 2 == 0 ? b : a;
            var r = await svc.SendAsync(sender.Id, new ComposeMailRequest("Re: Root", $"m{i}", [to.Nik], [], [], InReplyToId: ids[^1]), default);
            ids.Add(r.MailId!.Value);
        }

        var p1 = await svc.GetThreadAsync(a.Id, root.MailId.Value, 1, 2, default);
        var p2 = await svc.GetThreadAsync(a.Id, root.MailId.Value, 2, 2, default);
        var p3 = await svc.GetThreadAsync(a.Id, root.MailId.Value, 3, 2, default);

        Assert.Equal(5, p1.TotalCount);
        Assert.Equal([ids[4], ids[3]], p1.Items.Select(m => m.Id));
        Assert.Equal([ids[2], ids[1]], p2.Items.Select(m => m.Id));
        Assert.Equal([ids[0]], p3.Items.Select(m => m.Id));
    }

    [Fact]
    public async Task EmptyTrash_PermanentlyDeletesAllTrashedItemsForUser()
    {
        using var t = new TestDb();
        var a = t.AddUser("A");
        var b = t.AddUser("B");
        var svc = new MailService(t.Verify());

        var m1 = await svc.SendAsync(a.Id, new ComposeMailRequest("Mail 1", "b1", [b.Nik], [], []), default);
        var m2 = await svc.SendAsync(a.Id, new ComposeMailRequest("Mail 2", "b2", [b.Nik], [], []), default);

        await svc.DeleteAsync(b.Id, m1.MailId!.Value, default); // moves to trash
        await svc.DeleteAsync(b.Id, m2.MailId!.Value, default); // moves to trash

        var trashBefore = await svc.GetFolderAsync(b.Id, MailFolder.Trash, 1, 20, null, default);
        Assert.Equal(2, trashBefore.TotalCount);

        var count = await svc.EmptyTrashAsync(b.Id, default);
        Assert.Equal(2, count);

        var trashAfter = await svc.GetFolderAsync(b.Id, MailFolder.Trash, 1, 20, null, default);
        Assert.Equal(0, trashAfter.TotalCount);
    }

    [Fact]
    public async Task ToggleMute_TogglesMuteFlagAndReflectsInSummaryAndDetail()
    {
        using var t = new TestDb();
        var a = t.AddUser("A");
        var b = t.AddUser("B");
        var svc = new MailService(t.Verify());

        var m = await svc.SendAsync(a.Id, new ComposeMailRequest("Mail", "b", [b.Nik], [], []), default);
        var mailId = m.MailId!.Value;

        var muted = await svc.ToggleMuteAsync(b.Id, mailId, default);
        Assert.True(muted);

        var detail = await svc.GetAsync(b.Id, mailId, default);
        Assert.NotNull(detail);
        Assert.True(detail.IsMuted);

        var folder = await svc.GetFolderAsync(b.Id, MailFolder.Inbox, 1, 20, null, default);
        Assert.True(folder.Items[0].IsMuted);

        var unmuted = await svc.ToggleMuteAsync(b.Id, mailId, default);
        Assert.False(unmuted);
    }

    [Fact]
    public async Task Send_applies_only_the_senders_own_labels_to_the_senders_copy()
    {
        using var t = new TestDb();
        var a = t.AddUser("A");
        var b = t.AddUser("B");
        var mine = new MailLabel { Id = Guid.NewGuid(), UserId = a.Id, Name = "Proyek", Color = "#0A84FF", CreatedAt = DateTime.UtcNow };
        var theirs = new MailLabel { Id = Guid.NewGuid(), UserId = b.Id, Name = "Pribadi", Color = "#FF9F0A", CreatedAt = DateTime.UtcNow };
        t.Db.MailLabels.AddRange(mine, theirs);
        t.Db.SaveChanges();

        var result = await new MailService(t.Verify()).SendAsync(a.Id, new ComposeMailRequest(
            "Label", "<p>x</p>", [b.Nik], [], [], LabelIds: [mine.Id, theirs.Id, mine.Id]), default);

        Assert.True(result.Success);
        var assigned = t.Verify().MailLabelAssignments.Where(x => x.MailId == result.MailId).Select(x => x.LabelId).ToList();
        Assert.Equal([mine.Id], assigned);
    }

    [Fact]
    public async Task Draft_keeps_recipients_labels_and_extras_for_its_author_only()
    {
        using var t = new TestDb();
        var a = t.AddUser("A");
        var b = t.AddUser("B");
        var c = t.AddUser("C");
        var l1 = new MailLabel { Id = Guid.NewGuid(), UserId = a.Id, Name = "One", Color = "#0A84FF", CreatedAt = DateTime.UtcNow };
        var l2 = new MailLabel { Id = Guid.NewGuid(), UserId = a.Id, Name = "Two", Color = "#FF9F0A", CreatedAt = DateTime.UtcNow };
        t.Db.MailLabels.AddRange(l1, l2);
        t.Db.SaveChanges();

        var saved = await new MailService(t.Verify()).SaveDraftAsync(a.Id, new ComposeMailRequest(
            "WIP", "<p>x</p>", [b.Nik], [c.Nik], [], LabelIds: [l1.Id], DraftExtra: "{\"meeting\":true}"), default);
        var id = saved.MailId!.Value;

        var d = (await new MailService(t.Verify()).GetAsync(a.Id, id, default))!;
        Assert.Equal([(b.Nik, "to"), (c.Nik, "cc")], d.Recipients.Where(r => r.RecipientType != "from").Select(r => (r.Nik, r.RecipientType)));
        Assert.Equal([l1.Id], d.LabelIds);
        Assert.Equal("{\"meeting\":true}", d.DraftExtra);
        // Nobody else gets a row (or a notification) while it is a draft.
        Assert.Null(await new MailService(t.Verify()).GetAsync(b.Id, id, default));

        await new MailService(t.Verify()).UpdateDraftAsync(a.Id, id, new ComposeMailRequest(
            "WIP", "<p>x</p>", [], [], [c.Nik], LabelIds: [l2.Id]), default);
        d = (await new MailService(t.Verify()).GetAsync(a.Id, id, default))!;
        Assert.Equal([(c.Nik, "bcc")], d.Recipients.Where(r => r.RecipientType != "from").Select(r => (r.Nik, r.RecipientType)));
        Assert.Equal([l2.Id], d.LabelIds);
        Assert.Null(d.DraftExtra);
    }

    [Fact]
    public async Task BulkMarkRead_marks_only_the_callers_selected_rows()
    {
        using var t = new TestDb();
        var a = t.AddUser("A");
        var b = t.AddUser("B");
        var svc = new MailService(t.Verify());
        var m1 = (await svc.SendAsync(a.Id, new ComposeMailRequest("1", "<p>1</p>", [b.Nik], [], []), default)).MailId!.Value;
        var m2 = (await svc.SendAsync(a.Id, new ComposeMailRequest("2", "<p>2</p>", [b.Nik], [], []), default)).MailId!.Value;
        var m3 = (await svc.SendAsync(a.Id, new ComposeMailRequest("3", "<p>3</p>", [b.Nik], [], []), default)).MailId!.Value;

        var r = await new MailService(t.Verify()).BulkMarkReadAsync(b.Id, [m1, m2], default);

        Assert.Equal(2, r.Affected);
        var rows = t.Verify().MailRecipients.Where(x => x.UserId == b.Id).ToDictionary(x => x.MailId, x => x.IsRead);
        Assert.True(rows[m1]);
        Assert.True(rows[m2]);
        Assert.False(rows[m3]);
        // Ids the caller isn't a participant of are ignored.
        var c = t.AddUser("C");
        Assert.Equal(0, (await new MailService(t.Verify()).BulkMarkReadAsync(c.Id, [m3], default)).Affected);
        Assert.False(t.Verify().MailRecipients.Single(x => x.UserId == b.Id && x.MailId == m3).IsRead);
    }

    [Fact]
    public async Task Forward_carries_original_attachments_or_only_the_kept_ones()
    {
        using var t = new TestDb();
        var a = t.AddUser("A");
        var b = t.AddUser("B");
        var c = t.AddUser("C");
        var orig = (await new MailService(t.Verify()).SendAsync(a.Id, new ComposeMailRequest("Doc", "<p>x</p>", [b.Nik], [], []), default)).MailId!.Value;
        MailAttachment Att(string name) => new()
        {
            Id = Guid.NewGuid(), MailId = orig, FileName = name, FileSize = 10, MimeType = "application/pdf",
            SynologyServer = "nas", SynologyPath = "/mail/" + name, CreatedAt = DateTime.UtcNow,
        };
        var keep = Att("keep.pdf");
        t.Db.MailAttachments.AddRange(keep, Att("drop.pdf"));
        t.Db.SaveChanges();

        var all = await new MailService(t.Verify()).ForwardAsync(b.Id, orig, new ComposeMailRequest("Fwd: Doc", "<p>y</p>", [c.Nik], [], []), default);
        var some = await new MailService(t.Verify()).ForwardAsync(b.Id, orig,
            new ComposeMailRequest("Fwd: Doc", "<p>y</p>", [c.Nik], [], [], ForwardAttachmentIds: [keep.Id]), default);

        var db = t.Verify();
        Assert.Equal(2, db.MailAttachments.Count(x => x.MailId == all.MailId));
        Assert.Equal(["keep.pdf"], db.MailAttachments.Where(x => x.MailId == some.MailId).Select(x => x.FileName).ToList());
        // Only participants of the original may forward it.
        Assert.Equal("forbidden", (await new MailService(t.Verify()).ForwardAsync(c.Id, orig,
            new ComposeMailRequest("Fwd", "<p/>", [a.Nik], [], []), default)).Error);
    }
}
