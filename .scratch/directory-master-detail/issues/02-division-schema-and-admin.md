# 02 — Division schema + admin CRUD + user assignment

**What to build:** An admin can manage Divisions (one Division = one parent Department + one Site) and assign any user to a Division, so the org data backing the Directory tree is admin-owned and accurate. Assigning a user to a Division keeps their Site/Department in sync automatically.

**Blocked by:** None — can start immediately.

**Status:** ready-for-agent

- [ ] `Division` entity exists (Code, Name, DepartmentId, SiteId, IsActive) + `User.DivisionId` nullable FK, all mapped in the DbContext, covered by one migration applied at startup
- [ ] Admin CRUD endpoints for Divisions: GET (incl. inactive), POST, PATCH, DELETE — all `AdminOnly`, audit trail + notifier on every mutation, guarded against referencing an inactive Department/Site
- [ ] DELETE on a Division sets member `DivisionId` to null (no cascade) and preserves existing Site/Department
- [ ] Assign/unassign endpoint for a user's Division; assigning syncs the user's SiteId/DepartmentId from the Division's parents, unassigning clears only DivisionId
- [ ] Tests at the `TestDb` seam: CRUD, inactive-parent guard, delete set-null, assign-syncs-site-dept, admin authorization (403 for non-admin), audit entries