using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.WebUtilities;

namespace IPop.Infrastructure.Links;

/// &lt;img&gt; can't send the Bearer header, so preview images are served from a URL that carries
/// its own HMAC-SHA256 signature over (image URL, expiry). The key is derived (HKDF) from an
/// existing server secret with a purpose label, so it is never the secret itself.
public sealed class LinkPreviewImageSigner
{
    public const string Path = "/api/link-preview/image";
    private readonly byte[] _key;
    private readonly TimeProvider _time;

    public LinkPreviewImageSigner(string serverSecret, TimeProvider? time = null)
    {
        if (string.IsNullOrEmpty(serverSecret)) throw new ArgumentException("A server secret is required.", nameof(serverSecret));
        _key = HKDF.DeriveKey(HashAlgorithmName.SHA256, Encoding.UTF8.GetBytes(serverSecret), 32,
            info: Encoding.UTF8.GetBytes("ipop.link-preview.image.v1"));
        _time = time ?? TimeProvider.System;
    }

    /// API-relative URL; the expiry is rounded up to the next hour so the same image keeps the
    /// same URL for up to an hour.
    public string CreateUrl(Uri image, TimeSpan lifetime)
    {
        var target = _time.GetUtcNow().Add(lifetime).ToUnixTimeSeconds();
        var exp = (target + 3599) / 3600 * 3600;
        var u = image.AbsoluteUri;
        return $"{Path}?u={Uri.EscapeDataString(u)}&exp={exp}&sig={Sign(u, exp)}";
    }

    public bool Verify(string? u, long exp, string? sig)
    {
        if (string.IsNullOrEmpty(u) || string.IsNullOrEmpty(sig)) return false;
        if (exp < _time.GetUtcNow().ToUnixTimeSeconds()) return false;
        byte[] given;
        try { given = WebEncoders.Base64UrlDecode(sig); }
        catch (FormatException) { return false; }
        var expected = Mac(u, exp);
        return given.Length == expected.Length && CryptographicOperations.FixedTimeEquals(given, expected);
    }

    internal string Sign(string u, long exp) => WebEncoders.Base64UrlEncode(Mac(u, exp));

    private byte[] Mac(string u, long exp) => HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes($"{exp}\n{u}"));
}
