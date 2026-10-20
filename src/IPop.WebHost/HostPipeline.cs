namespace IPop.WebHost;

/// The WebHost request pipeline, shared by Program and the tests.
public static class HostPipeline
{
    public static void Configure(WebApplication app, bool staticAssets = true)
    {
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
            // Per-environment client config (appsettings.Development.json…) stays private in production.
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
        if (staticAssets) app.MapStaticAssets();

        // Android app (TWA): Digital Asset Links + the internal APK download. Both are mapped before
        // the SPA fallback so they are never answered with index.html.
        var assetLinks = AssetLinks.Build(app.Configuration);
        app.MapGet("/.well-known/assetlinks.json", () => Results.Text(assetLinks, "application/json"));
        app.MapMethods("/download/ipop.apk", ["GET", "HEAD"], (HttpContext ctx) =>
        {
            var apk = app.Configuration["AndroidApp:ApkPath"] ?? AssetLinks.DefaultApkPath;
            if (string.IsNullOrWhiteSpace(apk) || !File.Exists(apk)) return Results.NotFound();
            ctx.Response.Headers.CacheControl = "no-cache";
            return Results.File(Path.GetFullPath(apk), "application/vnd.android.package-archive", "ipop.apk", enableRangeProcessing: true);
        });

        // Company root CA (SJA-ROOT-CA, public certificate only). Domain PCs trust it through GPO, but
        // phones do not: without it Android refuses the https site's service worker, so the app gets
        // no push notifications at all. /app links here with install steps. Served over http too.
        app.MapMethods("/download/sja-root-ca.crt", ["GET", "HEAD"], (HttpContext ctx) =>
        {
            var crt = Path.Combine(AppContext.BaseDirectory, "certs", "sja-root-ca.crt");
            if (!File.Exists(crt)) return Results.NotFound();
            ctx.Response.Headers.CacheControl = "no-cache";
            return Results.File(crt, "application/x-x509-ca-cert", "sja-root-ca.crt");
        });

        // iPop Notifier (Windows): installer + Velopack update feed (releases.win.json, *.nupkg) from one folder.
        // Only plain file names from that folder; nothing outside it, no listing.
        app.MapMethods("/download/notifier/{file}", ["GET", "HEAD"], (string file, HttpContext ctx) =>
        {
            var dir = app.Configuration["Notifier:ReleasesPath"] ?? DefaultNotifierReleases;
            if (string.IsNullOrWhiteSpace(dir) || !NotifierFileAllowed(file)) return Results.NotFound();
            var full = Path.GetFullPath(Path.Combine(dir, file));
            if (!full.StartsWith(Path.GetFullPath(dir) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !File.Exists(full))
                return Results.NotFound();
            ctx.Response.Headers.CacheControl = "no-cache";
            var type = file.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? "application/json"
                : file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? "application/vnd.microsoft.portable-executable"
                : "application/octet-stream";
            return Results.File(full, type, file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? file : null, enableRangeProcessing: true);
        });

        app.MapFallbackToFile("index.html");
    }

    public const string DefaultNotifierReleases = "C:/ipop-ops/notifier/releases";

    /// Velopack feed files and the setup: "releases.win.json", "RELEASES", "iPopNotifier-1.0.1-full.nupkg", "iPopNotifier-win-Setup.exe".
    public static bool NotifierFileAllowed(string file) =>
        file.Length is > 0 and <= 120
        && file.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_')
        && !file.Contains("..")
        && (file is "releases.win.json" or "RELEASES" or "assets.win.json"
            || (file.StartsWith("iPopNotifier", StringComparison.Ordinal) && (file.EndsWith(".nupkg", StringComparison.Ordinal) || file.EndsWith("-Setup.exe", StringComparison.Ordinal))));
}
