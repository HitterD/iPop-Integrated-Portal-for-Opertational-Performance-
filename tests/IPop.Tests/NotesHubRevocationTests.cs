using System.Reflection;
using System.Security.Claims;
using IPop.Api.Notes;
using IPop.Application.Notes;
using IPop.Application.Rbac;
using IPop.Infrastructure.Notes;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Xunit;

namespace IPop.Tests;

/// A Notes tab keeps receiving a page's live content only while its user can still open that page. Access is inherited
/// down the tree, so removing a share on a parent, moving a page out from under its parent, or trashing the parent
/// must take every affected connection out of the live groups of the DESCENDANT pages too, not just of the page the
/// request named.
public class NotesHubRevocationTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    /// Records every call and hands back more recording proxies for interface-typed results (Clients, Groups, Client(id)).
    public class Rec : DispatchProxy
    {
        public List<string> Calls = [];
        protected override object? Invoke(MethodInfo? m, object?[]? args)
        {
            Calls.Add($"{m!.Name}:{string.Join(",", (args ?? []).Select(a => a?.ToString()))}");
            var rt = m.ReturnType;
            if (rt == typeof(Task)) return Task.CompletedTask;
            if (rt == typeof(ValueTask)) return default(ValueTask);
            if (!rt.IsInterface) return rt.IsValueType ? Activator.CreateInstance(rt) : null;
            var child = typeof(DispatchProxy).GetMethods()
                .First(x => x.Name == nameof(DispatchProxy.Create) && x.GetGenericArguments().Length == 2)
                .MakeGenericMethod(rt, typeof(Rec)).Invoke(null, null)!;
            ((Rec)child).Calls = Calls;
            return child;
        }
    }

    private sealed class Ctx(string connectionId, ClaimsPrincipal user) : HubCallerContext
    {
        public override string ConnectionId => connectionId;
        public override string? UserIdentifier => null;
        public override ClaimsPrincipal? User => user;
        public override IDictionary<object, object?> Items { get; } = new Dictionary<object, object?>();
        public override IFeatureCollection Features { get; } = new FeatureCollection();
        public override CancellationToken ConnectionAborted => default;
        public override void Abort() { }
    }

    private sealed class AllowAll : IPermissionEvaluator
    {
        public Task<bool> IsAllowedAsync(Guid userId, string code, string? dept, CancellationToken ct = default) => Task.FromResult(true);
    }

    private static NoteService Svc(TestDb t) => new(t.Verify());

    private static async Task<Guid> PageAsync(TestDb t, Guid owner, Guid? parent, string title)
    {
        var r = await Svc(t).CreateAsync(owner, new CreateNoteRequest(parent, title, null, null, null, null), Ct);
        Assert.True(r.Success, r.Error);
        return r.Data!.Id;
    }

    private static async Task<(string Connection, Rec Groups)> JoinAsync(TestDb t, Guid userId, Guid page, string connection)
    {
        var groups = DispatchProxy.Create<IGroupManager, Rec>();
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", userId.ToString())], "test"));
        var hub = new NotesHub(Svc(t), new AllowAll()) { Context = new Ctx(connection, principal), Groups = groups };
        Assert.True(await hub.JoinPage(page), "setup: the user must be able to join before access is revoked");
        return (connection, (Rec)(object)groups);
    }

    private static (IHubContext<NotesHub> Hub, Rec Calls) HubContext()
    {
        var hub = DispatchProxy.Create<IHubContext<NotesHub>, Rec>();
        return (hub, (Rec)(object)hub);
    }

    [Fact]
    public async Task Removing_a_share_on_the_parent_evicts_connections_open_on_its_sub_pages()
    {
        using var t = new TestDb();
        var owner = t.AddUser("NR1"); var guest = t.AddUser("NR2");
        var root = await PageAsync(t, owner.Id, null, "Handbook");
        var child = await PageAsync(t, owner.Id, root, "Chapter 1");
        await Svc(t).AddShareAsync(owner.Id, root, new AddNoteShareRequest(guest.Id, null, NoteLevels.View), Ct);
        await JoinAsync(t, guest.Id, child, "conn-guest");     // access to the child is inherited from the parent's share

        var share = t.Verify().NoteShares.Single(s => s.PageId == root);
        Assert.True((await Svc(t).RemoveShareAsync(owner.Id, root, share.Id, Ct)).Success);
        var (hub, calls) = HubContext();
        await NotesHub.EvictLostAccessAsync(hub, Svc(t), root, Ct);   // exactly what the DELETE share endpoint calls

        Assert.Contains(calls.Calls, c => c.StartsWith("RemoveFromGroupAsync:conn-guest,") && c.Contains(NotesHub.Group(child)));
        Assert.Contains(calls.Calls, c => c.StartsWith("SendCoreAsync:noteAccessLost"));
    }

    [Fact]
    public async Task Trashing_the_parent_evicts_every_connection_open_below_it_but_not_ones_that_still_have_access()
    {
        using var t = new TestDb();
        var owner = t.AddUser("NR3"); var guest = t.AddUser("NR4");
        var root = await PageAsync(t, owner.Id, null, "Wiki");
        var child = await PageAsync(t, owner.Id, root, "Setup");
        var other = await PageAsync(t, owner.Id, null, "Unrelated");
        await Svc(t).AddShareAsync(owner.Id, root, new AddNoteShareRequest(guest.Id, null, NoteLevels.Edit), Ct);
        await Svc(t).AddShareAsync(owner.Id, other, new AddNoteShareRequest(guest.Id, null, NoteLevels.View), Ct);
        await JoinAsync(t, guest.Id, child, "conn-below");
        await JoinAsync(t, guest.Id, other, "conn-unrelated");
        await JoinAsync(t, owner.Id, child, "conn-owner");

        Assert.True((await Svc(t).TrashAsync(owner.Id, root, Ct)).Success);
        var (hub, calls) = HubContext();
        await NotesHub.EvictLostAccessAsync(hub, Svc(t), root, Ct);

        Assert.Contains(calls.Calls, c => c.StartsWith("RemoveFromGroupAsync:conn-below,") && c.Contains(NotesHub.Group(child)));
        Assert.DoesNotContain(calls.Calls, c => c.Contains("conn-unrelated"));   // still shared on a page outside the trashed tree
    }

    [Fact]
    public async Task Control_removing_a_share_from_the_open_page_itself_evicts_that_connection()
    {
        using var t = new TestDb();
        var owner = t.AddUser("NR5"); var guest = t.AddUser("NR6");
        var page = await PageAsync(t, owner.Id, null, "Direct");
        await Svc(t).AddShareAsync(owner.Id, page, new AddNoteShareRequest(guest.Id, null, NoteLevels.View), Ct);
        await JoinAsync(t, guest.Id, page, "conn-direct");

        var share = t.Verify().NoteShares.Single(s => s.PageId == page);
        Assert.True((await Svc(t).RemoveShareAsync(owner.Id, page, share.Id, Ct)).Success);
        var (hub, calls) = HubContext();
        await NotesHub.EvictLostAccessAsync(hub, Svc(t), page, Ct);

        Assert.Contains(calls.Calls, c => c.StartsWith("RemoveFromGroupAsync:conn-direct,") && c.Contains(NotesHub.Group(page)));
    }
}
