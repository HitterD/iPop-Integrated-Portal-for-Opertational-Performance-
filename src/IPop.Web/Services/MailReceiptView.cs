using System.Globalization;

namespace IPop.Web.Services;

/// What the reading pane shows for GET /api/mail/{id}/receipts (spec 2026-10-14 §1/§2). Pure, so the rules are
/// tested without rendering. Names are Title Cased; NIK never appears (the API has none).
public static class MailReceiptView
{
    public enum Kind { None, Group, Single, CountsOnly, External, NotTracked }

    public sealed record Summary(Kind Kind, string Text, bool CanOpen, bool AllRead, IReadOnlyList<MailReceiptPerson> Faces, int Percent);

    public sealed record GroupView(MailReceiptGroup Group, string Caption, IReadOnlyList<MailReceiptPerson> Members);

    public sealed record ListView(IReadOnlyList<MailReceiptPerson> People, IReadOnlyList<GroupView> Groups);

    private static readonly CultureInfo Num = CultureInfo.InvariantCulture;

    public static Summary Summarise(MailReceipts? rc)
    {
        if (rc is null) return new(Kind.None, "", false, false, [], 0);
        if (rc.Total == 0 && rc.External.Count > 0)
            return new(Kind.External, "Read status not available · external address", false, false, [], 0);
        if (rc.Total == 0) return new(Kind.None, "", false, false, [], 0);
        if (!rc.Tracked)
        {
            var since = rc.TrackedSince is { } s ? $" before {s.ToLocalTime():d MMM}" : "";
            return new(Kind.NotTracked, $"Read status not tracked{since}", false, false, [], 0);
        }
        var all = rc.Read >= rc.Total;
        var pct = (int)Math.Round(rc.Read * 100.0 / rc.Total);
        if (!rc.NamesVisible)
            return new(Kind.CountsOnly, $"Read by {rc.Read.ToString("N0", Num)} of {rc.Total.ToString("N0", Num)}", true, all, [], pct);
        if (rc.Total == 1 && rc.People.Count == 1)
        {
            var p = rc.People[0];
            return p.SeenAt is { } at
                ? new(Kind.Single, $"Read {ShortTime(at)}", false, true, [], 100)
                : new(Kind.Single, p.Delivered ? "Delivered" : "Sent", false, false, [], 0);
        }
        var faces = rc.People.Where(p => p.SeenAt is not null).OrderBy(p => p.SeenAt).Take(4).ToList();
        return new(Kind.Group, $"Read by {rc.Read.ToString("N0", Num)} of {rc.Total.ToString("N0", Num)}", true, all, faces, pct);
    }

    /// Collapsed thread item: "all" (green ✓✓), "part" (grey ✓✓), "none" (✓), or null (nothing to show).
    public static string? Tick(MailReceipts? rc)
    {
        if (rc is null || !rc.Tracked || rc.Total == 0) return null;
        return rc.Read >= rc.Total ? "all" : rc.Read > 0 ? "part" : "none";
    }

    /// The Read by / Not yet tab: people picked by name (and group members no longer in their group), then each
    /// group with its members in that state. A search matches name, department or site, or the group's name.
    public static ListView Lists(MailReceipts rc, bool readTab, string? query)
    {
        var q = query?.Trim();
        bool InTab(MailReceiptPerson p) => (p.SeenAt is not null) == readTab;
        bool Hit(MailReceiptPerson p) => string.IsNullOrEmpty(q)
            || Has(p.Name, q) || Has(p.Department, q) || Has(p.Site, q);

        var grouped = rc.Groups.SelectMany(g => g.MemberKeys).ToHashSet();
        var people = rc.People
            .Where(p => !p.ViaGroup || !grouped.Contains(p.UserKey))
            .Where(p => InTab(p) && Hit(p))
            .OrderByDescending(p => p.IsMe)
            .ThenBy(p => readTab ? p.SeenAt : null)
            .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var byKey = rc.People.GroupBy(p => p.UserKey).ToDictionary(g => g.Key, g => g.First());
        var groups = new List<GroupView>();
        foreach (var g in rc.Groups)
        {
            var nameHit = !string.IsNullOrEmpty(q) && Has(g.Name, q);
            var members = g.MemberKeys.Where(byKey.ContainsKey).Select(k => byKey[k])
                .Where(p => InTab(p) && (nameHit || Hit(p)))
                .OrderBy(p => readTab ? p.SeenAt : null).ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (members.Count == 0) continue;
            groups.Add(new GroupView(g, $"{g.Read.ToString("N0", Num)} of {g.Total.ToString("N0", Num)} read", members));
        }
        return new ListView(people, groups);
    }

    public static string DisplayName(MailReceiptPerson p) => p.IsMe ? $"{TitleCase(p.Name)} (you)" : TitleCase(p.Name);

    public static string Place(MailReceiptPerson p)
        => string.Join(" · ", new[] { p.Department, p.Site }.Where(s => !string.IsNullOrWhiteSpace(s)));

    public static string ReadStamp(DateTime at) => $"Read · {at.ToLocalTime():ddd d MMM, HH:mm}";

    public static string ShortTime(DateTime at)
    {
        var local = at.ToLocalTime();
        return local.Date == DateTime.Today ? local.ToString("HH:mm") : local.ToString("d MMM, HH:mm");
    }

    /// "RM BAGASTYO ANGGORO" → "Rm Bagastyo Anggoro". Already mixed-case names are kept as typed.
    public static string TitleCase(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";
        var mixed = name.Any(char.IsLower) && name.Any(char.IsUpper);
        return mixed ? name : CultureInfo.InvariantCulture.TextInfo.ToTitleCase(name.ToLowerInvariant());
    }

    private static bool Has(string? s, string q) => s?.Contains(q, StringComparison.OrdinalIgnoreCase) == true;
}

/// EPop archive mail is the importer's "Arsip EPop" label (.scratch/epop/import_epop.py). List rows show an
/// archive icon instead of the label chip; the sidebar label stays.
public static class MailEpop
{
    public const string Label = "Arsip EPop";

    public static bool IsImported(IEnumerable<string>? labels) => labels?.Contains(Label, StringComparer.OrdinalIgnoreCase) == true;

    public static IEnumerable<string> RowLabels(IEnumerable<string>? labels)
        => labels?.Where(l => !l.Equals(Label, StringComparison.OrdinalIgnoreCase)) ?? [];
}

/// Per-message "Original formatting" switch, remembered for the session (spec §5).
public sealed class MailReadingPrefs
{
    private readonly HashSet<Guid> original = [];
    public bool ShowsOriginal(Guid mailId) => original.Contains(mailId);
    public void SetOriginal(Guid mailId, bool on) { if (on) original.Add(mailId); else original.Remove(mailId); }
}
