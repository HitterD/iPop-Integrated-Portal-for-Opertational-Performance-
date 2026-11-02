# Approval Hierarchy + HC Presets + Directory Tree Fill — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Store and edit each employee's 9-slot approver chain from HC's workbook. HC roles can edit it in /admin. The workbook import also places everyone in the existing Directory tree.

**Architecture:**
- New `ApproverChain` entity (one row per NIK, 9 nullable NIK slots).
- One Infrastructure service (`ApprovalService`) owns chain edits, the `ManagerNik` sync and history (AuditLog).
- A pure parser plus two planners (chains, tree) turn workbook rows into a previewable plan. Apply executes that same plan in one transaction.
- Endpoints sit under `/api/approval`, gated by new permissions.
- /admin shows sections per permission instead of Admin-only.

**Tech Stack:** .NET 10, EF Core (Postgres prod / SQLite tests), Blazor WASM, xUnit, DevExpress Spreadsheet via existing `ISpreadsheetService`.

**Spec:** `docs/superpowers/specs/2026-11-02-approval-hierarchy-design.md`

## Global Constraints
- Tree naming is unchanged: UI Plant › Division › Department = DB `Site` › `Department` (nestable via ParentId) › `Division`. New unit codes follow the existing style (`OPS-PROD`, `HC-HS-STAF`).
- Rows with `PDMR feb 2026` = `ex` (case-insensitive, trimmed) are never imported.
- Permission codes are exactly `approval.read`, `approval.manage`, `directory.manage`.
- Role names are exactly `HR – News`, `HC – Approval`, `HC – Approval & Directory`, `HC – Lengkap` (en dash U+2013).
- Slot labels (1..9): `Manager`, `Asst. Manager 1`, `Asst. Manager 2`, `Asst. Manager 3`, `Asst. Manager 4`, `Plant Manager`, `Plant Ops Manager`, `General Manager`, `Director`.
- UI copy is English (app→English rule). Never show NIK to non-HC users (office rule: NIK only for admins/HC screens).
- Nothing is deleted from the tree, only deactivated. No notifications are sent on chain change.
- No parallel heavy jobs: run builds and tests one at a time (the PC is the prod host).
- Never touch prod data. Test on a prod-DB copy; deploy only on an explicit "deploy ke prod".
- The prod API runs with `ASPNETCORE_ENVIRONMENT=Development`, so `DevSeed.EnsureSeededAsync` runs on prod startup. Seeding must be idempotent.

## Review Focus
1. **NIKs that Excel turned into numbers** (`277` instead of `00000277`): all-digit NIKs shorter than 8 are left-padded to 8. Pinned in the Task 3 test `Parse_PadsNumericNiks`.
2. **Placeholder cells** `-`, `None`, `#N/A`, blank and whitespace count as an empty slot, not as an approver NIK. Pinned in Task 3 `Parse_TreatsPlaceholdersAsEmpty`.
3. **The same NIK twice in one file:** the last row wins and the preview lists the NIK under warnings. Pinned in Task 4 `Plan_DuplicateNik_LastWinsAndWarns`.
4. **An employee listed as their own approver:** the slot is kept as given, but `ManagerNik` skips self and takes the next distinct approver. Pinned in Task 2 `ManagerNik_SkipsSelf`.
5. **Headers with line breaks / double spaces** (`'NIK Approver2 \n(Asst. Manager)'`) and an extra trailing column: header lookup normalises whitespace and matches by prefix (`NIK Approver{n}`), never by column index. Pinned in Task 3 `Parse_FindsColumnsByNormalisedHeader`.

---

### Task 1: ApproverChain entity + migration

**Files:**
- Create: `src/IPop.Core/Entities/ApproverChain.cs`
- Modify: `src/IPop.Infrastructure/Persistence/IPopDbContext.cs` (DbSet + config)
- Create: migration `AddApproverChains` under `src/IPop.Infrastructure/Persistence/Migrations/`
- Test: `tests/IPop.Tests/ApproverChainModelTests.cs`

**Interfaces:**
- Produces: `class ApproverChain { string Nik (PK, max 32); string? Slot1Nik … Slot9Nik; string? SourceName; string? SourceJobTitle; string? SourceLevel; string? SourceWorkLocation; DateTime UpdatedAt; string? UpdatedByNik; string?[] Slots { get; } /* NotMapped view of Slot1..9 */ void SetSlot(int slot1Based, string? nik); }`, plus `DbSet<ApproverChain> ApproverChains`.
- The PK is the NIK string, not a FK to User, so chains survive for NIKs that don't have an account yet.

- [ ] **Step 1: Failing test** `ApproverChain_RoundTripsSlotsByNik`: save a chain with Nik `"00000277"` and Slot1 `"00000653"`, Slot8 `"00001250"`; reread with `t.Verify()`; assert `Slots[0]=="00000653"`, `Slots[7]=="00001250"`, `Slots[1]==null`. Also check that `SetSlot(10, "x")` throws `ArgumentOutOfRangeException`.
- [ ] **Step 2: Run** `dotnet test tests/IPop.Tests --filter ApproverChainModelTests` → FAIL (type missing).
- [ ] **Step 3: Implement** the entity and its DbContext config (table `approver_chains`, index on each Slot column so "replace everywhere" and "who approves X" can use them).
- [ ] **Step 4: Add the migration.** The API locks DLLs, so follow the memory "dotnet-ef while API runs": build to separate artifacts, then run `dotnet ef migrations add AddApproverChains --no-build`. Confirm the migration only creates `approver_chains` and its indexes.
- [ ] **Step 5: Run** the test → PASS. Commit `feat(approval): approver chain entity`.

### Task 2: ApprovalService — chain edits, compaction, ManagerNik sync, history

**Files:**
- Create: `src/IPop.Application/Approval/ApprovalDtos.cs`, `src/IPop.Application/Approval/IApprovalService.cs`
- Create: `src/IPop.Infrastructure/Approval/ApprovalService.cs` (register in the Infrastructure DI extension next to other services)
- Modify: `src/IPop.Api/Endpoints/OrgChartEndpoints.cs:150-165` so Publish stops writing/clearing `ManagerNik` (Position unchanged)
- Test: `tests/IPop.Tests/ApprovalServiceTests.cs`; update `tests/IPop.Tests/OrgChartTests.cs` assertions on ManagerNik

**Interfaces:**
- Consumes: `ApproverChain` (Task 1).
- Produces:
  - `static class ApproverSlots { string[] Labels; }` (9 labels from Global Constraints).
  - `record ChainStep(int Slot, string Label, string Nik, string? Name, bool Active, bool Known)`. `Known` = a User with this NIK exists.
  - `record ChainView(string Nik, string? Name, string? Plant, string? Division, string? Department, string?[] Slots, IReadOnlyList<ChainStep> Steps, IReadOnlyList<string> Issues, DateTime? UpdatedAt, string? UpdatedByName)`. Issue codes: `inactive_approver`, `unknown_approver`, `empty_chain`, `not_in_matrix`.
  - `static IReadOnlyList<ChainStep> ApprovalService.Compact(string?[] slots, Func<string, (string? Name, bool Active, bool Known)> lookup)`: drops empty slots and collapses a NIK equal to the previous non-empty step.
  - `static string? ApprovalService.FirstManager(string nik, string?[] slots)`: first non-empty slot ≠ `nik`.
  - `Task<ChainView?> GetAsync(string nik, CancellationToken)`
  - `Task<PagedResult<ChainView>> ListAsync(ChainQuery q, CancellationToken)`, with `record ChainQuery(string? Search, string? SiteCode, string? DepartmentCode, string? DivisionCode, string? Issue, int Page, int PageSize)`. Search covers name or NIK. The list covers active Users ∪ chains whose NIK has no user.
  - `Task<ChainView> SetSlotsAsync(string nik, string?[] slots, string actorNik, CancellationToken)`. It validates length 9, normalises NIKs (Task 3 `WorkbookNik.Normalize`), writes an AuditLog `approval.chain.update` with `DetailJson {before, after}`, and sets `User.ManagerNik = FirstManager(...)` when the user exists and the result is non-null.
  - `Task<int> BulkSetSlotAsync(IReadOnlyList<string> niks, int slot, string? approverNik, string actorNik, CancellationToken)`
  - `Task<int> ReplaceEverywhereAsync(string oldNik, string? newNik, string actorNik, CancellationToken)`
  - `Task<IReadOnlyList<ChainHistoryEntry>> HistoryAsync(string nik, CancellationToken)`, with `record ChainHistoryEntry(DateTime At, string? ActorName, string Summary)`. The summary is human text such as "Manager: Muhammad Rifa'i → Nico Budiman" or "Imported from workbook".

- [ ] **Step 1: Failing tests:**
  - `Compact_DropsEmptyAndCollapsesRepeats`: slots `[A,null,A,B,null,null,null,C,C]` → steps at slots 1,4,8 with NIKs A,B,C and labels `Manager`,`Asst. Manager 3`,`General Manager`.
  - `ManagerNik_SkipsSelf`: `FirstManager("X", [X,null,Y,…]) == "Y"`.
  - `SetSlots_UpdatesManagerNikAndWritesHistory`: user 100 with chain set to slot1=200 → `ManagerNik=="200"`, one AuditLog `approval.chain.update`, and `HistoryAsync` returns one entry whose summary contains "Manager".
  - `Get_FlagsInactiveAndUnknownApprovers`: slot1 = an inactive user, slot2 = an unknown NIK → issues contain `inactive_approver` and `unknown_approver`.
  - `ReplaceEverywhere_RewritesAllSlotsAndManagerNik`: three chains contain old NIK O in different slots → returns 3, no slot equals O, and ManagerNik is recomputed.
  - `BulkSetSlot_SetsOneSlotForMany`.
  - `List_FiltersByIssueAndIncludesUsersWithoutChain` (`not_in_matrix`).
- [ ] **Step 2: Run** `--filter ApprovalServiceTests` → FAIL.
- [ ] **Step 3: Implement** the service. Load names/active flags for all NIKs on the page in one query (no N+1).
- [ ] **Step 4: Change OrgChart Publish** so it no longer touches `ManagerNik`. Update the `OrgChartTests` lines that assert ManagerNik to assert it is unchanged, and update the class doc comment in `OrgChartEndpoints.cs:9`.
- [ ] **Step 5: Run** `--filter "ApprovalServiceTests|OrgChartTests"` → PASS. Commit `feat(approval): chain service with manager sync and history`.

### Task 3: Workbook parser

**Files:**
- Create: `src/IPop.Application/Approval/ApproverWorkbook.cs` (pure, no IO)
- Test: `tests/IPop.Tests/ApproverWorkbookTests.cs`

**Interfaces:**
- Consumes: rows as `List<string[]>` from `ISpreadsheetService.ReadSheetDataAsync(stream, sheetName)` (the caller picks sheet `4. ALL DATA_SJA` when present, else the first sheet).
- Produces:
  - `static class WorkbookNik { string? Normalize(string? raw); }`: trims and strips a leading tab. Placeholders `-`, `None`, `#N/A` and empty become null. All-digit values are padded to 8. An `ex_` prefix becomes null.
  - `record WorkbookRow(int RowNumber, string Nik, string Name, string? Division, string? Department, string? DivDept, string? Bagian, string? JobTitle, string? Level, string? WorkLocation, string?[] Slots /*9*/)`.
  - `record WorkbookParseResult(IReadOnlyList<WorkbookRow> Rows, int SkippedResigned, IReadOnlyList<string> Errors)`.
  - `static WorkbookParseResult ApproverWorkbook.Parse(IReadOnlyList<string[]> rows)`.
  - `static List<string[]> ApproverWorkbook.ToSheet(IEnumerable<WorkbookRow> rows)`: the export layout, using the same headers as `ALL DATA_SJA` minus the flag columns.
- Header matching: collapse whitespace, case-insensitive. Required headers: `NIK`, `Nama Karyawan`; optional: `PDMR feb 2026`, `Division`, `Department`, `Div / Dept`, `Bagian`, `Nama Jabatan`, `Level Jabatan`, `Work Location`; slots: `NIK Approver{n}` prefix for n=1..9. A missing required header is reported in `Errors` and no rows are returned.

- [ ] **Step 1: Failing tests** (build rows inline):
  - `Parse_SkipsExRows` (2 rows, one `ex` → 1 row, `SkippedResigned==1`).
  - `Parse_PadsNumericNiks` (`"277"` → `"00000277"`, also approver slots).
  - `Parse_TreatsPlaceholdersAsEmpty` (`-`, `None`, `#N/A`, `"  "`).
  - `Parse_FindsColumnsByNormalisedHeader` (header `"NIK Approver2 \n(Asst. Manager)"`, an extra unnamed trailing column, columns shuffled).
  - `Parse_MissingNikHeader_ReturnsError`.
  - `ToSheet_ThenParse_RoundTrips`.
- [ ] **Step 2: Run** → FAIL. **Step 3: Implement.** **Step 4: Run** → PASS.
- [ ] **Step 5: Real-file smoke check.** Use a throwaway test or console snippet that is not committed. Copy the workbook into a fresh temp folder, then parse `C:\Users\IT12\AppData\Local\Temp\hirarki_in\data.xlsx` through `SpreadsheetService`. Expect 4,530 rows and `SkippedResigned==398`. Commit `feat(approval): workbook parser`.

### Task 4: Chain import planner + apply

**Files:**
- Create: `src/IPop.Infrastructure/Approval/ApprovalImport.cs`
- Test: `tests/IPop.Tests/ApprovalImportTests.cs`

**Interfaces:**
- Consumes: `WorkbookParseResult` (Task 3), `ApprovalService` (Task 2).
- Produces:
  - `record ChainChange(string Nik, string Name, string Kind /*new|changed|cleared*/, string?[] Before, string?[] After)`.
  - `record ImportPlan(IReadOnlyList<ChainChange> Chains, TreePlan Tree, IReadOnlyList<string> UnknownEmployeeNiks, IReadOnlyList<string> InactiveApproverNiks, IReadOnlyList<string> Warnings, int Unchanged, string Token)`. `Token` = SHA-256 of the file bytes plus the `complete` flag.
  - `Task<ImportPlan> PlanAsync(WorkbookParseResult parsed, bool fileIsComplete, CancellationToken)`. When `fileIsComplete` is set, existing chains whose NIK is not in the file get a `cleared` change.
  - `Task<ImportResult> ApplyAsync(ImportPlan plan, string actorNik, CancellationToken)` does three things:
    - writes chains, ManagerNik and the tree (Task 5 `TreePlanner.ApplyAsync`) in **one transaction**;
    - writes one AuditLog `approval.import` with counts;
    - writes a per-NIK `approval.chain.update` entry with detail `source:"import"`.
  - `record ImportResult(int ChainsWritten, int UsersPlaced, int UnitsCreated, int UnitsDeactivated)`.
- Apply re-plans from the uploaded bytes on the server and rejects with 409 if the recomputed `Token` differs. The client never sends the plan back.

- [ ] **Step 1: Failing tests:**
  - `Plan_ClassifiesNewChangedUnchanged`.
  - `Plan_DuplicateNik_LastWinsAndWarns`.
  - `Plan_ListsUnknownEmployeesAndInactiveApprovers`.
  - `Plan_CompleteFile_ClearsMissing_ElseLeavesThem`.
  - `Apply_WritesChainsManagerNikAndAudit`.
  - `Apply_IsAtomic`: inject a failure in the tree step and assert no chain was written.
- [ ] **Step 2: Run** → FAIL. **Step 3: Implement.** **Step 4: Run** → PASS. Commit `feat(approval): import plan and apply`.

### Task 5: Directory tree planner (placement from the workbook)

**Files:**
- Create: `src/IPop.Infrastructure/Approval/TreePlanner.cs`, `src/IPop.Infrastructure/Approval/TreeMapping.cs` (the fixed mapping tables)
- Test: `tests/IPop.Tests/TreePlannerTests.cs`

**Interfaces:**
- Consumes: `WorkbookRow` (Task 3), DB `Site`/`Department`/`Division`/`User`.
- Produces:
  - `record UnitCreate(string Kind /*department|division*/, string Code, string Name, string? ParentCode, string? SiteCode)`.
  - `record UserMove(string Nik, string Name, string? FromPath, string ToPath, string? SiteCode, string DepartmentCode, string? DivisionCode)`.
  - `record UnitDeactivate(string Kind, string Code, string Path)`.
  - `record TreePlan(IReadOnlyList<UnitCreate> Creates, IReadOnlyList<UserMove> Moves, IReadOnlyList<UnitDeactivate> Deactivations, IReadOnlyList<string> Unmapped)`.
  - `Task<TreePlan> TreePlanner.PlanAsync(IReadOnlyList<WorkbookRow> rows, CancellationToken)`.
  - `Task TreePlanner.ApplyAsync(TreePlan plan, CancellationToken)`: no SaveChanges of its own; the caller's transaction commits.
- `TreeMapping` values come from spec §8:
  - `WorkLocation → Site code`: SJA-1, SJA 1 → `SJA 1`; SJA-2, SJA 2 → `SJA-KRW`; SJA-3 → `SJA-SKD`; SJA-SEMARANG → `SJA-SMG`; SJA-JAKARTA → `SJA-JKT`.
  - `Division → top Department code`: OPERATION* → `OPS`; MARKETING → `MKT`; HUMAN CAPITAL → `HC`; DAFT → `DAFT`; PROCUREMENT → `PROC`; R & D → `RND`; ICT → `ICT`; S&OE → `SOE`.
  - `OPERATION SJA SEMARANG POWDER` and `… RTD` → sub-Department `Powder` / `RTD` under OPS at `SJA-SMG`, and the Excel Department then nests under it.
- Matching rules:
  - Departments are matched by case-insensitive name among the parent's children; a missing one is created with code `{PARENT}-{first 4 letters of each word, upper, max 2 words}`, and a numeric suffix is added on collision.
  - A Division (Bagian) is matched by name + Department + Site; a missing one is created with code `{DEPTCODE}-{ABBR}` (or `{DEPTCODE}-{SITEABBR}-{ABBR}` when the site differs from the department's), following the existing `HC-HS-KRW-STAF` style.
  - If Bagian equals the Department name (case-insensitive), the user is placed on the Department with no Division.
  - Generic units (`Head`, `Staff`, `Staffs`, `Coord`, `Coordinator`, `Supervisor`) that end up with zero active users are deactivated.
  - Rows whose NIK has no User are skipped from Moves.
  - Unknown Division values go to `Unmapped` and those users are not moved.

- [ ] **Step 1: Failing tests:**
  - `Plan_MapsWorkLocationToSite` (SJA-2 → SJA-KRW).
  - `Plan_ReusesExistingDepartmentCaseInsensitive`.
  - `Plan_CreatesMissingDepartmentAndDivisionWithHouseCodes`.
  - `Plan_SemarangPowderNestsUnderOps`.
  - `Plan_BagianEqualsDepartment_NoDivision`.
  - `Plan_OverridesManualPlacement`.
  - `Plan_DeactivatesEmptiedGenericUnits_NotOthers`.
  - `Plan_UnknownDivision_GoesToUnmapped`.
  - `Apply_CreatesThenPlaces`.
- [ ] **Step 2: Run** → FAIL. **Step 3: Implement.** **Step 4: Run** → PASS. Commit `feat(approval): directory tree planner`.

### Task 6: Permission presets + client permission list

**Files:**
- Create: `src/IPop.Infrastructure/Rbac/RolePresets.cs` (called from `DevSeed.EnsureSeededAsync`, after the permission loop and replacing the HR block at `DevSeed.cs:81-95`)
- Modify:
  - `src/IPop.Infrastructure/Persistence/DevSeed.cs`: add the 3 permission codes to `permissionCodes` (Admin gets them via the existing loop).
  - The role-name checks for `"HR"`: `src/IPop.Api/Endpoints/AdminEndpoints.cs:57-59,110`, `src/IPop.Infrastructure/Projects/PortfolioService.cs:21`, `src/IPop.Infrastructure/Projects/ProjectTimeService.cs:188`, `src/IPop.Web/Components/Admin/AdminUserManagement.razor:315-320`. They must match `RolePresets.HrNews` (and keep accepting legacy `"HR"`).
  - `src/IPop.Application/Rbac/IPermissionEvaluator.cs` + `PermissionEvaluator.cs`: add `Task<IReadOnlyList<string>> AllowedCodesAsync(Guid userId, CancellationToken)`.
  - `src/IPop.Api/Endpoints/MeEndpoints.cs` (or the `/api/directory/me` handler): add `GET /api/me/permissions` → `string[]`.
  - `src/IPop.Web/Services/AuthState.cs`: add `Permissions` (loaded with Me) and `bool Can(string code)`.
- Test: `tests/IPop.Tests/RolePresetsTests.cs`; extend `tests/IPop.Tests/PermissionEvaluatorTests.cs`

**Interfaces:**
- Produces: `static class RolePresets { const string HrNews = "HR – News", HcApproval = "HC – Approval", HcApprovalDirectory = "HC – Approval & Directory", HcFull = "HC – Lengkap"; static Task EnsureAsync(IPopDbContext db, CancellationToken ct); }`.
- Permission matrix: as in spec §4.
- `EnsureAsync` behaviour:
  - It renames a role named `HR` to `HR – News` (memberships kept) and creates any missing roles.
  - It only **adds** missing grants, never removes grants an admin added later.
  - It is safe to run on every startup.

- [ ] **Step 1: Failing tests:**
  - `Ensure_RenamesHrKeepingMembers`.
  - `Ensure_CreatesPresetsWithExactGrants`.
  - `Ensure_IsIdempotentAndKeepsExtraGrants`.
  - `AllowedCodes_UnionsRolesAndOverrides`.
  - `HrNewsRole_StillCountsForPortfolioAndAdminFilter`.
- [ ] **Step 2: Run** → FAIL. **Step 3: Implement.** **Step 4: Run** `--filter "RolePresetsTests|PermissionEvaluatorTests|Portfolio|AdminUser"` → PASS. Commit `feat(rbac): HR/HC permission presets`.

### Task 7: Approval API + org endpoints opened to directory.manage

**Files:**
- Create: `src/IPop.Api/Endpoints/ApprovalEndpoints.cs` (mapped in `Program.cs` next to the other `Map*Endpoints`)
- Modify: `src/IPop.Api/Endpoints/AdminEndpoints.cs:379` (`/api/admin/org` group) and `OrgChartEndpoints` mounting: replace `RequireAuthorization("AdminOnly")` with `RequireAuthorization()` + `.RequirePermission("directory.manage")`.
- Test: `tests/IPop.Tests/ApprovalEndpointsTests.cs` (same seam as `OrgChartTests`: call the static handlers with TestDb); extend `tests/IPop.Tests/AuthorizationPolicyTests.cs`

**Interfaces:**
- Consumes: Tasks 2–6.
- Produces the routes below. Every response DTO is from Task 2 / Task 4.

| Route | Permission |
|---|---|
| `GET /api/approval/me` | any signed-in user (own chain; steps show names only, no NIK) |
| `GET /api/approval/chains?search&site&department&division&issue&page&pageSize` | `approval.read` |
| `GET /api/approval/chains/{nik}` | `approval.read` |
| `GET /api/approval/chains/{nik}/history` | `approval.read` |
| `PUT /api/approval/chains/{nik}` body `{ slots: string?[9] }` | `approval.manage` |
| `POST /api/approval/chains/bulk` body `{ niks, slot, approverNik }` | `approval.manage` |
| `POST /api/approval/replace` body `{ oldNik, newNik }` | `approval.manage` |
| `POST /api/approval/import/preview` multipart `file`, `complete` | `approval.manage` |
| `POST /api/approval/import/apply` multipart `file`, `complete`, `token` | `approval.manage` (+ `directory.manage` when the plan has tree changes; otherwise 403 with message "Directory changes need the Directory permission") |
| `GET /api/approval/export` → xlsx | `approval.read` |

- The upload limit is 10 MB, and only `.xlsx` is accepted.
- After an import apply that touched the tree, fire the same `directoryChanged` broadcast Publish uses.

- [ ] **Step 1: Failing tests:**
  - `Me_ReturnsOwnChainWithoutNiks`.
  - `Put_Requires9Slots` (400).
  - `Apply_WithStaleToken_Returns409`.
  - `Apply_TreeChangesWithoutDirectoryManage_Returns403`.
  - `OrgGroup_AllowsDirectoryManageNonAdmin`.
  - `OrgGroup_DeniesPlainEmployee`.
- [ ] **Step 2: Run** → FAIL. **Step 3: Implement.** **Step 4: Run** → PASS. Commit `feat(approval): approval API, org endpoints on directory.manage`.

### Task 8: /admin per-permission sections + Approval editor UI

**Files:**
- Modify: `src/IPop.Web/Pages/Admin.razor`:
  - Replace the `!Auth.IsAdmin` gate with "has any visible section".
  - Each `SectionDef` gets a `Func<AuthState,bool> Visible`. Admin-only sections stay `IsAdmin`; `organization` = `Can("directory.manage")`; new `approval` = `Can("approval.read")`, placed in the Organization group, icon `git-fork`.
  - The default section is the first visible one.
- Create: `src/IPop.Web/Components/Admin/AdminApproval.razor` (+ `.razor.css` if the folder uses scoped CSS; otherwise follow the existing admin stylesheet), `AdminApprovalPanel.razor`, `AdminApprovalImport.razor`
- Phone: in `Admin.razor`, the phone branch shows `AdminReadOnlyPhone` for `approval` (no phone editor in v1)
- Test: `tests/IPop.Tests/AdminApprovalRenderTests.cs` (bUnit, same style as `AdminNotifierTests`)

**Interfaces:**
- Consumes: Task 7 routes. People are picked with the existing `AppMemberPicker` (search by name); this is a staff screen, so the NIK may show as secondary text.

**Behaviour:**
- **Toolbar:** search box (300 ms debounce), Plant / Division / Department `AppSelect` filters, an issue chip row (`All`, `Inactive approver`, `Empty chain`, `Not in matrix`, `Unknown approver`), and buttons `Import`, `Export`, `Replace approver…`.
- **Table:**
  - Columns: checkbox, Name (NIK small), Department path, and the chain as chips with the label above each name. Inactive or unknown chips are red with a tooltip ("No longer active" / "No iPop account").
  - 50 rows per page.
  - Selecting rows shows a bar: "Set {slot} for {n} people…".
- **Panel (click a row):**
  - 9 labelled slots, each an `AppMemberPicker` plus a ✕ clear button.
  - Autosave 600 ms after the last change, with "Saving…" / "Saved" states and Retry on failure.
  - Under the slots: "Shown to {name} as" with the compacted chain preview, then History (newest first).
- **Import dialog:**
  - Upload, plus a "This file is the complete list" checkbox (off by default, with the warning text "People not in the file will lose their approvers").
  - The preview has tabs **Approvers** (new / changed / cleared, before → after), **Directory** (new units, moves, deactivations, unmapped) and **Warnings**.
  - The Apply button shows the counts. Apply is disabled while the preview is loading and when there are 0 changes.
- [ ] **Step 1: Failing bUnit tests:**
  - `Sidebar_HcApprovalSeesOnlyApproval`.
  - `Sidebar_AdminSeesAll`.
  - `Table_RendersInactiveApproverChipRed`.
  - `Panel_ClearSlot_SendsNullInPut`.
  - `Import_ApplyDisabledUntilPreviewLoaded`.
- [ ] **Step 2: Run** → FAIL. **Step 3: Implement.** **Step 4: Run** → PASS.
- [ ] **Step 5: Real-app check** with the `run` skill: start Web + API against the dev DB, sign in as a test user holding `HC – Approval`, and screenshot the table, panel and import preview. Commit `feat(approval): admin approval editor`.

### Task 9: "My approvers" on the profile

**Files:**
- Modify: `src/IPop.Web/Pages/Profile.razor` (own profile) and `src/IPop.Web/Pages/EmployeeProfile.razor:120-130` (the "Direct Manager" block keeps using `ManagerNik`, which is now correct)
- Test: `tests/IPop.Tests/ProfileApproversRenderTests.cs`

**Interfaces:**
- Consumes: `GET /api/approval/me`.
- Adds a card titled "My approvers" on /profile: numbered steps "{Label} — {Name}", no NIK. Empty state: "No approvers set yet. Contact HC if this looks wrong."
- [ ] **Step 1: Failing tests** `Profile_ShowsCompactedChainWithoutNik`, `Profile_EmptyChainMessage`. **Step 2: Run** → FAIL. **Step 3: Implement.** **Step 4: Run** → PASS. Commit `feat(approval): my approvers on profile`.

### Task 10: Prod-copy dry run for the user's review

**Files:**
- Create: `.scratch/approval-import/` (gitignored), holding the copy-DB recipe notes and preview exports

- [ ] **Step 1: Make a copy DB.** Restore the prod Postgres DB into a new DB `ipop_qa_approval` using the copy-DB recipe from memory (mobile micro-interactions QA). Never point anything at `ipop`.
- [ ] **Step 2: Migrate the copy.** Run the API from this branch against the copy so the migration and `RolePresets` run. Check: the `HR` role is renamed with the same member count, and the 4 preset roles exist.
- [ ] **Step 3: Preview the import** of the real workbook. Save the preview (Approvers / Directory / Warnings) as CSVs plus screenshots. Expected: ~4,362 chains, 168 unknown employees, inactive approvers 00001080 / 00054468 / 00002571, and the units to create, moves and deactivations listed.
- [ ] **Step 4: Stop.** Show the user the Directory preview (units to create, deactivations, unmapped) and wait for OK before Apply on the copy.
- [ ] **Step 5: Apply on the copy** after OK. Check:
  - `ManagerNik` now equals the first approver for ≥ 95% of matched users.
  - Spot-check 5 people in /directory and in the Admin › Approval table.
  - Report the numbers to the user.
- [ ] **Step 6:** Deploy to prod only after an explicit "deploy ke prod", using the current prod base and the deploy pattern from memory.
