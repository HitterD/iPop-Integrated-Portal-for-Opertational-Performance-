using IPop.Notifier.Core;
using Xunit;

namespace IPop.Tests;

/// Notifier 1.7.1: sign-in on start depends on the saved session only (1.7.0 showed it after every update), and every
/// ticket notification opens the ticket on the iDesk site.
public class NotifierStartupAndTicketLinkTests
{
    [Fact]
    public void Update_restart_while_signed_in_connects_without_the_sign_in_window()
    {
        var p = StartupPlan.Decide(["--updated"], signedIn: true);
        Assert.True(p.StartSession);
        Assert.False(p.ShowSignIn);
        Assert.False(p.BrowserSignIn);
        Assert.False(p.OpenIPopAtLogin);
    }

    [Fact]
    public void Manual_start_while_signed_in_shows_no_sign_in_window()
    {
        var p = StartupPlan.Decide([], signedIn: true);
        Assert.True(p.StartSession);
        Assert.False(p.ShowSignIn);
        Assert.False(p.OpenIPopAtLogin);
    }

    [Fact]
    public void Windows_autostart_while_signed_in_connects_and_opens_iPop_at_login()
    {
        var p = StartupPlan.Decide(["--background"], signedIn: true);
        Assert.True(p.StartSession);
        Assert.False(p.ShowSignIn);
        Assert.True(p.OpenIPopAtLogin);
    }

    [Fact]
    public void Windows_autostart_while_signed_out_shows_the_sign_in_window()
    {
        var p = StartupPlan.Decide(["--background"], signedIn: false);
        Assert.False(p.StartSession);
        Assert.True(p.ShowSignIn);
        Assert.False(p.BrowserSignIn);
        // RunAtLoginAsync still runs and skips by itself while signed out.
        Assert.True(p.OpenIPopAtLogin);
    }

    [Fact]
    public void Manual_start_while_signed_out_shows_the_sign_in_window()
    {
        var p = StartupPlan.Decide([], signedIn: false);
        Assert.False(p.StartSession);
        Assert.True(p.ShowSignIn);
        Assert.False(p.BrowserSignIn);
        Assert.False(p.OpenIPopAtLogin);
    }

    [Fact]
    public void First_run_after_install_also_opens_the_browser_sign_in()
    {
        var p = StartupPlan.Decide(["--first-run"], signedIn: false);
        Assert.True(p.ShowSignIn);
        Assert.True(p.BrowserSignIn);
    }

    [Fact]
    public void First_run_with_a_kept_session_signs_nothing_in()
    {
        var p = StartupPlan.Decide(["--first-run"], signedIn: true);
        Assert.False(p.ShowSignIn);
        Assert.False(p.BrowserSignIn);
    }

    private const string Id = "6308def5-30a6-44a7-b16f-854b2d891e23";

    [Theory]
    [InlineData("/tickets/" + Id, "api/tickets/" + Id)]
    [InlineData("/tickets/" + Id + "#messages", "api/tickets/" + Id)]
    [InlineData("/tickets/" + Id + "?tab=chat", "api/tickets/" + Id)]
    [InlineData("/TICKETS/" + Id, "api/tickets/" + Id)]
    [InlineData("/idesk/tickets/" + Id, "api/tickets/idesk/" + Id + "/link")]
    [InlineData("/idesk/tickets/" + Id + "#update", "api/tickets/idesk/" + Id + "/link")]
    public void Ticket_targets_ask_iPop_for_the_iDesk_link(string target, string request) =>
        Assert.Equal(request, TicketLinks.LinkRequest(target));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("/tickets")]
    [InlineData("/tickets/new")]
    [InlineData("/tickets/not-a-guid-at-all-but-thirty-six-chars")]
    [InlineData("/tickets/" + Id + "x")]
    [InlineData("/chat/" + Id)]
    [InlineData("/notifications")]
    [InlineData("/idesk/tickets/")]
    public void Other_targets_stay_iPop_pages(string? target) =>
        Assert.Null(TicketLinks.LinkRequest(target));
}
