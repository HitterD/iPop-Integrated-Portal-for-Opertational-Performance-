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
