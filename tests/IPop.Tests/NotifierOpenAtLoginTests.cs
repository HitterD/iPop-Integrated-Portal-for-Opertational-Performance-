using IPop.Notifier.Core;
using Xunit;

namespace IPop.Tests;

/// "Open iPop when Windows starts": never a second copy, the installed app first, the browser as fallback.
public class NotifierOpenAtLoginTests
{
    private static readonly Uri Web = new("https://ipop.santos.co.id/");
    private const string EdgeApp = @"""C:\Program Files (x86)\Microsoft\Edge\Application\msedge_proxy.exe"" --profile-directory=Default --app-id=abc --single-argument %1";

    private sealed class Spy
    {
        public bool Open, AppStarts = true, BrowserOpens = true;
        public string? Command = EdgeApp;
        public readonly List<string> Calls = [];
        public IPopAppLauncher.Env Env => new(
            () => Open,
            () => Command,
            (file, args) => { Calls.Add("app " + file + " " + args); return AppStarts; },
            url => { Calls.Add("browser " + url); return BrowserOpens; });
    }

    [Fact]
    public void Already_open_skips_and_starts_nothing()
    {
        var s = new Spy { Open = true };
        Assert.Equal(IPopAppLauncher.Outcome.Skipped, IPopAppLauncher.Decide(s.Env, Web));
        Assert.Empty(s.Calls);
    }

    [Fact]
    public void Installed_app_opens_the_dashboard_and_not_the_browser()
    {
        var s = new Spy();
        Assert.Equal(IPopAppLauncher.Outcome.App, IPopAppLauncher.Decide(s.Env, Web));
        var call = Assert.Single(s.Calls);
        Assert.StartsWith(@"app C:\Program Files (x86)\Microsoft\Edge\Application\msedge_proxy.exe --profile-directory=Default --app-id=abc --single-argument", call);
        Assert.EndsWith("web+ipop://open?to=%2Fdashboard", call);
    }

    [Fact]
    public void No_app_opens_the_handoff_page_in_the_browser()
    {
        var s = new Spy { Command = null };
        Assert.Equal(IPopAppLauncher.Outcome.Browser, IPopAppLauncher.Decide(s.Env, Web));
        Assert.Equal(["browser https://ipop.santos.co.id/open.html?to=%2Fdashboard"], s.Calls);
    }

    [Fact]
    public void App_that_fails_to_start_falls_back_to_the_browser()
    {
        var s = new Spy { AppStarts = false };
        Assert.Equal(IPopAppLauncher.Outcome.Browser, IPopAppLauncher.Decide(s.Env, Web));
        Assert.Equal(2, s.Calls.Count);
        Assert.StartsWith("app ", s.Calls[0]);
        Assert.StartsWith("browser ", s.Calls[1]);
    }

    [Theory]
    [InlineData(IPopAppLauncher.Outcome.App, "app")]
    [InlineData(IPopAppLauncher.Outcome.Browser, "browser")]
    [InlineData(IPopAppLauncher.Outcome.Skipped, "skipped")]
    public void Log_words(IPopAppLauncher.Outcome o, string word) => Assert.Equal(word, IPopAppLauncher.LogWord(o));
}
