# Foundation — Implementation Plan

**Spec:** `docs/superpowers/specs/2026-08-07-foundation-design.md`
**Date:** 2026-08-07

Order follows dependency: nothing ships before the layer below it works.

---

## Phase 1 — Solution & Database

1. Create `IPop.sln` with projects `IPop.Core`, `IPop.Application`, `IPop.Infrastructure`, `IPop.Api`, `tests/IPop.Tests` (xunit). `IPop.Web` already exists — keep as-is.
2. `docker-compose.yml` (dev only): PostgreSQL 17, one container, volume for data. No Patroni yet.
3. EF Core entities in `IPop.Core`: `Department`, `User`, `AuthSession`, `HrisSyncState`, `AuditLog` — fields exactly per spec §5.
4. First migration + `DbContext` in `IPop.Infrastructure`. Run against the container.

**Done when:** `dotnet ef database update` succeeds; tables exist.

## Phase 2 — Auth Backend

5. `Argon2PasswordHasher` (IPop.Infrastructure) — hash/verify.
6. `JwtTokenService` — access token (15 min), refresh token (14 d, rotating, hash stored).
7. Endpoints in `IPop.Api`:
   - `POST /api/auth/login` — NIK + password; null-hash → accept only `"1234"`; fail counter → `locked_until` after 5; returns tokens + `mustChangePassword` flag.
   - `POST /api/auth/change-password` — policy check (spec §3.1), hash, set flag false, revoke all user sessions, audit log.
   - `POST /api/auth/refresh` — rotation, 401 on revoked/unknown.
   - `POST /api/auth/logout` — revoke current session.
8. Error behavior per spec §3.2; device fingerprint accepted from client header (spec §3.3).

**Done when:** integration test — login → change password → old refresh token rejected.

## Phase 3 — HRIS Sync

9. `IHrisGateway` (IPop.Application): single method `Task<IReadOnlyList<HrisEmployee>> SyncEmployees(DateTime? updatedSince, ...)`.
10. Adapters: `MockHrisGateway` (in-memory), `JsonHrisGateway` (Development), `HttpHrisGateway` (Production, Polly: 3 retries / breaker 30s / timeout 10s per page).
11. `HrisSyncService`: upsert Departments → Users, HRIS `updatedAt` wins, auth fields never overwritten, soft-deactivate + revoke on `statusAktif=false`, write `last_updated`, audit counts.
12. `HrisSyncJob` BackgroundService — 15-min interval + `POST /api/admin/hris/sync` manual trigger.

**Done when:** integration test — sync JSON file twice → identical DB state (idempotent); deactivate revokes sessions.

## Phase 4 — Web Wiring (replace mocks)

13. `AuthState` service in IPop.Web: token storage (memory + refresh), auto-refresh on 401, redirect to `/login` on failure.
14. Replace mock rules in `Login.razor` / `ForceChangePassword.razor` with API calls; block all routes when `mustChangePassword`.
15. `Directory.razor` reads from `GET /api/directory/employees?dept=&q=` instead of `MockDirectory`.
16. Locked menu items → toast "Coming soon" (`aria-disabled`, not links).

**Done when:** full flow in browser against real API: login `1234` → force change → directory shows synced data.

## Phase 5 — Tests & Self-Check

17. Unit tests: hasher, JWT, rate limiter, upsert (incl. circular manager), password policy, conflict policy.
18. One runnable self-check (`tests/IPop.Tests/SelfCheck.cs`): sync → login → change password → old sessions dead.

**Done when:** `dotnet test` green.

---

## Explicitly NOT in this plan

- RBAC tables (deferred to Chat sub-project), HA infra, Hangfire, per-IP rate limiting, real hardware fingerprinting (WPF sub-project).
