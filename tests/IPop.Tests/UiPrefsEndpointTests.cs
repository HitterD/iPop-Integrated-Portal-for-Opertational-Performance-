using System.Text.Json;
using IPop.Api.Endpoints;
using IPop.Application.Shell;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace IPop.Tests;

/// GET/PUT /api/me/ui-prefs/{key}: phone tab order (and the other small UI prefs) follow the account.
public class UiPrefsEndpointTests
{
    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public async Task Tabs_default_to_chat_mail_calls_calendar()
    {
        using var t = new TestDb();
        var u = t.AddUser("UI1");
        var (status, json) = await UiPrefsEndpoints.GetAsync(t.Db, u.Id, "tabs", isAdmin: false, default);
        Assert.Equal(200, status);
        Assert.Equal(["/chat", "/mail", "/calls", "/calendar"], JsonSerializer.Deserialize<string[]>(json!));
    }

    [Fact]
    public async Task Saved_tabs_are_returned_in_order()
    {
        using var t = new TestDb();
        var u = t.AddUser("UI2");
        Assert.Equal(204, await UiPrefsEndpoints.PutAsync(t.Db, u.Id, "tabs", J("""["/notes","/chat","/mail","/calendar"]"""), false, default));
        var (_, json) = await UiPrefsEndpoints.GetAsync(t.Verify(), u.Id, "tabs", false, default);
        Assert.Equal(["/notes", "/chat", "/mail", "/calendar"], JsonSerializer.Deserialize<string[]>(json!));
        // Saving again updates the same row.
        Assert.Equal(204, await UiPrefsEndpoints.PutAsync(t.Db, u.Id, "tabs", J("""["/chat","/mail","/calls","/files"]"""), false, default));
        await using var v = t.Verify();
        Assert.Single(await v.UserUiPrefs.Where(p => p.UserId == u.Id).ToListAsync());
    }

    [Theory]
    [InlineData("""["/chat","/mail","/calls"]""")]                        // three
    [InlineData("""["/chat","/mail","/calls","/calendar","/notes"]""")]    // five
    [InlineData("""["/chat","/chat","/calls","/calendar"]""")]             // duplicate
    [InlineData("""["/chat","/mail","/calls","/evil"]""")]                 // unknown
    [InlineData("""["/chat","/mail","/calls","/admin"]""")]                // admin-only for a non-admin
    [InlineData("""{"a":1}""")]
    [InlineData("""["/chat","/mail","/calls",4]""")]
    public async Task Invalid_tabs_are_rejected(string body)
    {
        using var t = new TestDb();
        var u = t.AddUser("UI3");
        Assert.Equal(400, await UiPrefsEndpoints.PutAsync(t.Db, u.Id, "tabs", J(body), false, default));
        Assert.Empty(await t.Verify().UserUiPrefs.ToListAsync());
    }

    [Fact]
    public async Task Admin_may_pin_admin_settings()
    {
        using var t = new TestDb();
        var u = t.AddAdmin("UI4");
        Assert.Equal(204, await UiPrefsEndpoints.PutAsync(t.Db, u.Id, "tabs", J("""["/chat","/mail","/calls","/admin"]"""), true, default));
        // Lost the role later: the saved list falls back to the default instead of showing /admin.
        var (_, json) = await UiPrefsEndpoints.GetAsync(t.Db, u.Id, "tabs", false, default);
        Assert.Equal(PhoneTabs.Default, JsonSerializer.Deserialize<string[]>(json!));
    }

    [Fact]
    public async Task Prefs_are_per_user()
    {
        using var t = new TestDb();
        var a = t.AddUser("UI5");
        var b = t.AddUser("UI6");
        await UiPrefsEndpoints.PutAsync(t.Db, a.Id, "tabs", J("""["/notes","/chat","/mail","/calendar"]"""), false, default);
        var (_, json) = await UiPrefsEndpoints.GetAsync(t.Verify(), b.Id, "tabs", false, default);
        Assert.Equal(PhoneTabs.Default, JsonSerializer.Deserialize<string[]>(json!));
    }

    [Fact]
    public async Task Unknown_keys_are_not_found()
    {
        using var t = new TestDb();
        var u = t.AddUser("UI7");
        Assert.Equal(404, (await UiPrefsEndpoints.GetAsync(t.Db, u.Id, "theme", false, default)).Status);
        Assert.Equal(404, await UiPrefsEndpoints.PutAsync(t.Db, u.Id, "theme", J("\"x\""), false, default));
    }

    [Fact]
    public async Task Notification_card_dismissal_is_remembered()
    {
        using var t = new TestDb();
        var u = t.AddUser("UI8");
        Assert.Equal(204, (await UiPrefsEndpoints.GetAsync(t.Db, u.Id, "notif-card", false, default)).Status);
        Assert.Equal(400, await UiPrefsEndpoints.PutAsync(t.Db, u.Id, "notif-card", J("\"whatever\""), false, default));
        Assert.Equal(204, await UiPrefsEndpoints.PutAsync(t.Db, u.Id, "notif-card", J("\"dismissed\""), false, default));
        var (status, json) = await UiPrefsEndpoints.GetAsync(t.Verify(), u.Id, "notif-card", false, default);
        Assert.Equal(200, status);
        Assert.Equal("\"dismissed\"", json);
    }

    [Fact]
    public async Task Values_over_2KB_are_rejected()
    {
        using var t = new TestDb();
        var u = t.AddUser("UI9");
        var big = "[" + string.Join(",", Enumerable.Repeat("\"/dashboard-widget-name\"", 120)) + "]";
        Assert.Equal(400, await UiPrefsEndpoints.PutAsync(t.Db, u.Id, "dash-order", J(big), false, default));
        Assert.Equal(204, await UiPrefsEndpoints.PutAsync(t.Db, u.Id, "dash-order", J("""["mail","chat"]"""), false, default));
    }

    [Fact]
    public void Tab_catalogue_covers_every_sidebar_module_once()
    {
        Assert.Equal(PhoneTabs.All.Count, PhoneTabs.All.Select(m => m.Href).Distinct().Count());
        Assert.All(PhoneTabs.Default, h => Assert.NotNull(PhoneTabs.Find(h)));
        Assert.Equal(["/admin"], PhoneTabs.All.Where(m => m.AdminOnly).Select(m => m.Href));
    }
}
