using IPop.Core.Entities;
using IPop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace IPop.Tests;

/// Behavior of the read-only directory-tree and org-tree data access — same seam as
/// HrisSyncServiceTests: SQLite in-memory via TestDb, asserting persisted state.
public class DirectoryTreeTests
{
    [Fact]
    public async Task DirGroups_ReturnsActiveGroups_WithMembers()
    {
        using var t = new TestDb();
        var user = t.AddUser("20080115");
        var group = new DirGroup { Id = Guid.NewGuid(), Name = "Engineering", SortOrder = 1 };
        t.Db.DirGroups.Add(group);
        t.Db.Add(new UserDirGroup { UserId = user.Id, DirGroupId = group.Id });
        await t.Db.SaveChangesAsync();
        t.Db.ChangeTracker.Clear();

        var groups = await t.Verify().DirGroups
            .Include(g => g.Members).ThenInclude(m => m.User).ThenInclude(u => u.Department)
            .Include(g => g.Members).ThenInclude(m => m.User).ThenInclude(u => u.Site)
            .Where(g => g.IsActive).OrderBy(g => g.SortOrder).ToListAsync();

        var dto = groups.Select(g => new
        {
            g.Id, g.Name, g.ParentId, g.SortOrder,
            Members = g.Members.Select(m => new { m.User.Nik, m.User.Name })
        }).ToArray();

        Assert.Single(dto);
        Assert.Equal("Engineering", dto[0].Name);
        Assert.Single(dto[0].Members);
        Assert.Equal("20080115", dto[0].Members.First().Nik);
    }

    [Fact]
    public async Task DirGroups_FiltersInactive()
    {
        using var t = new TestDb();
        t.Db.DirGroups.AddRange(
            new DirGroup { Id = Guid.NewGuid(), Name = "Active", SortOrder = 1 },
            new DirGroup { Id = Guid.NewGuid(), Name = "Inactive", SortOrder = 2, IsActive = false });
        await t.Db.SaveChangesAsync();
        t.Db.ChangeTracker.Clear();

        var groups = await t.Verify().DirGroups
            .Where(g => g.IsActive).OrderBy(g => g.SortOrder).ToListAsync();

        Assert.Single(groups);
        Assert.Equal("Active", groups[0].Name);
    }

    [Fact]
    public async Task DirGroups_NestedParents_ReturnChildren()
    {
        using var t = new TestDb();
        var parent = new DirGroup { Id = Guid.NewGuid(), Name = "Engineering", SortOrder = 1 };
        var child = new DirGroup { Id = Guid.NewGuid(), Name = "Frontend", ParentId = parent.Id, SortOrder = 1 };
        t.Db.DirGroups.AddRange(parent, child);
        await t.Db.SaveChangesAsync();
        t.Db.ChangeTracker.Clear();

        var groups = await t.Verify().DirGroups.OrderBy(g => g.SortOrder).ToListAsync();

        Assert.Equal(2, groups.Count);
        Assert.Equal(parent.Id, groups.First(g => g.Name == "Frontend").ParentId);
    }

    [Fact]
    public async Task OrgNodes_ReturnsActiveNodes_WithMembers()
    {
        using var t = new TestDb();
        var user = t.AddUser("20110543");
        var ceo = new OrgNode { Id = Guid.NewGuid(), Name = "CEO", SortOrder = 1 };
        var vp = new OrgNode { Id = Guid.NewGuid(), Name = "VP Eng", ParentId = ceo.Id, SortOrder = 1 };
        t.Db.OrgNodes.AddRange(ceo, vp);
        t.Db.Add(new UserOrgNode { UserId = user.Id, OrgNodeId = vp.Id });
        await t.Db.SaveChangesAsync();
        t.Db.ChangeTracker.Clear();

        var nodes = await t.Verify().OrgNodes
            .Include(n => n.Members).ThenInclude(m => m.User)
            .Where(n => n.IsActive).OrderBy(n => n.SortOrder).ToListAsync();

        Assert.Equal(2, nodes.Count);
        var vpNode = nodes.Single(n => n.Name == "VP Eng");
        Assert.Equal(ceo.Id, vpNode.ParentId);
        Assert.Single(vpNode.Members);
        Assert.Equal("20110543", vpNode.Members.First().User.Nik);
    }

    [Fact]
    public async Task UserDelete_CascadesMembershipRows()
    {
        using var t = new TestDb();
        var user = t.AddUser("20080115");
        var group = new DirGroup { Id = Guid.NewGuid(), Name = "Eng", SortOrder = 1 };
        t.Db.DirGroups.Add(group);
        t.Db.Add(new UserDirGroup { UserId = user.Id, DirGroupId = group.Id });
        await t.Db.SaveChangesAsync();

        t.Db.Users.Remove(user);
        await t.Db.SaveChangesAsync();

        await using var v = t.Verify();
        Assert.Equal(0, await v.UserDirGroups.CountAsync());
        Assert.Single(await v.DirGroups.ToListAsync()); // group survives, membership gone
    }

    [Fact]
    public async Task GroupDelete_CascadesMembershipRows()
    {
        using var t = new TestDb();
        var user = t.AddUser("20080115");
        var group = new DirGroup { Id = Guid.NewGuid(), Name = "Eng", SortOrder = 1 };
        t.Db.DirGroups.Add(group);
        t.Db.Add(new UserDirGroup { UserId = user.Id, DirGroupId = group.Id });
        await t.Db.SaveChangesAsync();

        t.Db.DirGroups.Remove(group);
        await t.Db.SaveChangesAsync();

        await using var v = t.Verify();
        Assert.Equal(0, await v.UserDirGroups.CountAsync());
        Assert.Single(await v.Users.ToListAsync()); // user survives, membership gone
    }
}
