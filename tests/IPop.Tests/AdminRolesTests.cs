using IPop.Web.Components.Admin;
using Xunit;

namespace IPop.Tests;

/// The Admin › People role picker: one preset per person, labels people understand, custom roles kept.
public class AdminRolesTests
{
    [Fact]
    public void Picker_OffersAllPresets_WithFriendlyLabels()
    {
        Assert.Equal(new[] { "Employee", "HR", "HC – Approval", "HC – Approval & Directory", "HC – Lengkap", "Admin" }, AdminRoles.Picker);
        Assert.Equal("HR – News", AdminRoles.Label("HR"));
        Assert.Equal("HC – Approval", AdminRoles.Label("HC – Approval"));
    }

    [Fact]
    public void Primary_PrefersAdmin_ThenHc_ThenHr()
    {
        Assert.Equal("Admin", AdminRoles.Primary(["HR", "Admin"]));
        Assert.Equal("HC – Lengkap", AdminRoles.Primary(["HR", "HC – Lengkap"]));
        Assert.Equal("HR", AdminRoles.Primary(["HR", "Custom"]));
        Assert.Equal("Employee", AdminRoles.Primary(["Custom"]));
    }

    [Fact]
    public void With_ReplacesPresetKeepsCustomRoles()
    {
        Assert.Equal(new[] { "Custom", "HC – Approval" }, AdminRoles.With(["HR", "Custom"], "HC – Approval"));
        Assert.Equal(new[] { "Admin" }, AdminRoles.With(["HC – Lengkap"], "Admin"));
    }

    [Fact]
    public void Css_IsStablePerRole()
    {
        Assert.Equal("admin", AdminRoles.Css("Admin"));
        Assert.Equal("hr", AdminRoles.Css("HR"));
        Assert.Equal("hc", AdminRoles.Css("HC – Approval"));
        Assert.Equal("emp", AdminRoles.Css("Employee"));
    }
}
