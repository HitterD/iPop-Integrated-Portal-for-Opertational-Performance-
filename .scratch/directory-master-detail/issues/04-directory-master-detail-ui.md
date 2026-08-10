# 04 — Directory master-detail UI (browse view)

**What to build:** The Directory page becomes a single master-detail view: an expandable `Site → Department → Division` tree on the left and a card grid of the selected division's people on the right, with Chat/Call buttons directly on each card and the existing profile drawer on card click. Mobile shows the tree as an overlay.

**Blocked by:** 03 — Directory read endpoints (structure tree + search → divisions + filter)

**Status:** ready-for-agent

- [ ] Directory page shows the tree on the left (expand/collapse, member-count badges, lazy branch loading, auto-selects the first division on load) and the card grid on the right
- [ ] Cards reuse the existing card styling (avatar, name, position, live presence dot), with presence chips and the existing Show-more pager for large divisions
- [ ] Each card has live Chat and Call buttons that surface a "module not yet available" notice on click (modules ship later)
- [ ] Clicking a card opens the existing profile drawer with full details and quick actions
- [ ] Below the mobile breakpoint the tree renders as an overlay toggled by a hamburger button; the grid stays full-width