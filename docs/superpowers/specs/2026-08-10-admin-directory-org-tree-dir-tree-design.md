# Admin Directory — Org Tree & Directory Tree Design

## Problem Statement

The Directory page at `/directory` currently shows a flat list of every employee with search, filter, and presence. The Administration page (tab "Users") lets an admin manage users (create, edit, deactivate, etc.) but only through a one-at-a-time drawer that edits department and site via dropdown selects. There is no visual tree view of the organization structure, and no way to batch-assign users to groups via drag-and-drop.

The user wants the Directory page split into three views accessible to all employees (read-only browse) and administrators (full edit):

1. **Everyone** — the existing flat employee grid (preserved)
2. **Directory Tree** — custom folder/grouping tree with drag-and-drop user assignment from a searchable user panel
3. **Organization Tree** — a full org chart (positions, departments, collapsible per department) built from scratch by admin, with zoom, pan, drag, and edit

## Solution

The single `/directory` page gets three sub-tabs: `Everyone | Directory Tree | Organization Tree`. Every authenticated user can see all three views. Edit controls (add, delete, rename, drag-assign) only appear when the logged-in user has the Admin role.

Two new entity types are added: `OrgNode` (position in the org chart) and `DirGroup` (custom folder in the directory tree), each with a many-to-many join table to `User`. Both are admin-owned, fully custom, and never touched by HRIS sync.

A new set of Admin-only REST endpoints supports CRUD for both entities plus member assignment. A set of read-only endpoints exposes the trees for non-admin users.

The rendering is done with vanilla JavaScript (no third-party library), following the project's existing pattern of minimal JS in `ipop.js` and Blazor Interop for the interactive parts.

## User Stories

1. As an employee, I want to see the Everyone view (flat grid with search, presence, and profile card) so that I can find and contact any colleague.
2. As an employee, I want to see the Directory Tree view so that I can browse users grouped into custom folders.
3. As an employee, I want to see the Organization Tree view so that I can understand the company's reporting structure and positions.
4. As an employee, I want to zoom and pan the org chart so that I can navigate it freely on any screen size.
5. As an employee, I want to click a node in the org chart to see its members and their profile cards.
6. As an employee, I want to collapse/expand departments in the org chart so that I can focus on one area.
7. As an employee, I want to click a folder in the directory tree to see its members.
8. As an admin, I want to create, edit, and delete custom folders in the Directory Tree so that I can organize users into any grouping I need.
9. As an admin, I want to nest folders in the Directory Tree so that I can create a hierarchy of groups.
10. As an admin, I want to search for a user by name or NIK in the Directory Tree view and drag them onto a folder to assign them, so that I don't have to edit each user one at a time.
11. As an admin, I want to remove a user from a directory group (without deleting the user) so that I can correct assignments.
12. As an admin, I want to create, edit, and delete position nodes in the Organization Tree so that I can build the org chart from scratch.
13. As an admin, I want to nest position nodes under a parent node so that I can represent reporting lines.
14. As an admin, I want to optionally group position nodes under a department so that the org chart can be collapsed/expanded by department.
15. As an admin, I want to search for a user in the Organization Tree view and drag them onto a position node to assign them to that role.
16. As an admin, I want to remove a user from a position node (without deleting the user) so that I can correct role assignments.
17. As an admin, I want to delete a position node and have its children promoted to the deleted node's parent (not cascade-deleted) so that the tree structure remains intact.
18. As an admin, I want to delete a directory group and have its children promoted to the deleted group's parent so that the tree structure remains intact.
19. As an admin, I want all my org chart and directory tree edits to be recorded in the audit trail so that I can track who changed what.
20. As an admin, I want to be prevented from creating cycles in the org tree or directory tree so that the tree structure stays valid.
21. As an admin, I want to see the Everyone view with the same searchable user list that I use in the other two views, so that I have a consistent user panel everywhere.

## Implementation Decisions

### 1. Page Layout

The `/directory` page (Blazor, `Directory.razor`) gets three sub-tabs styled identically to the existing admin tabs (`admin-tabs` / `admin-tab`). The tab state is stored in a local `int` field, same pattern as the Admin page.

- **Tab 0 (Everyone)**: unchanged — the existing flat employee grid. No additional user panel (the search/filter bar already does the job).
- **Tab 1 (Directory Tree)**: two-column layout. Left column: user search box + scrollable user list (drag source). Right column: tree folder list (drop target).
- **Tab 2 (Organization Tree)**: full-width layout. A canvas area displaying the org chart, with zoom controls (+/- buttons, mouse wheel) and pan (drag background). A floating or collapsible user panel (searchable) for drag-assignment. Admin edit controls appear as a toolbar or inline buttons on each node.

### 2. Data Model

Two new entities in `IPop.Core.Entities`:

```csharp
public class OrgNode
{
    public Guid Id { get; set; }
    public required string Name { get; set; }
    public Guid? ParentId { get; set; }
    public Guid? DepartmentId { get; set; }     // optional; for grouping by department
    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;
    // Navigation
    public OrgNode? Parent { get; set; }
    public Department? Department { get; set; }
    public ICollection<UserOrgNode> Members { get; set; } = [];
}

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

// Join tables
public class UserOrgNode
{
    public Guid UserId { get; set; }
    public Guid OrgNodeId { get; set; }
    public User User { get; set; } = null!;
    public OrgNode OrgNode { get; set; } = null!;
}

public class UserDirGroup
{
    public Guid UserId { get; set; }
    public Guid DirGroupId { get; set; }
    public User User { get; set; } = null!;
    public DirGroup DirGroup { get; set; } = null!;
}
```

- `OrgNode.DepartmentId` is optional. When set, the org chart groups nodes under a department header (collapsible). When null, the node sits at the top level or under its parent without a department group.
- `OrgNode.ParentId` self-references for reporting lines. Cycle detection is enforced at the API layer (same pattern as `ApplyParent` in `AdminEndpoints`).
- `DirGroup.ParentId` self-references for folder nesting. Same cycle guard.
- `SortOrder` controls display order among siblings.
- Both entities are admin-owned; HRIS sync never touches them.

### 3. API Endpoints

**Read-only endpoints (all authenticated users, no admin requirement):**

| Method | Path | Response |
|--------|------|----------|
| GET | `/api/directory/org-tree` | All active OrgNodes with their members (flat list, tree built client-side). Returns `OrgNodeDto[]` with nested `members: UserRefDto[]`. |
| GET | `/api/directory/dir-groups` | All active DirGroups with their members. Returns `DirGroupDto[]`. |

**Admin endpoints (require `AdminOnly`):**

| Method | Path | Function |
|--------|------|----------|
| GET | `/api/admin/org-nodes` | All OrgNodes (including inactive) + members |
| POST | `/api/admin/org-nodes` | Create node `{name, parentId?, departmentId?, sortOrder?}` |
| PATCH | `/api/admin/org-nodes/{id}` | Update node `{name?, parentId?, departmentId?, sortOrder?, isActive?}` |
| DELETE | `/api/admin/org-nodes/{id}` | Delete node; children promoted to deleted node's parent; members unlinked |
| PUT | `/api/admin/org-nodes/{id}/members` | Set members (full replace, array of NIK strings) |
| POST | `/api/admin/org-nodes/{id}/members` | Add single member `{nik}` |
| DELETE | `/api/admin/org-nodes/{id}/members/{nik}` | Remove single member |
| GET | `/api/admin/dir-groups` | All DirGroups (including inactive) + members |
| POST | `/api/admin/dir-groups` | Create group `{name, parentId?, sortOrder?}` |
| PATCH | `/api/admin/dir-groups/{id}` | Update group `{name?, parentId?, sortOrder?, isActive?}` |
| DELETE | `/api/admin/dir-groups/{id}` | Delete group; children promoted to parent; members unlinked |
| PUT | `/api/admin/dir-groups/{id}/members` | Set members (full replace) |
| POST | `/api/admin/dir-groups/{id}/members` | Add single member |
| DELETE | `/api/admin/dir-groups/{id}/members/{nik}` | Remove single member |

All admin endpoints follow the existing pattern: `RequireAuthorization("AdminOnly")`, `AddEndpointFilter` for notifier on write, audit log on every mutation, cycle guard for parent references.

**Dto shapes:**

```csharp
// For the read-only endpoints
public record OrgNodeDto(Guid Id, string Name, Guid? ParentId, Guid? DepartmentId,
    int SortOrder, bool IsActive, UserRefDto[] Members);
public record DirGroupDto(Guid Id, string Name, Guid? ParentId, int SortOrder,
    bool IsActive, UserRefDto[] Members);
public record UserRefDto(string Nik, string Name, string? Position, string? Department, string? Site);
```

### 4. Web UI — Directory.razor

The Blazor page adds a `tab` field (0=Everyone, 1=DirectoryTree, 2=OrgTree). Tab switching is done via `@onclick` handlers, same as the existing admin page.

**Everyone (Tab 0):** Unchanged — the existing code at `Directory.razor` lines 8-62.

**Directory Tree (Tab 1):** Two-column layout (CSS grid `grid-template-columns: 340px 1fr`).

Left column:
- Search input (same as Everyone search)
- Scrollable list of users (`emp-card` style) — this is the drag source
- Each user card has `draggable="true"`, `@ondragstart` sets `nik` in JS drag data

Right column:
- Root-level tree display with collapsible folders
- Each folder shows name, member count, expand/collapse chevron
- `@ondrop` handler reads the `nik` from drag data and calls the API to add the member
- Admin: "Add folder" button at top, each folder has edit/delete buttons
- Non-admin: read-only view

**Organization Tree (Tab 2):** Full-width canvas.

- A container div with CSS `overflow: hidden` and `transform: scale(translateX, translateY)` for zoom/pan
- Nodes rendered as positioned divs, connected by SVG lines (computed via a simple tidy-tree layout algorithm)
- Department headers are collapsible rows that show/hide their child nodes
- Zoom controls: buttons + mouse wheel (via `ipop.js` helper)
- Pan: drag the background or scroll (via `ipop.js` helper)
- Floating user panel (toggleable) for drag assignment
- Admin: node edit toolbar (add child, edit, delete, drag user from panel onto node)
- Non-admin: browse, zoom, pan, click node to see members

### 5. JavaScript Helpers (`ipop.js`)

New methods added to the existing `window.ipop` object:

- `ipop.treeLayout(nodes)` — given a flat array of nodes with `id`, `parentId`, computes a tidy tree layout (x, y positions) for absolute positioning. Returns `{ [id]: {x, y, w, h} }`.
- `ipop.chartZoom(container, delta)` — applied to a `div` that wraps the chart. Adjusts CSS `transform: scale()`.
- `ipop.chartPan(container, dx, dy)` — adjusts `transform: translate()`.
- `ipop.dragStart(event, nik)` — stores `nik` in `event.dataTransfer`.
- `ipop.dropHandler(event, callback)` — reads `nik` from `event.dataTransfer`, calls a Blazor `[JSInvokable]` method to add the member.
- `ipop.chartDragPan(container)` — sets up mousedown/mousemove/mouseup for panning the chart.

No third-party library is added. The project's existing dependency set (Bootstrap, Lucide) is unchanged.

### 6. Migration

A single EF Core migration (`dotnet ef migrations add OrgTreeDirGroups`) creates tables:
- `OrgNodes` (Id, Name, ParentId, DepartmentId, SortOrder, IsActive)
- `UserOrgNode` (UserId, OrgNodeId) — composite PK
- `DirGroups` (Id, Name, ParentId, SortOrder, IsActive)
- `UserDirGroup` (UserId, DirGroupId) — composite PK

Foreign keys:
- `OrgNode.ParentId` → `OrgNodes.Id` (restrict delete)
- `OrgNode.DepartmentId` → `Departments.Id` (set null on deactivate)
- `UserOrgNode.UserId` → `Users.Id` (cascade)
- `UserOrgNode.OrgNodeId` → `OrgNodes.Id` (cascade)
- `DirGroup.ParentId` → `DirGroups.Id` (restrict delete)
- `UserDirGroup.UserId` → `Users.Id` (cascade)
- `UserDirGroup.DirGroupId` → `DirGroups.Id` (cascade)

### 7. Delete Behavior

When a node/group is deleted (not just deactivated):
- All children have their `ParentId` set to the deleted node's `ParentId` (promotion, not cascade)
- All members in the join table are removed (cascade)
- The node itself is deleted

This is implemented in the endpoint handler, not as a database cascade, because we need to promote children before deleting the parent.

## Testing Decisions

Good tests assert external behavior (HTTP surface, persisted state) and guard logic (cycle detection, parent promotion), not implementation details. The existing seam (`TestDb` — SQLite in-memory, `IPopDbContext` direct assertions) is reused.

**New tests at the existing seam:**

- `OrgNode CRUD` — create, read, update, delete; soft deactivate with `IsActive = false`
- `DirGroup CRUD` — same pattern
- `Parent cycle guard` — creating a cycle returns error; follows existing `ApplyParent` pattern
- `Delete promotes children` — deleting a node sets its children's `ParentId` to the deleted node's parent
- `Delete removes members` — after delete, join table has no rows for the deleted node
- `Member assign/remove` — add member, verify join row; remove member, verify join row gone; full replace via PUT
- `Read endpoints without admin` — `/api/directory/org-tree` and `/api/directory/dir-groups` return 200 for non-admin users
- `Admin endpoints require admin` — `/api/admin/org-nodes` returns 403 for non-admin
- `Audit trail` — mutations create audit log entries (verify via `AuditLogs` table)

Prior art: `HrisSyncServiceTests` tests behavior at the DbContext seam. `TestDb` provides the in-memory SQLite database. `AuthWorkflowTests` tests endpoint behavior. Our tests follow the same pattern.

## Out of Scope

- Drag-and-drop reordering of nodes within the tree (only sort via `SortOrder`)
- Import/export of org chart or directory tree structure
- Auto-sync from HRIS or any external source for org chart positions
- Mobile-specific touch interactions for zoom/pan (desktop-only for v1)
- Dark mode specific styling for the tree/canvas (follows existing CSS variables)
- Color-coding or custom icons on org chart nodes
- CSV/bulk import of users into groups or positions
- Real-time sync of tree changes between multiple admin sessions (page refresh required)

## Further Notes

- The admin seed (`ipopadmin`) and any existing admin user get full access to these new features with no additional setup.
- The Department entity already exists and has `ParentId` for self-referencing hierarchy. The `OrgNode.DepartmentId` FK is optional and references this existing table. This means the org chart can optionally group nodes under the same departments already managed in the Organization tab.
- Deleting a department (in the Organization tab) that is referenced by OrgNodes will set `OrgNode.DepartmentId = null` via the FK `SetNull` behavior.
- The user list in the Directory Tree and Organization Tree views is the same data source — the existing `/api/directory/employees` endpoint. The drag panel is a filtered subset of the same list.
- Vanilla JS for tree layout and zoom/pan is chosen over a library to keep the project's dependency footprint minimal, consistent with the existing pattern (Bootstrap + Lucide only).