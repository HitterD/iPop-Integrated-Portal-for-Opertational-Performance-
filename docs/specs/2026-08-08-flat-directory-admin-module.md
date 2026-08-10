# Flat Directory + Admin Module

## Problem Statement

The portal syncs 4,748 employees from the HRIS gateway, but HRIS also owns the department assignment — so the Directory is locked to whatever the source feed says, and an admin has no way to onboard, correct, or manage a person without touching HRIS. The HRIS department codes are unreliable (`2.0034`, `MAGN.03`, `TES TR KARYAWAN` all coexist), which the user found unusable as an organizational view. The user wants to drop department assignment entirely, keep every user in one flat list, and give an administrator a UI to manage people directly.

## Solution

Directory becomes a flat list of all users — no grouping, no department tree. HRIS keeps syncing profile facts (name, position, email, location, manager) but no longer touches departments or sets `DepartmentId`. A new Administration module (Admin role only) provides a user list with create / edit / deactivate / reset-password / assign-role. A dedicated admin account (`ipopadmin`) is seeded so the module is reachable on first run.

## User Stories

Directory (flat):

1. As an employee, I want to see everyone in one searchable list, so that I don't need to know which department someone is in.
2. As an employee, I want each person to show name, position, location, extension, and live presence, so that I can see who is reachable before contacting them.
3. As an employee, I want the list to update presence live, so that I don't have to reload the page.
4. As an employee, I want to filter the list by name or ID, so that I can find one person out of thousands quickly.

Admin — user management:

5. As an admin, I want to open an Administration page visible only to me, so that employee management is not exposed to regular users.
6. As an admin, I want to see every user with their NIK, name, email, position, role, active state, and presence, so that I have one place to audit access.
7. As an admin, I want to search the user list by name or NIK, so that I can find a specific person.
8. As an admin, I want to add a user with NIK, name, and email, so that someone not yet in HRIS can sign in.
9. As an admin, I want a newly added user to land on the default-password flow (`1234` + forced change on first login), so that I don't have to invent or transmit a real password.
10. As an admin, I want to edit a user's name, email, and position, so that I can correct data without waiting on HRIS.
11. As an admin, I want to deactivate a user, so that a leaver loses access immediately.
12. As an admin, I want deactivation to revoke all of that user's sessions, so that a signed-in leaver is actually cut off.
13. As an admin, I want to reactivate a user, so that a returner regains access without a new account.
14. As an admin, I want to reset a user's password back to the default flow, so that a locked-out user can get back in.
15. As an admin, I want to grant or revoke the Admin role on a user, so that administration isn't tied to one account.
16. As an admin, I want to be blocked from deactivating or demoting myself, so that I can't lock out the last admin.

Admin — HRIS sync visibility:

17. As an admin, I want to see when the last HRIS sync ran and how many employees it covers, so that I know the data is fresh.
18. As an admin, I want to trigger a sync manually, so that I can pull a correction now instead of waiting for the next tick.

Data model:

19. As an employee, I want my name / position / email / location / manager to keep following HRIS, so that the portal stays correct when HRIS is corrected.
20. As an admin, I want my manual edits to a user to survive the next HRIS sync, so that I don't redo corrections every 15 minutes.
21. As an employee, I want the seeded admin account to exist after a fresh deploy, so that the system is administrable out of the box.

## Implementation Decisions

**Departments are dropped from the product surface, not the schema.** The `Department` table and `User.DepartmentId` column stay (removing them is a destructive migration with no upside), but nothing reads them for grouping and HRIS no longer populates them.

- `HrisSyncService` stops creating/updating departments and stops assigning `DepartmentId` — existing users keep whatever they have; the UI ignores it.
- The department-stubbing logic added for the dotted-code hierarchy is removed (dead once HRIS no longer drives the tree).

**Directory endpoint.** `/api/directory/employees` already returns the full flat list with presence — no endpoint change. The departments endpoint becomes unused by the UI and is left in place (harmless, may serve a later module).

**Admin endpoints (new, all `RequireAuthorization("AdminOnly")`):**

- `GET /api/admin/users?q=` — flat list, server-side filter by name/NIK, includes role + active + presence.
- `POST /api/admin/users` — create with NIK/name/email; `PasswordHash = null`, `MustChangePassword = true` (the existing default-password mechanism, zero new auth code).
- `PATCH /api/admin/users/{nik}` — edit name/email/position; sets an `IsManuallyEdited` flag on the row.
- `POST /api/admin/users/{nik}/deactivate` and `/reactivate` — deactivate also revokes sessions (reuse the session-revoke pattern already in `HrisSyncService`).
- `POST /api/admin/users/{nik}/reset-password` — back to `PasswordHash = null` + `MustChangePassword = true`.
- `PUT /api/admin/users/{nik}/roles` — set Admin/Employee membership (existing `Role`/`UserRole` tables).
- Self-lockout guard: deactivate / role-revoke reject when `{nik}` equals the caller's NIK.

**Sync honors manual edits.** `HrisSyncService` skips profile overwrites on rows where `IsManuallyEdited` is set. New column on `User`, one migration.

**Admin account seed.** `DevSeed` gains a user `ipopadmin` / Argon2 hash of `ipopadmin123`, granted the Admin role, `MustChangePassword = true` so the weak seed password is replaced on first login. Seeded idempotently (only when absent).

**Web — Administration page** (new route, visible only when `Me` carries the Admin role):

- Users table (search box, columns: NIK / Name / Email / Position / Role / Active / Presence), row actions for Edit / Reset password / Deactivate-Activate / Roles.
- A compact HRIS sync card (last-synced timestamp, employee count, "Sync now" button) — reuses the existing `/api/admin/hris/*` endpoints.
- Form and table reuse the existing tray/core/input styling and the `ProfileDrawer` pattern for the edit form. `AuthState.GetAsync` / `PostWithAuth` already handle bearer + silent refresh — the page consumes them, no new client plumbing.

**Web — Directory page** loses the tree panel and department filtering; the list, search, presence live-update, and profile drawer stay as-is.

## Testing Decisions

Good tests assert external behavior (HTTP surface, persisted state), not internals. Existing seam: the SQLite-in-memory xUnit suite against `HrisSyncService` and direct DbContext assertions.

- `HrisSyncService` tests updated: sync no longer creates departments or sets `DepartmentId`; a row with `IsManuallyEdited` survives a sync unchanged; auth/presence facts still untouched.
- New tests at the same seam: deactivate revokes sessions; self-lockout guard refuses; reset-password restores the default flow; create lands with `PasswordHash = null`.
- No new test seam — everything is reachable through the service layer or a DbContext, matching prior art in `HrisSyncServiceTests` and `AuthWorkflowTests`.

## Out of Scope

- Removing the `Department` table / `DepartmentId` column (kept dormant, not dropped).
- HRIS API-key rotation (separate standing item).
- Purging the 5 pre-sync dummy users (separate standing item; admin UI's deactivate covers it going forward).
- Endpoint-level TestHost integration tests (existing test-project constraint).
- Department reassignment UI — departments are gone, not reassigned.
- HTTPS / hosting hardening.

## Further Notes

- The admin seed password is weak by request; `MustChangePassword` forces its replacement on first login, so the weak value never survives first use.
- Departments surviving in the DB as dormant rows is deliberate: zero migration risk, and a future module that wants HRIS codes can re-enable the read path without a backfill.
