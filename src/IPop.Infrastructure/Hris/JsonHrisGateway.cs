using System.Text.Json;
using IPop.Application.Hris;
using Microsoft.Extensions.Configuration;

namespace IPop.Infrastructure.Hris;

/// Development adapter: reads employees from a local JSON file (spec §4).
public class JsonHrisGateway(IConfiguration config) : IHrisGateway
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public async Task<IReadOnlyList<HrisEmployee>> SyncEmployeesAsync(DateTime? updatedSince, CancellationToken ct)
    {
        var path = config["Hris:JsonPath"] ?? "hris-employees.json";
        if (!File.Exists(path)) return [];

        await using var fs = File.OpenRead(path);
        var data = await JsonSerializer.DeserializeAsync<List<HrisEmployee>>(fs, Json, ct) ?? [];
        return updatedSince is null ? data : [.. data.Where(e => e.UpdatedAt > updatedSince)];
    }
}
