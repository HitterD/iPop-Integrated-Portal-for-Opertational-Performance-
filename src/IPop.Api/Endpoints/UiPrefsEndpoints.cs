using System.Security.Claims;
using System.Text.Json;
using IPop.Application.Shell;
using IPop.Core.Entities;
using IPop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IPop.Api.Endpoints;

/// Small per-user UI preferences that follow the account across devices:
///   tabs        — the four pinned phone tabs (validated against PhoneTabs; default when unset/invalid)
///   notif-card  — "dismissed" once the phone notification card was answered with "Not now"
///   dash-order  — dashboard widget order (string array)
///   notifier    — iPop Notifier desktop settings (IPop.Application.Notifier.NotifierSettings, normalised on save)
public static class UiPrefsEndpoints
{
    public const int MaxJsonBytes = 2048;
    private static readonly string[] Keys = ["tabs", "notif-card", "dash-order", "notifier"];

    public static IEndpointRouteBuilder MapUiPrefs(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/me/ui-prefs/{key}", async (string key, HttpContext http, IPopDbContext db, CancellationToken ct) =>
        {
            var userId = await MyId(http, db, ct);
            if (userId is null) return Results.Unauthorized();
            var (status, json) = await GetAsync(db, userId.Value, key, http.User.IsInRole("Admin"), ct);
            return status == 200 ? Results.Content(json!, "application/json") : Results.StatusCode(status);
        }).RequireAuthorization();

        app.MapPut("/api/me/ui-prefs/{key}", async (string key, JsonElement value, HttpContext http, IPopDbContext db, CancellationToken ct) =>
        {
            var userId = await MyId(http, db, ct);
            if (userId is null) return Results.Unauthorized();
            return Results.StatusCode(await PutAsync(db, userId.Value, key, value, http.User.IsInRole("Admin"), ct));
        }).RequireAuthorization();

        return app;
    }

    /// 200 + JSON, 204 when nothing is saved (tabs always answer with the effective list), 404 for an unknown key.
    public static async Task<(int Status, string? Json)> GetAsync(IPopDbContext db, Guid userId, string key, bool isAdmin, CancellationToken ct)
    {
        if (!Keys.Contains(key)) return (404, null);
        var json = await db.UserUiPrefs.AsNoTracking().Where(p => p.UserId == userId && p.Key == key)
            .Select(p => p.Json).FirstOrDefaultAsync(ct);
        if (key == "tabs")
            return (200, JsonSerializer.Serialize(PhoneTabs.Resolve(ReadStrings(json), isAdmin)));
        if (key == "notifier") return (200, IPop.Application.Notifier.NotifierSettings.Parse(json).ToJson());
        return json is null ? (204, null) : (200, json);
    }

    /// 204 saved, 400 invalid value, 404 unknown key.
    public static async Task<int> PutAsync(IPopDbContext db, Guid userId, string key, JsonElement value, bool isAdmin, CancellationToken ct)
    {
        if (!Keys.Contains(key)) return 404;
        var json = value.GetRawText();
        if (System.Text.Encoding.UTF8.GetByteCount(json) > MaxJsonBytes) return 400;
        var ok = key switch
        {
            "tabs" => PhoneTabs.IsValid(ReadStrings(json), isAdmin),
            "notif-card" => value.ValueKind == JsonValueKind.String && value.GetString() is "dismissed" or "enabled",
            "dash-order" => ReadStrings(json) is { Count: <= 40 } list && list.All(s => s.Length is > 0 and <= 32),
            "notifier" => value.ValueKind == JsonValueKind.Object,
            _ => false,
        };
        if (!ok) return 400;
        // Store only the normalised form: unknown fields and out-of-range values never reach the database.
        if (key == "notifier") json = IPop.Application.Notifier.NotifierSettings.Parse(json).ToJson();
        var row = await db.UserUiPrefs.FirstOrDefaultAsync(p => p.UserId == userId && p.Key == key, ct);
        if (row is null)
        {
            row = new UserUiPref { Id = Guid.NewGuid(), UserId = userId, Key = key, Json = json };
            db.UserUiPrefs.Add(row);
        }
        else row.Json = json;
        row.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return 204;
    }

    private static List<string>? ReadStrings(string? json)
    {
        if (json is null) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;
            var list = new List<string>();
            foreach (var e in doc.RootElement.EnumerateArray())
            {
                if (e.ValueKind != JsonValueKind.String) return null;
                list.Add(e.GetString()!);
            }
            return list;
        }
        catch (JsonException) { return null; }
    }

    private static Task<Guid?> MyId(HttpContext http, IPopDbContext db, CancellationToken ct)
    {
        var nik = http.User.FindFirst("nik")?.Value;
        return db.Users.AsNoTracking().Where(u => u.Nik == nik).Select(u => (Guid?)u.Id).FirstOrDefaultAsync(ct);
    }
}
