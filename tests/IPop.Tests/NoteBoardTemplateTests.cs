using System.Text.Json;
using IPop.Api.Endpoints;
using IPop.Application.Notes;
using IPop.Infrastructure.Notes;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace IPop.Tests;

/// Whiteboard templates (round 6 Template Center): My templates, Team templates, who may remove them; and the two
/// per-person whiteboard prefs (My pens, recently used templates).
public class NoteBoardTemplateTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static NoteService Svc(TestDb t) => new(t.Verify());
    private const string Board = """[{"id":"f","type":"frame","x":0,"y":0,"w":900,"h":600},{"id":"s","type":"sticky","x":40,"y":60,"w":200,"h":150}]""";
    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public async Task A_saved_template_is_private_until_shared_and_then_everyone_sees_it_in_team_templates()
    {
        using var t = new TestDb();
        var a = t.AddUser("a");
        var b = t.AddUser("b");
        var mine = (await Svc(t).SaveBoardTemplateAsync(a.Id, new SaveBoardTemplateRequest("Weekly ICT review", "Incidents, changes, next week", Board), Ct)).Data!;
        Assert.Equal(2, mine.Count);
        Assert.True(mine.Mine);
        Assert.False(mine.Shared);
        Assert.Empty(await Svc(t).GetBoardTemplatesAsync(b.Id, Ct));

        Assert.True((await Svc(t).UpdateBoardTemplateAsync(a.Id, mine.Id, new UpdateBoardTemplateRequest(null, null, true), Ct)).Data!.Shared);
        var seen = Assert.Single(await Svc(t).GetBoardTemplatesAsync(b.Id, Ct));
        Assert.False(seen.Mine);
        Assert.Equal("User a", seen.OwnerName);
        Assert.Equal("Incidents, changes, next week", seen.Description);

        // someone else can neither change nor remove it
        Assert.Equal("not_found", (await Svc(t).UpdateBoardTemplateAsync(b.Id, mine.Id, new UpdateBoardTemplateRequest("Mine now", null, false), Ct)).Error);
        Assert.Equal("not_found", (await Svc(t).DeleteBoardTemplateAsync(b.Id, mine.Id, isAdmin: false, Ct)).Error);
    }

    [Fact]
    public async Task An_admin_can_remove_a_shared_template_but_not_a_private_one()
    {
        using var t = new TestDb();
        var a = t.AddUser("a");
        var admin = t.AddUser("admin");
        var priv = (await Svc(t).SaveBoardTemplateAsync(a.Id, new SaveBoardTemplateRequest("Private", null, Board), Ct)).Data!;
        var shared = (await Svc(t).SaveBoardTemplateAsync(a.Id, new SaveBoardTemplateRequest("Shared", null, Board, Shared: true), Ct)).Data!;
        Assert.Equal("not_found", (await Svc(t).DeleteBoardTemplateAsync(admin.Id, priv.Id, isAdmin: true, Ct)).Error);
        Assert.True((await Svc(t).DeleteBoardTemplateAsync(admin.Id, shared.Id, isAdmin: true, Ct)).Success);
        Assert.True((await Svc(t).DeleteBoardTemplateAsync(a.Id, priv.Id, isAdmin: false, Ct)).Success);
        Assert.Equal(0, await t.Verify().NoteBoardTemplates.CountAsync());
    }

    [Theory]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("""[{"type":"rect"}]""")]
    public async Task Anything_that_is_not_a_list_of_elements_is_refused(string json)
    {
        using var t = new TestDb();
        var a = t.AddUser("a");
        Assert.Equal("invalid_template", (await Svc(t).SaveBoardTemplateAsync(a.Id, new SaveBoardTemplateRequest("x", null, json), Ct)).Error);
    }

    [Fact]
    public async Task Too_big_is_refused_and_a_name_is_made_up_when_none_is_given()
    {
        using var t = new TestDb();
        var a = t.AddUser("a");
        var huge = "[" + string.Join(",", Enumerable.Range(0, 6000).Select(i => $$"""{"id":"e{{i}}","type":"rect"}""")) + "]";
        Assert.Equal("invalid_template", (await Svc(t).SaveBoardTemplateAsync(a.Id, new SaveBoardTemplateRequest("x", null, huge), Ct)).Error);
        Assert.Equal("My template", (await Svc(t).SaveBoardTemplateAsync(a.Id, new SaveBoardTemplateRequest("  ", null, Board), Ct)).Data!.Name);
    }

    [Fact]
    public async Task My_pens_and_recent_templates_are_kept_per_person_and_checked()
    {
        using var t = new TestDb();
        var u = t.AddUser("pens");
        Assert.Equal(204, await UiPrefsEndpoints.PutAsync(t.Db, u.Id, "wb-pens", J("""[{"name":"Red check","type":"marker","color":"#FF3B30","size":1},{"name":"Notes","type":"fine","color":"auto","size":0}]"""), false, Ct));
        var (status, json) = await UiPrefsEndpoints.GetAsync(t.Verify(), u.Id, "wb-pens", false, Ct);
        Assert.Equal(200, status);
        Assert.Contains("Red check", json);
        Assert.Equal(204, await UiPrefsEndpoints.PutAsync(t.Db, u.Id, "wb-tpl-recent", J("""["approval","meetingnotes","kanban"]"""), false, Ct));

        Assert.Equal(400, await UiPrefsEndpoints.PutAsync(t.Db, u.Id, "wb-pens", J("""[{"name":"x","type":"laser","color":"#FF3B30","size":1}]"""), false, Ct));   // unknown pen
        Assert.Equal(400, await UiPrefsEndpoints.PutAsync(t.Db, u.Id, "wb-pens", J("""[{"name":"x","type":"pen","color":"red","size":1}]"""), false, Ct));       // not a hex colour
        Assert.Equal(400, await UiPrefsEndpoints.PutAsync(t.Db, u.Id, "wb-pens", J("""[{"name":"x","type":"pen","color":"#000000","size":9}]"""), false, Ct));   // size out of range
        Assert.Equal(400, await UiPrefsEndpoints.PutAsync(t.Db, u.Id, "wb-tpl-recent", J("""["a","b","c","d","e","f","g"]"""), false, Ct));                      // more than 6
    }
}
