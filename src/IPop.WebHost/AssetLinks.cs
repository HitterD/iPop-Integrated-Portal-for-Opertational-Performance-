using System.Text.Json;

namespace IPop.WebHost;

/// /.well-known/assetlinks.json for the Android TWA (android/twa). Lets the APK open
/// https://ipop.santos.co.id full screen without a URL bar. Fingerprints are public (SHA-256 of the
/// release signing certificate); the keystore itself never lives in the repo.
/// Config: AndroidApp:PackageName (default id.co.santos.ipop), AndroidApp:Sha256CertFingerprints (string
/// array; empty until the release keystore exists, see android/twa/README.md).
public static class AssetLinks
{
    public const string DefaultPackage = "id.co.santos.ipop";
    public const string DefaultApkPath = "C:/ipop-ops/android/ipop.apk";

    private static bool IsFingerprint(string? s)
        => s is { Length: 95 } && s.Split(':').All(p => p.Length == 2 && p.All(Uri.IsHexDigit));

    public static string Build(IConfiguration config)
    {
        var package = config["AndroidApp:PackageName"] ?? DefaultPackage;
        var prints = config.GetSection("AndroidApp:Sha256CertFingerprints").GetChildren()
            .Select(c => c.Value?.Trim().ToUpperInvariant()).Where(IsFingerprint).Distinct().ToArray();
        if (string.IsNullOrWhiteSpace(package) || prints.Length == 0) return "[]";
        return JsonSerializer.Serialize(new[]
        {
            new
            {
                relation = new[] { "delegate_permission/common.handle_all_urls" },
                target = new { @namespace = "android_app", package_name = package, sha256_cert_fingerprints = prints },
            },
        });
    }
}
