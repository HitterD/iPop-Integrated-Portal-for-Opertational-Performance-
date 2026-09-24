using System.Text.Json;
using IPop.Application.Settings;

namespace IPop.Api.Endpoints;

public static class SettingsEndpoints
{
    public static IEndpointRouteBuilder MapSettings(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/admin/settings").RequireAuthorization("AdminOnly");

        g.MapGet("/{key}", async (string key, ISettings settings, CancellationToken ct) =>
        {
            SettingKeys.Defaults.TryGetValue(key, out var fallback);
            var value = await settings.GetAsync<JsonElement?>(key, null, ct);
            if (value is null && fallback is null) return Results.NotFound();
            return Results.Ok(new { key, value = value ?? JsonSerializer.SerializeToElement(fallback) });
        });

        g.MapPut("/{key}", async (string key, JsonElement value, ISettings settings,
            HttpContext http, CancellationToken ct) =>
        {
            // Only known keys: a typo would otherwise silently create a dead setting.
            if (!SettingKeys.Defaults.ContainsKey(key))
                return Results.BadRequest(new { code = "unknown_setting", message = $"Unknown setting '{key}'." });
            await settings.SetAsync(key, value, http.User.FindFirst("nik")?.Value, ct);
            return Results.NoContent();
        });

        return app;
    }
}
