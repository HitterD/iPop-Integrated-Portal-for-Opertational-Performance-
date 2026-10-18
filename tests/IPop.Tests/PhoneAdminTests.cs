using Bunit;
using IPop.Web.Components.Phone.Admin;
using IPop.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
namespace IPop.Tests;
public class PhoneAdminTests
{
    [Fact]
    public void Admin_home_is_permission_gated_and_grouped()
    {
        using var ctx = new BunitContext(); ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        var cut = ctx.Render<AdminHomePhone>(); Assert.Empty(cut.FindAll(".m-admin"));
        cut.Render(p => p.Add(x => x.IsAdmin, true));
        Assert.Equal(new[] { "People", "Structure", "Security", "Communication", "Integrations" }, cut.FindAll(".ph-sec").Select(x => x.TextContent));
        Assert.Contains("Admin only", cut.Markup); Assert.Equal("/admin/users", cut.Find("a[href='/admin/users']").GetAttribute("href"));
    }
    private sealed class ReadApi : System.Net.Http.HttpMessageHandler
    {
        public List<string> Paths = [];
        protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage r, CancellationToken ct)
        {
            Paths.Add(r.RequestUri!.PathAndQuery);
            return Task.FromResult(new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new System.Net.Http.StringContent("[{\"title\":\"Real holiday\",\"date\":\"2026-10-18\"}]", System.Text.Encoding.UTF8, "application/json") });
        }
    }
    [Theory]
    [InlineData("organization", "/api/admin/org")]
    [InlineData("security", "/api/admin/audit?page=1&pageSize=30")]
    [InlineData("hris", "/api/admin/hris/status")]
    [InlineData("calendar", "/api/calendar/holidays?year=")]
    [InlineData("meeting-rooms", "/api/meeting-rooms")]
    public async Task Readonly_routes_use_existing_endpoints_and_unknown_routes_do_not_fetch(string section, string path)
    {
        using var ctx = new BunitContext(); ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        var api = new ReadApi(); var auth = new AuthState(new HttpClient(api) { BaseAddress = new("http://localhost/") }, ctx.JSInterop.JSRuntime);
        typeof(AuthState).GetProperty("Me")!.SetValue(auth, new DirectoryEmployeeDto("admin", "Admin", null, null, null, null, null, null, "", null, null, "on", ["Admin"]));
        ctx.Services.AddSingleton(auth);
        var cut = ctx.Render<AdminReadOnlyPhone>(p => p.Add(x => x.Section, section));
        cut.WaitForAssertion(() => Assert.Contains(api.Paths, x => x.StartsWith(path)));
        Assert.Contains("Real holiday", cut.Markup); Assert.Contains("Edit on a computer", cut.Markup);
        var count = api.Paths.Count;
        cut.Render(p => p.Add(x => x.Section, "unknown"));
        Assert.Equal(count, api.Paths.Count);
        Assert.Contains("Choose a section", cut.Markup);
        await Task.CompletedTask;
    }
    [Fact]
    public void Cold_admin_root_waits_for_phone_and_shows_grouped_home()
    {
        using var ctx = new BunitContext(); ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.JSInterop.Setup<bool>("ipop.mobile.watch", _ => true).SetResult(true);
        var api = new ReadApi(); var auth = new AuthState(new HttpClient(api) { BaseAddress = new("http://localhost/") }, ctx.JSInterop.JSRuntime);
        typeof(AuthState).GetProperty("Me")!.SetValue(auth, new DirectoryEmployeeDto("admin", "Admin", null, null, null, null, null, null, "", null, null, "on", ["Admin"]));
        ctx.Services.AddSingleton(auth); ctx.Services.AddSingleton(new PhoneState(ctx.JSInterop.JSRuntime));
        var nav = ctx.Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>(); nav.NavigateTo("/admin");
        var cut = ctx.Render<IPop.Web.Pages.Admin>();
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".m-admin"))); Assert.EndsWith("/admin", nav.Uri);
    }
    [Fact]
    public async Task User_sheet_is_admin_only_and_dispatches_existing_role_and_access_actions()
    {
        using var ctx = new BunitContext(); ctx.JSInterop.Mode = JSRuntimeMode.Loose; ctx.Services.AddSingleton(new OverlayStack());
        var user = new AdminUserRowDto("QA-NIK", "Jane", "jane@example.invalid", "Engineer", null, "ICT", "ict", "SJA", "sja", true, false, ["Employee"], "on", DateTime.UtcNow);
        string? role = null; var reset = false; var locked = false;
        var cut = ctx.Render<AdminUserSheetPhone>(p => p.Add(x => x.User, user)); Assert.Empty(cut.FindAll(".ph-sheet"));
        cut.Render(p => p.Add(x => x.IsAdmin, true).Add(x => x.OnRole, (string r) => role = r).Add(x => x.OnReset, () => reset = true).Add(x => x.OnLock, () => locked = true));
        Assert.Contains("Admin only", cut.Markup); Assert.Contains("QA-NIK", cut.Markup);
        cut.Find("button[aria-label='Role HR']").Click(); cut.Find("button[aria-label='Reset password']").Click(); cut.Find("button[aria-label='Lock account']").Click();
        Assert.Equal("HR", role); Assert.True(reset); Assert.True(locked);
        Assert.Contains("Edit on a computer", cut.Markup);
        var closed = false; cut.Render(p => p.Add(x => x.OnClose, () => closed = true));
        await cut.InvokeAsync(() => ctx.Services.GetRequiredService<OverlayStack>().CloseTopAsync()); Assert.True(closed);
    }
}
