using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using IPop.Web;
using Radzen;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Services.AddScoped(sp =>
{
    var configured = builder.Configuration["ApiBase"];
    var hostBase = builder.HostEnvironment.BaseAddress;
    var baseUri = !string.IsNullOrWhiteSpace(configured) ? new Uri(configured) : new Uri(hostBase);
    if (hostBase.StartsWith("https://", StringComparison.OrdinalIgnoreCase) &&
        baseUri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase))
    {
        // An https page cannot call an http API (mixed content). run.ps1 serves the API on its
        // own https port and sets ApiBaseHttps; behind a reverse proxy the API shares the host.
        var secure = builder.Configuration["ApiBaseHttps"];
        baseUri = !string.IsNullOrWhiteSpace(secure) ? new Uri(secure) : new Uri(hostBase);
    }
    // PracticeGate answers /api calls with sample data while practice mode is on; above it,
    // ResilientHandler caps and retries reads that stall.
    var handler = new IPop.Web.Services.ResilientHandler(sp.GetRequiredService<IPop.Web.Services.PracticeGate>());
    return new HttpClient(handler) { BaseAddress = baseUri };
});
builder.Services.AddScoped<IPop.Web.Services.PracticeGate>();
builder.Services.AddScoped<IPop.Web.Services.PracticeSandbox>();
builder.Services.AddScoped<IPop.Web.Services.TutorialState>();
builder.Services.AddScoped<IPop.Web.Services.AuthState>();
builder.Services.AddScoped<IPop.Web.Services.PageCache>();
builder.Services.AddScoped(sp => new IPop.Web.Services.ChatState(sp.GetRequiredService<IPop.Web.Services.AuthState>(),
    sp.GetRequiredService<HttpClient>(), sp.GetService<IPop.Web.Services.DraftStore>(), sp.GetRequiredService<Microsoft.JSInterop.IJSRuntime>()));
builder.Services.AddScoped<IPop.Web.Services.ChatAttachments>();
builder.Services.AddScoped<IPop.Web.Services.ToastService>();
builder.Services.AddScoped<IPop.Web.Services.CallState>();
builder.Services.AddScoped<IPop.Web.Services.MeetingRoomState>();
builder.Services.AddScoped<IPop.Web.Services.PushState>();
builder.Services.AddScoped(sp => new IPop.Web.Services.DraftStore(sp.GetRequiredService<Microsoft.JSInterop.IJSRuntime>(), sp.GetRequiredService<IPop.Web.Services.AuthState>()));
builder.Services.AddScoped<IPop.Web.Services.OverlayStack>();
builder.Services.AddScoped<IPop.Web.Services.ConfirmService>();
builder.Services.AddScoped<IPop.Web.Services.CommandPaletteState>();
builder.Services.AddScoped<IPop.Web.Services.NotificationSettingsState>();
builder.Services.AddScoped<IPop.Web.Services.BadgeState>();
builder.Services.AddScoped<IPop.Web.Services.TimerState>();
builder.Services.AddScoped<IPop.Web.Services.MailService>();
builder.Services.AddScoped<IPop.Web.Services.TicketApi>();
builder.Services.AddScoped<IPop.Web.Services.MailComposerHost>();
builder.Services.AddScoped<IPop.Web.Services.LinkPreviewService>();
builder.Services.AddScoped<IPop.Web.Services.NotificationState>(sp =>
    new IPop.Web.Services.NotificationState(
        sp.GetRequiredService<IPop.Web.Services.AuthState>(),
        sp.GetRequiredService<HttpClient>(),
        sp.GetRequiredService<IPop.Web.Services.ToastService>(),
        sp.GetRequiredService<Microsoft.JSInterop.IJSRuntime>()));
builder.Services.AddScoped<IPop.Web.Services.FileState>();
builder.Services.AddScoped<IPop.Web.Services.FavoriteContacts>();
builder.Services.AddScoped<IPop.Web.Services.PeopleLinks>();
builder.Services.AddScoped<IPop.Web.Services.CalendarLinks>();
builder.Services.AddScoped<IPop.Web.Services.InviteApi>();
builder.Services.AddScoped<IPop.Web.Services.PhotoViewerState>();
builder.Services.AddScoped<IPop.Web.Services.SessionLockState>();
// Emoji picker: the Unicode list is a static file under wwwroot (fetched once from the app's own
// origin, not ApiBase); recents/tone are per account in localStorage.
builder.Services.AddScoped(_ => new IPop.Web.Services.EmojiCatalog(
    IPop.Web.Services.EmojiCatalog.HttpLoader(builder.HostEnvironment.BaseAddress)));
builder.Services.AddScoped<IPop.Web.Services.EmojiPrefs>();
builder.Services.AddRadzenComponents();

await builder.Build().RunAsync();
