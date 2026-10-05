using System.Net;
using Bunit;
using IPop.Application.Tickets;
using IPop.Web.Components.Tickets;
using IPop.Web.Services;
using Xunit;

namespace IPop.Tests;

/// Web side of ICT tickets: wording, sections, SLA, error mapping, module/sound routing, status pill.
public class TicketWebUiTests
{
    [Theory]
    [InlineData("it-support", 6)]
    [InlineData("oracle-k2", 3)]
    [InlineData("web-developer", 4)]
    [InlineData("mobile-developer", 4)]
    [InlineData("unknown-queue", 0)]
    public void Quick_templates_match_idesk_per_queue(string module, int count)
    {
        var list = TicketUi.Templates(module);
        Assert.Equal(count, list.Count);
        foreach (var q in list)
        {
            // Everything a template sets must pass iDesk's own validation.
            Assert.InRange(q.Subject.Length, 5, 200);
            Assert.InRange(q.Description.Length, 3, 5000);
            Assert.Contains(q.Priority, TicketUi.Priorities);
            if (module != "it-support") Assert.Null(q.Category);
        }
        Assert.Equal(list.Count, list.Select(q => q.Label).Distinct().Count());
    }

    [Fact]
    public void No_network_template_is_the_idesk_one()
    {
        var q = Assert.Single(TicketUi.Templates("it-support"), x => x.Label == "No Network");
        Assert.Equal(("NETWORK", "HIGH", "Koneksi internet bermasalah", "Tidak ada koneksi WiFi maupun LAN. Area/Lantai: "),
            (q.Category, q.Priority, q.Subject, q.Description));
    }

    private static TicketDetailDto Detail(string status, DateTime? sla = null, DateTime? resolvedAt = null,
        IReadOnlyList<TicketEventDto>? events = null, string origin = "IPOP") => new(
        Guid.NewGuid(), "041026-NET-0003", "IPOP-ICT-2026-000042", origin, status, "HIGH", "it-support", "IT Support Tickets",
        "NETWORK", "Wifi lantai 2 mati", "desc", null, null, null, null, null, null, null, sla,
        new DateTime(2026, 10, 4, 1, 0, 0, DateTimeKind.Utc), new DateTime(2026, 10, 4, 2, 0, 0, DateTimeKind.Utc),
        resolvedAt, null, "https://idesk/x", null, [], events ?? []);

    [Theory]
    [InlineData("HARDWARE_INSTALLATION", "Hardware installation")]
    [InlineData("ORACLE_REQUEST", "Oracle request")]
    [InlineData("WEB_DEVELOPER", "Web development")]
    [InlineData("NETWORK", "Network")]
    public void Category_codes_are_humanised(string code, string label) => Assert.Equal(label, TicketUi.CategoryLabel(code));

    [Theory]
    [InlineData("RESOLVED", "Please confirm", "Resolved — please confirm", "s-res")]
    [InlineData("FAILED", "Couldn't send", "Couldn't send", "s-fail")]
    [InlineData("WAITING_VENDOR", "Waiting on vendor", "Waiting on vendor", "s-vend")]
    [InlineData("TODO", "Open", "Open", "s-open")]
    public void Status_pills_match_the_mockup(string status, string shortLabel, string longLabel, string css)
    {
        var m = TicketUi.Status(status);
        Assert.Equal((shortLabel, longLabel, css), (m.Short, m.Long, m.Css));
    }

    [Fact]
    public void Every_status_lands_in_exactly_one_section()
    {
        foreach (var s in new[] { "SENDING", "FAILED", "TODO", "IN_PROGRESS", "WAITING_VENDOR", "RESOLVED", "CLOSED", "CANCELLED" })
            Assert.Single(TicketUi.Sections, x => x.Statuses.Contains(s));
        Assert.Equal(["Not sent yet", "Needs you", "Open", "Closed"], TicketUi.Sections.Select(x => x.Name));
    }

    [Fact]
    public void Sla_reads_met_hours_left_or_overdue()
    {
        var now = new DateTime(2026, 10, 5, 3, 0, 0, DateTimeKind.Utc);
        Assert.Equal(("warn", "4 h left"), TicketUi.Sla(Detail("IN_PROGRESS", now.AddHours(4.5)), now));
        Assert.Equal(("bad", "Overdue"), TicketUi.Sla(Detail("TODO", now.AddMinutes(-1)), now));
        Assert.Equal(("ok", "On track"), TicketUi.Sla(Detail("TODO", now.AddDays(2)), now));
        Assert.Equal(("ok", "Met"), TicketUi.Sla(Detail("RESOLVED", now, resolvedAt: now.AddHours(-1)), now));
        Assert.Null(TicketUi.Sla(Detail("TODO"), now));
    }

    [Fact]
    public void Activity_is_newest_first_and_humanised()
    {
        var t0 = new DateTime(2026, 10, 3, 1, 12, 0, DateTimeKind.Utc);
        var t = Detail("RESOLVED", events:
        [
            new("created", null, "TODO", null, t0),
            new("assigned", null, "TODO", "Rizky <Aditya>", t0.AddMinutes(18)),
            new("status_changed", "TODO", "IN_PROGRESS", "Rizky", t0.AddHours(1)),
            new("resolved", "IN_PROGRESS", "RESOLVED", "Rizky", t0.AddHours(2)),
        ]);
        var rows = TicketUi.Activity(t, t0.AddDays(2));
        Assert.Equal(["check", "play", "user", "send"], rows.Select(r => r.Icon));
        Assert.Contains("Submitted from iPop", rows[^1].Text.Value);
        Assert.Contains("IPOP-ICT-2026-000042", rows[^1].Sub);
        Assert.Contains("&lt;Aditya&gt;", rows[2].Text.Value); // assignee names are encoded
    }

    [Fact]
    public void Progress_steps_follow_the_status()
    {
        Assert.Equal(0, TicketUi.CurrentStep("TODO"));
        Assert.Equal(1, TicketUi.CurrentStep("WAITING_VENDOR"));
        Assert.Equal(4, TicketUi.CurrentStep("CLOSED"));
        Assert.Null(TicketUi.CurrentStep("FAILED"));
        Assert.Equal("Waiting on vendor", TicketUi.StepLabels("WAITING_VENDOR")[1]);
        Assert.Equal("after you confirm", TicketUi.StepStamps(Detail("RESOLVED"))[3]);
    }

    [Fact]
    public void Search_highlight_encodes_and_marks_the_first_match()
    {
        Assert.Equal("Wifi <mark>lan</mark>tai &lt;2&gt;", TicketUi.Highlight("Wifi lantai <2>", "LAN").Value);
        Assert.Equal("a &amp; b", TicketUi.Highlight("a & b", "").Value);
    }

    [Theory]
    [InlineData("shot.png", 1024, null)]
    [InlineData("video.mp4", 1024, "iDesk doesn't accept this file type")]
    [InlineData("big.pdf", 10L * 1024 * 1024 + 1, "Larger than 10 MB")]
    public void Attachments_follow_the_idesk_rules(string name, long size, string? reason)
        => Assert.Equal(reason, TicketUi.RejectReason(name, size));

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, "invalid", TicketCallStatus.Invalid)]
    [InlineData(HttpStatusCode.BadRequest, "rejected", TicketCallStatus.Rejected)]
    [InlineData(HttpStatusCode.UnprocessableEntity, "not_registered", TicketCallStatus.NotRegistered)]
    [InlineData(HttpStatusCode.ServiceUnavailable, "not_configured", TicketCallStatus.NotConfigured)]
    [InlineData(HttpStatusCode.ServiceUnavailable, "unreachable", TicketCallStatus.Unreachable)]
    [InlineData(HttpStatusCode.Conflict, "not_pending", TicketCallStatus.NotPending)]
    [InlineData(HttpStatusCode.NotFound, "not_found", TicketCallStatus.NotFound)]
    public void Api_error_bodies_map_to_outcomes(HttpStatusCode code, string errorCode, TicketCallStatus expected)
        => Assert.Equal(expected, TicketApi.Classify(code, errorCode));

    [Theory]
    [InlineData("ticket.resolved", "/tickets/5f0c6a1e-0000-0000-0000-000000000001")]
    [InlineData("system", "/tickets/5f0c6a1e-0000-0000-0000-000000000001")]
    public void Ticket_notifications_wear_the_tickets_module(string type, string url)
    {
        var m = NotificationModules.For(type, "Ticket resolved — please confirm", url);
        Assert.Equal(("tickets", "Tickets", "life-buoy"), (m.Key, m.Label, m.Icon));
    }

    [Fact]
    public void Ticket_notifications_play_the_tickets_sound()
        => Assert.Equal(("tickets", false), NotificationState.SoundFor("ticket.status_changed", null));

    [Fact]
    public void Status_pill_renders_spinner_for_sending_and_long_label()
    {
        using var ctx = new BunitContext();
        var sending = ctx.Render<StatusPill>(p => p.Add(x => x.Status, "SENDING"));
        Assert.NotNull(sending.Find(".tk-pill.s-send .tk-spin"));
        var resolved = ctx.Render<StatusPill>(p => p.Add(x => x.Status, "RESOLVED").Add(x => x.Long, true).Add(x => x.Pop, true));
        var pill = resolved.Find(".tk-pill");
        Assert.Contains("mi-pop-s", pill.ClassName);
        Assert.Contains("Resolved — please confirm", pill.TextContent);
    }
}
