# 05 — Org Tree admin CRUD + member management

**What to build:** Admins can build and edit the org chart structure: create, rename, reorder, move (parent), assign to a department, deactivate, and delete position nodes (delete promotes children to the deleted node's parent and unlinks members). Admins can also add/remove/overwrite members on each node. All mutations are admin-only, audit-logged, and guarded against parent cycles. The org chart re-renders after every mutation.

**Blocked by:** 04

**Status:** ready-for-agent

- [ ] Admin-only endpoints for org-node CRUD and member add/remove/replace exist, with the delete-promotes-children behavior and parent-cycle guard; optional DepartmentId FK references existing Departments table
- [ ] Every mutation writes an audit-log entry; admin-only guard on all endpoints
- [ ] Admin UI in the Organization Tree tab: add/edit/delete/move/reorder nodes, assign department for grouping, inline member add/remove (selecting from the user search), visible only to admins
- [ ] Org chart re-renders after every mutation, preserving zoom/pan state when possible
- [ ] Deactivating a node hides it from the read-only chart; reactivating brings it back