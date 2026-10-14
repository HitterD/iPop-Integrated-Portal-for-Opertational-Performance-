using IPop.Application.Mail;
using IPop.Application.Realtime;
using IPop.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MailEntity = IPop.Core.Entities.Mail;

namespace IPop.Infrastructure.Mail;

/// Read receipts (spec 2026-10-14 §2/§3). SeenAt is written only here, by the reading pane's "seen" call;
/// mark read / mark all read / search / sync keep using IsRead/ReadAt, the personal unread badge.
public sealed partial class MailService
{
    public async Task<MailSeenResult> RecordSeenAsync(Guid userId, Guid mailId, CancellationToken ct)
    {
        var row = await db.MailRecipients.Include(r => r.Mail)
            .FirstOrDefaultAsync(r => r.MailId == mailId && r.UserId == userId, ct);
        if (row is null || row.Mail.Status == "draft") return new MailSeenResult(false, false, null);
        // The sender's own copy is never a receipt; neither is a scheduled mail not yet delivered.
        if (row.RecipientType == "from" || row.UserId == row.Mail.SenderId || row.Folder == "pending")
            return new MailSeenResult(true, false, null);
        if (row.SeenAt is { } already) return new MailSeenResult(true, false, already);
        if (!IsTracked(row.Mail, await TrackedSinceAsync(ct))) return new MailSeenResult(true, false, null);

        // First writer wins even with two tabs racing: the update only touches a still-null SeenAt.
        var now = DateTime.UtcNow;
        var written = await db.MailRecipients
            .Where(r => r.Id == row.Id && r.SeenAt == null)
            .ExecuteUpdateAsync(u => u.SetProperty(r => r.SeenAt, now), ct);
        if (written == 0)
        {
            var stamp = await db.MailRecipients.AsNoTracking().Where(r => r.Id == row.Id).Select(r => r.SeenAt).FirstAsync(ct);
            return new MailSeenResult(true, false, stamp);
        }

        await NotifyReceiptAsync(row.Mail, userId, ct);
        return new MailSeenResult(true, true, now);
    }

    public async Task<MailReceiptsDto?> GetReceiptsAsync(Guid userId, Guid mailId, CancellationToken ct)
    {
        var mail = await db.Mails.AsNoTracking().FirstOrDefaultAsync(m => m.Id == mailId, ct);
        if (mail is null || mail.Status == "draft") return null;
        var isSender = mail.SenderId == userId;
        if (!isSender)
        {
            var mine = await db.MailRecipients.AsNoTracking()
                .Where(r => r.MailId == mailId && r.UserId == userId).Select(r => r.Folder).FirstOrDefaultAsync(ct);
            if (mine is null or "pending") return null;
        }

        var trackedSince = await TrackedSinceAsync(ct);
        var tracked = IsTracked(mail, trackedSince);
        var rows = await db.MailRecipients.AsNoTracking()
            .Where(r => r.MailId == mailId && r.RecipientType != "from" && r.UserId != mail.SenderId)
            // Bcc state is the sender's alone; for everyone else it is not in the list or the counts.
            .Where(r => isSender || r.RecipientType != "bcc")
            .Select(r => new
            {
                r.UserId, r.RecipientType, r.ViaGroup, r.SeenAt, r.DeliveredAt,
                r.User.Nik, r.User.Name, r.User.PhotoPath,
                Department = r.User.Department != null ? r.User.Department.Name : null,
                Site = r.User.Site != null ? r.User.Site.Name : null,
            })
            .ToListAsync(ct);

        var external = rows.Where(r => IsExternal(r.Nik)).ToList();
        var people = rows.Where(r => !IsExternal(r.Nik))
            .Select(r => new MailReceiptPersonDto(
                Key(r.UserId), r.Name, r.Department, r.Site, r.PhotoPath,
                r.RecipientType, r.ViaGroup, r.UserId == userId,
                tracked ? r.SeenAt : null, r.DeliveredAt != null))
            .OrderBy(p => p.Field == "to" ? 0 : p.Field == "cc" ? 1 : 2).ThenBy(p => p.Name)
            .ToList();

        // Mass mail (> 50 people) / hidden list: other recipients get the count only (no names, no groups).
        // Not Mail.IsMass: that also flags every group mail (for Reply all), and the approved mockup shows
        // names + group read counts on a 41-person group mail.
        var namesVisible = isSender || !CountsOnly(mail);
        var groups = namesVisible ? await ReceiptGroupsAsync(mail, isSender, people, ct) : [];

        return new MailReceiptsDto(
            mail.Id, tracked, trackedSince, isSender, namesVisible,
            people.Count, people.Count(p => p.SeenAt != null), people.Count(p => p.Delivered),
            namesVisible ? people : [],
            groups,
            new MailReceiptExternalDto(external.Count, "not_available"));
    }

    private async Task<List<MailReceiptGroupDto>> ReceiptGroupsAsync(MailEntity mail, bool isSender, List<MailReceiptPersonDto> people, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(mail.RecipientGroupsJson)) return [];
        List<RecipientGroupCountDto> picked;
        try { picked = System.Text.Json.JsonSerializer.Deserialize<List<RecipientGroupCountDto>>(mail.RecipientGroupsJson, Json) ?? []; }
        catch (System.Text.Json.JsonException) { return []; }

        var byKey = people.ToDictionary(p => p.UserKey);
        var result = new List<MailReceiptGroupDto>();
        foreach (var g in picked.Where(g => isSender || g.Field != "bcc"))
        {
            // Today's members that are on this mail: a person who joined the group later is not counted.
            var memberIds = g.Kind == "personal"
                ? await db.MailGroupMembers.AsNoTracking().Where(m => m.GroupId == g.Id && m.Group.OwnerId == mail.SenderId).Select(m => m.UserId).ToListAsync(ct)
                : await db.UserDirGroups.AsNoTracking().Where(m => m.DirGroupId == g.Id).Select(m => m.UserId).ToListAsync(ct);
            var keys = memberIds.Select(Key).Where(byKey.ContainsKey).Distinct().ToList();
            result.Add(new MailReceiptGroupDto(g.Field, g.Kind, g.Name, keys.Count, keys.Count(k => byKey[k].SeenAt != null), keys));
        }
        return result;
    }

    /// The sender and the To/Cc people refresh their receipts row. Mass / hidden mail: sender only
    /// (recipients there see a count, and a hint to 1000 people per read would be a storm).
    private async Task NotifyReceiptAsync(MailEntity mail, Guid seerId, CancellationToken ct)
    {
        if (realtime is null) return;
        try
        {
            var everyone = !CountsOnly(mail);
            var niks = await db.MailRecipients.AsNoTracking()
                .Where(r => r.MailId == mail.Id
                    && (r.UserId == mail.SenderId || r.UserId == seerId || (everyone && (r.RecipientType == "to" || r.RecipientType == "cc"))))
                .Select(r => r.User.Nik)
                .ToListAsync(ct);
            niks = niks.Where(n => !IsExternal(n)).Distinct().ToList();
            if (niks.Count > 0) await realtime.ToUsersAsync(niks, RealtimeEvents.MailReceiptChanged, new { mailId = mail.Id }, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.LogWarning(ex, "Mail {MailId} receipt hint failed", mail.Id);
        }
    }

    private static bool CountsOnly(MailEntity mail) => mail.HideRecipients || mail.RecipientCount > MailLimits.ConfirmAbove;

    private static bool IsTracked(MailEntity mail, DateTime since) => mail.SentAt is { } sent && sent >= since;

    private static bool IsExternal(string nik) => nik.StartsWith(MailReceipts.ExternalNikPrefix, StringComparison.Ordinal);

    private static string Key(Guid userId) => userId.ToString("N");

    /// The migration stores the start; a database created without it (tests, EnsureCreated) gets "now"
    /// on first use, so everything sent earlier reports "not tracked" rather than "nobody read it".
    private async Task<DateTime> TrackedSinceAsync(CancellationToken ct)
    {
        var json = await db.AppSettings.AsNoTracking()
            .Where(s => s.Key == MailReceipts.TrackedSinceKey).Select(s => s.ValueJson).FirstOrDefaultAsync(ct);
        if (json is not null && TryParseInstant(json) is { } parsed) return parsed;

        var now = DateTime.UtcNow;
        var row = new AppSetting
        {
            Key = MailReceipts.TrackedSinceKey,
            ValueJson = System.Text.Json.JsonSerializer.Serialize(now),
            UpdatedAt = now,
        };
        if (json is null)
        {
            db.AppSettings.Add(row);
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateException)
            {
                // Another request created it first: use theirs.
                db.Entry(row).State = EntityState.Detached;
                var again = await db.AppSettings.AsNoTracking()
                    .Where(s => s.Key == MailReceipts.TrackedSinceKey).Select(s => s.ValueJson).FirstOrDefaultAsync(ct);
                if (again is not null && TryParseInstant(again) is { } other) return other;
            }
        }
        return now;
    }

    private static DateTime? TryParseInstant(string json)
    {
        try
        {
            var value = System.Text.Json.JsonSerializer.Deserialize<DateTime>(json);
            return value.Kind switch
            {
                DateTimeKind.Utc => value,
                DateTimeKind.Local => value.ToUniversalTime(),
                _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
            };
        }
        catch (System.Text.Json.JsonException) { return null; }
    }
}
