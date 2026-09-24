using System.Text.Json;
using IPop.Application.Settings;
using IPop.Core.Entities;
using IPop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IPop.Infrastructure.Settings;

public sealed class DbSettings(IPopDbContext db) : ISettings
{
    public async Task<T> GetAsync<T>(string key, T fallback, CancellationToken ct = default)
    {
        var json = await db.AppSettings.AsNoTracking()
            .Where(s => s.Key == key).Select(s => s.ValueJson).FirstOrDefaultAsync(ct);
        if (json is null) return fallback;
        try { return JsonSerializer.Deserialize<T>(json) ?? fallback; }
        catch (JsonException) { return fallback; }
    }

    /// Every change is audited with old and new value — settings alter system behavior.
    public async Task SetAsync<T>(string key, T value, string? actorNik, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var json = JsonSerializer.Serialize(value);
        var row = await db.AppSettings.FirstOrDefaultAsync(s => s.Key == key, ct);
        var old = row?.ValueJson;
        if (row is null)
            db.AppSettings.Add(row = new AppSetting { Key = key, ValueJson = json });
        row.ValueJson = json;
        row.UpdatedAt = now;
        row.UpdatedByNik = actorNik;
        db.AuditLogs.Add(new AuditLog
        {
            ActorNik = actorNik, Action = "settings.changed", Entity = "AppSetting", EntityId = key,
            DetailJson = JsonSerializer.Serialize(new { old, @new = json }), CreatedAt = now,
        });
        await db.SaveChangesAsync(ct);
    }
}
