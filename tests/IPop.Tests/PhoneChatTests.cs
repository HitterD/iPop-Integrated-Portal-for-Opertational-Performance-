using Bunit;
using IPop.Application.Chat;
using IPop.Web.Components.Phone.Chat;
using IPop.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IPop.Tests;

/// Wave 2 phone chat (mockup v2 CH1–CH3): presentational sheets; Chat.razor keeps all state.
[Collection("AvatarPhotos")]
public class PhoneChatTests
{
    private static BunitContext Ctx()
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddSingleton(new OverlayStack());
        ctx.Services.AddScoped<PhotoViewerState>();
        return ctx;
    }

    private static ChatRoomDto Group(params MemberDto[] members) =>
        new(Guid.NewGuid(), "group", "ICT Team", null, members.Length, 1, members, "member", []);

    private static MemberDto M(string nik, string name, string role = "member", string? pres = "on") => new(Guid.NewGuid(), nik, name, role, pres);

    // ---------- CH3 long-press actions ----------

    [Fact]
    public void Actions_sheet_has_reaction_bar_and_the_mockup_actions_in_order()
    {
        using var ctx = Ctx();
        string? reacted = null; var fired = new List<string>();
        var cut = ctx.Render<MessageActionsSheet>(p => p
            .Add(x => x.Open, true).Add(x => x.Preview, "Kita ganti baterainya minggu ini")
            .Add(x => x.Reactions, ["👍", "❤️", "😂"]).Add(x => x.CanCopy, true).Add(x => x.CanPin, true)
            .Add(x => x.CanDelete, true)
            .Add(x => x.OnReact, (string e) => reacted = e)
            .Add(x => x.OnReply, () => fired.Add("reply")).Add(x => x.OnForward, () => fired.Add("fwd"))
            .Add(x => x.OnCopy, () => fired.Add("copy")).Add(x => x.OnPin, () => fired.Add("pin"))
            .Add(x => x.OnSave, () => fired.Add("save")).Add(x => x.OnDelete, () => fired.Add("del")));

        Assert.Equal(3, cut.FindAll(".m-chat-rx button.m-chat-rxe").Count);
        Assert.NotNull(cut.Find(".m-chat-rx button[aria-label='More reactions']"));
        Assert.Contains("Kita ganti", cut.Find(".m-chat-lift").TextContent);
        var labels = cut.FindAll(".m-chat-amenu button").Select(b => b.TextContent.Trim()).ToList();
        Assert.Equal(["Reply", "Forward", "Copy", "Pin", "Save", "Delete"], labels);
        Assert.Contains("danger", cut.Find(".m-chat-amenu button:last-child").ClassName);

        cut.FindAll(".m-chat-rx button.m-chat-rxe")[1].Click();
        Assert.Equal("❤️", reacted);
        foreach (var b in cut.FindAll(".m-chat-amenu button")) b.Click();
        Assert.Equal(["reply", "fwd", "copy", "pin", "save", "del"], fired);
    }

    [Fact]
    public void Delete_only_for_own_messages_and_edit_only_when_allowed()
    {
        using var ctx = Ctx();
        var cut = ctx.Render<MessageActionsSheet>(p => p.Add(x => x.Open, true).Add(x => x.Reactions, ["👍"])
            .Add(x => x.CanDelete, false).Add(x => x.CanEdit, false).Add(x => x.CanPin, false));
        var labels = cut.FindAll(".m-chat-amenu button").Select(b => b.TextContent.Trim()).ToList();
        Assert.DoesNotContain("Delete", labels);
        Assert.DoesNotContain("Edit", labels);
        Assert.DoesNotContain("Pin", labels);

        cut.Render(p => p.Add(x => x.CanEdit, true).Add(x => x.IsPinned, true).Add(x => x.CanPin, true));
        labels = cut.FindAll(".m-chat-amenu button").Select(b => b.TextContent.Trim()).ToList();
        Assert.Contains("Edit", labels);
        Assert.Contains("Unpin", labels);
    }

    [Fact]
    public void Actions_sheet_is_a_bottom_sheet_that_closes_on_back()
    {
        using var ctx = Ctx();
        var overlays = ctx.Services.GetRequiredService<OverlayStack>();
        var closed = false;
        var cut = ctx.Render<MessageActionsSheet>(p => p.Add(x => x.Open, true).Add(x => x.Reactions, ["👍"])
            .Add(x => x.OnClose, () => closed = true));
        Assert.NotNull(cut.Find(".ph-sheet.m-chat-acts"));
        Assert.NotEmpty(overlays.Stack);
        cut.Render(p => p.Add(x => x.Open, false));
        Assert.Empty(cut.FindAll(".ph-sheet"));
        _ = closed;
    }

    // ---------- CH2 attach sheet ----------

    [Fact]
    public void Attach_sheet_tiles_call_the_existing_handlers()
    {
        using var ctx = Ctx();
        var fired = new List<string>();
        var cut = ctx.Render<ChatAttachSheet>(p => p.Add(x => x.Open, true).Add(x => x.IsGroup, true).Add(x => x.CanEmergency, false)
            .Add(x => x.EventHref, "/calendar?new=1")
            .Add(x => x.OnGallery, () => fired.Add("gallery")).Add(x => x.OnCamera, () => fired.Add("camera"))
            .Add(x => x.OnDocument, () => fired.Add("doc")).Add(x => x.OnSticker, () => fired.Add("sticker"))
            .Add(x => x.OnPoll, () => fired.Add("poll")).Add(x => x.OnMention, () => fired.Add("mention")));

        var tiles = cut.FindAll(".m-chat-agrid > *").Select(b => b.TextContent.Trim()).ToList();
        Assert.Equal(["Gallery", "Camera", "Document", "Sticker", "Event", "Poll", "Mention"], tiles);
        Assert.Equal("/calendar?new=1", cut.Find(".m-chat-agrid a").GetAttribute("href"));
        foreach (var b in cut.FindAll(".m-chat-agrid button")) b.Click();
        Assert.Equal(["gallery", "camera", "doc", "sticker", "poll", "mention"], fired);
    }

    [Fact]
    public void Attach_sheet_hides_group_tiles_in_a_dm_and_shows_emergency_for_admins()
    {
        using var ctx = Ctx();
        var cut = ctx.Render<ChatAttachSheet>(p => p.Add(x => x.Open, true).Add(x => x.IsGroup, false).Add(x => x.EventHref, "/x"));
        var tiles = cut.FindAll(".m-chat-agrid > *").Select(b => b.TextContent.Trim()).ToList();
        Assert.DoesNotContain("Poll", tiles);
        Assert.DoesNotContain("Mention", tiles);
        Assert.DoesNotContain("Emergency", tiles);

        cut.Render(p => p.Add(x => x.IsGroup, true).Add(x => x.CanEmergency, true));
        Assert.Contains("Emergency", cut.Find(".m-chat-agrid").TextContent);
    }

    // ---------- CH1 chat info ----------

    [Fact]
    public void Info_has_four_actions_and_rows_to_pins_mentions_saved()
    {
        using var ctx = Ctx();
        var fired = new List<string>();
        var room = Group(M("123456", "Go Sonny Gozali", "owner"), M("777777", "Jason Kristanto"));
        var cut = ctx.Render<ChatInfoPhone>(p => p.Add(x => x.Room, room).Add(x => x.Title, "ICT Team").Add(x => x.PinnedCount, 3)
            .Add(x => x.MeetingHref, "/calendar?room=1")
            .Add(x => x.OnCall, () => fired.Add("call")).Add(x => x.OnSearch, () => fired.Add("search")).Add(x => x.OnMute, () => fired.Add("mute"))
            .Add(x => x.OnPinned, () => fired.Add("pinned")));

        Assert.Equal("ICT Team", cut.Find(".m-chat-gh h2").TextContent);
        Assert.Contains("Group · 2 members", cut.Find(".m-chat-gh small").TextContent);
        Assert.Equal(["Call", "Meeting", "Search", "Mute"], cut.FindAll(".m-chat-acts4 > *").Select(b => b.TextContent.Trim()).ToList());
        Assert.Equal("/calendar?room=1", cut.Find(".m-chat-acts4 a").GetAttribute("href"));

        Assert.Equal("/chat/mentions", cut.Find("a.ph-row[href='/chat/mentions']").GetAttribute("href"));
        Assert.NotNull(cut.Find("a.ph-row[href='/chat/saved']"));
        Assert.Contains("3", cut.Find(".m-chat-pinrow").TextContent);

        foreach (var b in cut.FindAll(".m-chat-acts4 button")) b.Click();
        cut.Find(".m-chat-pinrow").Click();
        Assert.Equal(["call", "search", "mute", "pinned"], fired);
    }

    [Fact]
    public void Info_members_show_name_presence_and_role_but_never_the_nik()
    {
        using var ctx = Ctx();
        var room = Group(M("00061058", "Go Sonny Gozali", "owner", "busy"), M("00099991", "Jason Kristanto", "member", "on"));
        string? profile = null;
        var cut = ctx.Render<ChatInfoPhone>(p => p.Add(x => x.Room, room).Add(x => x.Title, "ICT Team")
            .Add(x => x.OnProfile, (string nik) => profile = nik));

        var rows = cut.FindAll(".m-chat-mem");
        Assert.Equal(2, rows.Count);
        Assert.Contains("Admin", rows[0].TextContent);
        Assert.Contains("Busy", rows[0].TextContent);
        // Visible text only: avatars may key their photo by NIK, but no NIK is ever shown.
        Assert.DoesNotContain("00061058", cut.Find(".m-chat-info").TextContent);
        Assert.DoesNotContain("00099991", rows[1].TextContent);
        rows[1].QuerySelector("button")!.Click();
        Assert.Equal("00099991", profile);
    }

    [Fact]
    public void Info_for_a_dm_shows_contact_wording_and_no_leave_group()
    {
        using var ctx = Ctx();
        var room = new ChatRoomDto(Guid.NewGuid(), "dm", null, null, 2, 1, [M("1", "Me"), M("2", "Jason Kristanto")], "member", []);
        var cut = ctx.Render<ChatInfoPhone>(p => p.Add(x => x.Room, room).Add(x => x.Title, "Jason Kristanto").Add(x => x.Subtitle, "Online · ICT"));
        Assert.Contains("Online · ICT", cut.Find(".m-chat-gh small").TextContent);
        Assert.DoesNotContain("Leave group", cut.Markup);
        Assert.Empty(cut.FindAll(".m-chat-mem"));
    }
}
