namespace IPop.Application.Approval;

/// NIKs as typed or as Excel mangles them. All-digit NIKs are padded to 8 ("277" → "00000277")
/// unless that exact NIK already exists in iPop (a few real NIKs are 7 digits); placeholders
/// (-, None, any Excel error like #N/A or #REF!, blank) and resigned markers ("ex_…") mean "no NIK".
public static class WorkbookNik
{
    public static string? Normalize(string? raw, ISet<string>? known = null)
    {
        if (raw is null) return null;
        var s = raw.Trim().Trim('\t').Trim();
        if (s.Length == 0 || s == "-" || s == "0" || s.Equals("None", StringComparison.OrdinalIgnoreCase)
            || s.StartsWith('#') || s.StartsWith("ex_", StringComparison.OrdinalIgnoreCase))
            return null;
        if (s.EndsWith(".0") && s[..^2].All(char.IsAsciiDigit)) s = s[..^2];   // numeric cell read as double
        if (known is not null && known.Contains(s)) return s;
        return s.All(char.IsAsciiDigit) && s.Length < 8 ? s.PadLeft(8, '0') : s;
    }
}
