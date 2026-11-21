using System.Security.Claims;
using IPop.Api.Notes;
using IPop.Application.Notes;
using IPop.Application.Rbac;
using IPop.Infrastructure.Notes;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Xunit;

namespace IPop.Tests;

/// An update the server will never store must come back as a named refusal, not as null: the editor treated null like a
/// network blip and retried every 3 s forever, showing "Offline" while the user kept typing into a page that never saved.
public class NotesHubRefusalTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    private sealed class Ctx(ClaimsPrincipal user) : HubCallerContext
    {
        public override string ConnectionId => "c1";
        public override string? UserIdentifier => null;
        public override ClaimsPrincipal? User => user;
        public override IDictionary<object, object?> Items { get; } = new Dictionary<object, object?>();
        public override IFeatureCollection Features { get; } = new FeatureCollection();
        public override CancellationToken ConnectionAborted => default;
        public override void Abort() { }
    }

    private sealed class Perms(bool allow) : IPermissionEvaluator
    {
        public Task<bool> IsAllowedAsync(Guid userId, string code, string? dept, CancellationToken ct = default) => Task.FromResult(allow);
    }

    private static NotesHub Hub(TestDb t, Guid user, bool allow = true) =>
        new(new NoteService(t.Verify()), new Perms(allow))
        {
            Context = new Ctx(new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", user.ToString())], "test"))),
            Clients = System.Reflection.DispatchProxy.Create<IHubCallerClients, NotesHubRevocationTests.Rec>(),
        };

    private static async Task<Guid> PageAsync(TestDb t, Guid owner)
    {
        var r = await new NoteService(t.Verify()).CreateAsync(owner, new CreateNoteRequest(null, "P", null, null, null, null), Ct);
        return r.Data!.Id;
    }

    [Fact]
    public async Task Viewer_push_is_refused_with_a_reason()
    {
        using var t = new TestDb();
        var a = t.AddUser("a"); var b = t.AddUser("b");
        var page = await PageAsync(t, a.Id);
        await new NoteService(t.Verify()).AddShareAsync(a.Id, page, new AddNoteShareRequest(b.Id, null, NoteLevels.View), Ct);

        var ex = await Assert.ThrowsAsync<HubException>(() => Hub(t, b.Id).PushUpdate(page, Convert.ToBase64String([1, 2, 3])));
        Assert.Equal(NotesHub.RefusedPrefix + "forbidden", ex.Message);
    }

    [Fact]
    public async Task Push_to_a_page_without_access_or_permission_or_garbage_is_refused()
    {
        using var t = new TestDb();
        var a = t.AddUser("a"); var c = t.AddUser("c");
        var page = await PageAsync(t, a.Id);

        Assert.Equal(NotesHub.RefusedPrefix + "not_found",
            (await Assert.ThrowsAsync<HubException>(() => Hub(t, c.Id).PushUpdate(page, "AQID"))).Message);
        Assert.Equal(NotesHub.RefusedPrefix + "forbidden",
            (await Assert.ThrowsAsync<HubException>(() => Hub(t, a.Id, allow: false).PushUpdate(page, "AQID"))).Message);
        Assert.Equal(NotesHub.RefusedPrefix + "invalid_update",
            (await Assert.ThrowsAsync<HubException>(() => Hub(t, a.Id).PushUpdate(page, "not base64!"))).Message);
    }

    [Fact]
    public async Task Owner_push_is_stored()
    {
        using var t = new TestDb();
        var a = t.AddUser("a");
        var page = await PageAsync(t, a.Id);
        Assert.NotNull(await Hub(t, a.Id).PushUpdate(page, "AQID"));
    }
}
