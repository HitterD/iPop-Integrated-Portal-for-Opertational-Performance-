# Admin, Directory, and RBAC Hardening Design

## Problem Statement

The Admin and Directory modules are usable, but several operations are incomplete or inconsistent. New users do not reliably retain Department and Site assignments, edit forms do not load existing placement, Directory omits Site, and the Directory loads the entire employee population into the browser. Administration lacks operational filters, complete organization editing, session visibility, sync history, and audit viewing. The current role model stores a parent role but does not resolve permissions or user overrides.

## Solution

Harden the Admin and Directory modules in five sequential phases. Preserve the existing Blazor WebAssembly, ASP.NET Core minimal API, PostgreSQL, and SignalR architecture. Add the smallest domain/API/UI slices needed per phase, with migrations and external-behavior tests. Keep Chat, Mail, Calls, infrastructure HA monitoring, and photo upload out of scope.

## User Stories

1. As an administrator, I want a newly created user to retain the selected Department and Site, so that the directory is accurate immediately.
2. As an administrator, I want to edit a user's Department and Site without losing existing values, so that placement corrections are safe.
3. As an administrator, I want to see a user's Department, Site, role, active state, and manual-edit state in one list, so that I can manage records efficiently.
4. As an administrator, I want only active Departments and Sites selectable for placement, so that invalid assignments cannot be created.
5. As an administrator, I want inactive organization records to remain visible on existing users, so that deactivation does not hide historical placement.
6. As a directory user, I want to see Site in a profile, so that I can locate colleagues accurately.
7. As a directory user, I want profile data to refresh after HRIS or admin changes, so that I do not use stale contact information.
8. As a directory user, I want a profile drawer to close when the selected employee becomes inactive, so that I do not act on an unavailable account.
9. As a directory user, I want search and filtering to happen server-side, so that the page remains responsive with thousands of employees.
10. As a directory user, I want to filter by Department, Site, and presence, so that I can find the right colleague quickly.
11. As a directory user, I want to sort and page through results, so that large result sets remain navigable.
12. As an administrator, I want to filter users by active state, role, Department, Site, and HRIS/manual ownership, so that I can target operational work.
13. As an administrator, I want to sort and page user results on the server, so that the Admin page remains reliable as the user population grows.
14. As an administrator, I want to edit Department and Site names, so that organization data can be maintained without database access.
15. As an administrator, I want to configure parent Departments, so that organization structure can be represented accurately.
16. As an administrator, I want usage counts for Departments and Sites, so that I understand the impact of organization changes.
17. As an administrator, I want a warning before deactivating an organization record in use, so that changes are deliberate.
18. As an administrator, I want to see each HRIS sync's duration and result counts, so that I can diagnose synchronization behavior.
19. As an administrator, I want failed sync details, so that I can respond without inspecting server logs.
20. As an administrator, I want to see active sessions by user, device, IP, and expiry, so that I can investigate account access.
21. As an administrator, I want to revoke one session, so that a single device can be removed without signing out the user everywhere.
22. As an administrator, I want to revoke all sessions for a user, so that compromised accounts can be contained quickly.
23. As an administrator, I want to search and filter audit records, so that operational activity can be investigated.
24. As an administrator, I want structured audit details, so that I can understand what changed and why.
25. As an administrator, I want audit records to be append-only, so that the trail remains trustworthy.
26. As an administrator, I want new audit records to appear in real time, so that I do not need to refresh the audit page.
27. As an administrator, I want to create and edit roles, so that access can match the organization.
28. As an administrator, I want roles to inherit permissions from parent roles, so that common access is managed once.
29. As an administrator, I want to assign permissions to roles, so that access is explicit and reviewable.
30. As an administrator, I want user-level allow and deny overrides, so that exceptional access does not require creating unnecessary roles.
31. As an administrator, I want contextual department rules, so that access can be limited to the correct organizational scope.
32. As a system owner, I want default deny behavior, so that missing configuration never grants access accidentally.
33. As an administrator, I want self-lockout protection to remain active, so that the last administrator cannot remove their own access.
34. As a system operator, I want every write that changes Admin or Directory data to emit a realtime change event, so that connected clients converge without manual reloads.
35. As a developer, I want each phase to have external-behavior tests, so that the hardening does not regress existing authentication and sync behavior.

## Implementation Decisions

- Work is delivered in five phases: data correctness; Admin operations; audit; Directory UX/performance; RBAC.
- Existing ASP.NET Core minimal API and Blazor WASM patterns remain in place; no new framework or UI dependency is introduced.
- Existing SignalR `directoryChanged` infrastructure is reused for directory-affecting changes. A separate audit event is used for audit feeds.
- Create, edit, and placement validation share the same active Department/Site rules. Placement changes are committed atomically with the originating admin operation where possible.
- Directory and Admin list endpoints gain explicit query DTOs for search, filters, sort, page, and page size. Defaults remain bounded and safe.
- Directory rows include Site and preserve the existing initials fallback when no photo path exists.
- Organization deactivation is a soft state change. Existing assignments are retained and surfaced as inactive; new assignments reject inactive records.
- HRIS sync history is persisted as append-only run records containing start/end timestamps, counts, success state, and error detail. Existing sync state remains the watermark source.
- Session monitor endpoints expose metadata only; refresh token hashes are never returned. Revoke operations update `RevokedAt` and are audited.
- Audit logs remain append-only. No update/delete endpoint is created. Detail JSON is structured and validated at write boundaries.
- RBAC adds permissions, role-permission assignments, and user permission overrides. Parent roles inherit permissions recursively.
- Permission resolution order is: explicit user deny, explicit user allow, direct role permissions, inherited role permissions, contextual department rule, default deny.
- The existing Admin role remains protected by self-lockout guards. Permission-based policies are introduced without weakening the existing AdminOnly boundary until parity tests pass.
- Role hierarchy cycles are rejected at write time.
- HA infrastructure monitoring, photo upload, and Chat/Mail/Call actions remain out of scope.

## Testing Decisions

- Tests assert HTTP-visible behavior or service outcomes, not private implementation details.
- Existing HrisSyncService, authorization, presence, JWT, and password tests remain the baseline.
- Phase 1 tests cover create/edit placement persistence, inactive placement rejection, Directory Site output, and realtime-triggering writes.
- Phase 2 tests cover list filters/sorts, organization edits and counts, sync-history success/failure, session listing, and single/all-session revocation.
- Phase 3 tests cover audit filtering, structured details, append-only behavior, and realtime audit notification contracts.
- Phase 4 tests cover bounded pagination, filter combinations, selected-profile refresh behavior, and reconnect refresh behavior where the client seam permits it.
- Phase 5 tests cover inherited permissions, direct permissions, user allow/deny precedence, department context, cycle rejection, default deny, and self-lockout.
- Database-backed tests use the existing test database helper and avoid requiring external HRIS/network access.
- One runnable check is retained for each non-trivial decision path; no new test framework is introduced.

## Out of Scope

- Kubernetes, NGINX/HAProxy, PostgreSQL replication, PgBouncer, Redis Sentinel, MinIO cluster monitoring, and failover controls.
- Chat, Mail, Calls, notifications, and their quick-action backend workflows.
- Photo upload, image processing, and file storage integration.
- Mobile and WPF clients.
- Replacing the current modular monolith architecture.

## Further Notes

Implementation should land in small reviewable slices. Each phase must build and pass its tests before the next phase starts. Existing development seed data and the HRIS department-cleanup decision remain authoritative: HRIS owns employee profile facts, while Department and Site ownership remains administrative.
