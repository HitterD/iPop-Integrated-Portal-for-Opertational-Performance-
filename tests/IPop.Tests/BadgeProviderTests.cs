using IPop.Core.Entities;
using IPop.Infrastructure.Badges;
using Xunit;

namespace IPop.Tests;

public class BadgeProviderTests
{
    [Fact]
    public async Task Chat_CountsUnreadFromOthers_SkipsArchivedMutedAndRead()
    {
        using var t = new TestDb();
        var me = t.AddUser("100"); var other = t.AddUser("200");
        var now = DateTime.UtcNow;

        var fresh = t.AddRoom();
        t.AddMember(fresh.Id, me.Id); t.AddMember(fresh.Id, other.Id);
        t.AddMessage(fresh.Id, other.Id, at: now.AddMinutes(-3));
        t.AddMessage(fresh.Id, other.Id, at: now.AddMinutes(-2));
        t.AddMessage(fresh.Id, me.Id, at: now.AddMinutes(-1));          // own message: not unread

        var partlyRead = t.AddRoom();
        t.AddMember(partlyRead.Id, me.Id); t.AddMember(partlyRead.Id, other.Id);
        t.AddMessage(partlyRead.Id, other.Id, at: now.AddMinutes(-10));
        t.AddMessage(partlyRead.Id, other.Id, at: now.AddMinutes(-1));
        t.AddSettings(me.Id, partlyRead.Id, s => s.LastReadMessageAt = now.AddMinutes(-5));

        var archived = t.AddRoom();
        t.AddMember(archived.Id, me.Id); t.AddMember(archived.Id, other.Id);
        t.AddMessage(archived.Id, other.Id, at: now);
        t.AddSettings(me.Id, archived.Id, s => s.IsArchived = true);

        var muted = t.AddRoom();
        t.AddMember(muted.Id, me.Id); t.AddMember(muted.Id, other.Id);
        t.AddMessage(muted.Id, other.Id, at: now);
        t.AddSettings(me.Id, muted.Id, s => s.IsMuted = true);

        var muteExpired = t.AddRoom();
        t.AddMember(muteExpired.Id, me.Id); t.AddMember(muteExpired.Id, other.Id);
        t.AddMessage(muteExpired.Id, other.Id, at: now);
        t.AddSettings(me.Id, muteExpired.Id, s => { s.IsMuted = true; s.MuteExpiresAt = now.AddMinutes(-1); });

        Assert.Equal(4, await new ChatBadgeProvider(t.Db).CountAsync(me.Id, default));
    }

    [Fact]
    public async Task Mail_CountsUnreadInboxOnly()
    {
        using var t = new TestDb();
        var me = t.AddUser("100"); var sender = t.AddUser("200");
        void Add(string folder, bool read)
        {
            var m = new Mail { Id = Guid.NewGuid(), SenderId = sender.Id, Subject = "s", Body = "b", Status = "sent", CreatedAt = DateTime.UtcNow };
            t.Db.Add(m);
            t.Db.Add(new MailRecipient { Id = Guid.NewGuid(), MailId = m.Id, UserId = me.Id, RecipientType = "to", Folder = folder, IsRead = read, CreatedAt = DateTime.UtcNow });
        }
        Add("inbox", false); Add("inbox", false); Add("inbox", true); Add("archive", false); Add("trash", false);
        await t.Db.SaveChangesAsync();

        Assert.Equal(2, await new MailBadgeProvider(t.Db).CountAsync(me.Id, default));
    }
}
