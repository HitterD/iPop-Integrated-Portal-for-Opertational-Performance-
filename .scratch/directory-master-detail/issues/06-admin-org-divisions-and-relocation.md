# 06 — Admin Organization: Divisions tab + relocate old tree editors

**What to build:** Administrators get a Divisions tab under Organization (CRUD with active Department/Site dropdowns), and the old Directory-Tree folder / Organization-Tree chart editors move from the Directory page into Administration — Directory becomes a pure browse view, no admin capability is lost.

**Blocked by:** 02 — Division schema + admin CRUD + user assignment, and 04 — Directory master-detail UI (browse view)

**Status:** ready-for-agent

- [ ] Organization tab gains a Divisions section: table + create/edit drawer with active Department and Site dropdowns, active/inactive toggle, matching the Departments/Sites editors
- [ ] The Directory-Tree folder editor and Organization-Tree chart editor move to Administration (endpoints untouched — only the caller moves)
- [ ] The Directory page no longer hosts the old tabs; it is the single master-detail view
- [ ] Existing admin editing flows (dir-groups, org-nodes) still work from Administration