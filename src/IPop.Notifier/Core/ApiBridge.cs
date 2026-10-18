using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace IPop.Notifier.Core;

/// The pages ask the app to call iPop for them (they never hold the token). Only these routes are allowed, so a
/// page — which shows mail HTML and other people's text — can never reach anything else with the person's account.
public static partial class ApiBridge
{
    private const string G = "[0-9a-fA-F-]{36}";
    private static readonly (string Method, Regex Path)[] Allowed =
    [
        ("GET", R($"^api/mail/{G}$")),
        ("GET", R($"^api/tickets/{G}$")),
        ("GET", R($"^api/tickets/{G}/messages$")),
        ("GET", R($"^api/meetings/{G}$")),
        ("GET", R($"^api/calendar/events/{G}$")),
        ("GET", R($"^api/news/{G}$")),
        ("GET", R($"^api/news/composer/posts/{G}/media$")),
        ("GET", R($"^api/projects/tasks/{G}$")),
        ("GET", R($"^api/notes/{G}/threads$")),
        ("GET", R($"^api/chat/rooms/{G}/messages\\?page=1&pageSize=\\d+$")),
        ("GET", R("^api/notifications/v1/inbox\\?[a-zA-Z0-9=&]*$")),
        ("GET", R("^api/notifications/v1/unread-count$")),
        ("GET", R("^api/notifier/devices(\\?q=[^&]{0,80})?$")),
        ("POST", R("^api/mail/send$")),
        ("POST", R($"^api/mail/{G}/read$")),
        ("POST", R($"^api/meetings/{G}/rsvp$")),
        ("POST", R($"^api/news/{G}/acknowledge(\\?contentVersion=\\d+)?$")),
        ("POST", R($"^api/projects/tasks/{G}/comments$")),
        ("POST", R($"^api/projects/tasks/{G}/move$")),
        ("POST", R($"^api/notes/{G}/threads/{G}/replies$")),
        ("POST", R($"^api/chat/rooms/{G}/messages$")),
        ("POST", R($"^api/notifications/v1/notifications/{G}/(read|unread|snooze|acknowledge)$")),
        ("POST", R("^api/notifications/v1/inbox/read-all$")),
        ("POST", R("^api/notifications/v1/read-by-target$")),
    ];

    /// Images the pages may show (downloaded with the token, handed back as data: URLs).
    private static readonly Regex[] Images =
    [
        R($"^api/attachments/{G}$"),
        R($"^api/mail/attachments/{G}/(thumbnail|download\\?inline=true)$"),
        R($"^api/chat/attachments/{G}/thumbnail$"),
        R($"^api/photos/{G}(/large)?$"),
        R($"^api/news/composer/media/{G}\\?t=[A-Za-z0-9_\\-]+$"),
    ];

    private static Regex R(string p) => new(p, RegexOptions.CultureInvariant);

    public static bool IsAllowed(string method, string path) =>
        Allowed.Any(a => a.Method == method && a.Path.IsMatch(path));

    public static bool IsImage(string path) => Images.Any(r => r.IsMatch(path));

    public static async Task<object> CallAsync(IPopClient api, JsonNode msg)
    {
        var id = msg["id"]?.GetValue<int>() ?? 0;
        var method = (msg["method"]?.GetValue<string>() ?? "GET").ToUpperInvariant();
        var path = (msg["path"]?.GetValue<string>() ?? "").TrimStart('/');
        if (!IsAllowed(method, path)) return new { type = "apiResult", id, status = 403, json = (JsonNode?)null };
        try
        {
            var body = msg["body"];
            using var res = await api.SendAsync(new HttpMethod(method), path, body is null ? null : (object)body);
            var text = await res.Content.ReadAsStringAsync();
            JsonNode? json = null;
            try { json = text.Length > 0 ? JsonNode.Parse(text) : null; } catch { }
            return new { type = "apiResult", id, status = (int)res.StatusCode, json };
        }
        catch
        {
            return new { type = "apiResult", id, status = 0, json = (JsonNode?)null };
        }
    }

    /// Images are kept light: anything above 4 MB isn't shown inline (the window offers "Show full message").
    public static async Task<object> ImageAsync(IPopClient api, JsonNode msg)
    {
        var id = msg["id"]?.GetValue<int>() ?? 0;
        var path = (msg["path"]?.GetValue<string>() ?? "").TrimStart('/');
        if (!IsImage(path)) return new { type = "imageResult", id, url = (string?)null };
        try
        {
            using var res = await api.SendAsync(HttpMethod.Get, path);
            var type = res.Content.Headers.ContentType?.MediaType ?? "";
            if (!res.IsSuccessStatusCode || !type.StartsWith("image/") || res.Content.Headers.ContentLength > 4_000_000)
                return new { type = "imageResult", id, url = (string?)null };
            var bytes = await res.Content.ReadAsByteArrayAsync();
            if (bytes.Length > 4_000_000) return new { type = "imageResult", id, url = (string?)null };
            return new { type = "imageResult", id, url = $"data:{type};base64,{Convert.ToBase64String(bytes)}" };
        }
        catch { return new { type = "imageResult", id, url = (string?)null }; }
    }
}
