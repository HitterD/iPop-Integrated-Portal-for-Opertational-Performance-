using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace IPop.Notifier.Core;

/// The signed-in session on this PC: server, refresh token and a random device key. Encrypted with Windows DPAPI
/// for the current Windows user only (%LOCALAPPDATA%\iPopNotifier\session.bin), so another account on the same PC,
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
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "iPopNotifier");
    private static string FilePath => Path.Combine(Folder, "session.bin");
    private static string KeyPath => Path.Combine(Folder, "device.key");

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
