using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using IPop.Infrastructure.Files;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace IPop.Tests;

/// NAS TLS trust: pinned hosts accept only their own key (even with an invalid chain), unpinned hosts need a valid
/// chain, and every configured NAS server has a pin (otherwise that NAS would stop working).
public class NasCertificatePinTests
{
    private static X509Certificate2 SelfSigned(string cn)
    {
        using var key = RSA.Create(2048);
        var req = new CertificateRequest($"CN={cn}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
    }

    private static NasCertificatePins Pins(params (string Host, string Pin)[] pins) => new(new ConfigurationBuilder()
        .AddInMemoryCollection(pins.Select(p => new KeyValuePair<string, string?>($"Synology:CertificatePins:{p.Host}", p.Pin))).Build());

    [Fact]
    public void Pinned_host_accepts_its_key_despite_chain_errors_and_refuses_any_other()
    {
        using var nas = SelfSigned("synology"); using var attacker = SelfSigned("synology");
        var pins = Pins(("10.12.2.6", NasCertificatePins.PinOf(nas)));
        Assert.True(pins.Accept("10.12.2.6", nas, SslPolicyErrors.RemoteCertificateChainErrors | SslPolicyErrors.RemoteCertificateNameMismatch));
        Assert.False(pins.Accept("10.12.2.6", attacker, SslPolicyErrors.RemoteCertificateChainErrors));
        Assert.False(pins.Accept("10.12.2.6", attacker, SslPolicyErrors.None));   // pinned = the pin decides
    }

    [Fact]
    public void Unpinned_host_needs_a_valid_chain()
    {
        using var c = SelfSigned("other");
        var pins = Pins();
        Assert.False(pins.Accept("10.99.0.1", c, SslPolicyErrors.RemoteCertificateChainErrors));
        Assert.True(pins.Accept("10.99.0.1", c, SslPolicyErrors.None));
    }

    [Fact]
    public void Env_var_style_host_keys_are_understood()
    {
        using var c = SelfSigned("synology");
        Assert.True(Pins(("10_14_2_16", NasCertificatePins.PinOf(c))).Accept("10.14.2.16", c, SslPolicyErrors.RemoteCertificateChainErrors));
    }

    [Fact]
    public void Every_configured_nas_has_a_pin()
    {
        var api = Path.Combine(NotifierLinkTests.RepoRoot(), "src", "IPop.Api");
        var cfg = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(api, "appsettings.json"))
            .AddJsonFile(Path.Combine(api, "appsettings.Development.json"), optional: true)
            .Build();
        var pinned = cfg.GetSection("Synology:CertificatePins").GetChildren().Select(c => c.Key).ToHashSet();
        var hosts = cfg.GetSection("Synology:Servers").GetChildren().Select(s => new Uri(s.Value!).Host).ToList();
        Assert.NotEmpty(hosts);
        Assert.All(hosts, h => Assert.Contains(h, pinned));
    }
}
