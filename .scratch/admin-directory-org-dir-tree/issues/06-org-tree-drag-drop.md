# 06 — Org Tree drag-and-drop assignment

**What to build:** Admins assign users to org-chart position nodes by dragging them from the (already-shared) searchable user panel onto a node — no per-user form. Drop assigns the member via the member-add endpoint; the chart and panel update immediately; a user already on the node is not duplicated; invalid drops do nothing. Non-admin users see everything read-only.

**Blocked by:** 05

**Status:** ready-for-agent

- [ ] Drag a user card from the shared panel onto an org node assigns the user to that position; chart and panel reflect the change immediately
- [ ] Drop on a node the user already belongs to is a no-op (no duplicate join row)
- [ ] Drag-and-drop works only for admins; keyboard/screen-reader fallback exists via the node's member controls
- [ ] Zoom/pan state is preserved across the re-render after a drop
