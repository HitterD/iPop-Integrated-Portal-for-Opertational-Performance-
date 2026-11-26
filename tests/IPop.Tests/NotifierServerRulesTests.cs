using IPop.Application.Notifier;
using Xunit;

namespace IPop.Tests;

/// Review H2: a release Notifier signs in to https servers only, and updates always come from the fixed server.
public class NotifierServerRulesTests
{
    private const string Prod = "https://ipop.santos.co.id/";

    [Theory]
    [InlineData("https://ipop.santos.co.id", false, "https://ipop.santos.co.id/")]
    [InlineData("  https://ipop.santos.co.id/  ", false, "https://ipop.santos.co.id/")]
    [InlineData("https://test.local:8443/ipop?x=1", false, "https://test.local:8443/ipop/")]
    [InlineData("http://10.10.6.13:6391/", false, null)]
    [InlineData("http://10.10.6.13:6391/", true, "http://10.10.6.13:6391/")]
    [InlineData("ftp://x/", true, null)]
    [InlineData("ipop.santos.co.id", false, null)]
    [InlineData("https://user:pw@evil.example/", false, null)]
    [InlineData("", false, null)]
    public void Sign_in_server_must_be_https_in_release(string typed, bool allowHttp, string? expected) =>
        Assert.Equal(expected, NotifierServerRules.AcceptServer(typed, allowHttp));

    [Fact]
    public void Update_files_come_from_the_fixed_server_or_the_trial_feed()
    {
        Assert.Equal("https://ipop.santos.co.id/download/notifier/", NotifierServerRules.UpdateFiles(Prod, null));
        Assert.Equal("http://10.10.6.13:6393/", NotifierServerRules.UpdateFiles(Prod, "http://10.10.6.13:6393/"));
    }

    [Theory]
    [InlineData("https://ipop.santos.co.id/", false, true)]
    [InlineData("https://IPOP.santos.co.id/", false, true)]
    [InlineData("https://evil.example/", false, false)]
    [InlineData("http://ipop.santos.co.id/", false, false)]
    [InlineData("https://ipop.santos.co.id:8443/", false, false)]
    [InlineData("http://10.10.6.13:6391/", true, true)]
    public void Only_the_fixed_server_picks_the_release_channel(string signedIn, bool trial, bool expected) =>
        Assert.Equal(expected, NotifierServerRules.ChannelFromSignedInServer(new Uri(signedIn), Prod, trial));
}
