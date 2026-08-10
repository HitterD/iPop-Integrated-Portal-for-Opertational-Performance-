# 04 — Org Tree read-only (chart render)

**What to build:** The Organization Tree tab renders the org chart read-only for all employees: position nodes as a tidy tree (parents centered over children) with SVG connector lines, optional department grouping rendered as collapsible sections, zoom controls (buttons + mouse wheel) and pan (drag background / scrollbars), and clicking a node shows its members with the existing profile-card behavior. Layout and zoom/pan are implemented with the project's vanilla-JS helper methods (no third-party library).

**Blocked by:** 01

**Status:** ready-for-agent

- [ ] Read-only endpoint serves active org nodes with members (tree built client-side)
- [ ] Org chart renders as a tidy tree with connector lines; nodes show name and member count; departments group nodes into collapsible sections when assigned
- [ ] Zoom in/out via buttons and mouse wheel (bounded, with a reset); pan by dragging the background; both smooth and applied via CSS transform
- [ ] Clicking a node shows its members (avatar, name, position) reusing the profile-card interaction
- [ ] Works for all users; no edit affordances appear for non-admins
