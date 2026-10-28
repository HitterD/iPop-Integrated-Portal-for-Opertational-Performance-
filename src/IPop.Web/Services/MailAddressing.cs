namespace IPop.Web.Services;

/// Who a reply goes to, and the header's To / Cc names. One place for the rules so the thread's
/// per-message buttons, the toolbar, the keyboard shortcuts and Quick reply all agree.
public static class MailAddressing
{
    public sealed record ReplyPlan(List<MemberSearchRow> To, List<MemberSearchRow> Cc, string? Notice);

    public static string ToNames(MailDetail d)
        => string.Join(", ", d.Recipients.Where(r => r.RecipientType == "to").Select(r => r.Name));

    public static string CcNames(MailDetail d)
        => string.Join(", ", d.Recipients.Where(r => r.RecipientType == "cc").Select(r => r.Name));

    public static string ReplySubject(string subject)
        => subject.StartsWith("Re:", StringComparison.OrdinalIgnoreCase) ? subject : $"Re: {subject}";

    /// Like Outlook: replying to a mail I sent answers the people I sent it to, never myself.
    /// A sender without an active account (left, EPop archive) can't receive mail: To stays empty
    /// and Notice says why, using their name only.
    public static ReplyPlan ForReply(MailDetail d, string? myNik, bool all)
    {
        var me = myNik ?? "";
        bool IsMe(string nik) => nik.Equals(me, StringComparison.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { me };
        MemberSearchRow Row(string nik, string name) => new(nik, name, null, null);
        IEnumerable<MailRecipient> Of(string type) => d.Recipients.Where(r => r.RecipientType == type && !IsMe(r.Nik));

        var to = new List<MemberSearchRow>();
        var cc = new List<MemberSearchRow>();
        string? notice = null;

        if (IsMe(d.SenderNik))
        {
            var toPeople = Of("to").ToList();
            // Sent only as Cc: those are the people the mail went to.
            var primary = toPeople.Count > 0 ? toPeople : Of("cc").ToList();
            foreach (var r in primary) if (seen.Add(r.Nik)) to.Add(Row(r.Nik, r.Name));
            if (all && toPeople.Count > 0)
                foreach (var r in Of("cc")) if (seen.Add(r.Nik)) cc.Add(Row(r.Nik, r.Name));
            return new ReplyPlan(to, cc, to.Count == 0 ? "This mail has no other recipients to reply to." : null);
        }

        if (d.SenderIsActive && seen.Add(d.SenderNik)) to.Add(Row(d.SenderNik, d.SenderName));
        else if (!d.SenderIsActive)
        {
            seen.Add(d.SenderNik);
            notice = $"{d.SenderName} doesn't have an active iPop account, so a reply can't reach them. Add who should get it.";
        }
        if (all && d.CanReplyAll)
        {
            foreach (var r in Of("to")) if (seen.Add(r.Nik)) to.Add(Row(r.Nik, r.Name));
            foreach (var r in Of("cc")) if (seen.Add(r.Nik)) cc.Add(Row(r.Nik, r.Name));
        }
        return new ReplyPlan(to, cc, notice);
    }

    /// Quick reply to exactly this message (not necessarily the one opened from the list).
    /// all: Reply all — everyone but me, Cc kept, never the Bcc; mass mail falls back to the sender.
    public static MailSendRequest QuickReply(MailDetail d, string text, string? myNik, bool all = false, IReadOnlyList<Guid>? attachmentIds = null)
    {
        var plan = ForReply(d, myNik, all);
        return new MailSendRequest(
            Subject: ReplySubject(d.Subject),
            Body: $"{TypedHtml(text)}{QuoteOf(d)}",
            To: plan.To.Select(r => r.Nik).ToList(),
            Cc: plan.Cc.Select(r => r.Nik).ToList(),
            Bcc: [],
            AttachmentIds: attachmentIds is { Count: > 0 } ? attachmentIds : null,
            InReplyToId: d.Id,
            ThreadRootId: d.ThreadRootId ?? d.Id);
    }

    /// Plain typed text as one HTML paragraph (line breaks kept).
    public static string TypedHtml(string text)
        => $"<p>{System.Net.WebUtility.HtmlEncode(text.Trim()).Replace("\r\n", "\n").Replace("\n", "<br/>")}</p>";

    /// Reply all only when it is allowed and would reach someone Reply doesn't.
    public static bool ReplyAllReachesMore(MailDetail d, string? myNik)
    {
        if (!d.CanReplyAll) return false;
        var one = ForReply(d, myNik, all: false);
        var all = ForReply(d, myNik, all: true);
        return all.To.Count + all.Cc.Count > one.To.Count + one.Cc.Count;
    }

    /// Who a reply goes to: "Jason Kristanto" or "Jason Kristanto, Kevin Austin Widjaja · Cc Alphin Santoso +3".
    public static string ReplyToLabel(MailDetail d, string? myNik, bool all)
    {
        var plan = ForReply(d, myNik, all);
        static string Names(List<MemberSearchRow> rows, int max)
            => string.Join(", ", rows.Take(max).Select(r => MailReceiptView.TitleCase(r.Name))) + (rows.Count > max ? $" +{rows.Count - max}" : "");
        if (plan.To.Count == 0)
            return d.SenderNik.Equals(myNik ?? "", StringComparison.OrdinalIgnoreCase) ? "no one yet (no other recipients)" : MailReceiptView.TitleCase(d.SenderName);
        var to = Names(plan.To, 2);
        return plan.Cc.Count == 0 ? to : $"{to} · Cc {Names(plan.Cc, 1)}";
    }

    /// A plain-text body as safe markup: every mail sink renders the body as HTML, so plain text
    /// (older out-of-office replies) must be encoded or an onerror handler in it would run.
    public static string PlainToHtml(string? text)
        => "<div style=\"white-space:pre-wrap\">" + System.Net.WebUtility.HtmlEncode(text ?? "") + "</div>";

    public static string QuoteOf(MailDetail d)
    {
        var stamp = d.SentAt?.ToLocalTime().ToString("ddd, dd MMM yyyy 'at' HH:mm") ?? "earlier";
        return $"""
            <details class="mail-quote">
              <summary class="mail-quote-toggle" title="Show quoted message">⋯</summary>
              <div class="mail-quote-inner">
                <div class="mail-quote-attr">On {stamp}, {System.Net.WebUtility.HtmlEncode(d.SenderName)} wrote:</div>
                <blockquote>{(d.IsHtml ? d.Body : PlainToHtml(d.Body))}</blockquote>
              </div>
            </details>
            """;
    }
}
