namespace IPop.Application.Hris;

/// HRIS employee record as delivered by the Gateway bulk endpoint (spec §4).
public record HrisEmployee(
    string Nik,
    string Nama,
    string Email,
    string? Jabatan,
    string? KodeDept,
    string? NamaDept,
    string? ParentKodeDept,
    string? NikAtasan,
    string? Ekstensi,
    string? Lokasi,
    bool StatusAktif,
    DateTime UpdatedAt);

/// Single-method gateway per spec §4 — VerifyPassword/LookupNIK were removed
/// when auth went 100% local.
public interface IHrisGateway
{
    Task<IReadOnlyList<HrisEmployee>> SyncEmployeesAsync(DateTime? updatedSince, CancellationToken ct);
}

/// Result of one sync run, logged to audit.
public record HrisSyncResult(int Inserted, int Updated, int Deactivated, DateTime SyncedAt);

public interface IHrisSyncService
{
    Task<HrisSyncResult> RunAsync(CancellationToken ct);
    /// triggeredBy: NIK of the admin who started the run; null for scheduled runs.
    Task<HrisSyncResult> RunAsync(string? triggeredBy, CancellationToken ct);
}
