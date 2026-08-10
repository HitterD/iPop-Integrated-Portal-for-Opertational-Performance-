# RBAC & Presence — Spec

**Date:** 2026-08-08
**Sub-project:** follow-up to Foundation (#1)
**Status:** Agreed via grilling session; seams confirmed by user
**Source:** PRD.md (lines 23, 37, 77, 82, 90) + grilling rounds 1–3

---

## Problem Statement

Right now any signed-in employee can trigger admin operations (`/api/admin/hris/sync`, `/api/admin/hris/status`) because the API only checks that a caller is authenticated, not that they are authorized. Meanwhile the Directory shows a static "online/offline" derived only from whether a session row exists, which is both inaccurate and never updates without a manual page reload — and the topbar carries a "Live" badge that claims a live feed that does not exist.

The user (Bagas) needs two things before any further module is built:

1. Only designated administrators may call admin endpoints; everyone else is a plain employee. The role model must already support the future case where an HR user creates dedicated admin/IM accounts, and must not be overwritten by the nightly HRIS sync.
2. Colleagues' real availability (online / away / busy / offline) must be visible in the Directory, update on its own without a page refresh, and reflect a user's manually-set "busy" state as well as genuine inactivity.

## Solution

Introduce a proper role/permission model local to the portal (roles are portal business, not HRIS business) and lock admin endpoints behind an admin-only authorization policy. Replace the placeholder status dot with a real presence system: clients send a lightweight heartbeat, the server computes each user's effective status from their last heartbeat plus any manually chosen status, and status changes are pushed to all connected clients over SignalR so the Directory updates live. The fake "Live" badge is removed from the topbar.

## User Stories

**RBAC**

1. As an administrator, I want to trigger and monitor HRIS sync, so that I can keep the directory current without touching the database.
2. As an administrator, I want a role I hold to be encoded in my access token, so that the API can authorize me without an extra database lookup per request.
3. As an employee (non-admin), I want admin endpoints to reject me with 403, so that I cannot accidentally or maliciously trigger administrative operations.
4. As an employee, I want my existing session to keep working after roles are introduced, so that the change does not force everyone to sign in again.
5. As a developer building the future Administration module, I want a roles and user-roles table that already supports many-to-many assignment, so that an HR user can later create and assign dedicated admin/IM accounts without a schema change.
6. As a security reviewer, I want the HRIS sync to never overwrite a user's role assignments, so that a nightly feed cannot silently grant or revoke administrative rights.
7. As the CTO account (20080115), I want to be seeded as the first administrator, so that at least one account can administer the system on day one.

**Presence**

8. As a signed-in user, I want my client to tell the server I am still here while my tab is open, so that colleagues see me as online rather than offline.
9. As a signed-in user, I want to mark myself busy, so that colleagues know not to interrupt me even though I am active.
10. As a signed-in user, I want to be shown as away automatically when I stop interacting, so that I do not have to manage my status by hand.
11. As a signed-in user, I want to be shown as offline when my client stops heartbeating (tab closed, machine asleep, network dropped), so that colleagues do not think I am ignoring them.
12. As a user browsing the Directory, I want each person's card to show a coloured presence dot (online / away / busy / offline), so that I can tell at a glance who is reachable.
13. As a user browsing the Directory, I want presence changes to appear without me refreshing the page, so that the information I see is current.
14. As a user, I want every colleague's presence visible regardless of department, so that the Directory works as a whole-company phone book.
15. As a user, I want my own card in the Directory to reflect my own current status, so that what others see matches what I see.

**Honesty of chrome**

16. As a user, I want the topbar to not claim a "Live" feed that does not exist, so that I am not misled about which parts of the app are real.

## Implementation Decisions

### RBAC

- **New entities:** `Role` (Id, Name unique, optional ParentRoleId for future hierarchical inheritance per PRD line 23) and `UserRole` (UserId + RoleId composite). Many-to-many. `User` gains a `UserRoles` navigation; `Role` gains a self-reference `Parent` so the hierarchy is representable now even though no endpoint resolves inherited permissions yet.
- **HRIS sync must not touch role assignments.** `HrisSyncService` continues to update only profile fields; it never reads or writes `UserRoles`. (Existing behaviour — asserted by the current `Sync_NeverTouchesAuthFields` test pattern — is extended to cover roles.)
- **JWT:** `JwtTokenService.CreateAccessToken` gains a `roles` parameter and emits one `ClaimTypes.Role` claim per role name. `AuthWorkflow.LoginAsync` and `RefreshAsync` load the user's role names and pass them through. Because refresh re-issues the token, a role change takes effect on the next refresh at the latest (≤15 min), which is acceptable for now; instant revocation of elevated rights is out of scope (see below).
- **Authorization:** an `AdminOnly` authorization policy requiring a role claim of `Admin` is registered in the API host. It is applied to `/api/admin/hris/sync` and `/api/admin/hris/status`, replacing the current bare `RequireAuthorization()` and deleting the `ponytail:` comment that flagged this gap.
- **Seeding:** a `Role` named `Admin` and one named `Employee` are created by migration seed data; the `20080115` (CTO) user is assigned `Admin`. All other users receive no explicit role (default = employee). No "seed one NIK as admin" hack — the CTO seed is chosen because it is the natural root of the org tree already present in the HRIS feed.
- **User override / permission resolution:** out of scope for this spec (see Out of Scope). The schema supports it; no endpoint computes effective permissions yet.

### Presence

- **Schema:** `User` gains `PresenceManualStatus` (nullable string; `null` or `"busy"`) and `PresenceLastSeenAt` (nullable timestamp). Presence is an attribute of the user, not its own entity — no separate table, because there is no presence history requirement in the PRD.
- **Effective status** is computed, never stored as a derived value. A single pure function is the unit-testable seam:

  ```csharp
  // Decision-rich shape; from the agreed design, not a demo.
  // "busy" is sticky (manual); "away"/"offline" derive from heartbeat age.
  static string ComputeStatus(DateTime? lastSeenAt, string? manualStatus, DateTime now) =>
      manualStatus == "busy" ? "busy"
      : lastSeenAt is null ? "off"
      : now - lastSeenAt < AwayAfter ? "on"
      : now - lastSeenAt < OfflineAfter ? "away"
      : "off";
  // AwayAfter = 5 min, OfflineAfter = 15 min (tunable constants).
  ```

- **Heartbeat:** `POST /api/presence/beat` (authorized) updates the caller's `PresenceLastSeenAt = now` and returns the caller's current effective status. The Blazor client beats every 60 seconds while the tab is visible (`document.visibilityState`), and not at all while hidden — a hidden tab is how "away" is detected without an explicit sweep job.
- **Manual status:** `POST /api/presence/status` (authorized, body `{ "status": "busy" | null }`) sets or clears `PresenceManualStatus`. Setting `null` returns the user to automatic.
- **Directory reads:** `GET /api/directory/employees` and `/api/directory/me` replace the session-exists hack (the current `ponytail:` in `DirectoryEndpoints`) with `ComputeStatus` evaluated per user. The `AuthSessions` "online" query is deleted.
- **Push transport (SignalR):** a `PresenceHub` is added to the API. When a user's *effective* status changes (as a result of a beat, a manual set, or a beat that aged them across a threshold), the change is broadcast to all connected clients. Clients apply the update to the matching card in place. The hub exists now because the user explicitly chose real-time push over polling (grilling Q11 → answer "a"); Chat will reuse the same connection later without changing this data model.
- **Threshold-crossing detection:** because "away"/"offline" are computed from heartbeat age, a user who simply stops beating changes status without any request firing. A lightweight in-process sweep (`BackgroundService`, every 60 s) recomputes effective statuses and broadcasts only actual changes (kept in a small in-memory map). This is the same worker pattern already used by `HrisSyncWorker`.
- **Client surface:** the Blazor `AuthState` gains `MyStatus` and a way to set busy/clear it; the Directory renders the dot and subscribes to hub updates; the topbar "Live" badge and its markup/styles are removed entirely.

### Modules touched

- **IPop.Core:** new `Role`, `UserRole`; `User` extended (presence columns + `UserRoles` nav).
- **IPop.Application:** no new service interfaces — presence and roles are thin enough to live at the endpoint/hub layer; DTOs for presence beat/status requests and the presence payload carried to clients.
- **IPop.Infrastructure:** EF migration for schema; `JwtTokenService` role claims; `AuthWorkflow` role loading; `PresenceHub`; presence sweep worker; presence status computation (pure function).
- **IPop.Api:** register `AdminOnly` policy, apply to admin endpoints, map presence endpoints + hub, wire the sweep worker.
- **IPop.Web:** heartbeat timer, SignalR client, presence dot rendering, busy toggle, remove "Live" badge.

## Testing Decisions

**What makes a good test here:** test external behaviour at the highest existing seam, never implementation details. The existing suite's pattern (xUnit + EF Core on SQLite in-memory, one context per operation to mirror the scoped-per-request production behaviour) is the prior art and is reused as-is.

**Seams (confirmed with user):**

- **RBAC → HTTP endpoint + JWT.** Integration-style tests on `AuthWorkflow` assert the issued token carries role claims; endpoint-level behaviour (Employee → 403 on `/api/admin/*`, Admin → 200) is asserted through the workflow + policy shape. No new seam introduced.
- **Presence → HTTP endpoint + the pure `ComputeStatus` function.**
  - `ComputeStatus` is unit-tested directly across all boundaries (never-beaten → off; fresh beat → on; aged past away-threshold → away; aged past offline-threshold → off; manual busy wins regardless of age; clearing busy returns to automatic). No DB required — this is the one new unit seam, and it is the highest point that still captures the real logic.
  - `POST /api/presence/beat` and `/status` are tested at the workflow/endpoint seam with SQLite: a beat updates `LastSeenAt`; setting busy makes the directory report `busy`; clearing it returns to computed.
  - The Directory endpoints are tested to return the computed status per user.
- **SignalR hub content is not asserted** (transport is a detail); what is asserted is that a status change produces the correct new status value, which is what the hub broadcasts. Hub delivery itself is exercised end-to-end later with the Chat module, which is the first feature to genuinely depend on push.
- **Self-check:** extend the existing runnable `SelfCheck` with a presence beat → directory read assertion so the single-command lifecycle check still covers the newest seam.

**Prior art reused:** `TestDb` (SQLite in-memory, `Verify()` for post-`ExecuteUpdateAsync` reads), `AuthWorkflowTests`, `HrisSyncServiceTests`, `SelfCheck`.

## Out of Scope

- **Hierarchical permission resolution and user-level overrides** (PRD line 23). The schema represents the hierarchy (`Role.ParentRoleId`) but no endpoint computes effective/inherited permissions yet — that arrives with the Administration module, which is also where an HR user will get the UI to create and assign accounts.
- **Instant revocation of elevated rights.** Role changes take effect on next token refresh (≤15 min). Forcing immediate revocation requires token introspection per request or a revocation list — deliberately deferred.
- **Presence history / analytics.** No record of when someone was online; only current status.
- **Away/busy automation from a calendar or device signals.** Busy is manual; away is inactivity-based only. Calendar-driven status waits for a calendar/Projects signal that does not exist yet.
- **Per-user or per-department presence visibility rules.** Everyone sees everyone (confirmed in grilling Q10).
- **HTTPS, HA, and public deployment hardening.** Internal HTTP only for now (confirmed in grilling Q4); the SignalR connection and auth design do not preclude a later move behind a TLS-terminating reverse proxy.
- **Emergency Broadcast, Ghost Mode audit, notification routing (PRD lines 26, 38, 82).** These are separate later modules; this spec only lays the role/presence groundwork they will build on.

## Further Notes

- The existing `Employee.Status` field in the Web models ("on"/"away"/"off") is retained as the wire value shape, extended with `"busy"`. Its meaning changes from "derived from a live session" to "computed presence", which is a behavioural fix, not a breaking schema change.
- The fake "Live" topbar badge is removed rather than repurposed (grilling Q3/Q7): real sync status already lives in the notification area; the topbar stays empty rather than swapping one placeholder for another.
- A known deliberate simplification: the 60 s heartbeat cadence plus 60 s sweep means an "away"/"offline" transition can take up to ~2× the sweep interval to broadcast. That ceiling is acceptable for a status indicator and will be revisited if Chat demands tighter timing.
