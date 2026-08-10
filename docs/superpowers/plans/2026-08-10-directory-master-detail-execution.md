# Directory Master-Detail Execution Plan

## Goal
Implement the approved Directory master-detail feature as five dependency-aware tickets, preserving existing directory and administration behavior while adding the Site → Department → Division browse hierarchy.

## Global Constraints

- Keep the existing .NET 10, Blazor, EF Core, Minimal API, SQLite TestDb, CSS, and Lucide conventions; add no new dependency.
- Preserve existing Directory read permissions and AdminOnly authorization boundaries.
- Reuse the existing TestDb seam and persisted-state assertions; do not introduce bUnit or a second test infrastructure.
- Division assignment is admin-owned, does not come from HRIS, and synchronizes User SiteId and DepartmentId from the Division's active parents.
- Division deletion clears member DivisionId without deleting users or changing their existing SiteId or DepartmentId.
- Every Division mutation must write the existing audit trail and notify directory changes using established endpoint patterns.
- The Directory page becomes one master-detail browse view; old Directory-Tree and Organization-Tree editors remain available to administrators under Administration.
- Chat and Call controls are placeholders that show the existing notification/toast primitive; do not implement chat or calling.
- Use the exact acceptance criteria in each ticket file as the source of truth for that task.

---

## Task 1: Division schema + admin CRUD + user assignment

**Requirements source:** `.scratch/directory-master-detail/issues/02-division-schema-and-admin.md`

**Blocked by:** None — can start immediately.

- Add the Division entity and nullable User.DivisionId FK, DbContext mappings, and one startup-applied EF migration.
- Add AdminOnly Division GET/POST/PATCH/DELETE endpoints, inactive-parent guards, audit records, and directory notifier behavior.
- Delete divisions without cascading into users; preserve members' existing SiteId and DepartmentId.
- Add AdminOnly user division assignment/unassignment endpoint with assignment synchronization.
- Cover CRUD, guards, deletion semantics, assignment synchronization, authorization, and audit behavior at the TestDb seam.

## Task 2: Directory read endpoints

**Requirements source:** `.scratch/directory-master-detail/issues/03-directory-read-endpoints.md`

**Blocked by:** Task 1.

- Add authenticated directory structure endpoint with Site → Department → Division hierarchy, counts, and No Division safety node.
- Add authenticated division search endpoint with matching-division result counts.
- Extend employee read endpoint with divisionCode filtering.
- Test tree build, counts, No Division, search, filtering, and authorization behavior.

## Task 3: Directory master-detail UI

**Requirements source:** `.scratch/directory-master-detail/issues/04-directory-master-detail-ui.md`

**Blocked by:** Task 2.

- Replace the Directory page's old parallel views with the master-detail tree and responsive employee card grid.
- Support tree expansion, counts, lazy branch behavior where practical, first-division selection, presence chips, and Show-more paging.
- Add placeholder Chat and Call controls with a clear unavailable-module notice.
- Reuse ProfileDrawer on card selection and provide mobile tree overlay behavior.

## Task 4: Topbar search auto-expand

**Requirements source:** `.scratch/directory-master-detail/issues/05-topbar-search-auto-expand.md`

**Blocked by:** Task 3.

- Opt Directory into the existing topbar search and query the division search endpoint dynamically.
- Auto-expand matching branches, highlight matching people, and show per-division match counts.
- Restore browse state when search is cleared and preserve other pages' search behavior.

## Task 5: Administration divisions and editor relocation

**Requirements source:** `.scratch/directory-master-detail/issues/06-admin-org-divisions-and-relocation.md`

**Blocked by:** Task 1 and Task 3.

- Add Administration → Organization Divisions CRUD UI with active parent dropdowns and active toggle.
- Move the existing Directory-Tree and Organization-Tree editors to Administration without changing their endpoints or capabilities.
- Remove old tabs from Directory and verify existing admin flows still work.

## Verification

After all tasks, run the relevant .NET tests/build, inspect the complete branch diff, and perform a whole-branch review against the spec and all ticket acceptance criteria.
