using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Xunit;

namespace IPop.Tests;

public class AuthorizationPolicyTests
{
    [Fact]
    public void AdminOnly_RequiresAdminRoleClaim()
    {
        var policy = new AuthorizationPolicyBuilder().RequireRole("Admin").Build();
        Assert.Contains(policy.Requirements, r => r is RolesAuthorizationRequirement roles && roles.AllowedRoles.Contains("Admin"));

        var admin = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Role, "Admin")], "test"));
        var employee = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Role, "Employee")], "test"));
        Assert.True(admin.IsInRole("Admin"));
        Assert.False(employee.IsInRole("Admin"));
    }
}
