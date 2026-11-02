using System.Globalization;
using System.Text.RegularExpressions;

namespace IPop.Infrastructure.Approval;

/// Fixed vocabulary between HC's workbook and the existing Directory tree (spec §8). The DB names
/// stay as they are; this only says which workbook word means which existing unit.
public static partial class TreeMapping
{
    /// Work Location → Site (Plant) code.
    public static readonly IReadOnlyDictionary<string, string> Sites = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["SJA-1"] = "SJA 1", ["SJA 1"] = "SJA 1",
        ["SJA-2"] = "SJA-KRW", ["SJA 2"] = "SJA-KRW",
        ["SJA-3"] = "SJA-SKD", ["SJA 3"] = "SJA-SKD",
        ["SJA-SEMARANG"] = "SJA-SMG", ["SJA SEMARANG"] = "SJA-SMG",
        ["SJA-JAKARTA"] = "SJA-JKT", ["SJA JAKARTA"] = "SJA-JKT",
    };

    /// Site code → infix used in unit codes for a non-home plant (Sepanjang is home: no infix).
    public static readonly IReadOnlyDictionary<string, string> SiteInfix = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["SJA-KRW"] = "KRW", ["SJA-SKD"] = "SKD", ["SJA-SMG"] = "SMG", ["SJA-JKT"] = "JKT",
    };

    /// Workbook Division → (top-level Department code, optional plant-line sub-Department name).
    public static (string TopCode, string? Line)? Division(string? division)
    {
        var d = Squash(division);
        if (d.Length == 0) return null;
        if (d.StartsWith("OPERATION", StringComparison.Ordinal))
            return ("OPS", d.EndsWith("POWDER") ? "Powder" : d.EndsWith("RTD") ? "RTD" : null);
        return d switch
        {
            "MARKETING" => ("MKT", null),
            "HUMAN CAPITAL" => ("HC", null),
            "DAFT" => ("DAFT", null),
            "PROCUREMENT" => ("PROC", null),
            "R & D" or "R&D" => ("RND", null),
            "ICT" => ("ICT", null),
            "S&OE" or "S & OE" => ("SOE", null),
            _ => null,
        };
    }

    /// Workbook words that mean an existing unit under a different name.
    public static readonly IReadOnlyDictionary<string, string> Aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["QA"] = "Quality Assurance",
        ["OE"] = "Operational Excellence",
        ["S&PM"] = "Strategy & Project Management Office",
        ["SECURITY & NETWORK INFRASTURCTURE"] = "Network & Infrastructure",
        ["SECURITY & NETWORK INFRASTRUCTURE"] = "Network & Infrastructure",
        ["INFORMATION SYSTEM DEVELOPMENT"] = "Application & Information System",
        ["RAW MATERIAL PROCUREMENT"] = "Raw Material",
        ["FINANCE, TAX, ACCOUNTING"] = "Finance, Tax & Accounting",
        ["FINANCE. TAX. ACCOUNTING"] = "Finance, Tax & Accounting",
        ["HC COMPENSATION & BENEFIT"] = "Compensation & Benefit",
        ["TRANSPORT OPERATION"] = "Transport Operational",
    };

    /// A workbook Department that is a person's role rather than a unit: they sit on the top Division.
    public static bool IsRoleDepartment(string? department, string? division)
    {
        var d = Squash(department);
        return d.Length == 0 || d is "PLANT MANAGER" or "SECRETARY" or "MANAGEMENT TRAINEE" or "MARKETING"
            || d.EndsWith("GENERAL MANAGER") || PomRx().IsMatch(d) || d == Squash(division);
    }

    /// Generic role buckets that were used as units before; emptied ones get deactivated.
    public static bool IsGenericUnit(string name) =>
        Squash(name) is "HEAD" or "STAFF" or "STAFFS" or "COORD" or "COORDINATOR" or "SUPERVISOR";

    /// Drops a plant/area suffix the Plant already says ("HSE AREA - SEMARANG" → "HSE AREA").
    public static string StripLocation(string bagian)
    {
        var s = LocationSuffixRx().Replace(bagian.Trim(), "");
        return s.Length == 0 ? bagian.Trim() : s;
    }

    [GeneratedRegex(@"^POM \d+$")] private static partial Regex PomRx();
    [GeneratedRegex(@"\s*-\s*(SJA\s+)?(SEPANJANG|KARAWANG|SEMARANG|SUKODONO|JAKARTA|PUSAT)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex LocationSuffixRx();
    [GeneratedRegex(@"\s+")] private static partial Regex SpacesRx();
    [GeneratedRegex(@"[^A-Z0-9]+")] private static partial Regex NonAlnumRx();

    public static string Squash(string? s) => SpacesRx().Replace(s ?? "", " ").Trim().ToUpperInvariant();

    /// Comparable key: case, spacing, "&"/"AND" and punctuation don't matter.
    public static string Key(string? s) => NonAlnumRx().Replace(Squash(s).Replace(" AND ", " & ").Replace("&", "AND"), "");

    private static readonly HashSet<string> KeepUpper = new(StringComparer.OrdinalIgnoreCase)
    {
        "QC", "QA", "HC", "HCBP", "HSE", "ICT", "RTD", "R&G", "UPBM", "FGW", "RMW", "PMW", "RPW", "RMPW", "PGW", "PPIC",
        "OBD", "KAG", "OE", "S&PM", "S&OE", "MT", "GA", "PSW", "UHT", "AB", "CD", "IJ", "FGH", "GHIJ", "E", "F", "LP", "R&D", "BTL", "NPD",
    };

    /// "PACKING (3 IN 1 INSTANT)" → "Packing (3 in 1 Instant)"; acronyms stay upper.
    public static string Title(string raw)
    {
        var ti = CultureInfo.InvariantCulture.TextInfo;
        var words = SpacesRx().Replace(raw.Trim(), " ").Split(' ');
        return string.Join(' ', words.Select((w, i) =>
        {
            var core = w.Trim('(', ')');
            if (KeepUpper.Contains(core)) return w.ToUpperInvariant();
            if (i > 0 && core.ToLowerInvariant() is "in" or "of" or "and" or "the") return w.ToLowerInvariant();
            return ti.ToTitleCase(w.ToLowerInvariant());
        }));
    }

    /// Code fragment in house style: one word → first 4 letters, several → initials (max 4).
    public static string Abbr(string name)
    {
        var words = NonAlnumRx().Replace(Squash(name), " ").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return "UNIT";
        if (words.Length == 1) return words[0].Length <= 4 ? words[0] : words[0][..4];
        return string.Concat(words.Take(4).Select(w => w.All(char.IsAsciiDigit) ? w : w[..1]));
    }
}
