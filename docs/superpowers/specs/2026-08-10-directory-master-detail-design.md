# Directory Master-Detail (Site → Department → Division) Design

## Problem Statement

The Directory page at `/directory` currently offers three parallel tabs: a flat "Everyone" grid, a free-form "Directory Tree" (admin-editable folders), and a "Organization Tree" org chart. For the ordinary employee whose daily task is *find a co-worker and contact them*, none of these is user-friendly as a browsing surface: the flat grid hides structure, and neither tree is a real org hierarchy. The user knows people by their organization — "he's in the ICT division" — not by a flat search term or a custom folder name.

The user wants a master-detail directory: an org tree `Site > Department > Division` on the left, and a card grid of that division's people on the right, with Chat/Call actions directly on each card and a slide-in profile drawer on click. The trees that exist today are the wrong shape for this, and there is no "Division" concept in the data model at all (only flat `Site` and `Department` lookups).

## Solution

The Directory page becomes a single master-detail view. Left: an expandable org tree `Site ▸ Department ▸ Division`, each node showing a member-count badge, with the first division auto-selected on load. Right: a responsive grid of user cards (avatar, name, position, live presence dot) for the selected division, with **Chat** and **Call** buttons directly on each card and a **Show more** pager for large divisions. Clicking a card opens the existing right-side profile drawer (full details + quick actions). Presence-filter chips sit above the grid.

A new admin-owned **Division** lookup is added (one division = one parent Department + one Site), replacing nothing — `Site` and `Department` stay as-is. The topbar search becomes global and dynamic: typing a name/NIK auto-expands the tree to every division containing a match and highlights the matching cards, so the user never has to know where someone sits to find them.

The old "Directory Tree" and "Organization Tree" tabs move to the Administration module (admin-only editing preserved); the Directory page no longer hosts them. Chat and Call are placeholders for now — the buttons are live and give a "module not yet available" notice, since the Chat/Call modules ship later.

## User Stories

1. As an employee, I want a single org tree (`Site > Department > Division`) on the left of the Directory page, so that I can browse people by how the company is actually organized.
2. As an employee, I want to expand and collapse tree nodes, so that I can focus on one part of the organization.
3. As an employee, I want each node to show a member-count badge, so that I can see where people are concentrated without opening every node.
4. As an employee, I want the first division to be auto-selected when the page opens, so that I see results immediately instead of clicking to find something.
5. As an employee, I want to click a division and see its people as a responsive card grid on the right, so that I can scan colleagues visually.
6. As an employee, I want each card to show avatar, name, position, and a live presence dot, so that I can tell who is reachable before contacting them.
7. As an employee, I want **Chat** and **Call** buttons directly on each card, so that I can start a conversation in one click.
8. As an employee, when the Chat/Call module is not live yet, I want clicking those buttons to show a clear "module not yet available" notice, so that I am not misled into thinking the call started.
9. As an employee, I want to click a card to open the profile drawer, so that I can see the person's full details.
10. As an employee, I want the drawer to show the org line, contact details, and the quick actions, so that everything about a person is in one place.
11. As an employee, I want presence-filter chips (e.g. online / offline) above the card grid, so that I can focus on who is currently available.
12. As an employee, I want to search by name or NIK from the topbar and have the tree auto-expand to every division containing a match, so that I can find people without knowing where they sit.
13. As an employee, I want matching cards highlighted with a per-division result count, so that I can see exactly where the matches live.
14. As an employee, I want **Show more** pagination on large divisions, so that rendering stays responsive even for hundreds of people.
15. As an employee on a phone, I want the tree to become an overlay toggled by a hamburger button, so that the card grid keeps the full width.
16. As an employee, I want people without a division to appear under a **"No Division"** node, so that no one disappears from the directory.
17. As an admin, I want to create, edit, and deactivate Divisions in Administration → Organization, choosing the parent Department and Site for each, so that the org tree is admin-owned and accurate.
18. As an admin, I want to assign a user to a division, so that the tree reflects where people actually work.
19. As an admin, I want Division mutations recorded in the audit trail, so that I can track who changed what.
20. As an admin, I want to be blocked from assigning a Division to an inactive Department or Site, so that the tree never references dead data.
21. As an admin, I want the existing Directory-Tree folders and Organization-Tree chart editors to remain available under Administration, so that no existing admin capability is lost.
22. As an employee, I want the existing flat "everyone + search" behavior still reachable, so that a full search across all people is not lost.

## Implementation Decisions

### 1. Data Model — new `Division` entity

A new admin-owned lookup, mirroring the existing `Department`/`Site` pattern (Code + Name + IsActive):

```csharp
public class Division
{
    public Guid Id { get; set; }
    public required string Code { get; set; }       // unique, admin-defined
    public required string Name { get; set; }
    public Guid DepartmentId { get; set; }          // parent department (one division = one dept)
    public Guid SiteId { get; set; }                // parent site
    public bool IsActive { get; set; } = true;
    public Department Department { get; set; } = null!;
    public Site Site { get; set; } = null!;
    public ICollection<User> Members { get; set; } = [];
}
```

- `User` gains a nullable `DivisionId` FK. Assignment is admin-owned; HRIS never touches Divisions.
- **Consistency rule:** assigning a user a Division sets their `DivisionId` **and** keeps `SiteId`/`DepartmentId` in sync from the Division's parents, so existing read paths (employees endpoint, cards) keep showing a correct Site/Department. Users without a Division keep their existing Site/Department and surface under the "No Division" node.
- Relational guard: a Division can only reference an active Department and active Site (validated at the API boundary); a Division cannot be assigned to a deactivated parent.
- One EF Core migration: create `Divisions`, add `User.DivisionId` FK (set-null on Division delete), index on `DivisionId`.

### 2. Tree semantics

The tree is built from Division records: `Site → Department → Division`. A Department may appear under multiple Sites (it appears under each Site that hosts one of its Divisions). Nodes aggregate live member counts from the `DivisionId` assignment. A single **"No Division"** node at the tree root groups every active user with `DivisionId == null`, so legacy data stays visible.

### 3. API endpoints

**Read-only (all authenticated users, `directory.read`):**

| Method | Path | Function |
|--------|------|----------|
| GET | `/api/directory/structure` | The full `Site → Department → Division` tree with aggregate member counts + the No-Division node |
| GET | `/api/directory/divisions?q=` | Search: returns the divisions containing name/NIK matches, with per-division match counts (drives topbar search auto-expand) |
| GET | `/api/directory/employees?divisionCode=` | Existing endpoint gains a division filter for the card grid |

**Admin (require `AdminOnly`):**

| Method | Path | Function |
|--------|------|----------|
| GET | `/api/admin/divisions` | All Divisions (incl. inactive) with parent refs |
| POST | `/api/admin/divisions` | Create `{code, name, departmentId, siteId}` |
| PATCH | `/api/admin/divisions/{id}` | Update `{name?, departmentId?, siteId?, isActive?}` (parent changes re-sync members' Site/Department) |
| DELETE | `/api/admin/divisions/{id}` | Delete (set-null on members, no cascade) |
| PUT | `/api/admin/users/{nik}/division` | Assign a user to a division `{divisionId}` (or null to unassign); syncs Site/Department |

All admin endpoints follow the existing patterns: `RequireAuthorization("AdminOnly")`, `RequirePermission`, notifier/audit-log tap on every mutation. The existing admin endpoints for dir-groups and org-nodes are **unchanged** — only their caller moves from the Directory page to Administration.

### 4. Directory page — master-detail

- **Left column:** tree `Site ▸ Department ▸ Division`, expand/collapse chevrons, member-count badges, lazy branch loading (site expands → its departments load). Auto-select the first division on first load.
- **Right column:** responsive card grid (existing `.emp-card` styling). Each card: avatar, name, position, presence dot, plus **Chat** and **Call** buttons (Lucide icons) that are *enabled* and show a "module not yet available" toast on click. Card click opens the existing profile drawer. Presence chips + **Show more** pager above/under the grid.
- **Topbar search:** the page opts into the existing topbar search; on input it calls `/api/directory/divisions?q=`, auto-expands the tree to matching divisions, and highlights matched cards with a per-division count. Clearing the search restores the browse state.
- **Mobile:** below a breakpoint the tree renders as an overlay toggled by a hamburger button; the grid stays full-width.
- **Old tabs removed from Directory:** the Directory-Tree folder view and Organization-Tree chart view move to Administration (admin-editable as today). The Directory page is the single master-detail view.

### 5. Administration — Divisions tab

A new tab under Organization (alongside Departments and Sites): CRUD table + create/edit drawer with Department and Site dropdowns (active only), active/inactive toggle, and the same audit/notifier wiring as the Departments editor.

### 6. Styling & dependencies

All styling reuses the existing `ipop.css` design system (tree rows, `.emp-card`, `.tray`, drawer classes), Lucide icons, Figtree font. No new UI library, no new npm/managed dependency. The chat/call toast uses the existing notification primitive.

## Testing Decisions

Good tests assert external behavior (HTTP surface, persisted state, guard logic) — not Blazor rendering. The single existing seam (`TestDb` — SQLite in-memory `IPopDbContext` with direct assertions, as in `HrisSyncServiceTests`, `AuthWorkflowTests`, `FileAccessTests`) is reused. No new seam.

**New tests at the existing seam:**

- `Division CRUD` — create, read, update, deactivate; guards against inactive parent Department/Site at the API boundary.
- `Division delete` — members' `DivisionId` set null; existing Site/Department preserved; no cascade delete.
- `Assign user` — assigning a division sets `DivisionId` and syncs `SiteId`/`DepartmentId`; unassign clears `DivisionId` only.
- `Tree build` — `/api/directory/structure` returns correct Site→Department→Division nesting with aggregate counts; the No-Division node includes users with `DivisionId == null`.
- `Search → divisions` — matching divisions returned with per-division counts; no match returns empty.
- `Employees division filter` — grid endpoint filters correctly by divisionCode.
- `Admin authorization` — `/api/admin/divisions` returns 403 for non-admin; read endpoints return 200.
- `Audit trail` — Division mutations create audit-log entries (verified on `AuditLogs`).
- Existing `dir-groups`/`org-nodes` tests stay green (endpoints untouched).

**Not covered (no prior art, deliberately out of scope):** Blazor UI rendering (tree expand/collapse, auto-expand on search, drawer open). The project consistently treats the Blazor layer as a thin client over a tested API, and has no bUnit infrastructure; adding a UI-test seam is deferred.

## Out of Scope

- The Chat and Call modules themselves (their buttons are placeholders that surface a "not yet available" notice; the real modules wire into these same buttons later).
- A bUnit/UI-rendering test seam.
- Drag-and-drop reordering of Divisions within the tree (ordering via admin CRUD only).
- Auto-sync of Divisions from HRIS (admin-owned, like Department/Site).
- Reassigning Department/Site at the user level independently of Division (the Division is the source of truth).
- Mobile touch features beyond the tree-as-overlay behavior.
- Import/export or bulk assignment of users to Divisions.

## Further Notes

- The seeded admin account (`ipopadmin`) and any Admin-role user get full access to the new Division management with no extra setup.
- Assigning a user to a Division keeps `SiteId`/`DepartmentId` in sync so every existing endpoint that displays Site/Department (employees grid, profile drawer, org-tree members) stays correct without changes.
- The "No Division" node is the safety net for all existing users: nothing is hidden when the feature ships; admins can tag users into Divisions progressively.
- The topbar search behavior (auto-expand + highlight) only applies on the Directory page; other pages keep their existing search opt-in/out behavior.
- The old flat "Everyone" search is preserved through the same global search — a query with matches outside any division still surfaces them via the No-Division node and the standard grid.