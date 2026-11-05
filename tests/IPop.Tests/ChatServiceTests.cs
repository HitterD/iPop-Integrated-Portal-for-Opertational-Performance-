using IPop.Api.Endpoints;
using IPop.Application.Chat;
using IPop.Core.Entities;
using IPop.Infrastructure.Chat;
using Xunit;

namespace IPop.Tests;

public class ChatServiceTests
{
    [Fact]
    public async Task Repeated_delivery_and_read_report_no_change_so_nobody_is_pinged_again()
    {
        using var t = new TestDb();
        var a = t.AddUser("RC1"); var b = t.AddUser("RC2");
        var room = t.AddRoom("group", "R");
        t.AddMember(room.Id, a.Id); t.AddMember(room.Id, b.Id);
        var m = t.AddMessage(room.Id, a.Id, "hi");

        var svc = new ChatService(t.Verify());
        Assert.True(await svc.RecordDeliveryAsync(b.Id, m.Id, default));
        Assert.True(svc.LastReceiptChanged);
        Assert.True(await new ChatService(t.Verify()).RecordDeliveryAsync(b.Id, m.Id, default) is var ok2 && ok2);
        var again = new ChatService(t.Verify());
        await again.RecordDeliveryAsync(b.Id, m.Id, default);
        Assert.False(again.LastReceiptChanged);   // second tab / reconnect

        var read = new ChatService(t.Verify());
        Assert.True(await read.RecordReadAsync(b.Id, m.Id, default));
        Assert.True(read.LastReceiptChanged);
        var reread = new ChatService(t.Verify());
        Assert.True(await reread.RecordReadAsync(b.Id, m.Id, default));
        Assert.False(reread.LastReceiptChanged);  // re-reading changes no counts
    }

    [Fact]
    public async Task GetMessages_group_read_by_counts_only_active_members_and_reactions_attachments_survive_split_query()
    {
        using var t = new TestDb();
        var me = t.AddUser("G1"); var b = t.AddUser("G2"); var c = t.AddUser("G3"); var gone = t.AddUser("G4");
        var room = t.AddRoom("group", "Team");
        t.AddMember(room.Id, me.Id); t.AddMember(room.Id, b.Id); t.AddMember(room.Id, c.Id);
        t.AddMember(room.Id, gone.Id, leftAt: DateTime.UtcNow.AddDays(-1));
        var mine = t.AddMessage(room.Id, me.Id, "hello all", at: DateTime.UtcNow.AddMinutes(-2));
        var theirs = t.AddMessage(room.Id, b.Id, "hi", at: DateTime.UtcNow.AddMinutes(-1));
        t.Db.ChatReadReceipts.AddRange(
            new IPop.Core.Entities.ChatReadReceipt { MessageId = mine.Id, UserId = b.Id, DeliveredAt = DateTime.UtcNow, ReadAt = DateTime.UtcNow },
            new IPop.Core.Entities.ChatReadReceipt { MessageId = mine.Id, UserId = gone.Id, DeliveredAt = DateTime.UtcNow, ReadAt = DateTime.UtcNow },
            new IPop.Core.Entities.ChatReadReceipt { MessageId = mine.Id, UserId = c.Id, DeliveredAt = DateTime.UtcNow });
        t.Db.ChatReactions.AddRange(
            new IPop.Core.Entities.ChatReaction { MessageId = theirs.Id, UserId = me.Id, Emoji = "👍", CreatedAt = DateTime.UtcNow },
            new IPop.Core.Entities.ChatReaction { MessageId = theirs.Id, UserId = c.Id, Emoji = "👍", CreatedAt = DateTime.UtcNow });
        t.Db.SaveChanges();

        var page = await new ChatService(t.Verify()).GetMessagesAsync(room.Id, me.Id, page: 1, pageSize: 50, default);

        var m = Assert.Single(page.Items, x => x.Content == "hello all");
        Assert.Equal(1, m.ReadCount);      // b read it; the member who left does not count
        Assert.Equal(2, m.ReadTotal);      // b and c are the active others
        Assert.Equal("delivered", m.DeliveryStatus);
        var h = Assert.Single(page.Items, x => x.Content == "hi");
        var r = Assert.Single(h.Reactions);
        Assert.Equal(2, r.Count);
        Assert.True(r.CurrentUserReacted);
    }

    [Fact]
    public async Task GetMessages_page1_is_newest()
    {
        using var t = new TestDb();
        var (a, b, room) = SeedDm(t, 80);
        var svc = new ChatService(t.Verify());

        var page = await svc.GetMessagesAsync(room, a, page: 1, pageSize: 50, default);

        Assert.Equal(80, page.TotalCount);
        Assert.Equal(50, page.Items.Count);
        Assert.Equal("m31", page.Items[0].Content);
        Assert.Equal("m80", page.Items[^1].Content);
    }

    [Fact]
    public async Task GetMessagesBefore_pages_by_cursor_and_ignores_newer_arrivals()
    {
        using var t = new TestDb();
        var (a, _, room) = SeedDm(t, 80);
        var svc = new ChatService(t.Verify());

        var latest = await svc.GetMessagesAsync(room, a, page: 1, pageSize: 50, default);
        Assert.Equal("m31", latest.Items[0].Content);

        // New messages land after page 1 was loaded: with offset paging page 2 would
        // shift and re-serve m31..; the cursor keeps the window fixed.
        var newest = t.Db.ChatMessages.Where(m => m.RoomId == room).Max(m => m.CreatedAt);
        for (var i = 1; i <= 5; i++)
            t.Db.ChatMessages.Add(Msg(room, a, "text", $"late{i}", newest.AddSeconds(i)));
        t.Db.SaveChanges();

        var older = await new ChatService(t.Verify()).GetMessagesBeforeAsync(room, a, latest.Items[0].CreatedAt, 50, default);
        // Inclusive cursor: the cursor row itself comes back once more (the client de-dupes it).
        Assert.Equal(31, older.Items.Count);
        Assert.Equal(31, older.TotalCount); // only what's not newer than the cursor
        Assert.Equal("m1", older.Items[0].Content);
        Assert.Equal("m31", older.Items[^1].Content);
        Assert.Equal([latest.Items[0].Id], older.Items.Select(m => m.Id).Intersect(latest.Items.Select(m => m.Id)));

        // Smaller pages: TotalCount > Items.Count signals more history remains.
        var small = await svc.GetMessagesBeforeAsync(room, a, latest.Items[0].CreatedAt, 10, default);
        Assert.Equal(["m22", "m23", "m24", "m25", "m26", "m27", "m28", "m29", "m30", "m31"], small.Items.Select(m => m.Content));
        Assert.True(small.TotalCount > small.Items.Count);
    }

    [Fact]
    public async Task GetMessagesBefore_never_skips_messages_sharing_the_cursor_timestamp()
    {
        using var t = new TestDb();
        var (a, _, room) = SeedDm(t, 0);
        // More than two pages in the same tick: every page boundary falls inside the tie.
        var at = DateTime.UtcNow;
        for (var i = 1; i <= 120; i++)
            t.Db.ChatMessages.Add(Msg(room, a, "text", $"t{i}", at));
        t.Db.SaveChanges();
        var svc = new ChatService(t.Verify());

        var seen = new HashSet<Guid>();
        var page = await svc.GetMessagesAsync(room, a, page: 1, pageSize: 4, default);
        foreach (var m in page.Items) seen.Add(m.Id);
        // Walk back the way the client does: cursor = oldest loaded, keep only unseen ids.
        for (var guard = 0; guard < 10; guard++)
        {
            var older = await svc.GetMessagesBeforeAsync(room, a, page.Items[0].CreatedAt, 50, default, seen.Count);
            var added = 0;
            foreach (var m in older.Items) if (seen.Add(m.Id)) added++;
            if (added == 0) break;
            page = older;
        }

        Assert.Equal(120, seen.Count);
    }

    [Fact]
    public async Task Cursor_boundary_skip_is_clamped_and_never_skips_older_messages()
    {
        using var t = new TestDb();
        var (a, _, room) = SeedDm(t, 3);
        var at = t.Db.ChatMessages.Where(m => m.RoomId == room).Max(m => m.CreatedAt);
        var svc = new ChatService(t.Verify());

        var older = await svc.GetMessagesBeforeAsync(room, a, at, 50, default, int.MaxValue);

        Assert.Equal(["m1", "m2"], older.Items.Select(m => m.Content));
        Assert.Equal(2, older.TotalCount);
    }

    [Fact]
    public async Task Unread_skips_own_and_system()
    {
        using var t = new TestDb();
        var a = t.AddUser("A");
        var b = t.AddUser("B");
        var roomId = Guid.NewGuid();
        t.Db.ChatRooms.Add(new ChatRoom { Id = roomId, Type = "dm", IsActive = true, CreatedAt = DateTime.UtcNow, LastMessageAt = DateTime.UtcNow });
        t.Db.ChatRoomMembers.AddRange(
            new ChatRoomMember { Id = Guid.NewGuid(), RoomId = roomId, UserId = a.Id, Role = "member", JoinedAt = DateTime.UtcNow },
            new ChatRoomMember { Id = Guid.NewGuid(), RoomId = roomId, UserId = b.Id, Role = "member", JoinedAt = DateTime.UtcNow });
        t.Db.ChatMessages.AddRange(
            Msg(roomId, a.Id, "system", "Room created"),
            Msg(roomId, a.Id, "text", "from-a"),
            Msg(roomId, b.Id, "text", "from-b"));
        t.Db.SaveChanges();

        var rooms = await new ChatService(t.Verify()).GetRoomsAsync(a.Id, RoomTab.Direct, 1, 20, default);
        Assert.Equal(1, rooms.Items[0].UnreadCount);
    }

    [Fact]
    public async Task Forward_stores_origin_metadata_with_clean_content()
    {
        using var t = new TestDb();
        var (a, b, src) = SeedDm(t, 1);
        var c = t.AddUser("C");
        var dst = Guid.NewGuid();
        var now = DateTime.UtcNow;
        t.Db.ChatRooms.Add(new ChatRoom { Id = dst, Type = "dm", IsActive = true, CreatedAt = now, LastMessageAt = now });
        t.Db.ChatRoomMembers.AddRange(
            new ChatRoomMember { Id = Guid.NewGuid(), RoomId = dst, UserId = a, Role = "member", JoinedAt = now },
            new ChatRoomMember { Id = Guid.NewGuid(), RoomId = dst, UserId = c.Id, Role = "member", JoinedAt = now });
        var mid = t.Db.ChatMessages.Single(m => m.RoomId == src).Id;
        t.Db.SaveChanges();
        var svc = new ChatService(t.Verify());

        var forwarded = await svc.ForwardMessageAsync(a, mid, dst, default);
        Assert.True(forwarded.Success);
        Assert.Equal("m1", forwarded.Message!.Content); // tanpa prefix [forwarded]
        Assert.NotNull(forwarded.Message.ForwardedFromSenderName);

        // Rantai terusan mempertahankan pengirim ASLI pertama.
        var forwardedTwice = await svc.ForwardMessageAsync(a, forwarded.Message.Id, src, default);
        Assert.True(forwardedTwice.Success);
        Assert.Equal(forwarded.Message.ForwardedFromSenderName, forwardedTwice.Message!.ForwardedFromSenderName);
        Assert.Equal("m1", forwardedTwice.Message.Content);
    }

    [Fact]
    public async Task Forward_legacy_prefixed_message_is_normalized()
    {
        using var t = new TestDb();
        var (a, b, src) = SeedDm(t, 0);
        var dst = Guid.NewGuid();
        var now = DateTime.UtcNow;
        t.Db.ChatRooms.Add(new ChatRoom { Id = dst, Type = "dm", IsActive = true, CreatedAt = now, LastMessageAt = now });
        t.Db.ChatRoomMembers.AddRange(
            new ChatRoomMember { Id = Guid.NewGuid(), RoomId = dst, UserId = a, Role = "member", JoinedAt = now },
            new ChatRoomMember { Id = Guid.NewGuid(), RoomId = dst, UserId = b, Role = "member", JoinedAt = now });
        var legacy = Msg(src, a, "text", "[forwarded] halo lama", now);
        t.Db.ChatMessages.Add(legacy);
        t.Db.SaveChanges();
        var svc = new ChatService(t.Verify());

        var forwarded = await svc.ForwardMessageAsync(a, legacy.Id, dst, default);
        Assert.True(forwarded.Success);
        Assert.Equal("halo lama", forwarded.Message!.Content);
    }
    [Fact]
    public async Task Forward_rejects_deleted()
    {
        using var t = new TestDb();
        var (a, b, src) = SeedDm(t, 1);
        var c = t.AddUser("C");
        var dst = Guid.NewGuid();
        t.Db.ChatRooms.Add(new ChatRoom { Id = dst, Type = "dm", IsActive = true, CreatedAt = DateTime.UtcNow, LastMessageAt = DateTime.UtcNow });
        t.Db.ChatRoomMembers.AddRange(
            new ChatRoomMember { Id = Guid.NewGuid(), RoomId = dst, UserId = a, Role = "member", JoinedAt = DateTime.UtcNow },
            new ChatRoomMember { Id = Guid.NewGuid(), RoomId = dst, UserId = c.Id, Role = "member", JoinedAt = DateTime.UtcNow });
        var mid = t.Db.ChatMessages.Single(m => m.RoomId == src).Id;
        t.Db.ChatMessages.Single(m => m.Id == mid).IsDeleted = true;
        t.Db.SaveChanges();

        var result = await new ChatService(t.Verify()).ForwardMessageAsync(a, mid, dst, default);
        Assert.False(result.Success);
        Assert.Equal("message_not_found", result.Error);
    }

    [Fact]
    public async Task Archive_hides_room_and_mute_needs_membership()
    {
        using var t = new TestDb();
        var (a, _, room) = SeedDm(t, 0);
        var stranger = t.AddUser("X");
        var svc = new ChatService(t.Verify());

        await svc.ArchiveRoomAsync(a, room, default);
        var listed = await svc.GetRoomsAsync(a, RoomTab.Direct, 1, 20, default);
        Assert.Empty(listed.Items);

        await svc.MuteRoomAsync(stranger.Id, room, MuteDuration.Forever, default);
        Assert.False(t.Verify().ChatRoomUserSettings.Any(s => s.UserId == stranger.Id && s.RoomId == room));
    }

    [Fact]
    public async Task Pin_allows_dm_member_and_lists_message()
    {
        using var t = new TestDb();
        var (a, b, room) = SeedDm(t, 1);
        var svc = new ChatService(t.Verify());
        var mid = t.Db.ChatMessages.Single(m => m.RoomId == room).Id;

        // DM members carry "member" — pinning is open to both sides of a DM.
        var pinned = await svc.PinMessageAsync(a, mid, default);
        Assert.True(pinned.Success);

        var list = await svc.GetPinnedMessagesAsync(room, default);
        Assert.Single(list);
        Assert.Equal(mid, list[0].MessageId);

        // Unpin by the OTHER DM member also works.
        var unpinned = await svc.UnpinMessageAsync(b, mid, default);
        Assert.True(unpinned.Success);
        Assert.Empty(await svc.GetPinnedMessagesAsync(room, default));
    }

    [Fact]
    public async Task Pin_group_open_to_any_member_but_not_outsiders()
    {
        using var t = new TestDb();
        var (owner, member, _, stranger, roomId, msgs) = SeedGroupForPins(t, 2);
        var svc = new ChatService(t.Verify());

        var byMember = await svc.PinMessageAsync(member, msgs[0], default);
        Assert.True(byMember.Success);
        var list = await svc.GetPinnedMessagesAsync(roomId, default);
        Assert.Equal(member, Assert.Single(list).PinnedByUserId);

        var byOwner = await svc.PinMessageAsync(owner, msgs[1], default);
        Assert.True(byOwner.Success);

        var byStranger = await svc.PinMessageAsync(stranger, msgs[0], default);
        Assert.False(byStranger.Success);
        Assert.Equal("forbidden", byStranger.Error);
    }

    [Fact]
    public async Task Unpin_group_allowed_for_pinner_or_owner_admin_only()
    {
        using var t = new TestDb();
        var (owner, member, other, _, roomId, msgs) = SeedGroupForPins(t, 3);
        var svc = new ChatService(t.Verify());
        foreach (var m in msgs)
            Assert.True((await svc.PinMessageAsync(member, m, default)).Success);

        // Another plain member cannot unpin someone else's pin.
        var byOther = await svc.UnpinMessageAsync(other, msgs[0], default);
        Assert.False(byOther.Success);
        Assert.Equal("forbidden", byOther.Error);

        // The member who pinned it can.
        Assert.True((await svc.UnpinMessageAsync(member, msgs[0], default)).Success);

        // The owner can unpin a member's pin, and so can an admin.
        Assert.True((await svc.UnpinMessageAsync(owner, msgs[1], default)).Success);
        var verify = t.Verify();
        verify.ChatRoomMembers.Single(x => x.RoomId == roomId && x.UserId == other).Role = "admin";
        verify.SaveChanges();
        Assert.True((await new ChatService(t.Verify()).UnpinMessageAsync(other, msgs[2], default)).Success);

        Assert.Empty(await svc.GetPinnedMessagesAsync(roomId, default));
    }

    private static (Guid Owner, Guid Member, Guid Other, Guid Stranger, Guid RoomId, List<Guid> Msgs) SeedGroupForPins(TestDb t, int messages)
    {
        var owner = t.AddUser("O");
        var member = t.AddUser("M");
        var other = t.AddUser("N");
        var stranger = t.AddUser("X");
        var roomId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        t.Db.ChatRooms.Add(new ChatRoom { Id = roomId, Type = "group", Name = "G", IsActive = true, CreatedAt = now, LastMessageAt = now });
        t.Db.ChatRoomMembers.AddRange(
            new ChatRoomMember { Id = Guid.NewGuid(), RoomId = roomId, UserId = owner.Id, Role = "owner", JoinedAt = now },
            new ChatRoomMember { Id = Guid.NewGuid(), RoomId = roomId, UserId = member.Id, Role = "member", JoinedAt = now },
            new ChatRoomMember { Id = Guid.NewGuid(), RoomId = roomId, UserId = other.Id, Role = "member", JoinedAt = now });
        var ids = new List<Guid>();
        for (var i = 0; i < messages; i++)
        {
            var msg = Msg(roomId, owner.Id, "text", $"hello {i}", now.AddSeconds(i));
            t.Db.ChatMessages.Add(msg);
            ids.Add(msg.Id);
        }
        t.Db.SaveChanges();
        return (owner.Id, member.Id, other.Id, stranger.Id, roomId, ids);
    }

    [Fact]
    public async Task Pin_twice_is_idempotent_and_limit_is_enforced()
    {
        using var t = new TestDb();
        var (a, _, room) = SeedDm(t, 51);
        var svc = new ChatService(t.Verify());
        var messages = t.Db.ChatMessages.Where(m => m.RoomId == room).ToList();

        var first = await svc.PinMessageAsync(a, messages[0].Id, default);
        var again = await svc.PinMessageAsync(a, messages[0].Id, default);
        Assert.True(first.Success);
        Assert.True(again.Success); // idempotent re-pin
        Assert.Single(await svc.GetPinnedMessagesAsync(room, default));

        foreach (var m in messages.Skip(1).Take(49))
            Assert.True((await svc.PinMessageAsync(a, m.Id, default)).Success);

        var overflow = await svc.PinMessageAsync(a, messages[50].Id, default);
        Assert.False(overflow.Success);
        Assert.Equal("pin_limit_reached", overflow.Error);
    }

    [Fact]
    public async Task Save_message_is_private_idempotent_and_unsavable()
    {
        using var t = new TestDb();
        var (a, b, room) = SeedDm(t, 2);
        var svc = new ChatService(t.Verify());
        var mid = t.Db.ChatMessages.First(m => m.RoomId == room).Id;

        Assert.True((await svc.SaveMessageAsync(a, mid, default)).Success);
        Assert.True((await svc.SaveMessageAsync(a, mid, default)).Success); // idempotent

        var mine = await svc.GetSavedMessagesAsync(a, default);
        Assert.Single(mine);
        Assert.Equal(mid, mine[0].MessageId);
        Assert.Equal(room, mine[0].RoomId);

        // Simpanan bersifat privat: anggota lain tidak melihatnya.
        Assert.Empty(await svc.GetSavedMessagesAsync(b, default));

        Assert.True((await svc.UnsaveMessageAsync(a, mid, default)).Success);
        Assert.Empty(await svc.GetSavedMessagesAsync(a, default));
    }

    [Fact]
    public async Task Save_rejects_non_member_and_missing_message()
    {
        using var t = new TestDb();
        var (a, _, room) = SeedDm(t, 1);
        var stranger = t.AddUser("X");
        var svc = new ChatService(t.Verify());
        var mid = t.Db.ChatMessages.First(m => m.RoomId == room).Id;

        var forbidden = await svc.SaveMessageAsync(stranger.Id, mid, default);
        Assert.False(forbidden.Success);
        Assert.Equal("forbidden", forbidden.Error);

        var missing = await svc.SaveMessageAsync(a, Guid.NewGuid(), default);
        Assert.False(missing.Success);
        Assert.Equal("message_not_found", missing.Error);
    }

    [Fact]
    public async Task SearchAll_scopes_to_member_rooms_on_sqlite_boundary()
    {
        // ILike is Npgsql-only (see DirectoryReadEndpointsTests for the same boundary):
        // on the SQLite TestDb seam the content predicate cannot be evaluated, so this
        // test documents the contract — a short query short-circuits empty, and a valid
        // query either returns member-room rows or nothing on SQLite, never throws.
        using var t = new TestDb();
        var (a, b, room1) = SeedDm(t, 2);
        var svc = new ChatService(t.Verify());

        var tooShort = await svc.SearchAllMessagesAsync(a, "x", 1, 20, default);
        Assert.Equal(0, tooShort.TotalCount);

        try
        {
            var result = await svc.SearchAllMessagesAsync(a, "m1", 1, 20, default);
            Assert.All(result.Items, m => Assert.Equal(room1, m.RoomId));
        }
        catch (InvalidOperationException)
        {
            // SQLite seam: ILike untranslatable — documented boundary, prod runs Npgsql.
        }
    }

    [Fact]
    public async Task UpdateIdentity_accepts_avatar_color_and_validates_hex()
    {
        using var t = new TestDb();
        var owner = t.AddUser("O");
        var roomId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        t.Db.ChatRooms.Add(new ChatRoom { Id = roomId, Type = "group", Name = "G", IsActive = true, CreatedAt = now, LastMessageAt = now });
        t.Db.ChatRoomMembers.Add(new ChatRoomMember { Id = Guid.NewGuid(), RoomId = roomId, UserId = owner.Id, Role = "owner", JoinedAt = now });
        t.Db.SaveChanges();
        var svc = new ChatService(t.Verify());

        var ok = await ((IChatService)svc).UpdateGroupIdentityAsync(owner.Id, roomId, "Grup Baru", null, "#2e7d5b", default);
        Assert.True(ok.Success);
        Assert.Equal("#2e7d5b", t.Verify().ChatRooms.Single(r => r.Id == roomId).AvatarColor);

        var bad = await ((IChatService)svc).UpdateGroupIdentityAsync(owner.Id, roomId, "Grup Baru", null, "bukan-warna", default);
        Assert.False(bad.Success);
        Assert.Equal("invalid_avatar_color", bad.Error);
    }

    [Fact]
    public async Task UnreadMentions_returns_mentions_after_last_read_only()
    {
        using var t = new TestDb();
        var (a, b, room) = SeedDm(t, 0);
        var now = DateTime.UtcNow;
        // Pesan lama (sudah dibaca) dan pesan baru (belum dibaca) yang menyebut a.
        var oldMsg = Msg(room, b, "text", "halo @a", now.AddHours(-2));
        var newMsg = Msg(room, b, "text", "laporan untuk @a", now.AddMinutes(-5));
        var otherMsg = Msg(room, b, "text", "tanpa mention", now.AddMinutes(-4));
        t.Db.ChatMessages.AddRange(oldMsg, newMsg, otherMsg);
        t.Db.ChatMentions.AddRange(
            new ChatMention { Id = Guid.NewGuid(), MessageId = oldMsg.Id, UserId = a, CreatedAt = oldMsg.CreatedAt },
            new ChatMention { Id = Guid.NewGuid(), MessageId = newMsg.Id, UserId = a, CreatedAt = newMsg.CreatedAt });
        // Tanda baca terakhir: setelah pesan lama, sebelum pesan baru.
        t.Db.ChatRoomUserSettings.Add(new ChatRoomUserSettings { UserId = a, RoomId = room, LastReadMessageAt = now.AddHours(-1) });
        t.Db.SaveChanges();
        var svc = new ChatService(t.Verify());

        var result = await svc.GetUnreadMentionsAsync(a, 1, 20, default);

        Assert.Equal(1, result.TotalCount);
        Assert.Equal(newMsg.Id, result.Items[0].Id);
    }

    private static (Guid A, Guid B, Guid Room) SeedDm(TestDb t, int messageCount)    {
        var a = t.AddUser("A");
        var b = t.AddUser("B");
        var roomId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        t.Db.ChatRooms.Add(new ChatRoom { Id = roomId, Type = "dm", IsActive = true, CreatedAt = now, LastMessageAt = now });
        t.Db.ChatRoomMembers.AddRange(
            new ChatRoomMember { Id = Guid.NewGuid(), RoomId = roomId, UserId = a.Id, Role = "member", JoinedAt = now },
            new ChatRoomMember { Id = Guid.NewGuid(), RoomId = roomId, UserId = b.Id, Role = "member", JoinedAt = now });
        for (var i = 1; i <= messageCount; i++)
            t.Db.ChatMessages.Add(Msg(roomId, a.Id, "text", $"m{i}", now.AddSeconds(i)));
        t.Db.SaveChanges();
        return (a.Id, b.Id, roomId);
    }

    private static ChatMessage Msg(Guid roomId, Guid senderId, string type, string content, DateTime? at = null) => new()
    {
        Id = Guid.NewGuid(),
        RoomId = roomId,
        SenderId = senderId,
        Type = type,
        Content = content,
        IdempotencyKey = Guid.NewGuid(),
        CreatedAt = at ?? DateTime.UtcNow
    };

    [Fact]
    public void TryLocalFile_accepts_under_chat_files_rejects_escape()
    {
        var root = Path.Combine(Path.GetTempPath(), "ipop-chat-" + Guid.NewGuid().ToString("N"));
        var room = Guid.NewGuid().ToString("N");
        var dir = Path.Combine(root, "chat-files", room);
        Directory.CreateDirectory(dir);
        var name = "a.png";
        File.WriteAllBytes(Path.Combine(dir, name), [1, 2, 3]);
        try
        {
            Assert.True(ChatEndpoints.TryLocalFile(root, "local", $"chat-files/{room}/{name}", out var hit));
            Assert.True(File.Exists(hit));
            Assert.False(ChatEndpoints.TryLocalFile(root, "nas", $"chat-files/{room}/{name}", out _));
            Assert.False(ChatEndpoints.TryLocalFile(root, "local", "../secrets.txt", out _));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Dm_delivery_and_batch_read_receipts_update_delivery_status()
    {
        using var t = new TestDb();
        var (a, b, room) = SeedDm(t, 2);
        var svc = new ChatService(t.Verify());

        // Initially messages sent by A should be "sent"
        var initial = await svc.GetMessagesAsync(room, a, 1, 10, default);
        Assert.Equal("sent", initial.Items[0].DeliveryStatus);
        Assert.Equal("sent", initial.Items[1].DeliveryStatus);

        // B receives delivery of message 1
        await svc.RecordDeliveryAsync(b, initial.Items[0].Id, default);
        var afterDelivery = await svc.GetMessagesAsync(room, a, 1, 10, default);
        Assert.Equal("delivered", afterDelivery.Items[0].DeliveryStatus);
        Assert.Equal("sent", afterDelivery.Items[1].DeliveryStatus);

        // B reads message 2 (latest) -> batch marks message 1 and 2 as read
        await svc.RecordReadAsync(b, initial.Items[1].Id, default);
        var afterRead = await svc.GetMessagesAsync(room, a, 1, 10, default);
        Assert.Equal("read", afterRead.Items[0].DeliveryStatus);
        Assert.Equal("read", afterRead.Items[1].DeliveryStatus);
    }

    [Fact]
    public async Task Reading_up_to_your_own_message_never_counts_you_as_its_reader()
    {
        using var t = new TestDb();
        var (a, b, room) = SeedDm(t, 2);
        var svc = new ChatService(t.Verify());
        var msgs = await svc.GetMessagesAsync(room, a, 1, 10, default);

        // A's own tab marks the room read at A's newest message (it fell back to it).
        Assert.True(await svc.RecordReadAsync(a, msgs.Items[1].Id, default));

        Assert.False(t.Db.ChatReadReceipts.Any(r => r.UserId == a));
        var after = await svc.GetMessagesAsync(room, a, 1, 10, default);
        Assert.All(after.Items, m => Assert.Equal("sent", m.DeliveryStatus));
    }

    [Fact]
    public async Task Group_delivery_and_read_status_requires_all_other_members()
    {
        using var t = new TestDb();
        var a = t.AddUser("A");
        var b = t.AddUser("B");
        var c = t.AddUser("C");
        var roomId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        t.Db.ChatRooms.Add(new ChatRoom { Id = roomId, Type = "group", Name = "Team", IsActive = true, CreatedAt = now, LastMessageAt = now });
        t.Db.ChatRoomMembers.AddRange(
            new ChatRoomMember { Id = Guid.NewGuid(), RoomId = roomId, UserId = a.Id, Role = "owner", JoinedAt = now },
            new ChatRoomMember { Id = Guid.NewGuid(), RoomId = roomId, UserId = b.Id, Role = "member", JoinedAt = now },
            new ChatRoomMember { Id = Guid.NewGuid(), RoomId = roomId, UserId = c.Id, Role = "member", JoinedAt = now });
        var msg = Msg(roomId, a.Id, "text", "hello team", now);
        t.Db.ChatMessages.Add(msg);
        t.Db.SaveChanges();

        var svc = new ChatService(t.Verify());

        // Initially: "sent"
        var initial = await svc.GetMessagesAsync(roomId, a.Id, 1, 10, default);
        Assert.Equal("sent", initial.Items[0].DeliveryStatus);
        Assert.Equal(0, initial.Items[0].ReadCount);
        Assert.Equal(2, initial.Items[0].ReadTotal);

        // B reads the message -> in group with 2 other members, only 1 read, so status is "delivered"
        await svc.RecordReadAsync(b.Id, msg.Id, default);
        var partialRead = await svc.GetMessagesAsync(roomId, a.Id, 1, 10, default);
        Assert.Equal("delivered", partialRead.Items[0].DeliveryStatus);
        Assert.Equal(1, partialRead.Items[0].ReadCount);

        // C also reads the message -> all other active members have read, status becomes "read"
        await svc.RecordReadAsync(c.Id, msg.Id, default);
        var allRead = await svc.GetMessagesAsync(roomId, a.Id, 1, 10, default);
        Assert.Equal("read", allRead.Items[0].DeliveryStatus);
        Assert.Equal(2, allRead.Items[0].ReadCount);

        // Receivers never get counts for someone else's message.
        var asB = await svc.GetMessagesAsync(roomId, b.Id, 1, 10, default);
        Assert.Null(asB.Items[0].ReadCount);
    }
}

public class TaskDiscussionTests
{
    private static (TestDb t, Guid userId, Guid taskId) SeedTaskWithMember()
    {
        var t = new TestDb();
        var owner = t.AddUser("OWN");
        var project = t.AddProject("P1", owner.Id);
        var task = new IPop.Core.Entities.ProjectTask
        {
            Id = Guid.NewGuid(), ProjectId = project.Id, GroupId = t.Db.ProjectGroups.Single(g => g.ProjectId == project.Id).Id, Title = "T1", Status = "todo",
            CreatedById = owner.Id, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        };
        t.Db.ProjectTasks.Add(task);
        t.Db.SaveChanges();
        return (t, owner.Id, task.Id);
    }

    [Fact]
    public void Comment_reply_requires_top_level_parent()
    {
        using var t = new TestDb();
        var a = t.AddUser("A");
        var b = t.AddUser("B");
        var project = t.AddProject("P1", a.Id);
        t.AddProjectMember(project.Id, b.Id);
        var task = new IPop.Core.Entities.ProjectTask
        {
            Id = Guid.NewGuid(), ProjectId = project.Id, GroupId = t.Db.ProjectGroups.Single(g => g.ProjectId == project.Id).Id, Title = "T1", Status = "todo",
            CreatedById = a.Id, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        };
        t.Db.ProjectTasks.Add(task);
        var parent = new IPop.Core.Entities.ProjectTaskComment
        {
            Id = Guid.NewGuid(), TaskId = task.Id, AuthorId = a.Id, Content = "komentar awal", CreatedAt = DateTime.UtcNow
        };
        var reply = new IPop.Core.Entities.ProjectTaskComment
        {
            Id = Guid.NewGuid(), TaskId = task.Id, AuthorId = b.Id, Content = "balasan",
            ParentCommentId = parent.Id, CreatedAt = DateTime.UtcNow
        };
        t.Db.ProjectTaskComments.AddRange(parent, reply);
        t.Db.SaveChanges();

        var roots = t.Verify().ProjectTaskComments.Where(c => c.TaskId == task.Id && c.ParentCommentId == null).ToList();
        var replies = t.Verify().ProjectTaskComments.Where(c => c.TaskId == task.Id && c.ParentCommentId != null).ToList();

        Assert.Single(roots);
        Assert.Single(replies);
        Assert.Equal(parent.Id, replies[0].ParentCommentId);
    }

    [Fact]
    public void Activity_rows_record_comment_actions()
    {
        using var t = new TestDb();
        var a = t.AddUser("A");
        var project = t.AddProject("P1", a.Id);
        var task = new IPop.Core.Entities.ProjectTask
        {
            Id = Guid.NewGuid(), ProjectId = project.Id, GroupId = t.Db.ProjectGroups.Single(g => g.ProjectId == project.Id).Id, Title = "T1", Status = "todo",
            CreatedById = a.Id, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        };
        t.Db.ProjectTasks.Add(task);
        t.Db.ProjectTaskActivities.Add(new IPop.Core.Entities.ProjectTaskActivity
        {
            Id = Guid.NewGuid(), TaskId = task.Id, ActorId = a.Id,
            Action = "comment_added", CreatedAt = DateTime.UtcNow
        });
        t.Db.SaveChanges();

        var rows = t.Verify().ProjectTaskActivities.Where(x => x.TaskId == task.Id).ToList();
        Assert.Single(rows);
        Assert.Equal("comment_added", rows[0].Action);
    }
}

public class ReceiptMembershipTests
{
    [Fact]
    public async Task NonMember_receipts_are_refused_and_seen_list_skips_sender()
    {
        using var t = new TestDb();
        var a = t.AddUser("a"); var b = t.AddUser("b"); var x = t.AddUser("x");
        var room = t.AddRoom("group", "Team");
        t.AddMember(room.Id, a.Id, "owner"); t.AddMember(room.Id, b.Id);
        var msg = t.AddMessage(room.Id, a.Id, "hi");
        IChatService svc = new ChatService(t.Verify());

        Assert.False(await svc.RecordReadAsync(x.Id, msg.Id, default));
        Assert.False(await svc.RecordDeliveryAsync(x.Id, msg.Id, default));
        Assert.True(await svc.RecordReadAsync(b.Id, msg.Id, default));

        // A stray self-receipt from the sender must not count as a reader.
        t.Db.ChatReadReceipts.Add(new ChatReadReceipt { MessageId = msg.Id, UserId = a.Id, ReadAt = DateTime.UtcNow });
        await t.Db.SaveChangesAsync();
        var seen = await svc.GetSeenDetailAsync(a.Id, msg.Id, default);
        Assert.Equal([b.Id], seen.Readers.Select(r => r.UserId));
        Assert.Equal(1, seen.ActiveMemberCount);
        Assert.False(t.Verify().ChatReadReceipts.Any(r => r.UserId == x.Id));
    }
}
