using Bunit;
using IPop.Application.Chat;
using IPop.Web.Services;
using Xunit;

namespace IPop.Tests;

public class ChatBannerTests
{
    private static readonly Guid RoomId = Guid.NewGuid();

    private static MessageDto Msg(string content = "Halo tim", string type = "text", Guid? room = null) => new(
        Guid.NewGuid(), room ?? RoomId, Guid.NewGuid(), "Budi", "U2", type, content,
        DateTime.UtcNow, null, false, null, null, null, null, 0, null, [], [], [], false, Guid.NewGuid());

    private static RoomListDto Group(string name = "Tim Ops") => new(RoomId, "group", name, null, null, null, null,
        0, false, false, false, null, false, false, null);

    private static RoomListDto Dm() => new(RoomId, "dm", null, "U2", "Budi", null, null,
        0, false, false, false, null, false, false, null);

    private static IncomingChatMessage In(MessageDto? m = null, RoomListDto? room = null,
        bool active = false, bool mention = false, bool muted = false)
        => new(m ?? Msg(), room ?? Group(), active, mention, muted);

    private static NotificationState State(BunitContext ctx)
    {
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        var http = new HttpClient { BaseAddress = new Uri("http://localhost/") };
        var auth = new AuthState(http, ctx.JSInterop.JSRuntime);
        return new NotificationState(auth, http, new ToastService(), ctx.JSInterop.JSRuntime);
    }

    [Fact]
    public void Room_open_on_chat_page_is_skipped_but_shows_elsewhere()
    {
        var m = In(active: true);
        Assert.False(NotificationState.ShouldBannerChat(m, $"/chat/{RoomId}"));
        Assert.False(NotificationState.ShouldBannerChat(m, "/chat"));
        // ActiveRoomId lingers after leaving /chat — the user can't see that room any more.
        Assert.True(NotificationState.ShouldBannerChat(m, "/mail"));
        Assert.True(NotificationState.ShouldBannerChat(In(active: false), "/chat"));
    }

    [Fact]
    public void Muted_rooms_mentions_and_system_messages_are_skipped()
    {
        Assert.False(NotificationState.ShouldBannerChat(In(muted: true), "/home"));
        // Mentions/emergencies arrive as server notifications already.
        Assert.False(NotificationState.ShouldBannerChat(In(mention: true), "/home"));
        Assert.False(NotificationState.ShouldBannerChat(In(Msg(type: "emergency")), "/home"));
        Assert.False(NotificationState.ShouldBannerChat(In(Msg(type: "system")), "/home"));
    }

    [Fact]
    public void Group_card_names_the_room_and_sender_dm_card_names_the_person()
    {
        var g = NotificationState.BuildChatItem(In(), 1);
        Assert.Equal("Tim Ops", g.Title);
        Assert.Equal("Budi: Halo tim", g.Body);
        Assert.Equal($"/chat/{RoomId:D}", g.TargetUrl);

        var d = NotificationState.BuildChatItem(In(room: Dm()), 1);
        Assert.Equal("Budi", d.Title);
        Assert.Equal("Halo tim", d.Body);
    }

    [Fact]
    public void Attachment_only_message_gets_a_readable_preview()
    {
        var m = Msg("") with { Attachments = [new AttachmentDto(Guid.NewGuid(), "a.pdf", 10, "application/pdf", null)] };
        Assert.Equal("Budi: Mengirim lampiran", NotificationState.BuildChatItem(In(m), 1).Body);
    }

    [Fact]
    public void Messages_from_one_room_fold_into_one_card()
    {
        using var ctx = new BunitContext();
        var s = State(ctx);

        s.ShowChatMessage(In(Msg("satu")), "/home");
        s.ShowChatMessage(In(Msg("dua")), "/home");
        s.ShowChatMessage(In(Msg("tiga")), "/home");

        var card = Assert.Single(s.ActiveBanners);
        Assert.Equal("Tim Ops · 3 pesan baru", card.Item.Title);
        Assert.Equal("Budi: tiga", card.Item.Body);
        Assert.Empty(s.RecentNotifications); // banner only, never the bell
    }

    [Fact]
    public void Other_rooms_get_their_own_card_and_opening_a_room_clears_it()
    {
        using var ctx = new BunitContext();
        var s = State(ctx);
        var other = Guid.NewGuid();

        s.ShowChatMessage(In(Msg()), "/home");
        s.ShowChatMessage(In(Msg(room: other), room: Group("Finance") with { Id = other }), "/home");
        Assert.Equal(2, s.ActiveBanners.Count);

        s.ClearChatBanner(RoomId);
        Assert.Equal("Finance", Assert.Single(s.ActiveBanners).Item.Title);
    }

    [Fact]
    public void Chat_banner_leaves_windows_toasts_to_web_push()
    {
        using var ctx = new BunitContext();
        var s = State(ctx);

        s.ShowChatMessage(In(Msg("satu")), "/home");
        s.ShowChatMessage(In(Msg("dua")), "/home");

        // The service worker shows the toast (server push); the page never raises its own.
        Assert.Empty(ctx.JSInterop.Invocations["ipop.desktopNotify"]);
    }

    [Fact]
    public void Muted_room_raises_no_desktop_notification()
    {
        using var ctx = new BunitContext();
        var s = State(ctx);

        s.ShowChatMessage(In(muted: true), "/home");

        Assert.Empty(ctx.JSInterop.Invocations["ipop.desktopNotify"]);
    }

    [Fact]
    public void Dismissed_card_starts_counting_again()
    {
        using var ctx = new BunitContext();
        var s = State(ctx);

        s.ShowChatMessage(In(Msg()), "/home");
        s.DismissBanner(s.ActiveBanners[0].Id);
        s.ShowChatMessage(In(Msg("lagi")), "/home");

        Assert.Equal("Tim Ops", Assert.Single(s.ActiveBanners).Item.Title);
    }
}
