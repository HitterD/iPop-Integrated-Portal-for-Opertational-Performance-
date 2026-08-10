using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using IPop.Application.Hris;
using Microsoft.Extensions.Configuration;
using Polly;
using Polly.Retry;

namespace IPop.Infrastructure.Hris;

/// Production adapter for the HRIS-Santos replication gateway.
///
/// Real contract (verified against the live host, not the spec draft):
///   GET {base}/api/v1/employees?page=N   header: x-api-key
///   → { "data": [ …50 rows… ], "total": 4748 }
///
/// Three properties of that gateway drive this design:
///   • page size is server-fixed at 50 — pageSize/limit/per_page are all ignored;
///   • the quota is 10 requests/minute (429 + Retry-After), so pages are paced;
///   • there is no updatedSince filter, so every run is a full pull (~95 requests).
public class HttpHrisGateway : IHrisGateway
{
    private const int PageSize = 50;

    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly TimeSpan _pace;
    private readonly ResiliencePipeline _pipeline;

    public HttpHrisGateway(HttpClient http, IConfiguration config)
    {
        _http = http;
        _baseUrl = (config["Hris:BaseUrl"] ?? throw new InvalidOperationException("Hris:BaseUrl is required")).TrimEnd('/');
        _http.DefaultRequestHeaders.Add("x-api-key",
            config["Hris:ApiKey"] ?? throw new InvalidOperationException("Hris:ApiKey is required"));

        // 10 req/min quota; 6.5s leaves headroom for clock skew on the gateway's window.
        _pace = TimeSpan.FromSeconds(config.GetValue("Hris:PaceSeconds", 6.5));

        _pipeline = new ResiliencePipelineBuilder()
            .AddRetry(new RetryStrategyOptions { MaxRetryAttempts = 3, Delay = TimeSpan.FromSeconds(2) })
            .AddTimeout(TimeSpan.FromSeconds(30))
            .Build();
    }

    public async Task<IReadOnlyList<HrisEmployee>> SyncEmployeesAsync(DateTime? updatedSince, CancellationToken ct)
    {
        var all = new List<HrisEmployee>();
        for (var page = 1; ; page++)
        {
            if (page > 1) await Task.Delay(_pace, ct);
            var dto = await GetPageAsync(page, ct);
            if (dto?.Data is not { Count: > 0 }) break;
            all.AddRange(dto.Data.Select(Map));
            if (dto.Data.Count < PageSize) break;
        }
        return all;
    }

    /// One page. A 429 means the minute's quota is spent rather than that anything failed,
    /// so wait out the window the gateway names and ask again instead of aborting the pull.
    private async Task<PageDto?> GetPageAsync(int page, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var r = await _pipeline.ExecuteAsync(
                async c => await _http.GetAsync($"{_baseUrl}/api/v1/employees?page={page}", c), ct);

            if (r.StatusCode == HttpStatusCode.TooManyRequests && attempt < 2)
            {
                await Task.Delay((r.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(60)) + TimeSpan.FromSeconds(1), ct);
                continue;
            }
            r.EnsureSuccessStatusCode();
            return await r.Content.ReadFromJsonAsync<PageDto>(cancellationToken: ct);
        }
    }

    /// The feed is a straight replication of the HRIS tables, so it arrives with the
    /// warts of the source: Indonesian column names, department codes that carry stray
    /// whitespace, and no parent-department column — the hierarchy is encoded in the
    /// dotted code itself ("2.0002.002.001" sits under "2.0002.002").
    private static HrisEmployee Map(EmployeeDto e)
    {
        var nik = e.NikHris.Trim();
        var dept = Clean(e.IdDepartemen);
        // nik_gm names the GM of the person's own department, so for the GM it points at
        // themselves. A self-managing user would render as their own parent in the org tree.
        var manager = Clean(e.NikGm) is { } gm && gm != nik ? gm : null;

        return new HrisEmployee(
            Nik: nik,
            Nama: e.NamaKaryawan.Trim(),
            Email: Clean(e.Email) ?? "",
            Jabatan: Clean(e.NamaJabatan),
            KodeDept: dept,
            NamaDept: Clean(e.NamaDepartemen),
            ParentKodeDept: null,                 // hierarchy is implied by the dotted code; the sync service derives it
            NikAtasan: manager,
            Ekstensi: null,                       // not published by the gateway
            Lokasi: Clean(e.Lokasi),
            StatusAktif: e.TglKeluar is null,     // resignation date is the only active flag
            UpdatedAt: Utc(e.UpdateDate ?? e.CreateDate ?? DateTime.UnixEpoch));
    }

    private static string? Clean(string? s) => s?.Trim() is { Length: > 0 } v ? v : null;

    /// Npgsql rejects a non-UTC DateTime for `timestamptz`; the feed's trailing Z should
    /// already give us Utc, but a driver that hands back Unspecified must not shift.
    private static DateTime Utc(DateTime d) => d.Kind switch
    {
        DateTimeKind.Utc => d,
        DateTimeKind.Unspecified => DateTime.SpecifyKind(d, DateTimeKind.Utc),
        _ => d.ToUniversalTime(),
    };

    private record PageDto(
        [property: JsonPropertyName("data")] List<EmployeeDto> Data,
        [property: JsonPropertyName("total")] int Total);

    private record EmployeeDto(
        [property: JsonPropertyName("nik_hris")] string NikHris,
        [property: JsonPropertyName("nama_karyawan")] string NamaKaryawan,
        [property: JsonPropertyName("email")] string? Email,
        [property: JsonPropertyName("nama_jabatan")] string? NamaJabatan,
        [property: JsonPropertyName("id_departemen")] string? IdDepartemen,
        [property: JsonPropertyName("nama_departemen")] string? NamaDepartemen,
        [property: JsonPropertyName("nik_gm")] string? NikGm,
        [property: JsonPropertyName("lokasi")] string? Lokasi,
        [property: JsonPropertyName("tgl_keluar")] DateTime? TglKeluar,
        [property: JsonPropertyName("updatedate")] DateTime? UpdateDate,
        [property: JsonPropertyName("createdate")] DateTime? CreateDate);
}
