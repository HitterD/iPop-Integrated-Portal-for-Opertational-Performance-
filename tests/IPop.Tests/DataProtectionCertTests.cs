using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IPop.Tests;

/// F2 (2026-11 audit): with a certificate configured the key ring is stored encrypted, so a database dump alone cannot
/// open users' NAS passwords. Same calls Program.cs makes (ProtectKeysWithCertificate + UnprotectKeysWithAnyCertificate).
public class DataProtectionCertTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ipop-dp-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static X509Certificate2 Cert()
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest("CN=iPop DataProtection test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var c = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        return X509CertificateLoader.LoadPkcs12(c.Export(X509ContentType.Pfx, "pw"), "pw", X509KeyStorageFlags.EphemeralKeySet);
    }

    private IDataProtectionProvider Provider(X509Certificate2? cert)
    {
        var dp = new ServiceCollection().AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(_dir)).SetApplicationName("ipop-test");
        if (cert is not null) dp.ProtectKeysWithCertificate(cert).UnprotectKeysWithAnyCertificate(cert);
        return dp.Services.BuildServiceProvider().GetRequiredService<IDataProtectionProvider>();
    }

    [Fact]
    public void New_keys_are_stored_encrypted_and_still_open_the_data()
    {
        using var cert = Cert();
        var secret = Provider(cert).CreateProtector("nas").Protect("hunter2");

        var xml = File.ReadAllText(Directory.GetFiles(_dir, "key-*.xml").Single());
        Assert.Contains("encryptedSecret", xml);
        Assert.DoesNotContain("<masterKey", xml.Replace("<masterKey p4:requiresEncryption", ""));   // no plaintext key
        Assert.Equal("hunter2", Provider(cert).CreateProtector("nas").Unprotect(secret));
    }
}
