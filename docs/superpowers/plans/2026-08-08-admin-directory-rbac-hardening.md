# Admin, Directory, and RBAC Hardening Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Complete the Admin and Directory modules with correct placement, operational administration, audit/session visibility, scalable directory queries, realtime convergence, and hierarchical RBAC with user overrides.

**Architecture:** Keep the existing .NET 10 modular monolith, minimal API, PostgreSQL/EF Core, Blazor WebAssembly, and SignalR seams. Add focused application contracts and core entities, expose bounded query endpoints, keep append-only operational records, and use the existing notifier abstraction for realtime events. Deliver five independently buildable phases in order.

**Tech Stack:** .NET 10, ASP.NET Core Minimal APIs, EF Core/Npgsql, Blazor WebAssembly, SignalR, SQLite-backed xUnit tests.

## Global Constraints

- Use the existing ASP.NET Core minimal API and Blazor WASM patterns; do not add a UI or framework dependency.
- HRIS owns employee profile facts; Department and Site remain admin-owned and are never written by HRIS.
- Use bounded server-side pagination; never return unbounded user lists from new endpoints.
- Refresh token hashes and password hashes are never returned by APIs.
- Audit records are append-only; do not add update/delete audit endpoints.
- Permission resolution is default deny with precedence: user deny, user allow, direct role, inherited role, contextual rule, default deny.
- Preserve self-lockout protection for administrators.
- Reuse `IDirectoryChangeNotifier` and SignalR rather than creating a second realtime transport.
- Keep HA infrastructure monitoring, photo upload, Chat, Mail, Calls, mobile, and WPF out of scope.
- Each task ends with a targeted test, build, and commit.

---

## File Map

### Core and application contracts

- Create `src/IPop.Core/Entities/Permission.cs`: named permission entity.
- Create `src/IPop.Core/Entities/RolePermission.cs`: role-to-permission join entity.
- Create `src/IPop.Core/Entities/UserPermissionOverride.cs`: per-user allow/deny override entity with optional Department scope.
- Create `src/IPop.Core/Entities/HrisSyncRun.cs`: append-only sync run metadata.
- Modify `src/IPop.Core/Entities/User.cs`: expose placement/status data already persisted and navigation for overrides if needed.
- Modify `src/IPop.Core/Entities/Role.cs`: add permission navigation and hierarchy validation support.
- Modify `src/IPop.Core/Entities/AuditLog.cs`: retain immutable fields; add structured detail conventions without exposing secrets.
- Create `src/IPop.Application/Admin/AdminContracts.cs`: admin list, organization, session, and sync-history DTOs.
- Create `src/IPop.Application/Audit/AuditContracts.cs`: audit query/row DTOs.
- Create `src/IPop.Application/Rbac/RbacContracts.cs`: permission and authorization contracts.
- Create `src/IPop.Application/Rbac/IPermissionEvaluator.cs`: permission resolution interface.
- Modify `src/IPop.Application/Hris/DirectoryDtos.cs`: include Site fields and bounded directory response shapes.

### Infrastructure

- Modify `src/IPop.Infrastructure/Persistence/IPopDbContext.cs`: register new sets, relationships, indexes, and immutable audit conventions.
- Create EF migration via `dotnet ef migrations add AdminDirectoryRbacHardening --project src/IPop.Infrastructure --startup-project src/IPop.Api` after schema tasks.
- Create `src/IPop.Infrastructure/Rbac/PermissionEvaluator.cs`: recursive role inheritance, override precedence, contextual Department rule, default deny.
- Create `src/IPop.Infrastructure/Hris/HrisSyncRunRecorder.cs` or keep run persistence inside the sync service using the new entity.
- Modify `src/IPop.Infrastructure/Hris/HrisSyncService.cs`: record successful and failed runs with duration and counts.
- Modify `src/IPop.Infrastructure/Auth/AuthWorkflow.cs`: retain metadata behavior and audit session lifecycle consistently.

### API

- Modify `src/IPop.Api/Endpoints/AdminEndpoints.cs`: placement correctness, filters/sorts, organization CRUD/counts, session monitor, and role/permission endpoints.
- Modify `src/IPop.Api/Endpoints/DirectoryEndpoints.cs`: bounded server-side search/filter/sort/paging and Site output.
- Create `src/IPop.Api/Endpoints/AuditEndpoints.cs`: admin-only immutable audit query endpoint.
- Create `src/IPop.Api/Endpoints/RbacEndpoints.cs`: admin-only roles, permissions, role assignments, and user overrides.
- Modify `src/IPop.Api/Presence/DirectoryChangeNotifier.cs` or add an audit notifier: publish `directoryChanged` and `auditChanged` events.
- Modify `src/IPop.Api/Program.cs`: register evaluator, new services, and map endpoints.

### Web

- Modify `src/IPop.Web/Services/DirectoryEmployeeDto.cs`: Site and paging fields.
- Modify `src/IPop.Web/Services/AdminDtos.cs`: filters, organization counts, sessions, sync history, audit, and RBAC DTOs.
- Modify `src/IPop.Web/Models/Employee.cs`: Site and refresh state.
- Modify `src/IPop.Web/Pages/Admin.razor`: filters, placement preservation, organization edit/count controls, sync history, sessions, audit, and RBAC sections.
- Modify `src/IPop.Web/Pages/Directory.razor`: server-side query state, filters, sort/paging, reconnect indicator, selected-profile handling.
- Modify `src/IPop.Web/Components/ProfileDrawer.razor`: Site output and inactive/refresh state.
- Modify `src/IPop.Web/Services/AuthState.cs`: expose `auditChanged` if audit UI subscribes.

### Tests

- Modify `tests/IPop.Tests/HrisSyncServiceTests.cs`: sync run persistence and unchanged-row behavior.
- Create `tests/IPop.Tests/AdminEndpointTests.cs` or service-level tests for placement, filters, organization, sessions.
- Create `tests/IPop.Tests/AuditTests.cs`: query and append-only behavior.
- Create `tests/IPop.Tests/PermissionEvaluatorTests.cs`: inheritance, override precedence, scope, cycle/default deny.
- Modify `tests/IPop.Tests/AuthorizationPolicyTests.cs`: permission policy parity and self-lockout.

---

## Phase 1 — Data correctness

### Task 1: Make placement data atomic and visible

**Files:**
- Modify: `src/IPop.Api/Endpoints/AdminEndpoints.cs`
- Modify: `src/IPop.Application/Hris/DirectoryDtos.cs`
- Modify: `src/IPop.Api/Endpoints/DirectoryEndpoints.cs`
- Modify: `src/IPop.Web/Services/DirectoryEmployeeDto.cs`
- Modify: `src/IPop.Web/Services/AdminDtos.cs`
- Modify: `src/IPop.Web/Pages/Admin.razor`
- Modify: `src/IPop.Web/Models/Employee.cs`
- Modify: `src/IPop.Web/Components/ProfileDrawer.razor`
- Test: `tests/IPop.Tests/AdminEndpointTests.cs`

**Interfaces:**
- `DirectoryEmployee` gains `Site` and `SiteCode` after Department fields.
- Admin user row gains `Department`, `DepartmentCode`, `Site`, and `SiteCode`.
- Placement validation accepts only active records and returns `400` with `unknown_department` or `unknown_site`.

- [ ] **Step 1: Write failing tests for create/edit placement and Directory Site.**

```csharp
[Fact]
public async Task CreatedUserKeepsSelectedPlacement()
{
    await using var db = new TestDb();
    var department = new Department { Id = Guid.NewGuid(), Code = "ENG", Name = "Engineering", IsActive = true };
    var site = new Site { Id = Guid.NewGuid(), Code = "HQ", Name = "Headquarters", IsActive = true };
    db.Db.Departments.Add(department);
    db.Db.Sites.Add(site);
    await db.Db.SaveChangesAsync();

    var user = new User
    {
        Id = Guid.NewGuid(), Nik = "U1", Name = "User One", Email = "u1@example.test",
        DepartmentId = department.Id, SiteId = site.Id, CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow
    };
    db.Db.Users.Add(user);
    await db.Db.SaveChangesAsync();

    await using var verify = db.Verify();
    var saved = await verify.Users.SingleAsync(u => u.Nik == "U1");
    Assert.Equal(department.Id, saved.DepartmentId);
    Assert.Equal(site.Id, saved.SiteId);
}

[Fact]
public async Task InactivePlacementIsRejected()
{
    await using var db = new TestDb();
    db.Db.Departments.Add(new Department { Id = Guid.NewGuid(), Code = "OLD", Name = "Old", IsActive = false });
    await db.Db.SaveChangesAsync();

    var active = await db.Verify().Departments.SingleAsync(d => d.Code == "OLD");
    var result = await db.Verify().Departments
        .Where(d => d.Code == "OLD" && d.IsActive)
        .SingleOrDefaultAsync();
    Assert.Null(result);
}

[Fact]
public async Task DirectoryEmployeeContainsSite()
{
    await using var db = new TestDb();
    var site = new Site { Id = Guid.NewGuid(), Code = "HQ", Name = "Headquarters", IsActive = true };
    db.Db.Sites.Add(site);
    db.Db.Users.Add(new User
    {
        Id = Guid.NewGuid(), Nik = "U2", Name = "User Two", Email = "u2@example.test",
        SiteId = site.Id, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
    });
    await db.Db.SaveChangesAsync();

    var saved = await db.Verify().Users.Include(u => u.Site).SingleAsync(u => u.Nik == "U2");
    var row = new DirectoryEmployee(saved.Nik, saved.Name, saved.Position,
        saved.Department?.Name, saved.Department?.Code, saved.Site?.Name, saved.Site?.Code,
        null, saved.Email, saved.Extension, saved.Location, "off");
    Assert.Equal("HQ", row.SiteCode);
}
```

- [ ] **Step 2: Run the focused tests and verify they fail for the missing placement/Site behavior.**

Run: `dotnet test tests/IPop.Tests/IPop.Tests.csproj --no-restore --filter "FullyQualifiedName~AdminEndpointTests"`

Expected: FAIL because create does not apply placement and Directory DTO does not include Site.

- [ ] **Step 3: Implement a shared placement helper and apply it during create/edit.**

Use one private method in `AdminEndpoints`:

```csharp
private static async Task<IResult?> ApplyPlacement(
    User user, string? departmentCode, string? siteCode, IPopDbContext db, CancellationToken ct)
{
    user.DepartmentId = null;
    user.SiteId = null;
    if (!string.IsNullOrWhiteSpace(departmentCode))
    {
        var department = await db.Departments.SingleOrDefaultAsync(
            d => d.Code == departmentCode.Trim() && d.IsActive, ct);
        if (department is null) return Results.BadRequest(new { code = "unknown_department" });
        user.DepartmentId = department.Id;
    }
    if (!string.IsNullOrWhiteSpace(siteCode))
    {
        var site = await db.Sites.SingleOrDefaultAsync(
            s => s.Code == siteCode.Trim() && s.IsActive, ct);
        if (site is null) return Results.BadRequest(new { code = "unknown_site" });
        user.SiteId = site.Id;
    }
    return null;
}
```

Call it before `SaveChangesAsync` in create and edit/placement flows. Return early on a validation result so no partial placement is persisted.

- [ ] **Step 4: Add Site to API and client DTOs and render it in Admin and ProfileDrawer.**

The Directory query must include `u.Site`; the projection must emit `u.Site?.Name` and `u.Site?.Code`. The Admin edit drawer must initialize `DepartmentCode` and `SiteCode` from the row, and create must send placement after the user exists only if the API contract cannot make create atomic; preferred behavior is one create request containing placement.

- [ ] **Step 5: Run focused tests and both API/Web builds.**

Run:

```text
dotnet test tests/IPop.Tests/IPop.Tests.csproj --no-restore --filter "FullyQualifiedName~AdminEndpointTests"
dotnet build src/IPop.Api/IPop.Api.csproj --no-restore
dotnet build src/IPop.Web/IPop.Web.csproj --no-restore
```

Expected: PASS and zero build errors.

- [ ] **Step 6: Commit the phase-1 correctness slice.**

```text
git add src tests
 git commit -m "fix: keep admin placement and directory site data consistent"
```

### Task 2: Harden organization editing and inactive assignment semantics

**Files:**
- Modify: `src/IPop.Api/Endpoints/AdminEndpoints.cs`
- Modify: `src/IPop.Web/Pages/Admin.razor`
- Test: `tests/IPop.Tests/AdminEndpointTests.cs`

**Interfaces:**
- Organization rows include `MemberCount` and `ActiveMemberCount`.
- Department update accepts `Name`, `ParentCode`, and `IsActive`.
- Site update accepts `Name` and `IsActive`.

- [ ] **Step 1: Write tests for parent update, counts, and retaining existing inactive assignments.**

```csharp
[Fact]
public async Task OrganizationRowsExposeUsageCounts() { }

[Fact]
public async Task DeactivatingDepartmentDoesNotClearExistingAssignments() { }

[Fact]
public async Task DepartmentParentCannotCreateCycle() { }
```

- [ ] **Step 2: Run focused tests and verify failure.**

Run: `dotnet test tests/IPop.Tests/IPop.Tests.csproj --no-restore --filter "FullyQualifiedName~AdminEndpointTests"`

Expected: FAIL until counts, parent handling, and cycle validation exist.

- [ ] **Step 3: Implement parent-code validation and usage-count projections.**

Reject a parent that is the department itself, missing, inactive, or reachable from the child through the parent chain. Project counts with grouped user queries and leave existing `DepartmentId`/`SiteId` untouched when deactivating organization records.

- [ ] **Step 4: Add edit controls and explicit confirmation for records in use.**

Render member counts and require a browser confirmation before deactivation when count is non-zero. Keep server-side state authoritative; the confirmation is a usability guard, not a security guard.

- [ ] **Step 5: Run tests/build and commit.**

```text
dotnet test tests/IPop.Tests/IPop.Tests.csproj --no-restore --filter "FullyQualifiedName~AdminEndpointTests"
dotnet build src/IPop.Api/IPop.Api.csproj --no-restore
git add src tests && git commit -m "feat: complete organization administration"
```

---

## Phase 2 — Admin operations

### Task 3: Add bounded Admin user filtering and sorting

**Files:**
- Modify: `src/IPop.Api/Endpoints/AdminEndpoints.cs`
- Modify: `src/IPop.Web/Services/AdminDtos.cs`
- Modify: `src/IPop.Web/Pages/Admin.razor`
- Test: `tests/IPop.Tests/AdminEndpointTests.cs`

**Interfaces:**

```csharp
public record AdminUserQuery(
    string? Q, bool? Active, string? Role, string? DepartmentCode,
    string? SiteCode, bool? ManuallyEdited, string Sort = "name",
    bool Desc = false, int Page = 1, int PageSize = 50);
```

- [ ] **Step 1: Write tests for each filter and stable sort.**

```csharp
[Fact]
public async Task AdminUsersFilterByActiveRoleDepartmentSiteAndManualFlag() { }

[Fact]
public async Task AdminUsersSortIsStableAndPageSizeIsBounded() { }
```

- [ ] **Step 2: Run tests and verify failure.**

Run: `dotnet test tests/IPop.Tests/IPop.Tests.csproj --no-restore --filter "FullyQualifiedName~AdminEndpointTests"`

- [ ] **Step 3: Implement query composition with bounded page size.**

Apply filters before count, join role/Department/Site only as needed, whitelist sort columns (`nik`, `name`, `updated`, `status`), then apply `OrderBy` with `Nik` as a deterministic tie-breaker.

- [ ] **Step 4: Add filter controls and preserve query state on realtime reload.**

Use native selects and debounced text input already available in the page; reload page 1 when filters change and current page when a `directoryChanged` event arrives.

- [ ] **Step 5: Run build/tests and commit.**

```text
dotnet test tests/IPop.Tests/IPop.Tests.csproj --no-restore --filter "FullyQualifiedName~AdminEndpointTests"
dotnet build src/IPop.Web/IPop.Web.csproj --no-restore
git add src tests && git commit -m "feat: add admin user filters and sorting"
```

### Task 4: Persist HRIS sync history

**Files:**
- Create: `src/IPop.Core/Entities/HrisSyncRun.cs`
- Modify: `src/IPop.Infrastructure/Persistence/IPopDbContext.cs`
- Modify: `src/IPop.Infrastructure/Hris/HrisSyncService.cs`
- Modify: `src/IPop.Infrastructure/Hris/HrisSyncWorker.cs`
- Modify: `src/IPop.Api/Endpoints/HrisEndpoints.cs`
- Modify: `src/IPop.Web/Pages/Admin.razor`
- Test: `tests/IPop.Tests/HrisSyncServiceTests.cs`

**Interfaces:**

```csharp
public class HrisSyncRun
{
    public Guid Id { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime FinishedAt { get; set; }
    public int Inserted { get; set; }
    public int Updated { get; set; }
    public int Deactivated { get; set; }
    public bool Succeeded { get; set; }
    public string? Error { get; set; }
}
```

- [ ] **Step 1: Write success and failure persistence tests.**

```csharp
[Fact]
public async Task SyncWritesSuccessfulRunWithCountsAndDuration() { }

[Fact]
public async Task SyncWritesFailedRunWithErrorAndRethrows() { }
```

- [ ] **Step 2: Run tests and verify failure.**

Run: `dotnet test tests/IPop.Tests/IPop.Tests.csproj --no-restore --filter "FullyQualifiedName~HrisSyncServiceTests"`

- [ ] **Step 3: Record run start, success, and failure without advancing watermark on failure.**

Create a run before gateway access, update counts after persistence, set `Succeeded=false` and a bounded error message in the catch path, and rethrow. Set `FinishedAt` in both outcomes. The worker continues logging and retrying as today.

- [ ] **Step 4: Expose a paged history endpoint and render recent runs in Admin.**

Return newest first with a bounded page size; do not include stack traces or secrets in the API response.

- [ ] **Step 5: Migrate, test, build, and commit.**

```text
dotnet ef migrations add AddHrisSyncRuns --project src/IPop.Infrastructure --startup-project src/IPop.Api
dotnet test tests/IPop.Tests/IPop.Tests.csproj --no-restore --filter "FullyQualifiedName~HrisSyncServiceTests"
dotnet build src/IPop.Api/IPop.Api.csproj --no-restore
git add src tests && git commit -m "feat: record HRIS sync history"
```

### Task 5: Add session monitor and revocation

**Files:**
- Modify: `src/IPop.Api/Endpoints/AdminEndpoints.cs`
- Modify: `src/IPop.Web/Services/AdminDtos.cs`
- Modify: `src/IPop.Web/Pages/Admin.razor`
- Test: `tests/IPop.Tests/AdminEndpointTests.cs`

**Interfaces:**

```csharp
public record AdminSessionRow(Guid Id, string Nik, string Name, string? DeviceFingerprint,
    string? IpAddress, DateTime CreatedAt, DateTime ExpiresAt);
```

- [ ] **Step 1: Write tests for listing, single revoke, and revoke-all.**

```csharp
[Fact]
public async Task SessionListNeverReturnsRefreshTokenHash() { }

[Fact]
public async Task AdminCanRevokeOneSession() { }

[Fact]
public async Task AdminCanRevokeAllUserSessions() { }
```

- [ ] **Step 2: Run tests and verify failure.**

Run: `dotnet test tests/IPop.Tests/IPop.Tests.csproj --no-restore --filter "FullyQualifiedName~AdminEndpointTests"`

- [ ] **Step 3: Implement active-session query and revoke endpoints.**

Filter `RevokedAt == null` and `ExpiresAt > now`; select only safe metadata. Revoke writes `RevokedAt`, audits the action, and protects against self-session destructive surprises only where the existing self-lockout policy requires it.

- [ ] **Step 4: Render a compact session monitor with revoke controls.**

Show device fingerprint as a truncated label, never raw secrets beyond the existing fingerprint metadata contract. Confirm revoke-all.

- [ ] **Step 5: Run build/tests and commit.**

```text
dotnet test tests/IPop.Tests/IPop.Tests.csproj --no-restore --filter "FullyQualifiedName~AdminEndpointTests"
dotnet build src/IPop.Web/IPop.Web.csproj --no-restore
git add src tests && git commit -m "feat: add admin session monitor"
```

---

## Phase 3 — Audit

### Task 6: Add immutable audit query and structured details

**Files:**
- Create: `src/IPop.Application/Audit/AuditContracts.cs`
- Create: `src/IPop.Api/Endpoints/AuditEndpoints.cs`
- Modify: `src/IPop.Api/Endpoints/AdminEndpoints.cs`
- Modify: `src/IPop.Infrastructure/Auth/AuthWorkflow.cs`
- Modify: `src/IPop.Web/Services/AdminDtos.cs`
- Modify: `src/IPop.Web/Pages/Admin.razor`
- Test: `tests/IPop.Tests/AuditTests.cs`

**Interfaces:**

```csharp
public record AuditQuery(string? Actor, string? Action, string? Entity,
    DateTime? From, DateTime? To, int Page = 1, int PageSize = 50);
public record AuditRow(Guid Id, string? ActorNik, string Action, string? Entity,
    string? EntityId, string? DetailJson, string? IpAddress, DateTime CreatedAt);
```

- [ ] **Step 1: Write tests proving filtering, pagination, and append-only behavior.**

```csharp
[Fact]
public async Task AuditQueryFiltersByActorActionEntityAndDate() { }

[Fact]
public async Task AuditRecordsCannotBeUpdatedOrDeletedThroughAdminSurface() { }

[Fact]
public async Task AdminMutationStoresStructuredDetailJson() { }
```

- [ ] **Step 2: Run tests and verify failure.**

Run: `dotnet test tests/IPop.Tests/IPop.Tests.csproj --no-restore --filter "FullyQualifiedName~AuditTests"`

- [ ] **Step 3: Implement bounded immutable query endpoint and structured mutation details.**

Return newest first with `Id` tie-breaker. Use serialized objects for before/after or operation details, and redact password/token fields. Do not add mutation routes for audit rows.

- [ ] **Step 4: Add Audit section to Admin and audit event subscription.**

Render newest records with filters and reload on `auditChanged`; keep the existing admin page usable if the event connection is unavailable.

- [ ] **Step 5: Run build/tests and commit.**

```text
dotnet test tests/IPop.Tests/IPop.Tests.csproj --no-restore --filter "FullyQualifiedName~AuditTests"
dotnet build src/IPop.Api/IPop.Api.csproj --no-restore
git add src tests && git commit -m "feat: add immutable audit viewer"
```

### Task 7: Broadcast audit changes

**Files:**
- Modify: `src/IPop.Application/Realtime/IDirectoryChangeNotifier.cs` or create `src/IPop.Application/Realtime/IAuditChangeNotifier.cs`
- Modify: `src/IPop.Api/Presence/DirectoryChangeNotifier.cs`
- Modify: `src/IPop.Web/Services/AuthState.cs`
- Modify: `src/IPop.Web/Pages/Admin.razor`
- Test: `tests/IPop.Tests/AuditTests.cs`

**Interfaces:**

```csharp
public interface IAuditChangeNotifier
{
    Task NotifyAsync(CancellationToken ct = default);
}
```

- [ ] **Step 1: Write a notifier contract test using a fake hub/notifier.**

```csharp
[Fact]
public async Task AdminMutationNotifiesAuditSubscribers() { }
```

- [ ] **Step 2: Implement the notifier and invoke it after successful audit writes.**

Use `auditChanged` as the SignalR method name and never send audit contents through the event; clients refetch through the authorized endpoint.

- [ ] **Step 3: Subscribe/unsubscribe safely in `AuthState` and `Admin.razor`.**

Use `InvokeAsync` before component state mutation and remove handlers in `Dispose`.

- [ ] **Step 4: Run tests/build and commit.**

```text
dotnet test tests/IPop.Tests/IPop.Tests.csproj --no-restore --filter "FullyQualifiedName~AuditTests"
dotnet build src/IPop.Web/IPop.Web.csproj --no-restore
git add src tests && git commit -m "feat: stream audit updates in realtime"
```

---

## Phase 4 — Directory UX and performance

### Task 8: Add bounded Directory queries

**Files:**
- Modify: `src/IPop.Api/Endpoints/DirectoryEndpoints.cs`
- Modify: `src/IPop.Application/Hris/DirectoryDtos.cs`
- Modify: `src/IPop.Web/Services/DirectoryEmployeeDto.cs`
- Modify: `src/IPop.Web/Pages/Directory.razor`
- Modify: `src/IPop.Web/Models/Employee.cs`
- Test: `tests/IPop.Tests/DirectoryEndpointTests.cs`

**Interfaces:**

```csharp
public record DirectoryQuery(string? Q, string? DepartmentCode, string? SiteCode,
    string? Presence, string Sort = "name", bool Desc = false,
    int Page = 1, int PageSize = 120);
public record DirectoryPage(int Total, int Page, int PageSize, DirectoryEmployee[] Items);
```

Create `tests/IPop.Tests/DirectoryEndpointTests.cs` with a `DirectoryQueryService` seam if endpoint hosting is not already available. Do not invent an HTTP test server solely for this slice; the service must accept `IPopDbContext` and return `DirectoryPage` so filters are testable without authentication plumbing.

- [ ] **Step 1: Write tests for bounded page size, search, filters, and stable sorting.**

```csharp
[Fact]
public async Task DirectoryQueryReturnsBoundedPageAndTotal() { }

[Fact]
public async Task DirectoryQueryFiltersByDepartmentSiteAndPresence() { }
```

- [ ] **Step 2: Run tests and verify failure.**

Run: `dotnet test tests/IPop.Tests/IPop.Tests.csproj --no-restore --filter "FullyQualifiedName~DirectoryEndpointTests"`

- [ ] **Step 3: Implement server-side query composition.**

Filter active users, apply optional query against name/NIK, join Department/Site, compute presence in the projected result or query-compatible expression, whitelist sort keys, cap `PageSize` at 200, and return `DirectoryPage`.

- [ ] **Step 4: Replace client-side full-list loading with query state.**

Debounce text search using the existing event pattern, reset to page 1 on filter changes, preserve selected NIK, and keep Load More bounded by requesting the next page.

- [ ] **Step 5: Run tests/build and commit.**

```text
dotnet test tests/IPop.Tests/IPop.Tests.csproj --no-restore --filter "FullyQualifiedName~DirectoryEndpointTests"
dotnet build src/IPop.Api/IPop.Api.csproj --no-restore
dotnet build src/IPop.Web/IPop.Web.csproj --no-restore
git add src tests && git commit -m "perf: page and filter directory queries server-side"
```

### Task 9: Add Directory connection and profile states

**Files:**
- Modify: `src/IPop.Web/Services/AuthState.cs`
- Modify: `src/IPop.Web/Pages/Directory.razor`
- Modify: `src/IPop.Web/Components/ProfileDrawer.razor`
- Modify: `src/IPop.Web/Models/Employee.cs`
- Test: `tests/IPop.Tests/DirectoryEndpointTests.cs` or client seam test

**Interfaces:**

```csharp
public event Action<HubConnectionState>? PresenceConnectionChanged;
```

- [ ] **Step 1: Write a state-transition test for connected/reconnecting/disconnected.**

```csharp
[Fact]
public async Task DirectoryConnectionStateIsObservable() { }
```

- [ ] **Step 2: Implement connection-state events and UI indicator.**

Raise state changes from HubConnection lifecycle handlers. Render a non-blocking “Live”, “Reconnecting”, or “Offline — retry” indicator.

- [ ] **Step 3: Make selected profile converge safely.**

On directory refresh, replace the selected row by NIK; clear the drawer when no active row remains. Render Site and show a neutral unavailable state for inactive placement.

- [ ] **Step 4: Run build/tests and commit.**

```text
dotnet test tests/IPop.Tests/IPop.Tests.csproj --no-restore --filter "FullyQualifiedName~DirectoryEndpointTests"
dotnet build src/IPop.Web/IPop.Web.csproj --no-restore
git add src tests && git commit -m "feat: show directory live connection state"
```

---

## Phase 5 — RBAC

### Task 10: Add permission model and migration

**Files:**
- Create: `src/IPop.Core/Entities/Permission.cs`
- Create: `src/IPop.Core/Entities/RolePermission.cs`
- Create: `src/IPop.Core/Entities/UserPermissionOverride.cs`
- Modify: `src/IPop.Core/Entities/Role.cs`
- Modify: `src/IPop.Core/Entities/User.cs`
- Modify: `src/IPop.Infrastructure/Persistence/IPopDbContext.cs`
- Create: EF migration `AddPermissionsAndOverrides`
- Test: `tests/IPop.Tests/PermissionEvaluatorTests.cs`

**Interfaces:**

```csharp
public enum PermissionEffect { Allow, Deny }
public class Permission { public Guid Id; public required string Code; public string? Description; }
public class RolePermission { public Guid RoleId; public Guid PermissionId; public string? DepartmentCode; }
public class UserPermissionOverride { public Guid UserId; public Guid PermissionId; public PermissionEffect Effect; public string? DepartmentCode; }
```

- [ ] **Step 1: Write schema-level tests for unique permission codes and join relationships.**

```csharp
[Fact]
public async Task PermissionCodesAreUniqueAndJoinsPersist() { }
```

- [ ] **Step 2: Run test and verify failure.**

Run: `dotnet test tests/IPop.Tests/IPop.Tests.csproj --no-restore --filter "FullyQualifiedName~PermissionEvaluatorTests"`

- [ ] **Step 3: Add entities, navigation properties, indexes, and migration.**

Use unique indexes for permission code and join keys; configure optional Department scope and cascade behavior only for join rows, never audit rows.

- [ ] **Step 4: Seed minimal permissions and preserve Admin/Employee role semantics.**

Seed read-directory, manage-users, manage-organization, view-audit, manage-sessions, and manage-rbac codes. Admin receives all; Employee receives read-directory only. Do not grant unknown permissions.

- [ ] **Step 5: Run migration/tests/build and commit.**

```text
dotnet ef migrations add AddPermissionsAndOverrides --project src/IPop.Infrastructure --startup-project src/IPop.Api
dotnet test tests/IPop.Tests/IPop.Tests.csproj --no-restore --filter "FullyQualifiedName~PermissionEvaluatorTests"
dotnet build src/IPop.Api/IPop.Api.csproj --no-restore
git add src tests && git commit -m "feat: add permission and user override model"
```

### Task 11: Implement permission evaluation

**Files:**
- Create: `src/IPop.Application/Rbac/RbacContracts.cs`
- Create: `src/IPop.Application/Rbac/IPermissionEvaluator.cs`
- Create: `src/IPop.Infrastructure/Rbac/PermissionEvaluator.cs`
- Modify: `src/IPop.Api/Program.cs`
- Test: `tests/IPop.Tests/PermissionEvaluatorTests.cs`

**Interfaces:**

```csharp
public interface IPermissionEvaluator
{
    Task<bool> IsAllowedAsync(Guid userId, string permissionCode,
        string? departmentCode, CancellationToken ct = default);
}
```

- [ ] **Step 1: Write failing tests for inheritance and precedence.**

```csharp
[Fact]
public async Task ParentRolePermissionIsInherited() { }

[Fact]
public async Task UserDenyBeatsUserAllowAndRoleAllow() { }

[Fact]
public async Task DepartmentScopeMustMatch() { }

[Fact]
public async Task MissingPermissionDefaultsToDeny() { }
```

- [ ] **Step 2: Run focused tests and verify failure.**

Run: `dotnet test tests/IPop.Tests/IPop.Tests.csproj --no-restore --filter "FullyQualifiedName~PermissionEvaluatorTests"`

- [ ] **Step 3: Implement recursive role walk with cycle guard.**

Load user overrides and assigned roles, walk `ParentRoleId` until null while tracking visited role IDs, apply the stated precedence, and return false on cycles or missing permission.

- [ ] **Step 4: Register evaluator and add policy adapter.**

Register `IPermissionEvaluator` as scoped. Add a reusable authorization helper that gets the caller NIK/user ID and returns `403` when the permission is denied; keep `AdminOnly` on existing routes until parity is verified.

- [ ] **Step 5: Run tests/build and commit.**

```text
dotnet test tests/IPop.Tests/IPop.Tests.csproj --no-restore --filter "FullyQualifiedName~PermissionEvaluatorTests"
dotnet build src/IPop.Api/IPop.Api.csproj --no-restore
git add src tests && git commit -m "feat: resolve inherited and overridden permissions"
```

### Task 12: Add RBAC administration endpoints and UI

**Files:**
- Create: `src/IPop.Api/Endpoints/RbacEndpoints.cs`
- Modify: `src/IPop.Api/Program.cs`
- Modify: `src/IPop.Web/Services/AdminDtos.cs`
- Modify: `src/IPop.Web/Pages/Admin.razor`
- Modify: `tests/IPop.Tests/AuthorizationPolicyTests.cs`
- Create: `tests/IPop.Tests/RbacEndpointTests.cs`

**Interfaces:**

```csharp
GET  /api/admin/rbac
POST /api/admin/rbac/roles
PATCH /api/admin/rbac/roles/{id}
PUT  /api/admin/rbac/roles/{id}/permissions
PUT  /api/admin/rbac/users/{nik}/overrides
```

- [ ] **Step 1: Write endpoint tests for role CRUD, permission assignment, overrides, and self-lockout.**

```csharp
[Fact]
public async Task RoleCycleIsRejected() { }

[Fact]
public async Task AdminCannotRemoveTheirOwnAdminAccess() { }

[Fact]
public async Task UnknownPermissionIsRejected() { }
```

- [ ] **Step 2: Run tests and verify failure.**

Run: `dotnet test tests/IPop.Tests/IPop.Tests.csproj --no-restore --filter "FullyQualifiedName~RbacEndpointTests"`

- [ ] **Step 3: Implement admin-only RBAC endpoints with validation.**

Whitelist permission codes from the database, reject parent cycles, preserve the last-admin/self-lockout guard, and audit every RBAC mutation with actor, entity, and structured details.

- [ ] **Step 4: Add a compact RBAC management section in Admin.**

Display roles, parent roles, permission checkboxes, and user overrides. Reload after `directoryChanged`/RBAC writes. Keep the UI accessible with labels and keyboard controls.

- [ ] **Step 5: Run full tests/build and commit.**

```text
dotnet test tests/IPop.Tests/IPop.Tests.csproj --no-restore
dotnet build src/IPop.Api/IPop.Api.csproj --no-restore
dotnet build src/IPop.Web/IPop.Web.csproj --no-restore
git add src tests && git commit -m "feat: add admin RBAC management"
```

### Task 13: Verify phase integration and update PRD

**Files:**
- Modify: `PRD.md`
- Test: `tests/IPop.Tests/*`

- [ ] **Step 1: Run the complete test suite and both application builds.**

```text
dotnet test tests/IPop.Tests/IPop.Tests.csproj --no-restore
dotnet build src/IPop.Api/IPop.Api.csproj --no-restore
dotnet build src/IPop.Web/IPop.Web.csproj --no-restore
```

Expected: all tests pass and both builds have zero errors.

- [ ] **Step 2: Verify live behavior manually.**

Start API on `http://localhost:5083` and Web on `http://localhost:5103`; sign in as `ipopadmin`, verify create/edit placement, filtering, sync history, session revocation, audit refresh, Directory paging, and RBAC self-lockout behavior.

- [ ] **Step 3: Update PRD statuses and operational notes.**

Mark completed Admin/Directory capabilities as implemented, state that HA monitoring and communication modules remain future work, and document the server-side paging contract.

- [ ] **Step 4: Commit final integration documentation.**

```text
git add PRD.md tests && git commit -m "docs: mark admin directory and RBAC hardening complete"
```
