using IPop.Application.Tickets;
using Microsoft.EntityFrameworkCore;

namespace IPop.Infrastructure.Tickets;

public sealed partial class TicketService
{
    /// Live ticket facts and public conversation for the assigned agent (/idesk/tickets/{id}),
    /// who has no local mirror row in db.Tickets. Authorizes via iDesk assignee/requester NIK or
    /// an existing notification delivery for this iDesk ticket.
    public async Task<IdeskPreviewOutcome> GetIdeskPreviewAsync(Guid userId, Guid ideskTicketId, CancellationToken ct = default)
    {
        if (!idesk.IsConfigured)
            return new(IdeskPreviewKind.NotConfigured, Message: "iDesk is not configured on this server.");

        var user = await db.Users.AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => new { u.Nik, u.Name })
            .FirstOrDefaultAsync(ct);
        if (user is null) return new(IdeskPreviewKind.NotFound);

        var idStr = ideskTicketId.ToString("D");
        var prefix = IdeskTargetPrefix + idStr;
        var updateUrl = prefix + IdeskUpdateSuffix;

        var hasNotification = await db.NotificationDeliveries.AsNoTracking()
            .AnyAsync(d => d.UserId == userId
                && d.Request.TargetUrl != null
                && (d.Request.TargetUrl == prefix || d.Request.TargetUrl == updateUrl), ct);

        var tr = await idesk.GetTicketAsync(idStr, ct);
        if (tr.Outcome == IdeskOutcome.NotFound) return new(IdeskPreviewKind.NotFound);
        if (!tr.Ok || tr.Value is null) return new(IdeskPreviewKind.Unreachable, Message: tr.Message);

        var it = tr.Value;
        var isAssignee = !string.IsNullOrWhiteSpace(it.AssigneeNik)
            && string.Equals(it.AssigneeNik, user.Nik, StringComparison.OrdinalIgnoreCase);
        var isRequester = !string.IsNullOrWhiteSpace(it.RequesterNik)
            && string.Equals(it.RequesterNik, user.Nik, StringComparison.OrdinalIgnoreCase);

        if (!isAssignee && !isRequester && !hasNotification)
            return new(IdeskPreviewKind.NotFound);

        var mr = await idesk.GetMessagesAsync(it.TicketNumber ?? idStr, ct);
        var msgs = mr.Ok && mr.Value is not null
            ? mr.Value.Messages.Select(m =>
            {
                var mine = !m.FromRequester
                    && !string.IsNullOrWhiteSpace(m.SenderName)
                    && string.Equals(m.SenderName.Trim(), user.Name.Trim(), StringComparison.OrdinalIgnoreCase);
                var sender = m.FromRequester
                    ? (string.IsNullOrWhiteSpace(m.SenderName) ? it.RequesterName ?? "Requester" : m.SenderName)
                    : m.SenderName;
                return new TicketMessageDto(m.Id, sender, mine, m.Content, m.Attachments, m.CreatedAt);
            }).ToList()
            : [];

        return new(IdeskPreviewKind.Ok, new IdeskTicketPreviewDto(
            it.TicketId,
            it.TicketNumber,
            it.Status,
            it.Priority,
            it.AssigneeName,
            msgs,
            MessagesUnavailable: !mr.Ok));
    }
}
