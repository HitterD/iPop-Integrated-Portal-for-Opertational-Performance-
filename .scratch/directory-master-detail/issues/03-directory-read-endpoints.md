# 03 — Directory read endpoints (structure tree + search → divisions + filter)

**What to build:** An employee can fetch the full `Site → Department → Division` tree with member counts, search by name/NIK to find which divisions contain a match, and filter the people grid by division — the read API the master-detail page and the topbar search call.

**Blocked by:** 02 — Division schema + admin CRUD + user assignment

**Status:** ready-for-agent

- [ ] GET `/api/directory/structure` returns the Site→Department→Division tree with aggregate member counts plus a "No Division" node covering active users with no Division (all `directory.read`)
- [ ] GET `/api/directory/divisions?q=` returns the divisions containing name/NIK matches with per-division match counts (empty for no match)
- [ ] GET `/api/directory/employees` accepts a `divisionCode` filter alongside the existing filters
- [ ] Tests at the `TestDb` seam: tree build + counts + No-Division node, search→divisions + empty, employees division filter, read endpoints 200 for non-admin, admin endpoints still 403