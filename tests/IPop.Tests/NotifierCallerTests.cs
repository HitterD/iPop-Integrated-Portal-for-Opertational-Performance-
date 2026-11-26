using System.Security.Claims;
using IPop.Api.Endpoints;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace IPop.Tests;

/// Review M5: /api/notifier/* finds the caller by the token's user id (sub) first, like the notification gateway;
/// the NIK claim is only a fallback, and a token with neither is nobody.
public class NotifierCallerTests
{
    private static HttpContext Http(params Claim[] claims) =>
        new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")) };

    [Fact]
    public async Task Sub_wins_over_a_nik_that_points_at_someone_else()
    {
        using var t = new TestDb();
        var me = t.AddUser("MC1");
        var other = t.AddUser("MC2");
        var id = await NotifierEndpoints.MyId(Http(new Claim("sub", me.Id.ToString()), new Claim("nik", other.Nik)), t.Verify(), default);
        Assert.Equal(me.Id, id);
    }

    [Fact]
    public async Task Without_sub_the_nik_claim_is_used()
    {
        using var t = new TestDb();
        var me = t.AddUser("MC3");
        Assert.Equal(me.Id, await NotifierEndpoints.MyId(Http(new Claim("nik", me.Nik)), t.Verify(), default));
    }

    [Fact]
    public async Task No_sub_and_no_nik_is_nobody()
    {
        using var t = new TestDb();
        t.AddUser("MC4");
        Assert.Null(await NotifierEndpoints.MyId(Http(new Claim("name", "x")), t.Verify(), default));
        Assert.Null(await NotifierEndpoints.MyId(new DefaultHttpContext(), t.Verify(), default));
    }
}
