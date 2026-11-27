using IPop.Core.Entities;
using IPop.Infrastructure.Chat;
using Xunit;

namespace IPop.Tests;

/// Search honours "clear history"; the Seen-by list only names people whose read receipt was recorded.
public class ChatSearchSeenPrivacyTests
{
    [Fact]
    public void Search_skips_messages_hidden_by_clear_history()
    {
        using var t = new TestDb();
        var a = t.AddUser("SP1"); var b = t.AddUser("SP2");
        var room = t.AddRoom("dm");
        t.AddMember(room.Id, a.Id); t.AddMember(room.Id, b.Id);
        t.AddMessage(room.Id, b.Id, "needle old", at: DateTime.UtcNow.AddMinutes(-10));
        t.AddMessage(room.Id, b.Id, "needle new", at: DateTime.UtcNow.AddMinutes(-1));
        t.Db.ChatRoomUserSettings.Add(new ChatRoomUserSettings { UserId = a.Id, RoomId = room.Id, ClearedAt = DateTime.UtcNow.AddMinutes(-5) });
        t.Db.SaveChanges();

        // ILike is Npgsql-only, so the search's shared clear-history filter is tested directly.
        var db = t.Verify();
        var kept = new ChatService(db).ExcludeCleared(db.ChatMessages, a.Id).Select(m => m.Content).ToList();

        Assert.Equal(new[] { "needle new" }, kept);
    }

    [Fact]
    public async Task Seen_list_does_not_name_a_reader_whose_receipts_are_off()
    {
        using var t = new TestDb();
        var a = t.AddUser("SP3"); var b = t.AddUser("SP4"); var c = t.AddUser("SP5");
        var room = t.AddRoom("group", "G");
        t.AddMember(room.Id, a.Id, "owner"); t.AddMember(room.Id, b.Id); t.AddMember(room.Id, c.Id);
        var m = t.AddMessage(room.Id, a.Id, "hello", at: DateTime.UtcNow.AddMinutes(-2));
        var now = DateTime.UtcNow;
        t.Db.ChatRoomUserSettings.AddRange(
            new ChatRoomUserSettings { UserId = b.Id, RoomId = room.Id, LastReadMessageAt = now, ReadReceiptsEnabled = false },
            new ChatRoomUserSettings { UserId = c.Id, RoomId = room.Id, LastReadMessageAt = now });
        t.Db.ChatReadReceipts.AddRange(
            new ChatReadReceipt { MessageId = m.Id, UserId = b.Id, DeliveredAt = now, ReadAt = null },
            new ChatReadReceipt { MessageId = m.Id, UserId = c.Id, DeliveredAt = now, ReadAt = now });
        t.Db.SaveChanges();

        var seen = await new ChatService(t.Verify()).GetSeenDetailAsyncImpl(a.Id, m.Id, default);

        Assert.Equal(new[] { c.Id }, seen.Readers.Select(r => r.UserId));
        Assert.Contains(seen.Pending!, p => p.UserId == b.Id);
    }
}
