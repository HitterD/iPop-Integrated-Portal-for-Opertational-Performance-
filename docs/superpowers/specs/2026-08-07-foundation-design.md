# Foundation — Design Spec

**Date:** 2026-08-07
**Sub-project:** #1 of 10 (IPop decomposition)
**Status:** Approved for implementation

---

## 1. Purpose

Foundation is the first sub-project of IPop. Every other module depends on it for user identity, org structure, and session management. Scope: **Auth, HRIS sync, Directory, Blazor WASM shell**.

## 2. Architecture

Modular monolith, single solution:

```
IPop.sln
├── src/
│   ├── IPop.Api/            → ASP.NET Core host, controllers
│   ├── IPop.Core/           → entities, enums (no external dependencies)
│   ├── IPop.Application/    → use cases, DTOs, interfaces (IHrisGateway, etc.)
│   ├── IPop.Infrastructure/ → EF Core, GatewayClient, Argon2, JWT, sync jobs
│   └── IPop.Web/            → Blazor WASM (shell + auth + directory)
├── tests/IPop.Tests/        → unit + integration
└── docs/
```

## 3. Auth — 100% Local

No calls to HRIS Gateway during login.

**Flow:**

1. User enters NIK + password
2. Look up `Users` row by NIK. If `locked_until > now` → reject with lock message.
3. Verify password:
   - If `password_hash` is **not null** → Argon2 verify against stored hash.
   - If `password_hash` is **null** → accept **only** the literal `"1234"` (default password from HR). Any other value counts as a failed attempt.
4. New NIK arrives via HRIS sync → row created with `password_hash = null`, `must_change_password = true`.
5. `MustChangePassword = true` → redirect to force-change screen, all other routes blocked.
6. Password change → validate policy (see §3.1) → Argon2 hash → set `password_hash`, `must_change_password = false` → **global session invalidation** (revoke all this user's refresh tokens) → audit log.
7. Issue JWT access token (15 min) + refresh token (rotating, 14 days). Store refresh-token **hash** in `AuthSessions`, never the raw token.
8. Rate limiting: 5 failed attempts (wrong password or wrong default) → set `locked_until = now + 15 min` (tracked per NIK in `Users.failed_login_count` / `locked_until`; per-IP tracking is a §3.2 stretch).

**Key decisions:**
- `password_hash` nullable is the mechanism that makes the default-password flow work without storing `"1234"` anywhere.
- `IHrisGateway.VerifyPassword` and `LookupNIK` are **removed** — only `SyncEmployees()` remains.
- No Gateway fallback policy needed in auth path (zero Gateway dependency).

### 3.1 Password Policy

Enforced in the force-change screen (UI) **and** the change-password endpoint (server — never trust the client):

| Rule | Check |
|---|---|
| Minimum length | `≥ 8` characters |
| Character classes | at least one uppercase letter **and** one digit |
| Not the default | must differ from `"1234"` |
| Fields match | new password == confirm password |

Server rejects with a 400 + field-level error map if any rule fails.

### 3.2 Error Handling (auth path)

| Failure | Behavior |
|---|---|
| DB unreachable during login | 503 + generic "Service temporarily unavailable." No stack trace to client. Logged server-side. |
| JWT expired mid-session | Client refreshes using refresh token; if refresh also fails → redirect to `/login`. |
| Refresh token invalid / revoked | 401 → client clears tokens → redirect to `/login`. |
| Refresh token valid but user `is_active=false` | Revoke session, 401, redirect to `/login` with "Account deactivated" message. |
| Concurrent refresh of same token | First wins (token rotated); second gets 401 — standard rotation race. |
| Rate limit hit | 423 Locked + "Too many attempts. Try again in N min." |

### 3.3 Device Fingerprint

Stored in `AuthSessions.device_fingerprint`. Source in the web client: `navigator.userAgentData` (brand, platform, mobile) + `navigator.language` + screen dimensions, joined into a stable hash. **Not** a hardware hash — WASM cannot read one. The WPF Agent (later sub-project) supplies a real hardware hash; the schema column is sized to hold either. Purpose: anomaly surfacing in the future session/devices admin view, not auth blocking in Foundation.

## 4. HRIS Sync

**Endpoint required from Gateway team:**

```
GET /employees?updatedSince={iso8601}&page={n}&pageSize=500
→ { items: [{ nik, nama, email, jabatan, kodeDept, namaDept,
              parentKodeDept, nikAtasan, ekstensi, lokasi,
              statusAktif, updatedAt }], nextCursor }
```

`updatedSince` is an ISO-8601 timestamp; `nextCursor` is opaque (page handle). The local `HrisSyncState.last_updated` stores the **timestamp** of the most recent record seen (not the cursor), so a re-run after a crashed job re-queries from that timestamp and re-upserts idempotently.

**Sync job** (`BackgroundService`, every 15 min + manual admin trigger):

1. Read `last_updated` from `HrisSyncState` (null on first run → full pull).
2. Pull page by page, **upsert per NIK** (idempotent — safe to re-run).
3. Order: `Departments` first (self-referencing parent), then `Users`.
4. Conflict policy: **HRIS `updatedAt` always wins** for employee profile fields (name, email, position, dept, manager, ext, location). Local `Users.updated_at` is only a tracking column, never an authority. Exception: `password_hash`, `must_change_password`, `failed_login_count`, `locked_until` are **never** overwritten by sync — those are IPop-local auth state.
5. `statusAktif=false` → soft deactivate (`is_active=false`) + revoke that user's active sessions.
6. Write `last_updated = max(item.updatedAt)`; log insert/update/deactivate counts → audit log.
7. Initial run without `last_updated` = full pull.

**Sync failure handling:** Polly on the HTTP call (retry 3x, circuit breaker 30s, timeout 10s per page). On circuit open or unhandled error → log, leave cursor unchanged, retry next tick. No partial cursor advance.

**Adapters** (`IHrisGateway` — single method `SyncEmployees()`):
- `Production` → HTTP to Gateway (Polly-wrapped)
- `Development` → local JSON file
- `Mock` → in-memory for tests

## 5. Database Schema (Foundation only)

PostgreSQL (single container for dev; HA Primary-Secondary at deployment phase).

```sql
Departments   : id, code (unique), name, parent_id (nullable self-ref), is_active
Users         : id, nik (unique), name, email, password_hash (nullable),
                must_change_password, position, department_id → Departments,
                manager_nik, extension, location, photo_path,
                is_active, failed_login_count, locked_until, created_at, updated_at
AuthSessions  : id, user_id, refresh_token_hash, device_fingerprint,
                ip_address, created_at, expires_at, revoked_at
HrisSyncState : id (singleton), last_updated, last_full_sync_at, last_incremental_at
AuditLogs     : id, actor_nik, action, entity, entity_id, detail_json,
                ip_address, created_at   -- append-only, immutable
```

**Audit log retention:** default 90 days. Partition by `created_at` month (`audit_logs_yyyymm`) so old partitions are detachable without a delete scan. Configurable via `Audit:RetentionDays`. Append-only enforced at the app layer (no UPDATE/DELETE path); a DB role with only INSERT on `AuditLogs` is the belt-and-suspenders.

**RBAC tables deferred** — `Roles`, `Permissions`, `ContextualRules` are added in the first sub-project that needs them (Chat admin actions). Foundation provides `position` + `department_id` as the raw material.

## 6. UI — Blazor WASM

- **Shell:** left sidebar (Dashboard, Directory active; Chat/Mail/Projects/etc. shown as locked menu items), header with user name
- **Pages:** Login → Force Change Password → Dashboard (placeholder) → **Directory**: interactive org tree + employee table (search by name/NIK/dept) + profile card
- **Style:** functional enterprise — Lucide icons, no hero/gradient/emoji, WCAG AA contrast

**Locked menu behavior:** clicking a locked item shows a transient toast "Coming soon" (no navigation, no route change). Keyboard: locked items are not focusable as links (`aria-disabled`).

**Already implemented and verified** (mockup + Blazor app):
- Login with `demo` → `/directory`; login with `1234` → `/set-password` → rules live-update → submit → `/directory`
- Directory: dept tree filter, employee grid, profile drawer (Escape to close)
- Dept switch clears search, updates crumb
- Sign out → `/login`
- Password rules keyed correctly (no Blazor duplicate-key crash)

> Note: the current UI uses a mock auth (`password == "demo"` / `"1234"`). The real flow (§3) replaces this when the API ships — the UI surface stays the same.

## 7. Testing

- **Unit:** Argon2 hasher, JWT service, sync upsert (incl. circular-manager edge case), rate limiter, force-change-password policy, conflict policy (HRIS wins, auth fields preserved)
- **Integration:** full login flow with Mock adapter; sync job with Development adapter (JSON file); refresh-token rotation race; session invalidation on password change
- **Self-check:** one runnable `Program.cs`-style check — sync → login → change password → old sessions killed. Fails loudly if any step breaks.

## 8. Open Items

| Question | Status |
|---|---|
| Blazor WASM vs Server | WASM per PRD. Revisit if internal-network-only deployment is confirmed |
| Android only, no iPhone? | Assumed yes per PRD. Affects future MAUI sub-project, not Foundation |
| Company brand color (currently emerald `#2e7d5b`) | Change one CSS variable `--accent` if different |
| Per-IP rate limiting | Stretch — Foundation ships per-NIK lock only; per-IP needs a shared store (Redis) added in a later sub-project |
