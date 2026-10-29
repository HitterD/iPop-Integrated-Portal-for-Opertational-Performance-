using System.Globalization;
using System.Text.RegularExpressions;
using IPop.Application.Tickets;

namespace IPop.Infrastructure.Tickets;

/// <summary>
/// Human-readable text of iDesk ticket notifications (docs/mockups/notifier/aligned.html, section 5).
/// Title is always the ticket's problem title. The body is plain lines the web bell, the Notifier popup and the
/// Windows toast all read:
/// <code>
/// Rizky Ardiansyah · Your PIC · IT Support      ← person line: name, then what they are to me
/// Marked as resolved. Please confirm it works…  ← one sentence of what happened
/// #281026-ORD-0012                              ← ticket number (optional, always starts with '#')
/// </code>
/// No NIK and no GUID ever appear in it.
/// </summary>
public static partial class TicketNotificationText
{
    public const string YourPic = "Your PIC";
    public const string Reporter = "Reporter";

    public static string Body(string person, string sentence, string? number) =>
        string.IsNullOrWhiteSpace(number) ? $"{person}\n{sentence}" : $"{person}\n{sentence}\n#{number.TrimStart('#')}";

    /// "Rizky Ardiansyah · Your PIC · IT Support"; with nobody assigned yet "IT Support · Not assigned yet".
    public static string PicLine(string? assignee, string? module) =>
        Clean(assignee) is { } a ? Join(Nice(a), YourPic, Clean(module)) : Join(Clean(module) ?? "ICT", "Not assigned yet");

    /// "Budi Santoso · Reporter · Logistic, Sepanjang".
    public static string ReporterLine(string? name, string? department, string? site)
    {
        var where = string.Join(", ", new[] { Clean(department), Clean(site) }.OfType<string>());
        return Join(Nice(Clean(name) ?? "Requester"), Reporter, where.Length > 0 ? where : null);
    }

    /// "Rizky: “Sudah saya cek”", with a running count "… · 3 new messages" when more than one is unread.
    public static string ChatSentence(string? sender, string preview, int count, string noun = "messages") =>
        $"{FirstName(sender) ?? "ICT"}: “{Trim(preview, 160)}”" + (count > 1 ? $" · {count} new {noun}" : "");

    /// The running count of a chat notification body ("… · 3 new messages" → 3); 1 for anything else.
    public static int CountOf(string? body)
    {
        if (body is null) return 1;
        var m = CountRx().Match(body);
        return m.Success && int.TryParse(m.Groups[1].ValueSpan, out var n) && n > 0 ? n : 1;
    }

    [GeneratedRegex(@" · (\d+) new (?:messages|replies)$", RegexOptions.Multiline)]
    private static partial Regex CountRx();

    /// What happened, for the requester. Null = stay silent (ticket.created, unknown events, own actions).
    public static string? ForRequester(string kind, IdeskTicket it, TicketWebhookFacts f, string? visitLabel)
    {
        if (f.Actor?.Nik is { } actor && actor == it.RequesterNik) return null;   // they did it themselves
        var by = FirstName(f.Actor?.Name);
        switch (kind)
        {
            case "assigned":
                var to = FirstName(it.AssigneeName);
                if (to is null) return "Your ticket was assigned to ICT.";
                return FirstName(f.PreviousAssignee?.Name) is { } from && !string.Equals(from, to, StringComparison.OrdinalIgnoreCase)
                    ? $"Handed over from {from} to {to}, your new PIC."
                    : $"{to} picked up your ticket and is now your PIC.";
            case "resolved":
                return "Marked as resolved. Please confirm it works, or reply if it still doesn't."
                       + (it.ResolutionDeadlineAt is { } d ? $" Closes automatically on {Day(d)}." : "");
            case "closed":
                return ClosedReason(f) switch
                {
                    Closed.Auto => "Closed automatically: not confirmed in time.",
                    Closed.Confirmed => "Confirmed as fixed. The ticket is closed.",
                    Closed.Manual when by is not null => $"Closed by {by}.",
                    Closed.Manual => "The ticket was closed.",
                    _ => StatusSentence(f.PreviousStatus, it.Status),
                };
            case "status_changed":
                if (IsReopen(f.PreviousStatus, it.Status)) return by is null ? "Reopened." : $"Reopened by {by}.";
                return StatusSentence(f.PreviousStatus, it.Status);
            case "visit_changed":
                return visitLabel is null ? null : $"ICT visit moved to {visitLabel}.";
            default:
                return null;
        }
    }

    /// What happened, for the assigned technician. Null = not for them (or they did it themselves).
    public static string? ForTechnician(string kind, IdeskTicket it, TicketWebhookFacts f, string? visitLabel)
    {
        var me = it.AssigneeNik;
        if (me is null || me == it.RequesterNik) return null;
        if (f.Actor?.Nik is { } actor && actor == me) return null;   // my own change
        var requester = FirstName(it.RequesterName) ?? "the reporter";
        var byRequester = f.Actor is null ? !f.HasActor : f.Actor.Nik == it.RequesterNik;
        switch (kind)
        {
            case "assigned":
                // A reassignment away from me isn't mine any more (assignee is the new one); only "to me" counts.
                if (f.PreviousAssignee?.Nik is { } prev && prev == me) return null;
                var who = Clean(f.Actor?.Name) is { } a ? Nice(a) : null;
                return (who is null ? "Assigned to you." : $"Assigned to you by {who}.")
                       + (it.SlaTarget is { } sla ? $" SLA target: {When(sla)}." : "");
            case "status_changed" when IsReopen(f.PreviousStatus, it.Status) && byRequester:
                return Clean(f.Reason) is { } why ? $"Reopened by {requester}: “{Trim(why, 160)}”" : $"Reopened by {requester}.";
            case "closed" when ClosedReason(f) == Closed.Confirmed
                               || (f.ClosedReason is null && f.Actor is not null && f.Actor.Nik == it.RequesterNik):
                return $"Confirmed as fixed by {requester}. The ticket is closed.";
            case "visit_changed" when visitLabel is not null:
                return $"Visit moved to {visitLabel}.";
            default:
                return null;
        }
    }

    public static string StatusSentence(string? from, string to) =>
        from is null || from == to ? $"Status: {StatusLabel(to)}." : $"Status: {StatusLabel(from)} → {StatusLabel(to)}.";

    public static string StatusLabel(string code) => code switch
    {
        TicketStatus.Todo => "Open",
        TicketStatus.InProgress => "In progress",
        TicketStatus.WaitingVendor => "Waiting for vendor",
        TicketStatus.Resolved => "Resolved",
        TicketStatus.Closed => "Closed",
        TicketStatus.Cancelled => "Cancelled",
        TicketStatus.Sending => "Sending",
        TicketStatus.Failed => "Not sent",
        _ => TicketService.HumanizeStatus(code),
    };

    private static bool IsReopen(string? from, string to) =>
        from is TicketStatus.Resolved or TicketStatus.Closed && to is TicketStatus.Todo or TicketStatus.InProgress or TicketStatus.WaitingVendor;

    private enum Closed { Unknown, Auto, Confirmed, Manual }

    /// closedReason when iDesk sends it; otherwise "actor": null (the system) closing a Resolved ticket is the
    /// auto-close. Without either field we can't tell, and say only "Status: Resolved → Closed".
    private static Closed ClosedReason(TicketWebhookFacts f) => f.ClosedReason switch
    {
        "auto_unconfirmed" => Closed.Auto,
        "confirmed" => Closed.Confirmed,
        "manual" => Closed.Manual,
        _ when f.HasActor && f.Actor is null && f.PreviousStatus == TicketStatus.Resolved => Closed.Auto,
        _ => Closed.Unknown,
    };

    /// "RIZKY ARDIANSYAH" → "Rizky Ardiansyah"; mixed-case names are kept as written.
    public static string Nice(string name)
    {
        var n = name.Trim();
        return n.Any(char.IsLower) ? n : CultureInfo.InvariantCulture.TextInfo.ToTitleCase(n.ToLowerInvariant());
    }

    public static string? FirstName(string? name) =>
        Clean(name) is { } n ? Nice(n).Split(' ', StringSplitOptions.RemoveEmptyEntries)[0] : null;

    private static readonly TimeSpan Wib = TimeSpan.FromHours(7);
    private static string Day(DateTime utc) => (utc + Wib).ToString("d MMM", CultureInfo.InvariantCulture);
    private static string When(DateTime utc) => (utc + Wib).ToString("ddd d MMM, HH:mm", CultureInfo.InvariantCulture);

    private static string Join(params string?[] parts) => string.Join(" · ", parts.OfType<string>().Where(p => p.Length > 0));
    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    private static string Trim(string s, int max)
    {
        s = s.ReplaceLineEndings(" ").Trim();
        return s.Length > max ? s[..max].TrimEnd() + "…" : s;
    }
}

/// Person in a webhook (actor, previousAssignee). Nik is only used to compare, never shown.
public sealed record TicketWebhookPerson(string? Nik, string? Name);

/// The parts of an iDesk webhook beyond the ticket view. All optional: older iDesk builds send none of
/// closedReason / actor / reason. HasActor tells "actor": null (the system did it) from no actor field at all.
public sealed record TicketWebhookFacts(
    string? PreviousStatus,
    TicketWebhookPerson? PreviousAssignee = null,
    string? ClosedReason = null,
    bool HasActor = false,
    TicketWebhookPerson? Actor = null,
    string? Reason = null);
