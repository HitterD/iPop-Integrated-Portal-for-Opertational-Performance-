using System.Text.Json;
using IPop.Application.Tickets;
using IPop.Core.Entities;
using IPop.Infrastructure.Tickets;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace IPop.Tests;

/// Human-readable iDesk ticket notifications (docs/mockups/notifier/aligned.html §5): title = the problem, body =
/// person line + one sentence + "#number"; technicians notified by assignee.nik; never a NIK or GUID in the text.
public partial class TicketTests
{
    /// A webhook as the newer iDesk sends it: assignee.nik, resolutionDeadlineAt, closedReason, actor, reason.
    /// Fields left null are omitted, which is what an older iDesk sends.
    private static string Hook(string id, string evt, IdeskTicket t, string? previousStatus = null, string? assigneeNik = null,
        string? deadline = null, string? closedReason = null, object? actor = null, bool withActor = false,
        object? previousAssignee = null, string? reason = null, object? message = null, string[]? recipients = null)
    {
        var ticket = new Dictionary<string, object?>
        {
            ["ticketId"] = t.TicketId, ["ticketNumber"] = t.TicketNumber, ["externalRef"] = t.ExternalRef, ["origin"] = t.Origin,
            ["requester"] = new { nik = t.RequesterNik, name = t.RequesterName }, ["status"] = t.Status, ["priority"] = t.Priority,
            ["module"] = t.Module, ["category"] = t.Category, ["title"] = t.Title,
            ["assignee"] = t.AssigneeName is null ? null : new { nik = assigneeNik, name = t.AssigneeName },
            ["createdAt"] = t.CreatedAt.ToString("O"), ["updatedAt"] = t.UpdatedAt.ToString("O"), ["url"] = t.Url,
        };
        if (deadline is not null) ticket["resolutionDeadlineAt"] = deadline;
        var root = new Dictionary<string, object?>
        {
            ["id"] = id, ["event"] = evt, ["occurredAt"] = DateTime.UtcNow.ToString("O"), ["previousStatus"] = previousStatus, ["ticket"] = ticket,
        };
        if (closedReason is not null) root["closedReason"] = closedReason;
        if (withActor || actor is not null) root["actor"] = actor;
        if (previousAssignee is not null) root["previousAssignee"] = previousAssignee;
        if (reason is not null) root["reason"] = reason;
        if (message is not null) root["message"] = message;
        if (recipients is not null) root["recipients"] = recipients.Select(n => new { nik = n }).ToArray();
        return JsonSerializer.Serialize(root);
    }

    private const string Req1 = "00000024", Tech = "00061058";

    private static Rig RigWithPeople(out User requester, out User tech)
    {
        var x = Setup();
        var dept = new Department { Id = Guid.NewGuid(), Code = "LOG", Name = "Logistic" };
        var ict = new Department { Id = Guid.NewGuid(), Code = "ICT", Name = "ICT Support" };
        var site = new Site { Id = Guid.NewGuid(), Code = "SPJ", Name = "Sepanjang" };
        x.T.Db.AddRange(dept, ict, site);
        requester = x.T.AddUser(Req1); requester.Name = "BUDI SANTOSO"; requester.DepartmentId = dept.Id; requester.SiteId = site.Id;
        tech = x.T.AddUser(Tech); tech.Name = "Rizky Ardiansyah"; tech.DepartmentId = ict.Id;
        x.T.Db.SaveChanges();
        return x;
    }

    private static IdeskTicket Printer(string status, string? assignee = "Rizky Ardiansyah") =>
        Remote(null, Req1, status, number: "281026-ORD-0012", assignee: assignee, title: "Printer lantai 2 tidak bisa print",
            updatedAt: DateTime.UtcNow.AddSeconds(Interlocked.Increment(ref _seq))) with { RequesterName = "Budi Santoso" };

    private static void NoCodes((string Type, string Title, string Message, Guid? To, string? Url) n, IdeskTicket t)
    {
        Assert.DoesNotContain(t.TicketId, n.Title + n.Message);
        Assert.DoesNotContain(Req1, n.Title + n.Message);
        Assert.DoesNotContain(Tech, n.Title + n.Message);
        Assert.DoesNotContain(t.TicketNumber!, n.Title);
    }

    [Fact]
    public async Task Resolved_tells_the_requester_to_confirm_with_the_auto_close_day()
    {
        var x = RigWithPeople(out var u, out _); using var db = x.T;
        var t = Printer(TicketStatus.Resolved);
        await x.Svc.ApplyWebhookAsync(Hook("r1", "ticket.resolved", t, TicketStatus.InProgress, assigneeNik: Tech,
            deadline: "2026-10-31T03:00:00.000Z", withActor: true, actor: new { nik = Tech, name = "Rizky Ardiansyah" }));

        var n = Assert.Single(x.Gw.Sent);
        Assert.Equal(u.Id, n.To);
        Assert.Equal("Printer lantai 2 tidak bisa print", n.Title);
        Assert.Equal("Rizky Ardiansyah · Your PIC · ICT Support\nMarked as resolved. Please confirm it works, or reply if it still doesn't. "
                     + "Closes automatically on 31 Oct.\n#281026-ORD-0012", n.Message);
        NoCodes(n, t);
    }

    [Fact]
    public async Task Auto_close_says_so_with_closed_reason_or_a_system_actor()
    {
        var x = RigWithPeople(out _, out _); using var db = x.T;
        await x.Svc.ApplyWebhookAsync(Hook("c1", "ticket.closed", Printer(TicketStatus.Closed), TicketStatus.Resolved, closedReason: "auto_unconfirmed"));
        Assert.Contains("\nClosed automatically: not confirmed in time.\n", x.Gw.Sent[^1].Message);

        // No closedReason, but "actor": null (the system) closing a Resolved ticket is the same auto-close.
        var y = RigWithPeople(out _, out _); using var db2 = y.T;
        await y.Svc.ApplyWebhookAsync(Hook("c2", "ticket.closed", Printer(TicketStatus.Closed), TicketStatus.Resolved, withActor: true));
        Assert.Contains("\nClosed automatically: not confirmed in time.\n", y.Gw.Sent[^1].Message);
    }

    [Fact]
    public async Task Requester_own_confirm_is_silent_for_them_but_tells_the_technician()
    {
        var x = RigWithPeople(out _, out var tech); using var db = x.T;
        var t = Printer(TicketStatus.Closed);
        await x.Svc.ApplyWebhookAsync(Hook("c1", "ticket.closed", t, TicketStatus.Resolved, assigneeNik: Tech, closedReason: "confirmed",
            actor: new { nik = Req1, name = "Budi Santoso" }));

        var n = Assert.Single(x.Gw.Sent);
        Assert.Equal(tech.Id, n.To);
        Assert.Equal("Printer lantai 2 tidak bisa print", n.Title);
        Assert.Equal("Budi Santoso · Reporter · Logistic, Sepanjang\nConfirmed as fixed by Budi. The ticket is closed.\n#281026-ORD-0012", n.Message);
        Assert.Equal(TicketService.IdeskTargetPrefix + t.TicketId + TicketService.IdeskUpdateSuffix, n.Url);
        NoCodes(n, t);
    }

    [Fact]
    public async Task Technician_hears_about_being_assigned_but_not_their_own_pick_up()
    {
        var x = RigWithPeople(out var u, out var tech); using var db = x.T;
        var t = Printer(TicketStatus.InProgress);
        await x.Svc.ApplyWebhookAsync(Hook("a1", "ticket.assigned", t, assigneeNik: Tech, actor: new { nik = "00000001", name = "ICT SUPPORT LEAD" }));

        var forTech = Assert.Single(x.Gw.Sent, s => s.To == tech.Id);
        Assert.Equal("Budi Santoso · Reporter · Logistic, Sepanjang\nAssigned to you by Ict Support Lead.\n#281026-ORD-0012", forTech.Message);
        var forReq = Assert.Single(x.Gw.Sent, s => s.To == u.Id);
        Assert.Equal("Rizky Ardiansyah · Your PIC · ICT Support\nRizky picked up your ticket and is now your PIC.\n#281026-ORD-0012", forReq.Message);

        // The technician picks a ticket up themselves: the requester is told, the technician isn't.
        x.Gw.Sent.Clear();
        await x.Svc.ApplyWebhookAsync(Hook("a2", "ticket.assigned", Printer(TicketStatus.InProgress), assigneeNik: Tech,
            actor: new { nik = Tech, name = "Rizky Ardiansyah" }));
        Assert.DoesNotContain(x.Gw.Sent, s => s.To == tech.Id);
        Assert.Single(x.Gw.Sent, s => s.To == u.Id);
    }

    [Fact]
    public async Task Reopen_by_the_requester_reaches_the_technician_with_the_comment()
    {
        var x = RigWithPeople(out var u, out var tech); using var db = x.T;
        await x.Svc.ApplyWebhookAsync(Hook("o1", "ticket.status_changed", Printer(TicketStatus.InProgress), TicketStatus.Resolved, assigneeNik: Tech,
            actor: new { nik = Req1, name = "Budi Santoso" }, reason: "Masih offline lagi setelah jam 11."));

        var n = Assert.Single(x.Gw.Sent);   // the requester did it: nothing for them
        Assert.Equal(tech.Id, n.To);
        Assert.Contains("\nReopened by Budi: “Masih offline lagi setelah jam 11.”\n", n.Message);
        Assert.DoesNotContain(x.Gw.Sent, s => s.To == u.Id);
    }

    [Fact]
    public async Task Technician_status_change_reaches_only_the_requester()
    {
        var x = RigWithPeople(out var u, out var tech); using var db = x.T;
        await x.Svc.ApplyWebhookAsync(Hook("s1", "ticket.status_changed", Printer(TicketStatus.WaitingVendor), TicketStatus.InProgress,
            assigneeNik: Tech, actor: new { nik = Tech, name = "Rizky Ardiansyah" }));

        var n = Assert.Single(x.Gw.Sent);
        Assert.Equal(u.Id, n.To);
        Assert.Contains("\nStatus: In progress → Waiting for vendor.\n", n.Message);
        Assert.DoesNotContain(x.Gw.Sent, s => s.To == tech.Id);
    }

    [Fact]
    public async Task Without_the_new_fields_technicians_get_nothing_and_requesters_get_the_fallback()
    {
        var x = RigWithPeople(out var u, out _); using var db = x.T;
        // Old iDesk: no assignee.nik, no actor, no closedReason, no deadline.
        await x.Svc.ApplyWebhookAsync(Hook("r1", "ticket.resolved", Printer(TicketStatus.Resolved), TicketStatus.InProgress));
        await x.Svc.ApplyWebhookAsync(Hook("c1", "ticket.closed", Printer(TicketStatus.Closed), TicketStatus.Resolved));

        Assert.All(x.Gw.Sent, s => Assert.Equal(u.Id, s.To));
        Assert.Equal("Rizky Ardiansyah · Your PIC · it-support\nMarked as resolved. Please confirm it works, or reply if it still doesn't.\n#281026-ORD-0012",
            x.Gw.Sent[0].Message);
        Assert.Contains("\nStatus: Resolved → Closed.\n", x.Gw.Sent[1].Message);
    }

    [Fact]
    public async Task Chat_reads_with_the_pic_on_one_side_and_the_reporter_on_the_other()
    {
        var x = RigWithPeople(out var u, out var tech); using var db = x.T;
        var t = Printer(TicketStatus.InProgress);
        await x.Svc.ApplyWebhookAsync(Hook("m1", "ticket.message_created", t, assigneeNik: Tech,
            message: MsgWith("Baik Pak, saya ke meja Bapak jam 10.00.", "Rizky Ardiansyah", false), recipients: [Req1]));
        await x.Svc.ApplyWebhookAsync(Hook("m2", "ticket.message_created", t, assigneeNik: Tech,
            message: MsgWith("Sudah restart tapi tetap tidak bisa.", "Budi Santoso", true), recipients: [Tech]));

        var toReq = Assert.Single(x.Gw.Sent, s => s.To == u.Id);
        Assert.Equal("Printer lantai 2 tidak bisa print", toReq.Title);
        Assert.Equal("Rizky Ardiansyah · Your PIC · ICT Support\nRizky: “Baik Pak, saya ke meja Bapak jam 10.00.”\n#281026-ORD-0012", toReq.Message);
        var toTech = Assert.Single(x.Gw.Sent, s => s.To == tech.Id);
        Assert.Equal("Printer lantai 2 tidak bisa print", toTech.Title);
        Assert.Equal("Budi Santoso · Reporter · Logistic, Sepanjang\nBudi: “Sudah restart tapi tetap tidak bisa.”\n#281026-ORD-0012", toTech.Message);
        NoCodes(toReq, t); NoCodes(toTech, t);
    }

    [Fact]
    public async Task Redelivered_technician_event_notifies_once()
    {
        var x = RigWithPeople(out _, out var tech); using var db = x.T;
        var body = Hook("a1", "ticket.assigned", Printer(TicketStatus.InProgress), assigneeNik: Tech, withActor: true);
        await x.Svc.ApplyWebhookAsync(body);
        Assert.Equal(TicketWebhookKind.Duplicate, await x.Svc.ApplyWebhookAsync(body));
        Assert.Single(x.Gw.Sent, s => s.To == tech.Id);
    }

    [Theory]
    [InlineData("Rizky · it-support\nRizky: “hi” · 3 new replies\n#1", 3)]
    [InlineData("Budi · Reporter\nBudi: “hi” · 12 new messages\n#1", 12)]
    [InlineData("Budi · Reporter\nBudi: “hi”\n#1", 1)]
    [InlineData(null, 1)]
    public void Running_count_is_read_from_the_body(string? body, int expected) =>
        Assert.Equal(expected, TicketNotificationText.CountOf(body));

    [Fact]
    public void Web_preview_leads_with_what_happened()
    {
        Assert.Equal("Rizky: “Saya ke meja Bapak jam 10.00”", IPop.Web.Services.NotificationPreview.Of(
            "Rizky Ardiansyah · Your PIC · ICT Support\nRizky: “Saya ke meja Bapak jam 10.00”\n#281026-ORD-0012", "/tickets/x#messages"));
        Assert.Equal("Closed automatically: not confirmed in time. · Rizky Ardiansyah",
            IPop.Web.Services.NotificationPreview.Of("Rizky Ardiansyah · Your PIC · ICT\nClosed automatically: not confirmed in time.\n#1", "/tickets/x"));
        Assert.Equal("a\nb", IPop.Web.Services.NotificationPreview.Of("a\nb", "/mail?mailId=1"));
    }
}
