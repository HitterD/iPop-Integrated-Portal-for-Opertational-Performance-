using System.Globalization;

namespace IPop.Web.Services;

/// The collapsed recipients line (spec 2026-10-14 §4, QA 3). Each line is a list of whole items — "you", names,
/// group chips — of which the browser shows as many as fit (js/mail-fit.js), then "+N" for the people behind the
/// rest. Pure, so the rules are tested without a browser. NIK is only compared, never shown.
public static class MailRecipientLine
{
    /// One name or group chip. Count = people it stands for (1 for a name). Title = the full, unaltered name.
    public sealed record Item(string Text, string Title, int Count, bool Chip);

    /// Items in display order; Default = how many show before the browser has measured (and without script).
    /// Unlisted = people on the mail that no item stands for (the detail lists at most 50 names).
    public sealed record Line(string Field, string Label, IReadOnlyList<Item> Items, int Default, int Unlisted)
    {
        public int MoreAfter(int shown) => Items.Skip(shown).Sum(i => i.Count) + Unlisted;
    }

    /// Names/More: the first two items and the count of the rest. Narrow/NarrowMore: one item, for a phone row.
    public sealed record Short(string Names, int More, string Narrow, int NarrowMore);

    private const int DefaultNames = 3, DefaultGroups = 2, ThreadItems = 2;

    private static bool Eq(string? a, string? b) => a is not null && b is not null && a.Equals(b, StringComparison.OrdinalIgnoreCase);

    public static List<Line> Lines(MailDetail d, string? myNik)
    {
        var isSender = Eq(d.SenderNik, myNik);
        var lines = new List<Line>();
        var listed = d.Recipients.Count(r => r.RecipientType is "to" or "cc" or "bcc");
        var inGroups = d.RecipientGroups.Where(g => g.Field != "bcc" || isSender).Sum(g => g.Count);
        var unlisted = Math.Max(0, d.RecipientTotal - listed - inGroups);
        foreach (var (field, label) in new[] { ("to", "To"), ("cc", "Cc"), ("bcc", "Bcc") })
        {
            if (field == "bcc" && !isSender) continue;
            var names = d.Recipients.Where(r => r.RecipientType == field).OrderByDescending(r => Eq(r.Nik, myNik))
                .Select(r => Eq(r.Nik, myNik) ? new Item("you", "you", 1, false) : new Item(GroupTitle(r.Name), r.Name, 1, false)).ToList();
            var groups = d.RecipientGroups.Where(g => g.Field == field)
                .Select(g => new Item(GroupTitle(g.Name), $"{g.Name}: {g.Count.ToString("N0", CultureInfo.InvariantCulture)} people", g.Count, true)).ToList();
            var extra = field == "to" ? unlisted : 0;
            if (names.Count == 0 && groups.Count == 0 && extra == 0) continue;
            // A few names first, then the group chips, then the remaining names (mockup: "you, A, B, [chip] +38").
            var first = names.Take(DefaultNames).ToList();
            var items = first.Concat(groups).Concat(names.Skip(DefaultNames)).ToList();
            var dflt = Math.Min(items.Count, first.Count + Math.Min(groups.Count, DefaultGroups));
            lines.Add(new Line(field, label, items, dflt, extra));
        }
        return lines;
    }

    /// The open thread item's "to …" line: the first two items, then a count that never sits inside the ellipsis.
    public static Short ThreadTo(MailDetail d, string? myNik)
    {
        if (d.HideRecipients && !Eq(d.SenderNik, myNik)) return new("undisclosed recipients", 0, "undisclosed recipients", 0);
        var lines = Lines(d, myNik).Where(l => l.Field != "bcc").ToList();
        var items = lines.SelectMany(l => l.Items).ToList();
        if (items.Count == 0)
        {
            var unlisted = lines.Sum(l => l.Unlisted);
            var label = unlisted > 0 ? $"{unlisted.ToString("N0", CultureInfo.InvariantCulture)} people" : "—";
            return new(label, 0, label, 0);
        }
        var shown = items.Take(ThreadItems).ToList();
        var more = items.Skip(ThreadItems).Sum(i => i.Count) + lines.Sum(l => l.Unlisted);
        var unlistedAll = lines.Sum(l => l.Unlisted);
        return new(string.Join(", ", shown.Select(i => i.Text)), more, items[0].Text, items.Skip(1).Sum(i => i.Count) + unlistedAll);
    }

    /// One part of the compact recipients line under the sender ("To you, Kevin Austin Widjaja · Cc Alphin Santoso +3").
    public sealed record Part(string Label, string Names, int More);

    private const int CompactTo = 2, CompactCc = 1;

    /// The reading pane's one-line recipients: To with two items, Cc with one, each followed by a count of the rest.
    /// Bcc is left to the Details table (sender only). Hidden lists read "undisclosed recipients".
    public static List<Part> Compact(MailDetail d, string? myNik)
    {
        if (d.HideRecipients && !Eq(d.SenderNik, myNik)) return [new("To", "undisclosed recipients", 0)];
        var parts = new List<Part>();
        foreach (var line in Lines(d, myNik).Where(l => l.Field != "bcc"))
        {
            var n = Math.Min(line.Items.Count, line.Field == "to" ? CompactTo : CompactCc);
            var names = string.Join(", ", line.Items.Take(n).Select(i => i.Text));
            var more = line.MoreAfter(n);
            if (names.Length == 0 && more == 0) continue;
            if (names.Length == 0) { names = $"{more.ToString("N0", CultureInfo.InvariantCulture)} people"; more = 0; }
            parts.Add(new(line.Label, names, more));
        }
        return parts;
    }

    /// The sender's address for the hover hint, or null when unknown or when it would show the employee id.
    public static string? SenderEmail(MailDetail d)
    {
        var from = d.Recipients.FirstOrDefault(r => r.RecipientType == "from" && Eq(r.Nik, d.SenderNik));
        var email = from?.Email?.Trim();
        if (string.IsNullOrEmpty(email) || !email.Contains('@')) return null;
        var local = email[..email.IndexOf('@')];
        if (!string.IsNullOrEmpty(d.SenderNik) && local.Contains(d.SenderNik, StringComparison.OrdinalIgnoreCase)) return null;
        return email;
    }

    // Short all-caps words that are acronyms in this company's group and department names.
    private static readonly HashSet<string> Acronyms = new(StringComparer.Ordinal)
    {
        "SJA", "PT", "ICT", "IT", "HC", "HR", "HCBP", "HRD", "DAFT", "QA", "QC", "PPIC", "POM", "SMG", "SPJ", "BOD", "SMO", "MKT",
        "GA", "HSE", "KPI", "CEO", "CFO", "COO", "TBK", "R&D", "SGM", "EES", "CA", "SAP", "ERP", "NAS", "PIC", "SPV", "MGR",
    };

    /// "SJA JAKARTA" → "SJA Jakarta", "PLANT-UTILITY · SJA SEMARANG" → "Plant-Utility · SJA Semarang",
    /// "RIKKI RINALDO" → "Rikki Rinaldo". Parts already in mixed case are kept as typed; known acronyms and
    /// tokens with a dot (titles like "S.KOM") stay upper case.
    public static string GroupTitle(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var parts = raw.Split(" · ");
        for (var i = 0; i < parts.Length; i++)
        {
            var p = parts[i];
            if (p.Any(char.IsLower)) continue;
            parts[i] = string.Join(' ', p.Split(' ').Select(Word));
        }
        return string.Join(" · ", parts);
    }

    private static string Word(string w)
    {
        if (w.Length == 0 || !w.Any(char.IsLetter)) return w;
        var core = w.TrimEnd(',', ';', ':');
        if (Acronyms.Contains(core) || core.Contains('.')) return w;
        return string.Join('-', w.Split('-').Select(h => h.Length == 0 ? h
            : Acronyms.Contains(h.TrimEnd(',', ';', ':')) ? h
            : char.ToUpperInvariant(h[0]) + h[1..].ToLowerInvariant()));
    }
}
