namespace IPop.Application.Sja;

public record SjaDeviceLinkResult(
    bool Success,
    string? DeviceName,
    string? EmployeeName,
    IReadOnlyList<string>? Capabilities,
    string? ErrorMessage = null);

public interface ISjaGateway
{
    /// <summary>
    /// Menghubungkan perangkat desktop ke akun karyawan menggunakan kode 6-digit (Fase 2).
    /// NIK diambil dari JWT sesi login IPOP, kemudian dikirim via request bertanda tangan HMAC ke SJA Backend.
    /// </summary>
    Task<SjaDeviceLinkResult> ApproveDeviceActivationAsync(string code, string nik, CancellationToken ct = default);
}
