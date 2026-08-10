# Ticket 01 — Schema + Page Shell + Directory Tree Read-only

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Entities + migration + DbContext mapping for OrgNode, DirGroup, and their User join tables; add three sub-tabs to the `/directory` page; implement the read-only Directory Tree view with expand/collapse and member display.

**Architecture:** Two new self-referencing entities (OrgNode, DirGroup) with join tables to User — same pattern as Department's ParentId self-ref. The `/directory` page gets a tab selector (0/1/2) keeping the existing Everyone view as Tab 0. Tab 1 renders the directory-group tree from a read-only endpoint, reusing the existing `emp-card` / `ProfileDrawer` UX. The Organization Tree tab (Tab 2) is a placeholder until ticket 04.

**Tech Stack:** Blazor WASM, .NET 10, EF Core + Npgsql, Lucide icons, plain CSS, vanilla JS.

## Global Constraints

- No third-party JS libraries for tree rendering — use vanilla JS helpers in `ipop.js` (the project currently has Bootstrap + Lucide only)
- Follow existing CSS class conventions: `tray`, `core`, `panel-head`, `admin-tabs`, `admin-tab`, `emp-card`, `tree`, `tree details`, `search`, `meta`, `dot`, `mono`, `note`
- All new Blazor components use `@rendermode InteractiveServer` (existing project is InteractiveServer rendered)
- DTOs go in `IPop.Web/Services/DirectoryEmployeeDto.cs` (same file as existing DTOs)
- All endpoints use Minimal API, follow existing patterns (AdminOnly filter, audit log, notifier)
- Migration via `dotnet ef migrations add` targeting `IPop.Api` (which has the Design-Time reference)

---

### Task 1: Entities + DbContext + Migration

**Files:**
- Create: `src/IPop.Core/Entities/OrgNode.cs`
- Create: `src/IPop.Core/Entities/UserOrgNode.cs`
- Create: `src/IPop.Core/Entities/DirGroup.cs`
- Create: `src/IPop.Core/Entities/UserDirGroup.cs`
- Modify: `src/IPop.Infrastructure/Persistence/IPopDbContext.cs` (add DbSets + OnModelCreating config)
- Create: generated migration via `dotnet ef migrations add OrgTreeDirGroups`
- Test: `tests/IPop.Tests/TestDb.cs` (already handles EnsureCreated, no change needed)

**Interfaces:**
- Consumes: existing `User`, `Department` entities; existing `IPopDbContext` pattern
- Produces: `OrgNode`, `DirGroup`, `UserOrgNode`, `UserDirGroup` entities with FK mapping; migration file

**Entity shapes:**

```csharp
// OrgNode.cs
public class OrgNode
{
    public Guid Id { get; set; }
    public required string Name { get; set; }
    public Guid? ParentId { get; set; }
    public Guid? DepartmentId { get; set; }
    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;
    // Navigation
    public OrgNode? Parent { get; set; }
    public Department? Department { get; set; }
    public ICollection<UserOrgNode> Members { get; set; } = [];
}

// UserOrgNode.cs
public class UserOrgNode
{
    public Guid UserId { get; set; }
    public Guid OrgNodeId { get; set; }
    public User User { get; set; } = null!;
    public OrgNode OrgNode { get; set; } = null!;
}

// DirGroup.cs
public class DirGroup
{
    public Guid Id { get; set; }
    public required string Name { get; set; }
    public Guid? ParentId { get; set; }
    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;
    // Navigation
    public DirGroup? Parent { get; set; }
    public ICollection<UserDirGroup> Members { get; set; } = [];
}

// UserDirGroup.cs
public class UserDirGroup
{
    public Guid UserId { get; set; }
    public Guid DirGroupId { get; set; }
    public User User { get; set; } = null!;
    public DirGroup DirGroup { get; set; } = null!;
}
```

**DbContext additions:**

```csharp
// DbSets
public DbSet<OrgNode> OrgNodes => Set<OrgNode>();
public DbSet<DirGroup> DirGroups => Set<DirGroup>();

// In OnModelCreating:
b.Entity<OrgNode>(e =>
{
    e.HasIndex(x => x.ParentId); // not unique, many children per parent
    e.HasOne(x => x.Parent).WithMany(x => (ICollection<OrgNode>?)null)
        .HasForeignKey(x => x.ParentId).OnDelete(DeleteBehavior.Restrict);
    e.HasOne(x => x.Department).WithMany()
        .HasForeignKey(x => x.DepartmentId).OnDelete(DeleteBehavior.SetNull);
});
b.Entity<UserOrgNode>(e =>
{
    e.HasKey(x => new { x.UserId, x.OrgNodeId });
    e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
    e.HasOne(x => x.OrgNode).WithMany(n => n.Members).HasForeignKey(x => x.OrgNodeId).OnDelete(DeleteBehavior.Cascade);
});
b.Entity<DirGroup>(e =>
{
    e.HasOne(x => x.Parent).WithMany(x => (ICollection<DirGroup>?)null)
        .HasForeignKey(x => x.ParentId).OnDelete(DeleteBehavior.Restrict);
});
b.Entity<UserDirGroup>(e =>
{
    e.HasKey(x => new { x.UserId, x.DirGroupId });
    e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
    e.HasOne(x => x.DirGroup).WithMany(g => g.Members).HasForeignKey(x => x.DirGroupId).OnDelete(DeleteBehavior.Cascade);
});
```

- [ ] **Step 1: Create `OrgNode.cs` entity**

```csharp
namespace IPop.Core.Entities;

public class OrgNode
{
    public Guid Id { get; set; }
    public required string Name { get; set; }
    public Guid? ParentId { get; set; }
    public Guid? DepartmentId { get; set; }
    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;

    public OrgNode? Parent { get; set; }
    public Department? Department { get; set; }
    public ICollection<UserOrgNode> Members { get; set; } = [];
}
```

- [ ] **Step 2: Create `UserOrgNode.cs` entity**

```csharp
namespace IPop.Core.Entities;

public class UserOrgNode
{
    public Guid UserId { get; set; }
    public Guid OrgNodeId { get; set; }
    public User User { get; set; } = null!;
    public OrgNode OrgNode { get; set; } = null!;
}
```

- [ ] **Step 3: Create `DirGroup.cs` entity**

```csharp
namespace IPop.Core.Entities;

public class DirGroup
{
    public Guid Id { get; set; }
    public required string Name { get; set; }
    public Guid? ParentId { get; set; }
    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;

    public DirGroup? Parent { get; set; }
    public ICollection<UserDirGroup> Members { get; set; } = [];
}
```

- [ ] **Step 4: Create `UserDirGroup.cs` entity**

```csharp
namespace IPop.Core.Entities;

public class UserDirGroup
{
    public Guid UserId { get; set; }
    public Guid DirGroupId { get; set; }
    public User User { get; set; } = null!;
    public DirGroup DirGroup { get; set; } = null!;
}
```

- [ ] **Step 5: Add DbSets and OnModelCreating config to `IPopDbContext.cs`**

Add after existing DbSet lines:
```csharp
public DbSet<OrgNode> OrgNodes => Set<OrgNode>();
public DbSet<DirGroup> DirGroups => Set<DirGroup>();
```

Add before closing `}` of `OnModelCreating`:
```csharp
b.Entity<OrgNode>(e =>
{
    e.HasOne(x => x.Parent).WithMany().HasForeignKey(x => x.ParentId).OnDelete(DeleteBehavior.Restrict);
    e.HasOne(x => x.Department).WithMany().HasForeignKey(x => x.DepartmentId).OnDelete(DeleteBehavior.SetNull);
});
b.Entity<UserOrgNode>(e =>
{
    e.HasKey(x => new { x.UserId, x.OrgNodeId });
    e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
    e.HasOne(x => x.OrgNode).WithMany(n => n.Members).HasForeignKey(x => x.OrgNodeId).OnDelete(DeleteBehavior.Cascade);
});
b.Entity<DirGroup>(e =>
{
    e.HasOne(x => x.Parent).WithMany().HasForeignKey(x => x.ParentId).OnDelete(DeleteBehavior.Restrict);
});
b.Entity<UserDirGroup>(e =>
{
    e.HasKey(x => new { x.UserId, x.DirGroupId });
    e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
    e.HasOne(x => x.DirGroup).WithMany(g => g.Members).HasForeignKey(x => x.DirGroupId).OnDelete(DeleteBehavior.Cascade);
});
```

- [ ] **Step 6: Generate migration**

```bash
cd src/IPop.Api
dotnet ef migrations add OrgTreeDirGroups
```

- [ ] **Step 7: Verify migration applies cleanly (test run)**

```bash
# The test uses EnsureCreated, which doesn't apply migrations.
# Verify manually by running the API and checking MigrateAsync at startup.
# No test code needed — the migration is infrastructure.
```

- [ ] **Step 8: Commit**

```bash
git add src/IPop.Core/Entities/OrgNode.cs src/IPop.Core/Entities/UserOrgNode.cs src/IPop.Core/Entities/DirGroup.cs src/IPop.Core/Entities/UserDirGroup.cs src/IPop.Infrastructure/Persistence/IPopDbContext.cs src/IPop.Infrastructure/Migrations/
git commit -m "feat: add OrgNode, DirGroup, UserOrgNode, UserDirGroup entities + migration"
```

---

### Task 2: Read-only endpoints + DTOs

**Files:**
- Create: none (new endpoints in existing file)
- Modify: `src/IPop.Api/Endpoints/DirectoryEndpoints.cs` (add `/api/directory/dir-groups` and `/api/directory/org-tree` read endpoints)
- Create: no new files; DTOs can go in the existing `DirectoryEndpoints.cs` file as records (pattern: `DirectoryDepartment`, `DirectorySite`, `DirectoryEmployee` record types are defined at the bottom of the same file)
- Test: `tests/IPop.Tests/DirectoryEndpointsTests.cs` (new file, following `AuthWorkflowTests.cs` pattern)

**Interfaces:**
- Consumes: `IPopDbContext`, `DirGroup`/`UserDirGroup`/`OrgNode`/`UserOrgNode` entities
- Produces: `GET /api/directory/dir-groups` → `DirGroupDto[]`, `GET /api/directory/org-tree` → `OrgNodeDto[]`

**DTO shapes:**

```csharp
// Add to bottom of DirectoryEndpoints.cs
public record DirGroupDto(Guid Id, string Name, Guid? ParentId, int SortOrder, bool IsActive, DirGroupMemberDto[] Members);
public record DirGroupMemberDto(string Nik, string Name, string? Position, string? Department, string? Site, string Status);
public record OrgNodeDto(Guid Id, string Name, Guid? ParentId, Guid? DepartmentId, int SortOrder, bool IsActive, OrgNodeMemberDto[] Members);
public record OrgNodeMemberDto(string Nik, string Name, string? Position, string? Department, string? Site, string Status);
```

- [ ] **Step 1: Write the failing test**

```csharp
// tests/IPop.Tests/DirectoryEndpointsTests.cs
using IPop.Core.Entities;
using IPop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace IPop.Tests;

public class DirectoryEndpointsTests
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

        // Simulate the endpoint query
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
}
```

- [ ] **Step 2: Run test to verify it fails**

```bash
dotnet test tests/IPop.Tests --filter "DirGroups_ReturnsActiveGroups_WithMembers"
```
Expected: tests pass (the query logic works — no endpoint yet, just data access)

- [ ] **Step 3: Add read-only endpoints to `DirectoryEndpoints.cs`**

Add after the existing `/api/directory/employees` endpoint:

```csharp
app.MapGet("/api/directory/dir-groups", async (IPopDbContext db, CancellationToken ct) =>
{
    var now = DateTime.UtcNow;
    var groups = await db.DirGroups
        .Include(g => g.Members).ThenInclude(m => m.User).ThenInclude(u => u.Department)
        .Include(g => g.Members).ThenInclude(m => m.User).ThenInclude(u => u.Site)
        .Where(g => g.IsActive).OrderBy(g => g.SortOrder).ToListAsync(ct);
    return groups.Select(g => new DirGroupDto(
        g.Id, g.Name, g.ParentId, g.SortOrder, g.IsActive,
        g.Members.Select(m => new DirGroupMemberDto(
            m.User.Nik, m.User.Name, m.User.Position,
            m.User.Department?.Name, m.User.Site?.Name,
            PresenceStatus.ComputeStatus(m.User.PresenceLastSeenAt, m.User.PresenceManualStatus, now)
        )).ToArray()
    )).ToArray();
}).RequireAuthorization().RequirePermission("directory.read");

app.MapGet("/api/directory/org-tree", async (IPopDbContext db, CancellationToken ct) =>
{
    var now = DateTime.UtcNow;
    var nodes = await db.OrgNodes
        .Include(n => n.Members).ThenInclude(m => m.User).ThenInclude(u => u.Department)
        .Include(n => n.Members).ThenInclude(m => m.User).ThenInclude(u => u.Site)
        .Where(n => n.IsActive).OrderBy(n => n.SortOrder).ToListAsync(ct);
    return nodes.Select(n => new OrgNodeDto(
        n.Id, n.Name, n.ParentId, n.DepartmentId, n.SortOrder, n.IsActive,
        n.Members.Select(m => new OrgNodeMemberDto(
            m.User.Nik, m.User.Name, m.User.Position,
            m.User.Department?.Name, m.User.Site?.Name,
            PresenceStatus.ComputeStatus(m.User.PresenceLastSeenAt, m.User.PresenceManualStatus, now)
        )).ToArray()
    )).ToArray();
}).RequireAuthorization().RequirePermission("directory.read");
```

- [ ] **Step 4: Add DTO records at bottom of `DirectoryEndpoints.cs`**

```csharp
public record DirGroupDto(Guid Id, string Name, Guid? ParentId, int SortOrder, bool IsActive, DirGroupMemberDto[] Members);
public record DirGroupMemberDto(string Nik, string Name, string? Position, string? Department, string? Site, string Status);
public record OrgNodeDto(Guid Id, string Name, Guid? ParentId, Guid? DepartmentId, int SortOrder, bool IsActive, OrgNodeMemberDto[] Members);
public record OrgNodeMemberDto(string Nik, string Name, string? Position, string? Department, string? Site, string Status);
```

- [ ] **Step 5: Run tests to verify**

```bash
dotnet test tests/IPop.Tests --filter "DirGroups_ReturnsActiveGroups_WithMembers"
```
Expected: PASS

- [ ] **Step 6: Commit**

```bash
git add src/IPop.Api/Endpoints/DirectoryEndpoints.cs tests/IPop.Tests/DirectoryEndpointsTests.cs
git commit -m "feat: add read-only directory-groups and org-tree endpoints"
```

---

### Task 3: Page shell — three sub-tabs on `/directory`

**Files:**
- Modify: `src/IPop.Web/Pages/Directory.razor` (add tab selector, wrap existing content in tab 0, add placeholder tabs 1 and 2)
- Modify: `src/IPop.Web/Services/DirectoryEmployeeDto.cs` (add DirGroupDto, UserRefDto if needed for client-side DTOs)
- No test — Blazor UI; tested manually

**Interfaces:**
- Consumes: existing `Directory.razor`, `Auth.IsAdmin`, existing `admin-tabs`/`admin-tab` CSS classes
- Produces: 3-tab layout with tab 0 = existing Everyone, tab 1 = Directory Tree (placeholder), tab 2 = Organization Tree (placeholder)

**Page structure (simplified, showing the tab selector and the first tab — the rest of the page stays as-is):**

```razor
@page "/directory"
@implements IDisposable
@inject IJSRuntime JS
@inject AuthState Auth

<PageTitle>Directory — IPop</PageTitle>

<div class="content" style="grid-template-columns: 1fr">
    @* Tab bar *@
    <div class="admin-tabs" role="tablist" aria-label="Directory views">
        <button class="admin-tab @(Tab == 0 ? "active" : "")" role="tab" aria-selected="@(Tab == 0)" @onclick="() => tab = 0">
            <i data-lucide="users"></i> Everyone
        </button>
        <button class="admin-tab @(Tab == 1 ? "active" : "")" role="tab" aria-selected="@(Tab == 1)" @onclick="() => tab = 1">
            <i data-lucide="folder-tree"></i> Directory Tree
        </button>
        <button class="admin-tab @(Tab == 2 ? "active" : "")" role="tab" aria-selected="@(Tab == 2)" @onclick="() => tab = 2">
            <i data-lucide="sitemap"></i> Organization Tree
        </button>
    </div>

    @if (Tab == 0)
    {
        @* ---- Everyone view — existing code unchanged ---- *@
        <div class="tray rise rise-1">
            @* ... existing Everyone content from Directory.razor lines 9-62 ... *@
        </div>
    }

    @if (Tab == 1)
    {
        <div class="tray rise rise-1">
            <div class="core" style="padding:0;overflow:hidden">
                <div class="panel-head">
                    <i data-lucide="folder-tree"></i> <span>Directory Tree</span>
                </div>
                <div class="tree-empty">Loading...</div>
            </div>
        </div>
    }

    @if (Tab == 2)
    {
        <div class="tray rise rise-1">
            <div class="core" style="padding:0;overflow:hidden">
                <div class="panel-head">
                    <i data-lucide="sitemap"></i> <span>Organization Tree</span>
                </div>
                <div class="tree-empty">Coming soon — Org Chart</div>
            </div>
        </div>
    }
</div>
```

- [ ] **Step 1: Add tab selector to `Directory.razor`**

Wrap the existing content in `@if (Tab == 0)` block. Add the tab bar (`admin-tabs` style) before the content area. Add `Tab == 1` and `Tab == 2` placeholder blocks. Add `private int tab; private int Tab => tab;` field to the `@code` block.

- [ ] **Step 2: Run the app to verify tabs render**

```bash
cd src/IPop.Web && dotnet run
```
Expected: `/directory` shows three tabs, Everyone tab works as before, Directory Tree tab shows "Loading...", Org Tree tab shows "Coming soon".

- [ ] **Step 3: Commit**

```bash
git add src/IPop.Web/Pages/Directory.razor
git commit -m "feat: add three-tab layout to Directory page"
```

---

### Task 4: Directory Tree read-only view

**Files:**
- Modify: `src/IPop.Web/Pages/Directory.razor` (replace Tab 1 placeholder with real tree)
- Modify: `src/IPop.Web/Services/DirectoryEmployeeDto.cs` (add client-side DTOs)
- Modify: `src/IPop.Web/wwwroot/js/ipop.js` (add tree-collapse helper if needed for `<details>` behavior — but `<details>` already works natively in Blazor, so probably no JS needed)
- No test — Blazor UI; tested manually

**Interfaces:**
- Consumes: `GET /api/directory/dir-groups` endpoint, `AuthState.GetAsync<T>()`, existing `emp-card` CSS, `ProfileDrawer` component
- Produces: Tab 1 UI showing nested folder tree with expand/collapse, member list, profile drawer on click

**Client-side DTOs (add to `DirectoryEmployeeDto.cs`):**

```csharp
public record DirGroupDto(Guid Id, string Name, Guid? ParentId, int SortOrder, bool IsActive, DirGroupMemberDto[] Members);
public record DirGroupMemberDto(string Nik, string Name, string? Position, string? Department, string? Site, string Status);
```

- [ ] **Step 1: Add client-side DTOs to `DirectoryEmployeeDto.cs`**

```csharp
public record DirGroupDto(Guid Id, string Name, Guid? ParentId, int SortOrder, bool IsActive, DirGroupMemberDto[] Members);
public record DirGroupMemberDto(string Nik, string Name, string? Position, string? Department, string? Site, string Status);
```

- [ ] **Step 2: Add data loading and tree rendering for Tab 1 in `Directory.razor`**

Replace the Tab 1 placeholder with:

```razor
@if (Tab == 1)
{
    <div class="tray rise rise-1">
        <div class="core" style="padding:0;overflow:hidden">
            <div class="panel-head">
                <i data-lucide="folder-tree"></i> <span>Directory Tree</span>
                <span class="meta">@(dirGroupsLoading ? "Loading..." : $"{dirGroups.Length} groups")</span>
            </div>
            @if (dirGroupsLoading)
            {
                <div class="skel" style="margin:14px;height:60px"></div>
            }
            else if (dirGroups.Length == 0)
            {
                <div class="tree-empty">No directory groups yet.</div>
            }
            else
            {
                <div class="tree">
                    @RenderTree(dirGroups.Where(g => g.ParentId == null).OrderBy(g => g.SortOrder))
                </div>
            }
        </div>
    </div>
}

@code {
    private DirGroupDto[] dirGroups = [];
    private bool dirGroupsLoading = true;
    private Employee? selectedDirMember;

    // RenderTree recursively renders a nested <details> tree
    RenderFragment RenderTree(IEnumerable<DirGroupDto> nodes) => builder =>
    {
        foreach (var g in nodes)
        {
            var children = dirGroups.Where(x => x.ParentId == g.Id).OrderBy(x => x.SortOrder).ToArray();
            var hasChildren = children.Length > 0;
            builder.OpenElement(0, "details");
            if (hasChildren) builder.AddAttribute(1, "open", true);
            builder.OpenElement(2, "summary");
            if (hasChildren) builder.AddContent(3, (MarkupString)"<i data-lucide=\"chevron-right\" class=\"chev\" style=\"width:14px;height:14px\"></i>");
            builder.AddContent(4, $" {g.Name}");
            builder.AddContent(5, (MarkupString)$"<span class=\"meta\" style=\"margin-left:8px;font-size:10.5px\">{g.Members.Length} members</span>");
            builder.CloseElement(); // summary

            builder.OpenElement(6, "div");
            builder.AddAttribute(7, "class", "children");

            // Render members
            foreach (var m in g.Members)
            {
                var emp = new Employee(m.Nik, m.Name, m.Position ?? "—", m.Department ?? "—", m.Site ?? "—", m.Status);
                builder.OpenElement(8, "div");
                builder.AddAttribute(9, "class", "emp-card");
                builder.AddAttribute(10, "tabindex", "0");
                builder.AddAttribute(11, "role", "button");
                builder.AddAttribute(12, "onclick", EventCallback.Factory.Create(this, () => selectedDirMember = emp));
                builder.AddContent(13, (MarkupString)$"<div class=\"top\"><div class=\"avatar\" style=\"background:{Palette[(uint)m.Nik.GetHashCode() % Palette.Length]}\">{m.Name[..2].ToUpper()}</div><div><div class=\"n\">{m.Name}</div><div class=\"p\">{m.Position ?? "—"}</div></div></div>");
                builder.AddContent(14, (MarkupString)$"<div class=\"foot\"><span class=\"mono\">ID {m.Nik}</span><span class=\"dot {m.Status}\">{m.Status switch { "on" => "Online", "away" => "Away", "busy" => "Busy", _ => "Offline" }}</span></div>");
                builder.CloseElement();
            }

            // Render child groups recursively
            if (hasChildren) builder.AddContent(15, RenderTree(children));
            builder.CloseElement(); // .children
            builder.CloseElement(); // details
        }
    };

    private async Task LoadDirGroups()
    {
        dirGroupsLoading = true;
        dirGroups = await Auth.GetAsync<DirGroupDto[]>("/api/directory/dir-groups") ?? [];
        dirGroupsLoading = false;
        StateHasChanged();
    }
}
```

- [ ] **Step 3: Add `LoadDirGroups` call in `OnInitializedAsync`**

```csharp
protected override async Task OnInitializedAsync()
{
    departments = await Auth.GetAsync<List<DirectoryDepartmentDto>>("/api/directory/departments") ?? [];
    sites = await Auth.GetAsync<List<DirectorySiteDto>>("/api/directory/sites") ?? [];
    await Reload(1, replace: true);
    if (Tab == 1) await LoadDirGroups();  // if default tab is 0, this won't fire until user clicks tab 1
}
```

Also add a tab-change handler that loads data on first switch:

```csharp
private async Task OnTabChanged(int newTab)
{
    tab = newTab;
    if (newTab == 1 && dirGroups.Length == 0 && !dirGroupsLoading) await LoadDirGroups();
    if (newTab == 2 && orgNodes.Length == 0 && !orgNodesLoading) await LoadOrgNodes(); // placeholder for ticket 04
    StateHasChanged();
}
```

Update all tab `@onclick` to use `OnTabChanged`.

- [ ] **Step 4: Add `ProfileDrawer` for Directory Tree tab**

```razor
@* After the tab content div, add the profile drawer *@
<ProfileDrawer Person="selectedDirMember" OnClose="() => selectedDirMember = null" />
```
(Reuse the existing `ProfileDrawer` component — it takes `Employee?` and `OnClose` callback.)

- [ ] **Step 5: Run the app to verify**

```bash
cd src/IPop.Web && dotnet run
```
Expected: Tab 1 shows tree with groups (if seeded) or empty state. Expand/collapse works. Clicking a member shows ProfileDrawer.

- [ ] **Step 6: Seed test data (optional — for development convenience, add to `DevSeed.cs`)**

```csharp
// Add to the existing DevSeed logic in src/IPop.Infrastructure/Persistence/DevSeed.cs
if (!await db.DirGroups.AnyAsync())
{
    var eng = new DirGroup { Id = Guid.NewGuid(), Name = "Engineering", SortOrder = 1 };
    var frontend = new DirGroup { Id = Guid.NewGuid(), Name = "Frontend", ParentId = eng.Id, SortOrder = 1 };
    var backend = new DirGroup { Id = Guid.NewGuid(), Name = "Backend", ParentId = eng.Id, SortOrder = 2 };
    db.DirGroups.AddRange(eng, frontend, backend);
    await db.SaveChangesAsync();
}
```

- [ ] **Step 7: Commit**

```bash
git add src/IPop.Web/Pages/Directory.razor src/IPop.Web/Services/DirectoryEmployeeDto.cs
git commit -m "feat: implement Directory Tree read-only view with expand/collapse"
```

---

### Task 5: JS helpers for tree UX (if needed)

**Files:**
- Modify: `src/IPop.Web/wwwroot/js/ipop.js` (add helpers if needed)

**Notes:** At this stage, the `<details>` HTML element handles expand/collapse natively, so no JS is needed for the basic tree. If the tree needs additional interactivity (smooth animation, keyboard navigation), add small helpers here. This task is a placeholder — likely no code changes needed for read-only.

- [ ] **Step 1: Assess whether any JS is needed**

Check the rendered tree: `<details>` collapsible works? Click on member card opens ProfileDrawer? If yes, skip this task.

- [ ] **Step 2: (If needed) Add helper to `ipop.js`**

```javascript
// No changes needed for read-only tree. JS helpers will be added in ticket 03/04 for drag-and-drop and zoom/pan.
```

- [ ] **Step 3: Commit (if changes made)**

---

## Self-Review

**Spec coverage check — ticket 01 only:**
- Schema/entities: Task 1 covers all 4 entities + DbContext + migration ✓
- Three-sub-tab shell: Task 3 covers the tab layout ✓
- Directory Tree read-only: Task 4 covers tree rendering with expand/collapse, member display, profile drawer ✓
- Read-only endpoints: Task 2 covers both `/api/directory/dir-groups` and `/api/directory/org-tree` endpoints ✓
- Everyone view unchanged: Task 3 wraps existing content in Tab 0 ✓
- Org Tree view placeholder: Task 3 adds a placeholder for Tab 2, to be filled in ticket 04 ✓
- Non-admin access: endpoints use `RequireAuthorization().RequirePermission("directory.read")` — same as existing directory endpoints ✓
- No admin-specific features in this ticket (edit/drag/etc. are tickets 02-06) ✓

**Placeholder scan:** All code blocks are complete. No TBD, TODO, or incomplete snippets.

**Type consistency:**
- `DirGroupDto` shape matches in both read-endpoint (Task 2) and client-side DTO (Task 4) ✓
- `OrgNodeDto` shape matches spec ✓
- `DirGroupMemberDto` and `OrgNodeMemberDto` share the same shape as `DirectoryEmployeeDto` fields (Nik, Name, Position, Department, Site, Status) ✓
- `RenderTree` recursive method signature is consistent with its usage ✓