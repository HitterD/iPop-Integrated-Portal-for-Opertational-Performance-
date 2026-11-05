using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Configuration;

namespace IPop.Infrastructure.Files;

/// <summary>
/// TLS trust for the Synology NAS. None of them carries a certificate a normal chain check accepts (the SJA NAS
/// show a Let's Encrypt certificate that expired in 2019, the plants Synology's own self-signed one), so the API
/// used to accept ANY certificate - anyone on a plant link could pose as the NAS and collect the passwords sent to it.
/// Now a certificate is accepted when it passes the normal chain check, or when its public key matches a pin
/// configured for that host (Synology:CertificatePins:{host} = base64 SHA-256 of the SubjectPublicKeyInfo, several
/// separated by ';' for a planned key change). Unpinned hosts with an invalid chain are refused.
/// Get a pin: openssl s_client -connect HOST:PORT &lt;/dev/null | openssl x509 -pubkey -noout
///   | openssl pkey -pubin -outform der | openssl dgst -sha256 -binary | openssl base64
/// </summary>
public sealed class NasCertificatePins
{
    private readonly Dictionary<string, HashSet<string>> _pins = new(StringComparer.OrdinalIgnoreCase);

    public NasCertificatePins(IConfiguration cfg)
    {
        foreach (var s in cfg.GetSection("Synology:CertificatePins").GetChildren())
        {
            var host = s.Key.Replace('_', '.');   // env vars cannot carry dots: Synology__CertificatePins__10_12_2_6
            var set = (s.Value ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet();
            if (set.Count > 0) _pins[host] = set;
        }
    }

    public int Count => _pins.Count;

    public static string PinOf(X509Certificate2 cert) =>
        Convert.ToBase64String(SHA256.HashData(cert.PublicKey.ExportSubjectPublicKeyInfo()));

    public bool Accept(string host, X509Certificate2? cert, SslPolicyErrors errors)
    {
        if (cert is null) return false;
        if (!string.IsNullOrEmpty(host) && _pins.TryGetValue(host, out var pins)) return pins.Contains(PinOf(cert));
        // The NAS are addressed by IP, and .NET leaves SslStream.TargetHostName empty for an IP (no SNI), so the host
        // is often unknown here: then the key must be one of the configured NAS keys (any host), or the chain valid.
        if (string.IsNullOrEmpty(host) && _pins.Count > 0) return _all.Value.Contains(PinOf(cert)) || errors == SslPolicyErrors.None;
        return errors == SslPolicyErrors.None;
    }

    private Lazy<HashSet<string>> _all => new(() => _pins.Values.SelectMany(v => v).ToHashSet());
}
