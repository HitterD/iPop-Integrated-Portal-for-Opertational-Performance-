# 01 — Directory master-detail (Site → Department → Division)

**What to build:** Replace the Directory page's three tabs (Everyone / Directory Tree / Organization Tree) with a single master-detail view: an org tree `Site ▸ Department ▸ Division` on the left and a card grid of the selected division's people on the right, with Chat/Call buttons directly on each card and the existing profile drawer on card click. The old tree views move to Administration; a new admin-owned Division lookup (one Division = one Department + one Site) backs the tree, and the topbar search becomes a global dynamic search that auto-expands the tree to matching divisions.

**Blocked by:** None — can start immediately.

**Status:** ready-for-agent

- [ ] New `Division` entity + `User.DivisionId` FK, one migration, admin-owned (HRIS never touches it); assigning a user a Division keeps their Site/Department in sync
- [ ] Admin CRUD endpoints for Divisions (guards: active parent only, set-null delete, audit trail + notifier on mutations)
- [ ] Read endpoints: `/api/directory/structure` (tree + counts + No-Division node), `/api/directory/divisions?q=` (search → divisions with counts), employees endpoint gains `divisionCode` filter
- [ ] Directory page becomes master-detail: tree left (badges, lazy branches, auto-select first division), card grid right (avatar/name/position/presence, Chat/Call buttons → "module not yet available" toast, card click → profile drawer), presence chips + Show more pager
- [ ] Topbar search: auto-expands tree to matching divisions, highlights matched cards with per-division count; clearing restores browse state
- [ ] Mobile: tree renders as overlay behind a hamburger toggle; grid stays full-width
- [ ] Directory-Tree and Organization-Tree editors moved to Administration (endpoints untouched, only caller moves); Directory page drops its tabs
- [ ] Administration → Organization gains a Divisions tab (CRUD + create/edit drawer with active Department/Site dropdowns)
- [ ] Tests at the existing `TestDb` seam: Division CRUD + guards, delete set-null, assign-syncs-site-dept, tree build + counts, search→divisions, employees division filter, admin authorization, audit trail; existing dir-groups/org-nodes tests stay green
