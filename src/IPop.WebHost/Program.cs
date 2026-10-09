var builder = WebApplication.CreateBuilder(args);
// Serves IPop.Web's wwwroot + _framework straight from the build output in every environment
// (the default only wires this up in Development).
builder.WebHost.UseStaticWebAssets();
builder.WebHost.ConfigureKestrel(k => k.AddServerHeader = false);

var app = builder.Build();

// Allows: the CDNs index.html loads (Tailwind, jsDelivr), Blazor's wasm/eval, inline boot
// scripts, LiveKit/SignalR sockets, ONLYOFFICE in a same-host iframe. Blocks framing by
// other sites, plugins and <base> hijacking. Override with "Security:ContentSecurityPolicy".
var csp = app.Configuration["Security:ContentSecurityPolicy"] ?? string.Join("; ",
    "default-src 'self'",
    "script-src 'self' 'unsafe-inline' 'unsafe-eval' 'wasm-unsafe-eval' blob: https://cdn.tailwindcss.com https://cdn.jsdelivr.net",
    "style-src 'self' 'unsafe-inline' https://cdn.jsdelivr.net",
    "font-src 'self' data: https://cdn.jsdelivr.net",
    "img-src 'self' data: blob: https: http:",
    "media-src 'self' data: blob: https: http:",
    "connect-src 'self' blob: https: wss: ws: http:",
    "worker-src 'self' blob:",
    "frame-src 'self' blob: data: https:",
    "frame-ancestors 'self'",
    "object-src 'none'",
    "base-uri 'self'",
    "form-action 'self'");

var isProduction = app.Environment.IsProduction();
app.Use(async (ctx, next) =>
{
    // Debug symbols are a build artefact, never content (pentest H-01).
    // Per-environment client config (appsettings.Development.json�) stays private in production.
    var path = ctx.Request.Path.Value ?? "";
    if (path.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase) ||
        (isProduction && path.StartsWith("/appsettings.", StringComparison.OrdinalIgnoreCase) &&
         !path.Equals("/appsettings.json", StringComparison.OrdinalIgnoreCase)))
    {
        ctx.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }
    ctx.Response.OnStarting(() =>
    {
        var h = ctx.Response.Headers;
        // The client picks appsettings.{env}.json from this header; production sends none
        // (pentest H-03) and so loads appsettings.json only.
        if (!isProduction) h["Blazor-Environment"] = app.Environment.EnvironmentName;
        h.ContentSecurityPolicy = csp;
        h.XFrameOptions = "SAMEORIGIN";
        h.XContentTypeOptions = "nosniff";
        h["Referrer-Policy"] = "strict-origin-when-cross-origin";
        h["Permissions-Policy"] = "camera=(self), microphone=(self), geolocation=(), payment=()";
        if (ctx.Request.IsHttps) h.StrictTransportSecurity = "max-age=31536000";
        return Task.CompletedTask;
    });
    await next();
});

// Classic static files: MIME types come from the extension (the static-assets manifest leaves
// some framework files without one, which nosniff then refuses to execute).
// Published assets keep index.html as a fallback rather than a direct endpoint. The version
// checker fetches /index.html explicitly; route it through the same root document.
app.Use(async (ctx, next) =>
{
    if (ctx.Request.Path.Equals("/index.html", StringComparison.OrdinalIgnoreCase))
        ctx.Request.Path = "/";
    await next();
});
app.UseRouting();
app.MapStaticAssets();
app.MapFallbackToFile("index.html");

app.Run();
