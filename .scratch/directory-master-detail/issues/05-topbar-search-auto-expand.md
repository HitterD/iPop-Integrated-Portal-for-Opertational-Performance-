# 05 — Topbar global search → auto-expand tree + highlight

**What to build:** An employee can search by name or NIK from the topbar on the Directory page and the tree auto-expands to every division containing a match, with matching cards highlighted and a per-division result count — so people can be found without knowing where they sit.

**Blocked by:** 04 — Directory master-detail UI (browse view)

**Status:** ready-for-agent

- [ ] Directory page opts into the existing topbar search; typing calls the divisions search endpoint
- [ ] The tree auto-expands to every division containing a match and matching cards are highlighted with a per-division result count
- [ ] Clearing the search restores the previous browse state (no lingering expansion or highlight)
- [ ] Other pages' existing search behavior is unchanged