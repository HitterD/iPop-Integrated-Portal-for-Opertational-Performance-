using IPop.Application.Chat;
using IPop.Application.Mail;
using IPop.Core.Entities;
using IPop.Infrastructure.Badges;
using IPop.Infrastructure.Chat;
using IPop.Infrastructure.Mail;
using IPop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;
using ChatPage = IPop.Application.Chat.PagedResult<IPop.Application.Chat.MessageDto>;
using MailPage = IPop.Application.Mail.PagedResult<IPop.Application.Mail.MailSummaryDto>;

namespace IPop.Tests;

/// Volume test 2026-11-12 (26M chat, 5M mail on a copy DB): room list, open chat, unread badges and
/// the inbox read or sorted every row of a room/user history. These pin the bounded behaviour.
public class ChatVolumeQueryTests
{
    private static (TestDb T, User Me, User Other, ChatRoom Room) Room(int fromOther, string type = "group")
    {
        var t = new TestDb();
        var me = t.AddUser("VQ_ME"); var other = t.AddUser("VQ_OTHER");
        var room = t.AddRoom(type, type == "group" ? "Ops" : null);
        t.AddMember(room.Id, me.Id); t.AddMember(room.Id, other.Id);
        var start = DateTime.UtcNow.AddHours(-1);
        for (var i = 0; i < fromOther; i++)
            t.Db.ChatMessages.Add(new ChatMessage { Id = Guid.NewGuid(), RoomId = room.Id, SenderId = other.Id, Type = "text",
                Content = $"m{i}", IdempotencyKey = Guid.NewGuid(), CreatedAt = start.AddSeconds(i) });
        t.Db.SaveChanges();
        return (t, me, other, room);
    }

    [Fact]
    public async Task Room_list_unread_is_capped_so_a_long_unread_room_is_not_counted_row_by_row()
    {
        var (t, me, _, room) = Room(ChatService.UnreadCap + 40);
        using var _t = t;

        var rooms = await new ChatService(t.Verify()).GetRoomsAsync(me.Id, RoomTab.Groups, 1, 20, default);

        Assert.Equal(ChatService.UnreadCap, Assert.Single(rooms.Items).UnreadCount);
    }

    [Fact]
    public async Task Room_list_unread_below_the_cap_is_exact_and_respects_the_read_mark()
    {
        var (t, me, other, room) = Room(5);
        using var _t = t;
        var mark = t.Db.ChatMessages.Where(m => m.RoomId == room.Id).OrderBy(m => m.CreatedAt).Skip(1).First().CreatedAt;
        t.AddSettings(me.Id, room.Id, s => s.LastReadMessageAt = mark);
        t.AddMessage(room.Id, me.Id, "mine");                       // own message never counts
        t.AddMessage(room.Id, other.Id, "joined", type: "system");  // nor system lines

        var rooms = await new ChatService(t.Verify()).GetRoomsAsync(me.Id, RoomTab.Groups, 1, 20, default);

        Assert.Equal(3, Assert.Single(rooms.Items).UnreadCount);
    }

    [Fact]
    public async Task Room_list_last_message_is_still_the_newest_top_level_message()
    {
        var (t, me, other, room) = Room(3);
        using var _t = t;
        var parent = t.AddMessage(room.Id, other.Id, "newest top level");
        t.Db.ChatMessages.Add(new ChatMessage { Id = Guid.NewGuid(), RoomId = room.Id, SenderId = other.Id, Type = "text",
            Content = "thread reply", ThreadParentId = parent.Id, IdempotencyKey = Guid.NewGuid(), CreatedAt = DateTime.UtcNow.AddMinutes(1) });
        t.Db.SaveChanges();

        var rooms = await new ChatService(t.Verify()).GetRoomsAsync(me.Id, RoomTab.Groups, 1, 20, default);

        Assert.Contains("newest top level", Assert.Single(rooms.Items).LastMessage);
    }

    [Fact]
    public async Task Chat_badge_is_capped_per_room()
    {
        var (t, me, other, room) = Room(ChatService.UnreadCap + 10);
        using var _t = t;
        var second = t.AddRoom("dm");
        t.AddMember(second.Id, me.Id); t.AddMember(second.Id, other.Id);
        t.AddMessage(second.Id, other.Id, "hi");

        Assert.Equal(ChatService.UnreadCap + 1, await new ChatBadgeProvider(t.Verify()).CountAsync(me.Id, default));
    }

    [Fact]
    public async Task Opening_a_chat_says_older_history_exists_without_counting_all_of_it()
    {
        var (t, me, _, room) = Room(120);
        using var _t = t;
        var counter = new SqlLog();
        var options = new DbContextOptionsBuilder<IPopDbContext>().UseSqlite(t.Db.Database.GetDbConnection()).AddInterceptors(counter).Options;

        ChatPage page;
        await using (var db = new IPopDbContext(options))
            page = await new ChatService(db).GetMessagesAsync(room.Id, me.Id, 1, 50, default);

        Assert.Equal(50, page.Items.Count);
        Assert.True(page.TotalCount > page.Items.Count, "client must still see that older messages exist");
        Assert.DoesNotContain(counter.Texts, q => q.Contains("COUNT(*)") && q.Contains("\"ChatMessages\""));
    }

    [Fact]
    public async Task Last_page_of_a_chat_reports_no_more_history()
    {
        var (t, me, _, room) = Room(30);
        using var _t = t;

        var page = await new ChatService(t.Verify()).GetMessagesAsync(room.Id, me.Id, 1, 50, default);

        Assert.Equal(30, page.Items.Count);
        Assert.Equal(30, page.TotalCount);
    }

    [Fact]
    public async Task Room_search_pages_without_counting_every_match()
    {
        var (t, me, _, room) = Room(0);
        using var _t = t;
        for (var i = 0; i < 25; i++) t.AddMessage(room.Id, me.Id, $"laporan {i}");
        var counter = new SqlLog();
        var options = new DbContextOptionsBuilder<IPopDbContext>().UseSqlite(t.Db.Database.GetDbConnection()).AddInterceptors(counter).Options;

        ChatPage page;
        await using (var db = new IPopDbContext(options))
            page = await new ChatService(db).SearchMessagesAsync(me.Id, room.Id, "laporan", 1, 20, default);

        Assert.Equal(20, page.Items.Count);
        Assert.True(page.TotalCount > 20);
        Assert.DoesNotContain(counter.Texts, q => q.Contains("COUNT(*)") && q.Contains("\"ChatMessages\""));
    }

    [Fact]
    public async Task Inbox_is_ordered_by_the_recipient_row_so_the_index_serves_the_page()
    {
        using var t = new TestDb();
        var me = t.AddUser("VQ_INBOX"); var sender = t.AddUser("VQ_SENDER");
        var svc = new MailService(t.Verify());
        for (var i = 0; i < 3; i++)
            Assert.True((await svc.SendAsync(sender.Id, new ComposeMailRequest($"S{i}", "<p>x</p>", [me.Nik], [], []), default)).Success);
        var counter = new SqlLog();
        var options = new DbContextOptionsBuilder<IPopDbContext>().UseSqlite(t.Db.Database.GetDbConnection()).AddInterceptors(counter).Options;

        MailPage inbox;
        await using (var db = new IPopDbContext(options))
            inbox = await new MailService(db).GetFolderAsync(me.Id, MailFolder.Inbox, 1, 20, null, default);

        Assert.Equal(["S2", "S1", "S0"], inbox.Items.Select(m => m.Subject));
        var pageQuery = Assert.Single(counter.Texts, q => q.Contains("LIMIT") && q.Contains("\"MailRecipients\"") && q.Contains("ORDER BY"));
        Assert.DoesNotContain("COALESCE", pageQuery[pageQuery.IndexOf("ORDER BY", StringComparison.Ordinal)..]);
    }

    [Fact]
    public async Task Scheduled_mail_lands_in_the_inbox_at_delivery_time_not_at_scheduling_time()
    {
        using var t = new TestDb();
        var me = t.AddUser("VQ_SCHED_TO"); var sender = t.AddUser("VQ_SCHED_FROM");
        var svc = new MailService(t.Verify());
        var scheduled = await svc.SendAsync(sender.Id, new ComposeMailRequest("Later", "<p>x</p>", [me.Nik], [], [],
            ScheduledAt: DateTime.UtcNow.AddHours(5)), default);
        Assert.True(scheduled.Success);
        Assert.True((await svc.SendAsync(sender.Id, new ComposeMailRequest("Now", "<p>x</p>", [me.Nik], [], []), default)).Success);

        Assert.True((await new MailService(t.Verify()).SendScheduledImmediatelyAsync(sender.Id, scheduled.MailId!.Value, default)).Success);

        var inbox = await new MailService(t.Verify()).GetFolderAsync(me.Id, MailFolder.Inbox, 1, 20, null, default);
        Assert.Equal(["Later", "Now"], inbox.Items.Select(m => m.Subject));
    }

    private sealed class SqlLog : Microsoft.EntityFrameworkCore.Diagnostics.DbCommandInterceptor
    {
        public List<string> Texts { get; } = [];
        public override ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbDataReader>> ReaderExecutingAsync(
            System.Data.Common.DbCommand command, Microsoft.EntityFrameworkCore.Diagnostics.CommandEventData eventData,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbDataReader> result, CancellationToken cancellationToken = default)
        { Texts.Add(command.CommandText); return base.ReaderExecutingAsync(command, eventData, result, cancellationToken); }
        public override ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<object>> ScalarExecutingAsync(
            System.Data.Common.DbCommand command, Microsoft.EntityFrameworkCore.Diagnostics.CommandEventData eventData,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<object> result, CancellationToken cancellationToken = default)
        { Texts.Add(command.CommandText); return base.ScalarExecutingAsync(command, eventData, result, cancellationToken); }
    }
}
