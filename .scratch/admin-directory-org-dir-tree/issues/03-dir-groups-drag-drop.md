# 03 — Directory Tree drag-and-drop assignment

**What to build:** Admins assign users to directory groups by dragging them from a searchable user panel onto a folder — no per-user form needed. The user panel (search by name/NIK, presence, scrollable) is a reusable component, also serving the org-tree view later. Drag uses native HTML5 DnD with the project's vanilla-JS helper; dropping a user onto a folder assigns them, and the panel/tree update without a full reload. A user already in the target group is not duplicated; dropping on an invalid target does nothing visible.

**Blocked by:** 02

**Status:** ready-for-agent

- [ ] Reusable searchable user panel (name/NIK search, presence dot, scrollable list, profile-card behavior) shared between Directory Tree and Organization Tree views
- [ ] Drag a user card onto a folder assigns the user to that group via the member-add endpoint; UI reflects the change immediately
- [ ] Drop on a folder the user already belongs to is a no-op (no duplicate join row)
- [ ] Drag-and-drop works only for admins; non-admin users see the panel and tree read-only
- [ ] Keyboard/screen-reader fallback exists for member assignment (menu in the folder's edit controls), since DnD alone is not accessible
