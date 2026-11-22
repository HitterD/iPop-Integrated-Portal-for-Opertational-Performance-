using System.Text.Json;
using IPop.Application.Notifier;
using Xunit;

namespace IPop.Tests;

/// Notifier 1.5.0 priority inbox (docs/notifier/PRIORITY-150.md): every notification falls into one kind and one of
/// three tiers. The case table lives in tests/notifier-tier-cases.json so the app's tiers.js is checked against the
/// very same cases.
public class NotifierTierTests
{
    /// AgeHours / Read (optional, 1.5.1): how old the notification is and whether it was opened — an iDesk ticket
    /// assigned to me is Needs action only while unread and under 24 h.
    public sealed record TierCase(string? EventType, string? Priority, bool Leader, string Kind, string Tier, string? TargetUrl = null,
        double? AgeHours = null, bool Read = false);

    private static readonly DateTime Now = new(2026, 11, 21, 8, 0, 0, DateTimeKind.Utc);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static IEnumerable<object[]> Cases()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "IPop.slnx"))) dir = dir.Parent;
        var path = Path.Combine(dir!.FullName, "tests", "notifier-tier-cases.json");
        var cases = JsonSerializer.Deserialize<List<TierCase>>(File.ReadAllText(path), Json)!;
        return cases.Select(c => new object[] { c });
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Default_settings_classify_every_case(TierCase c)
    {
        var kind = NotifierSettings.KindOf(c.EventType, c.Priority, c.TargetUrl);
        Assert.Equal(c.Kind, kind);
        DateTime? at = c.AgeHours is { } h ? Now.AddHours(-h) : null;
        Assert.Equal(c.Tier, NotifierSettings.Parse(null).TierFor(kind, c.EventType, c.Priority, c.Leader, at, c.Read, Now));
    }

    [Fact]
    public void Assigned_ticket_leaves_needs_action_after_24_hours_with_the_real_clock()
    {
        var s = NotifierSettings.Parse(null);
        Assert.Equal("act", s.TierFor("ticketassigned", "ticket.assigned", "normal", false, DateTime.UtcNow.AddMinutes(-5)));
        Assert.Equal("msg", s.TierFor("ticketassigned", "ticket.assigned", "normal", false, DateTime.UtcNow.AddHours(-25)));
        Assert.Equal("act", s.TierFor("ticketassigned", "ticket.assigned", "normal", false));   // live, no time: new
        // A chosen "msg" or "info" stays as chosen; the rule only ever lowers Needs action.
        var quiet = NotifierSettings.Parse("""{"tiers":{"ticketassigned":"info"}}""");
        Assert.Equal("info", quiet.TierFor("ticketassigned", "ticket.assigned", "normal", false, DateTime.UtcNow.AddHours(-30)));
    }

    [Fact]
    public void The_case_table_covers_every_kind()
    {
        var kinds = Cases().Select(o => ((TierCase)o[0]).Kind).ToHashSet();
        Assert.All(NotifierSettings.KindKeys, k => Assert.Contains(k, kinds));
    }

    [Fact]
    public void Defaults_match_the_plan()
    {
        var s = NotifierSettings.Parse(null);
        Assert.Equal(NotifierSettings.KindKeys.Length, s.Tiers.Count);
        Assert.Equal("act", s.Tiers["approval"]);
        Assert.Equal("act", s.Tiers["ticketassigned"]);
        Assert.Equal("act", s.Tiers["meetingsoon"]);
        Assert.Equal("info", s.Tiers["mailmass"]);
        Assert.Equal("info", s.Tiers["projectwatch"]);
        Assert.Equal("info", s.Tiers["news"]);
        Assert.Equal("info", s.Tiers["system"]);
        Assert.Equal("msg", s.Tiers["chat"]);
        Assert.Empty(s.Vips);
        Assert.False(s.HideMessageToasts);
    }

    [Fact]
    public void Chosen_tiers_apply_but_never_override_always_act()
    {
        var s = NotifierSettings.Parse("""{"tiers":{"chat":"info","mail":"act","news":"bogus"}}""");
        Assert.Equal("info", s.TierFor("chat", "chat.message", "normal", false));
        Assert.Equal("act", s.TierFor("mail", "mail.new", "normal", false));
        Assert.Equal("info", s.Tiers["news"]);                                    // unknown value → default
        Assert.Equal("act", s.TierFor("chat", "chat.message", "normal", true));    // leader/VIP
        Assert.Equal("act", s.TierFor("chat", "chat.emergency", "normal", false));
        Assert.Equal("act", s.TierFor("news", "memo.mandatory", "normal", false));
        Assert.Equal("info", s.TierFor("chat", "chat.mention", "high", false));   // a colleague's mention follows the chat tier
        Assert.Equal("act", s.TierFor("mail", "mail.new", "high", false));        // other high priority always acts
    }

    [Fact]
    public void Old_settings_without_tiers_migrate_from_modules()
    {
        var s = NotifierSettings.Parse("""{"modules":{"mail":"off","projects":"off","chat":"toast","tickets":"popup"}}""");
        Assert.Equal("info", s.Tiers["mail"]);
        Assert.Equal("info", s.Tiers["mailmass"]);
        Assert.Equal("info", s.Tiers["projects"]);
        Assert.Equal("info", s.Tiers["projectwatch"]);
        Assert.Equal("msg", s.Tiers["chat"]);
        Assert.Equal("act", s.Tiers["ticketassigned"]);
        Assert.Equal("msg", s.Tiers["tickets"]);
        Assert.Equal("act", s.Tiers["approval"]);
        // Once tiers are stored, the old modules no longer decide.
        var again = NotifierSettings.Parse(s.ToJson().Replace("\"mail\":\"info\"", "\"mail\":\"msg\""));
        Assert.Equal("msg", again.Tiers["mail"]);
    }

    [Fact]
    public void Vips_are_deduplicated_and_capped_at_50()
    {
        var ids = Enumerable.Range(0, 60).Select(_ => Guid.NewGuid()).ToList();
        var json = JsonSerializer.Serialize(new { vips = ids.Concat(ids.Take(5)).Append(Guid.Empty), hideMessageToasts = true });
        var s = NotifierSettings.Parse(json);
        Assert.Equal(50, s.Vips.Count);
        Assert.Equal(ids.Take(50), s.Vips);
        Assert.True(s.HideMessageToasts);
        Assert.Equal(50, NotifierSettings.Parse(s.ToJson()).Vips.Count);
    }
}
