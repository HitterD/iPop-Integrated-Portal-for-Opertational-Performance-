namespace IPop.Application;

/// HRIS names arrive in capitals ("GO SONNY GOZALI", "M. KHUBAN MUSAFA"). These helpers only
/// shape what is shown; the stored name is never changed.
public static class PersonName
{
    /// Title Case for display: "GO SONNY GOZALI" → "Go Sonny Gozali", "M. AL QODIM" → "M. Al Qodim".
    /// Names already typed in mixed case are left alone (someone chose "McDonald" on purpose).
    public static string Display(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";
        var words = name.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (name.Any(char.IsLower)) return string.Join(' ', words);
        return string.Join(' ', words.Select(TitleWord));
    }

    /// The name for tight spots (chips, previews, "X mentioned you"): the first two words,
    /// after a leading abbreviated prefix like "M." / "MOH." / "R." — so "GO SONNY GOZALI"
    /// becomes "Go Sonny" rather than "Go", and "M. KHUBAN MUSAFA" becomes "Khuban Musafa".
    public static string Short(string? name)
    {
        var words = Display(name).Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        while (words.Count > 1 && words[0].EndsWith('.')) words.RemoveAt(0);
        return string.Join(' ', words.Take(2));
    }

    private static string TitleWord(string w)
    {
        // Vowel-less initials ("KD", "RM") stay as written.
        if (w.Length <= 3 && !w.Any(c => "AEIOUY".Contains(c))) return w;
        var chars = w.ToLowerInvariant().ToCharArray();
        // Capitalise after the start and after - ' . so "SITI-AISYAH" and "O'NEIL" read right.
        for (var i = 0; i < chars.Length; i++)
            if (i == 0 || chars[i - 1] is '-' or '\'' or '.')
                chars[i] = char.ToUpperInvariant(chars[i]);
        return new string(chars);
    }
}
