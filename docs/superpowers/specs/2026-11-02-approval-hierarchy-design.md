# Approval hierarchy + HC presets + Directory tree fill — design

Grilled with the user 2026-11-02. Source data: HC workbook `Data Hirarki Approver.xlsx`, sheet `4. ALL DATA_SJA`.

## Source data
- 4,928 rows; column `PDMR feb 2026` = `ex` marks a resigned employee → ignored (4,530 active rows).
- Per employee: NIK, Nama, Division, Department, Div / Dept, Bagian, Nama Jabatan, Level Jabatan, Work Location, and 9 approver slots (NIK + Nama each):
  1 Manager · 2–5 Asst. Manager · 6 Senior/Plant Manager · 7 Senior/Plant Ops Manager · 8 General Manager · 9 Director.
- Other sheets are ignored (incl. `APPROVER_*_DIV_DEPT`, `Hak Akses Manager`, `user.odoo`).

## Decisions
1. **Master data only.** No request/approval workflow yet; the data model must allow one later.
2. **Storage = fixed slots** (1–9, as the workbook). Display = compacted chain (empty slots dropped, consecutive duplicates collapsed) with the slot's role label.
3. **Resigned / inactive approvers** are stored as given and flagged ("approver no longer active"). HRIS deactivation makes the flag appear automatically (computed at read time).
4. **Permission presets** (Roles), with new permissions `approval.read`, `approval.manage`, `directory.manage`:
   | Role | Permissions |
   |---|---|
   | Admin | everything (unchanged) |
   | HR – News | news.read, news.write, news.publish (existing "HR" role renamed; members kept) |
   | HC – Approval | approval.read, approval.manage |
   | HC – Approval & Directory | + directory.manage |
   | HC – Lengkap | + news.read, news.write, news.publish |
5. **Visibility:** every employee sees their own chain on their profile; `approval.read` holders see everyone's.
6. **Editing UI lives in /admin**; sections are shown per permission (HC sees Approval, and Organization if `directory.manage`). Features: searchable/filterable table (Plant/Division/Department, "has issues": inactive approver, empty chain, not in matrix), side panel with name pickers per slot + clear + autosave, bulk "set slot X = person Y", "replace approver everywhere", per-employee history.
7. **Import** = same format as `ALL DATA_SJA` (`ex` rows skipped). Upload → diff preview (new, changed, inactive approvers, unknown NIKs, tree changes) → Apply. Rows absent from the file are untouched unless "this file is the complete list" is ticked (then their chains are cleared). **Export/template** produces the same layout. No notifications on change.
8. **Directory tree** keeps the current naming: UI Plant › Division › Department = DB `Site` › `Department` (nestable) › `Division`.
   - Work Location → Plant: SJA-1 / SJA 1 → `SJA 1` (Sepanjang), SJA-2 / SJA 2 → `SJA-KRW`, SJA-3 → `SJA-SKD`, SJA-SEMARANG → `SJA-SMG`, SJA-JAKARTA → `SJA-JKT`.
   - Excel Division → existing top-level Department (OPERATION* → OPS, MARKETING → MKT, HUMAN CAPITAL → HC, DAFT, PROCUREMENT → PROC, R & D → RND, ICT, S&OE → SOE). "OPERATION SJA SEMARANG POWDER/RTD" → sub-Departments "Powder" / "RTD" under OPS, site SJA-SMG.
   - Excel Department → sub-Department (matched case-insensitively by name under the parent; created when missing, code `{PARENT}-{ABBR}`).
   - Excel Bagian → Division (unit) under that sub-Department at the user's Plant; created when missing; if Bagian equals the Department name the user sits on the sub-Department with no Division.
   - Excel placement overrides existing manual placement.
   - Generic role-units (Head, Staff, Staffs, Coord, Coordinator, Supervisor) left with zero users are deactivated (never deleted).
   - Every tree change is shown in a preview before anything is written.
9. **ManagerNik := first non-empty approver** of the chain, recomputed on every chain change. Org chart Publish stops writing `ManagerNik` (still writes `Position`). Users with no chain keep their current value.
10. **Excel NIKs without an iPop account** (168): chain stored by NIK; it shows up once HRIS creates the account. iPop users absent from the workbook (593) are left as they are and flagged "not in matrix".
11. **Rollout:** build on branch `hirarki-approval-dan-directory-tree`, first import on a copy of the prod DB for the user to review; prod only on an explicit "deploy ke prod".
