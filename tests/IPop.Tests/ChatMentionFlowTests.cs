using IPop.Application.Chat;
using IPop.Application.Chat.Mentions;
using IPop.Web.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace IPop.Tests;

/// Chat @mentions end to end: composer display text → wire tokens, parser,
/// @all permission, and MentionedNiks/MentionsAll on MessageDto.
public class ChatMentionFlowTests
{
    private const string LongName = "ALPHIN SANTOSO HERMANTHAN, S.T.";

    // ── Composer: caret-aware detection ──

    [Fact]
    public void DetectActiveAt_bare_at_opens_with_empty_query()
    {
        var a = ComposerMentionModel.DetectActiveAt("hi @", 4);
        Assert.NotNull(a);
        Assert.Equal(3, a!.Value.AtIndex);
        Assert.Equal("", a.Value.Query);
    }

    [Fact]
    public void DetectActiveAt_ignores_email_and_uses_caret_not_last_at()
    {
        Assert.Null(ComposerMentionModel.DetectActiveAt("mail bob@corp.com", 17));
        // Caret sits after "@al", a later "@bo" exists further right.
        var text = "hey @al and @bo";
        var a = ComposerMentionModel.DetectActiveAt(text, 7);
        Assert.Equal(("al", 4), (a!.Value.Query, a.Value.AtIndex));
    }

    [Fact]
    public void DetectActiveAt_allows_spaces_for_multiword_names()
    {
        var text = "@alphin san";
        var a = ComposerMentionModel.DetectActiveAt(text, text.Length);
        Assert.Equal("alphin san", a!.Value.Query);
        Assert.Null(ComposerMentionModel.DetectActiveAt("@a\nb", 4));
    }

    // ── Composer: display text + picked map → wire tokens ──

    [Fact]
    public void InsertDisplay_replaces_query_and_places_caret_after_name()
    {
        var active = ComposerMentionModel.DetectActiveAt("hi @alp", 7)!.Value;
        var (text, caret) = ComposerMentionModel.InsertDisplay("hi @alp", active, LongName);
        Assert.Equal($"hi @{LongName} ", text);
        Assert.Equal(text.Length, caret);
    }

    [Fact]
    public void ToWire_converts_picked_names_with_punctuation_and_spaces()
    {
        var id = Guid.NewGuid();
        var picked = new List<MentionSegment> { new MentionSegment.User(id, LongName) };
        var wire = ComposerMentionModel.ToWire($"Tolong cek @{LongName} ya", picked);
        Assert.Equal($"Tolong cek @[{LongName}](u:{id:D}) ya", wire);

        var segs = MentionParser.Parse(wire);
        var user = Assert.Single(segs.OfType<MentionSegment.User>());
        Assert.Equal(id, user.UserId);
        Assert.Equal(LongName, user.Name);
    }

    [Fact]
    public void ToWire_prefers_longest_name_and_keeps_existing_tokens()
    {
        var ann = Guid.NewGuid();
        var annLee = Guid.NewGuid();
        var proj = Guid.NewGuid();
        var picked = new List<MentionSegment>
        {
            new MentionSegment.User(ann, "Ann"),
            new MentionSegment.User(annLee, "Ann Lee"),
            new MentionSegment.All(),
        };
        var wire = ComposerMentionModel.ToWire($"@Ann Lee and @Ann, @all see @[Plan](p:{proj:D})", picked);
        Assert.Equal($"@[Ann Lee](u:{annLee:D}) and @[Ann](u:{ann:D}), @[all](all) see @[Plan](p:{proj:D})", wire);
    }

    [Fact]
    public void ToWire_does_not_match_name_prefix_inside_longer_word()
    {
        var id = Guid.NewGuid();
        var wire = ComposerMentionModel.ToWire("@Annabel hi", [new MentionSegment.User(id, "Ann")]);
        Assert.Equal("@Annabel hi", wire);
    }

    [Fact]
    public void Prune_drops_mentions_whose_text_was_edited_away()
    {
        var keep = new MentionSegment.User(Guid.NewGuid(), "Bob Tan");
        var gone = new MentionSegment.User(Guid.NewGuid(), "Ann Lee");
        var pruned = ComposerMentionModel.Prune("hi @Bob Tan and @Ann Le", [keep, gone]);
        Assert.Equal([keep], pruned);
        // Pruned list → no token for the altered text.
        Assert.DoesNotContain("u:", ComposerMentionModel.ToWire("hi @Ann Le", pruned).Replace($"u:{keep.UserId:D}", ""));
    }

    [Fact]
    public void FromWire_roundtrips_for_editing()
    {
        var id = Guid.NewGuid();
        var proj = Guid.NewGuid();
        var wire = $"hi @[{LongName}](u:{id:D}) @[all](all) @[Plan](p:{proj:D})";
        var (text, picked) = ComposerMentionModel.FromWire(wire);
        Assert.Equal($"hi @{LongName} @all @[Plan](p:{proj:D})", text);
        Assert.Equal(wire, ComposerMentionModel.ToWire(text, picked));
    }

    // ── Server: @all permission ──

    [Fact]
    public void Resolver_ignores_all_when_sender_not_admin()
    {
        var sender = Guid.NewGuid();
        var other = Guid.NewGuid();
        var segs = MentionParser.Parse("@[all](all) hello");
        var members = new Dictionary<Guid, bool> { [sender] = true, [other] = true };

        Assert.Empty(MentionResolver.Resolve(new MentionResolutionInput(sender, "group", segs, members, SenderMayMentionAll: false)));
        var admin = MentionResolver.Resolve(new MentionResolutionInput(sender, "group", segs, members, SenderMayMentionAll: true));
        Assert.Equal(other, Assert.Single(admin).UserId);
    }

    [Fact]
    public async Task SendMessage_all_from_member_is_plain_but_from_admin_fans_out()
    {
        using var t = new TestDb();
        var owner = t.AddUser("OWN");
        var member = t.AddUser("MEM");
        var third = t.AddUser("THR");
        var room = t.AddRoom("group", "G");
        t.AddMember(room.Id, owner.Id, "owner");
        t.AddMember(room.Id, member.Id, "member");
        t.AddMember(room.Id, third.Id, "member");

        var r1 = await t.NewChatService().SendMessageAsync(member.Id,
            new SendMessageRequest(room.Id, "@[all](all) hi", Guid.NewGuid(), null, null), default);
        Assert.True(r1.Success);
        Assert.False(r1.Message!.MentionsAll);
        Assert.Equal(0, await t.Verify().ChatMentions.CountAsync(m => m.MessageId == r1.Message.Id));

        var r2 = await t.NewChatService().SendMessageAsync(owner.Id,
            new SendMessageRequest(room.Id, "@[all](all) hi", Guid.NewGuid(), null, null), default);
        Assert.True(r2.Success);
        Assert.True(r2.Message!.MentionsAll);
        Assert.Equal(2, await t.Verify().ChatMentions.CountAsync(m => m.MessageId == r2.Message.Id && m.IsAllMention));
    }

    // ── Server: DTO carries MentionedNiks ──

    [Fact]
    public async Task Messages_carry_MentionedNiks_from_tokens()
    {
        using var t = new TestDb();
        var a = t.AddUser("A1");
        var b = t.AddUser("B1");
        var c = t.AddUser("C1");
        var room = t.AddRoom("group", "G");
        t.AddMember(room.Id, a.Id, "member");
        t.AddMember(room.Id, b.Id, "member");
        t.AddMember(room.Id, c.Id, "member");

        var sent = await t.NewChatService().SendMessageAsync(a.Id,
            new SendMessageRequest(room.Id, $"hey @[{b.Name}](u:{b.Id:D})", Guid.NewGuid(), null, null), default);
        Assert.Equal(["B1"], sent.Message!.MentionedNiks);
        await t.NewChatService().SendMessageAsync(a.Id,
            new SendMessageRequest(room.Id, "plain", Guid.NewGuid(), null, null), default);

        var page = await t.NewChatService().GetMessagesAsync(room.Id, c.Id, 1, 50, default);
        var mentioned = page.Items.Single(m => m.Id == sent.Message.Id);
        Assert.Equal(["B1"], mentioned.MentionedNiks);
        Assert.False(mentioned.MentionsAll);
        Assert.Empty(page.Items.Single(m => m.Content == "plain").MentionedNiks);
    }

    // ── Rendering ──

    private sealed class Dir(Dictionary<Guid, (string Nik, string Name)> map) : IMentionDirectory
    {
        public bool TryResolve(Guid id, out string nik, out string name, out bool active)
        {
            if (map.TryGetValue(id, out var v)) { nik = v.Nik; name = v.Name; active = true; return true; }
            nik = name = ""; active = false; return false;
        }
    }

    [Fact]
    public void Renderer_marks_me_and_is_clickable_in_dm_too()
    {
        var id = Guid.NewGuid();
        var dir = new Dir(new() { [id] = ("N1", "Ann Lee") });
        var html = MentionRenderer.ToHtml($"hi @[Ann Lee](u:{id:D})", dir, isGroupRoom: false, "N1", null);
        Assert.Contains("data-nik=\"N1\"", html);
        Assert.Contains("mention user-mention me", html);
        Assert.Contains("@Ann Lee</span>", html);
    }

    [Fact]
    public void Renderer_legacy_full_name_is_one_unit_and_emails_are_ignored()
    {
        var dir = new Dir(new());
        var html = MentionRenderer.ToHtml($"cc @{LongName} and bob@corp.com and @someone", dir, true, null,
            [("N9", LongName)]);
        Assert.Contains($"data-nik=\"N9\"", html);
        Assert.Contains($">@{LongName}</span>", html);
        Assert.DoesNotContain("@corp</span>", html);
        Assert.Contains("<span class=\"mention legacy\">@someone</span>", html);
    }
}
