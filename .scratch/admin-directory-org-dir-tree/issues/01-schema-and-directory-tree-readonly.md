# 01 — Schema + page shell + Directory Tree read-only

**What to build:** The `/directory` page gains three sub-tabs (Everyone, Directory Tree, Organization Tree) visible to all authenticated users. The Directory Tree tab shows the custom folder hierarchy (nested, collapsible) with member counts; clicking a folder reveals its members (reusable profile card behavior). Everything is read-only at this stage. Backing this up: the schema for admin-owned org/dir structures is in place (entities + join tables + migration + DbContext configuration + audit-log wiring).

**Blocked by:** None — can start immediately.

**Status:** ready-for-agent

- [ ] Entities exist (DirGroup, UserDirGroup, OrgNode, UserOrgNode) with the agreed shapes (self-referencing parent, SortOrder, IsActive, optional DepartmentId on OrgNode; composite-PK join tables), mapped in the DbContext with cascade/restrict/set-null FKs, covered by one migration applied at startup
- [ ] `/directory` page shows three sub-tabs (Everyone, Directory Tree, Organization Tree), tab state persists across postbacks; Everyone view is unchanged
- [ ] Read-only endpoint serves the active directory groups with members; Directory Tree tab renders the nested tree with expand/collapse and member counts
- [ ] Clicking a group shows its members (avatar, name, position) reusing the existing profile-card interaction
- [ ] Employees (non-admin) see the tree but no edit affordances anywhere in this tab
