# 02 — Directory Tree admin CRUD + member management

**What to build:** Admins can manage the custom folder hierarchy without drag-and-drop yet: create, rename, reorder (SortOrder), move (parent), deactivate, and delete groups (delete promotes children to the deleted group's parent and unlinks members). Admins can also add/remove/overwrite members through explicit controls in the Directory Tree tab. All mutations are admin-only, audit-logged, and guarded against parent cycles.

**Blocked by:** 01

**Status:** ready-for-agent

- [ ] Admin-only endpoints for directory-group CRUD and member add/remove/replace exist, with the delete-promotes-children behavior and parent-cycle guard
- [ ] Every mutation writes an audit-log entry (same action vocabulary as the existing admin module)
- [ ] Admin UI in the Directory Tree tab: add/edit/delete/move/deactivate groups, inline member add/remove (selecting from the user search), visible only to admins
- [ ] Tree re-renders from server state after every mutation; members show the existing profile-card interaction
- [ ] Deactivating a group hides it from the read-only tree; reactivating brings it back
