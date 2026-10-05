using Xunit;
using IPop.Application.Notes;
using IPop.Infrastructure.Notes;

namespace IPop.Tests;

public class NoteImportTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static NoteService Svc(TestDb t) => new(t.Verify());

    private static ImportNotesRequest Tree(Guid? parent = null) => new(parent, "notion",
    [
        new("root", null, "SJA ICT Wiki", "🗂", null, "file:abc"),
        new("a", "root", "Install Aplikasi Standard SJA", "💻", "teal", "notion:e5f02d5e594a463b91180a4ad8ee3cec"),
        new("b", "root", "Printer", null, "not-a-cover", "notion:bbbb"),
        new("c", "a", "Oracle", null, null, null),
    ]);

    [Fact]
    public async Task Import_creates_the_tree_private_to_the_importer()
    {
        using var t = new TestDb();
        var a = t.AddUser("a"); var b = t.AddUser("b");
        var r = await Svc(t).ImportAsync(a.Id, Tree(), Ct);
        Assert.True(r.Success, r.Error);
        var ids = r.Data!.Pages.ToDictionary(p => p.Key, p => p.Id);

        var root = (await Svc(t).GetPageAsync(a.Id, ids["root"], Ct)).Data!;
        Assert.Null(root.ParentId);
        Assert.Equal("notion", root.ImportSource);
        Assert.Equal(["Install Aplikasi Standard SJA", "Printer"], root.Children.Select(c => c.Title));
        var page = (await Svc(t).GetPageAsync(a.Id, ids["a"], Ct)).Data!;
        Assert.Equal((ids["root"], "💻", "teal"), (page.ParentId!.Value, page.Emoji!, page.Cover!));
        Assert.Null((await Svc(t).GetPageAsync(a.Id, ids["b"], Ct)).Data!.Cover);
        Assert.Equal(ids["a"], (await Svc(t).GetPageAsync(a.Id, ids["c"], Ct)).Data!.ParentId);
        Assert.False(root.IsShared);
        Assert.Equal(NoteAccess.None, await Svc(t).GetAccessAsync(b.Id, ids["c"], Ct));

        // The content arrives afterwards through the normal snapshot (version 0 → 1).
        var snap = await Svc(t).SnapshotAsync(a.Id, ids["a"], new NoteSnapshotRequest(Convert.ToBase64String([1, 2]), 0, [], "text", 0, 0), Ct);
        Assert.True(snap.Success, snap.Error);
    }

    [Fact]
    public async Task Import_under_a_shared_page_inherits_its_sharing_and_needs_edit_access()
    {
        using var t = new TestDb();
        var a = t.AddUser("a"); var b = t.AddUser("b"); var v = t.AddUser("v");
        var shared = (await Svc(t).CreateAsync(a.Id, new CreateNoteRequest(null, "Team", null, null, null, null), Ct)).Data!.Id;
        await Svc(t).AddShareAsync(a.Id, shared, new AddNoteShareRequest(b.Id, null, NoteLevels.Edit), Ct);
        await Svc(t).AddShareAsync(a.Id, shared, new AddNoteShareRequest(v.Id, null, NoteLevels.View), Ct);

        var r = await Svc(t).ImportAsync(b.Id, Tree(shared), Ct);
        Assert.True(r.Success, r.Error);
        var leaf = r.Data!.Pages.Single(p => p.Key == "c").Id;
        Assert.Equal(NoteAccess.Owner, await Svc(t).GetAccessAsync(b.Id, leaf, Ct));
        Assert.Equal(NoteAccess.Owner, await Svc(t).GetAccessAsync(a.Id, leaf, Ct));
        Assert.Equal(NoteAccess.View, await Svc(t).GetAccessAsync(v.Id, leaf, Ct));

        Assert.Equal("forbidden", (await Svc(t).ImportAsync(v.Id, Tree(shared), Ct)).Error);
        Assert.Equal("not_found", (await Svc(t).ImportAsync(t.AddUser("x").Id, Tree(shared), Ct)).Error);
    }

    [Fact]
    public async Task Import_rejects_bad_trees_sources_and_too_many_pages()
    {
        using var t = new TestDb();
        var a = t.AddUser("a");
        Assert.Equal("invalid_import", (await Svc(t).ImportAsync(a.Id, new(null, "notion", []), Ct)).Error);
        Assert.Equal("invalid_import", (await Svc(t).ImportAsync(a.Id, Tree() with { Source = "docx" }, Ct)).Error);
        Assert.Equal("invalid_import", (await Svc(t).ImportAsync(a.Id, new(null, "notion", [new("x", "later", "X", null, null, null), new("later", null, "L", null, null, null)]), Ct)).Error);
        Assert.Equal("invalid_import", (await Svc(t).ImportAsync(a.Id, new(null, "notion", [new("x", null, "X", null, null, null), new("x", null, "X", null, null, null)]), Ct)).Error);
        var many = Enumerable.Range(0, NoteImport.MaxPages + 1).Select(i => new ImportNotePage($"p{i}", null, $"P{i}", null, null, null)).ToList();
        Assert.Equal("invalid_import", (await Svc(t).ImportAsync(a.Id, new(null, "markdown", many), Ct)).Error);
    }

    [Fact]
    public async Task Previous_imports_are_found_per_owner_and_ignore_trashed_pages()
    {
        using var t = new TestDb();
        var a = t.AddUser("a"); var b = t.AddUser("b");
        var first = (await Svc(t).ImportAsync(a.Id, Tree(), Ct)).Data!;
        await Svc(t).ImportAsync(b.Id, Tree(), Ct);
        var key = "notion:e5f02d5e594a463b91180a4ad8ee3cec";

        var found = await Svc(t).FindImportedAsync(a.Id, [key, "notion:none"], Ct);
        Assert.Equal(first.Pages.Single(p => p.Key == "a").Id, Assert.Single(found).Id);

        await Svc(t).TrashAsync(a.Id, first.Pages.Single(p => p.Key == "a").Id, Ct);
        Assert.Empty(await Svc(t).FindImportedAsync(a.Id, [key], Ct));
    }
}
