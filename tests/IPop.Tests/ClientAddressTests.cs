using System.Net;
using IPop.Api.Notifier;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace IPop.Tests;

public class ClientAddressTests
{
    private static HttpContext Request(string peer, string? realIp)
    {
        var http = new DefaultHttpContext();
        http.Connection.RemoteIpAddress = IPAddress.Parse(peer);
        if (realIp is not null) http.Request.Headers["X-Real-IP"] = realIp;
        return http;
    }

    private static IConfiguration Proxies(params string[] proxies) => new ConfigurationBuilder()
        .AddInMemoryCollection(proxies.Select((p, i) => new KeyValuePair<string, string?>($"Notifier:TrustedProxies:{i}", p)))
        .Build();

    [Fact]
    public void Proxy_in_a_trusted_cidr_range_passes_on_the_browser_address()
    {
        Assert.Equal("192.168.5.20", ClientAddress.Of(Request("10.42.1.7", "192.168.5.20"), Proxies("10.42.0.0/16")));
    }

    [Fact]
    public void Peer_outside_the_trusted_range_is_taken_as_is()
    {
        Assert.Equal("10.43.0.9", ClientAddress.Of(Request("10.43.0.9", "192.168.5.20"), Proxies("10.42.0.0/16")));
    }

    [Fact]
    public void Single_address_entries_still_work()
    {
        Assert.Equal("192.168.5.20", ClientAddress.Of(Request("192.168.2.2", "192.168.5.20"), Proxies("192.168.2.2")));
    }

    [Fact]
    public void Auth_endpoints_record_the_browser_address_not_the_proxy_behind_NPM()
    {
        // Production: every request reaches the API from NPM, so the raw peer is the same for the whole office.
        var cfg = Proxies("192.168.2.3");
        Assert.Equal("10.10.6.51", IPop.Api.Endpoints.AuthEndpoints.ClientIp(Request("192.168.2.3", "10.10.6.51"), cfg));
        Assert.Equal("10.10.6.77", IPop.Api.Endpoints.AuthEndpoints.ClientIp(Request("192.168.2.3", "10.10.6.77"), cfg));
        // A direct caller cannot spoof the header: only a trusted proxy's forwarded address is believed.
        Assert.Equal("203.0.113.9", IPop.Api.Endpoints.AuthEndpoints.ClientIp(Request("203.0.113.9", "10.10.6.51"), cfg));
    }
}
