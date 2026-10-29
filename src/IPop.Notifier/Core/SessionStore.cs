using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace IPop.Notifier.Core;

/// The signed-in session on this PC: server, refresh token and a random device key. Encrypted with Windows DPAPI
/// for the current Windows user only (%LOCALAPPDATA%\SantosJayaAbadi\iPopNotifier\session.bin), so another account on the same PC,
/// or a copied file, can't use it. The refresh token slides 90 days on every use (iPop AuthWorkflow).
public sealed class SessionStore
{
    public sealed record Session(string Server, string RefreshToken, string DeviceKey, string? Nik, string? Name);

    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("iPop.Notifier.session.v1");
    public static string Folder =>
#if DEBUG
        // QA only (Debug builds): a throwaway profile so a test run never touches the real signed-in session.
        Environment.GetEnvironmentVariable("IPOP_NOTIFIER_QA_DIR") is { Length: > 0 } qa ? qa :
#endif
        // Not %LOCALAPPDATA%\iPopNotifier: that is the Velopack install folder, replaced on update and removed on uninstall.
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SantosJayaAbadi", "iPopNotifier" + AppInfo.Flavour);
    private static string FilePath => Path.Combine(Folder, "session.bin");
    private static string KeyPath => Path.Combine(Folder, "device.key");
    private static string LastPath => Path.Combine(Folder, "last-account.bin");

    /// Who this PC's Notifier was last signed in as (kept after sign-out, DPAPI like the session): a browser sign-in
    /// as anyone else asks "Switch iPop Notifier to X?" first, even from the signed-out sign-in window.
    public sealed record LastSignedIn(string Server, string Nik);

    public LastSignedIn? LastAccount()
    {
        try
        {
            if (!File.Exists(LastPath)) return null;
            return JsonSerializer.Deserialize<LastSignedIn>(ProtectedData.Unprotect(File.ReadAllBytes(LastPath), Entropy, DataProtectionScope.CurrentUser));
        }
        catch { return null; }
    }

    public Session? Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return null;
            var plain = ProtectedData.Unprotect(File.ReadAllBytes(FilePath), Entropy, DataProtectionScope.CurrentUser);
            return JsonSerializer.Deserialize<Session>(plain);
        }
        catch { return null; } // unreadable (other user / corrupted) = signed out
    }

    public void Save(Session s)
    {
        Directory.CreateDirectory(Folder);
        var cipher = ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(s), Entropy, DataProtectionScope.CurrentUser);
        var tmp = FilePath + ".tmp";
        File.WriteAllBytes(tmp, cipher);
        File.Move(tmp, FilePath, overwrite: true);
        if (s.Nik is { Length: > 0 } nik)
            try { File.WriteAllBytes(LastPath, ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(new LastSignedIn(s.Server, nik)), Entropy, DataProtectionScope.CurrentUser)); } catch { }
    }

    public void Clear()
    {
        try { File.Delete(FilePath); } catch { }
    }

    /// Stable per Windows profile; reported to the server so ICT sees one row per PC.
    public string DeviceKey()
    {
        Directory.CreateDirectory(Folder);
        if (File.Exists(KeyPath)) { var k = File.ReadAllText(KeyPath).Trim(); if (k.Length >= 8) return k; }
        var key = "pc-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();
        File.WriteAllText(KeyPath, key);
        return key;
    }
}
