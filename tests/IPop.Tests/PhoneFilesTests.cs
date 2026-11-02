using Bunit;
using IPop.Web.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IPop.Tests;

public class PhoneFilesTests
{
    private static BunitContext Context()
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddScoped<OverlayStack>();
        return ctx;
    }

    private static IRenderedComponent<DynamicComponent> Render(BunitContext ctx, string name, Dictionary<string, object>? parameters = null)
    {
        var type = typeof(PhoneState).Assembly.GetType("IPop.Web.Components.Phone.Files." + name);
        Assert.NotNull(type);
        return ctx.Render<DynamicComponent>(p => p.Add(x => x.Type, type).Add(x => x.Parameters, parameters ?? new()));
    }

    // Exercise the real page render tree/handlers without its NAS-loading lifecycle.
    public class OfflineFiles : IPop.Web.Pages.Files
    {
        protected override void OnInitialized() { }
        protected override Task OnInitializedAsync() => Task.CompletedTask;
        protected override Task OnAfterRenderAsync(bool firstRender) => Task.CompletedTask;
    }

    private static IRenderedComponent<OfflineFiles> Page(BunitContext ctx, bool phone)
    {
        var state = new PhoneState(ctx.JSInterop.JSRuntime); state.OnPhoneChanged(phone);
        var http = new HttpClient(new OfflineApi()) { BaseAddress = new Uri("http://localhost/") };
        var auth = new AuthState(http, ctx.JSInterop.JSRuntime);
        ctx.Services.AddSingleton(state); ctx.Services.AddSingleton(http); ctx.Services.AddSingleton(auth); ctx.Services.AddSingleton(new IPop.Web.Services.MediaStream(auth, http));
        ctx.Services.AddSingleton(new FileState(auth)); ctx.Services.AddSingleton(new ToastService()); ctx.Services.AddSingleton(new ConfirmService());
        return ctx.Render<OfflineFiles>();
    }
    private sealed class OfflineApi : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.Unauthorized));
    }

    [Fact]
    public async Task Real_phone_page_recent_and_Back_use_detail_history_without_NAS_requests()
    {
        using var ctx = Context(); var cut = Page(ctx, true);
        Assert.Empty(cut.FindAll(".fx-window"));
        cut.Find("button[data-place=recent]").Click();
        Assert.Contains("Nothing opened yet", cut.Markup);
        Assert.Equal("Recent", cut.Find(".ph-large-title").TextContent);
        Assert.NotEmpty(ctx.JSInterop.Invocations.Where(x => x.Identifier == "ipop.mobile.pushDetail"));
        await cut.InvokeAsync(() => cut.Instance.BackPhoneFolder());
        Assert.Single(cut.FindAll(".m-files-locations"));
        Assert.DoesNotContain("NIK", cut.Markup); Assert.DoesNotContain("position", cut.Markup, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Deep_link_Back_returns_to_locations_even_without_in_app_folder_stack()
    {
        using var ctx = Context(); var cut = Page(ctx, true);
        Set(cut.Instance, "phoneLocations", false); cut.Render();
        Assert.Empty(cut.FindAll(".m-files-locations"));
        await cut.InvokeAsync(() => cut.Instance.BackPhoneFolder());
        Assert.Single(cut.FindAll(".m-files-locations"));
    }

    [Fact]
    public void Real_phone_share_root_actions_preserve_desktop_capability_gates()
    {
        using var ctx = Context(); var cut = Page(ctx, true);
        Set(cut.Instance, "phoneLocations", false);
        Set(cut.Instance, "shares", new List<string> { "/ICT" });
        cut.Render();
        cut.Find("button[aria-label='Actions for ICT']").Click();
        Assert.DoesNotContain("Move to Recycle Bin", cut.Find(".ph-sheet").TextContent);
        Assert.DoesNotContain("Rename", cut.Find(".ph-sheet").TextContent);
        Assert.DoesNotContain("Download", cut.Find(".ph-sheet").TextContent);
    }
    private static void Set(object instance, string field, object value) => typeof(IPop.Web.Pages.Files)
        .GetField(field, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.SetValue(instance, value);

    [Fact]
    public void Real_phone_preview_renders_actual_blob_not_literal_parameter_names()
    {
        using var ctx = Context(); var cut = Page(ctx, true);
        Set(cut.Instance, "showPreview", true);
        var field = typeof(IPop.Web.Pages.Files).GetField("previewItem", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var item = Activator.CreateInstance(field.FieldType, "guide.pdf", "/ICT/guide.pdf", false, 100L, 0L)!;
        field.SetValue(cut.Instance, item);
        Set(cut.Instance, "previewUrl", "blob:actual"); Set(cut.Instance, "previewMime", "application/pdf");
        cut.Render();
        Assert.Equal("blob:actual", cut.Find(".m-files-preview iframe").GetAttribute("src"));
    }

    [Fact]
    public void Real_desktop_page_has_original_Finder_DOM_without_phone_screens()
    {
        using var ctx = Context(); var cut = Page(ctx, false);
        Assert.Single(cut.FindAll(".fx-window")); Assert.Single(cut.FindAll(".fx-side"));
        Assert.Empty(cut.FindAll(".m-files-page,.ph-screen,.ph-sheet"));
    }

    [Fact]
    public void Locations_have_real_favorites_and_no_unsupported_shared_action()
    {
        using var ctx = Context();
        string? action = null;
        var cut = Render(ctx, "LocationsPhone", new() {
            ["OnFavorite"] = EventCallback.Factory.Create<string>(this, value => action = value),
            ["StarCount"] = 2
        });
        cut.Find("button[data-place=recent]").Click(); Assert.Equal("recent", action);
        cut.Find("button[data-place=starred]").Click(); Assert.Equal("starred", action);
        Assert.Contains("2", cut.Find("button[data-place=starred]").TextContent);
        Assert.DoesNotContain("Shared with me", cut.Markup);
        Assert.Equal(4, cut.FindAll("button[data-site]").Count);
        Assert.Contains("Not connected", cut.Markup);
        Assert.DoesNotContain("192.168", cut.Markup);
    }

    [Fact]
    public void Locations_show_connected_rejected_unreachable_without_exposing_credential_usernames()
    {
        using var ctx = Context(); string? site = null;
        var cut = Render(ctx, "LocationsPhone", new() {
            ["States"] = new Dictionary<string, string> { ["spj"] = "on", ["krw"] = "warn", ["smg"] = "err" },
            ["OnSite"] = EventCallback.Factory.Create<string>(this, value => site = value)
        });
        Assert.Contains("Connected", cut.Find("button[data-site=spj]").TextContent);
        Assert.Contains("Sign in again", cut.Find("button[data-site=krw]").TextContent);
        Assert.Contains("Unreachable", cut.Find("button[data-site=smg]").TextContent);
        cut.Find("button[data-site=jtb]").Click(); Assert.Equal("jtb", site);
        Assert.Contains("Connect", cut.Find("button[data-site=jtb]").TextContent);
    }

    [Fact]
    public void Browse_exposes_sort_options_list_grid_and_selection_events()
    {
        using var ctx = Context(); string? sort = null; bool? grid = null; bool selected = false;
        var cut = Render(ctx, "BrowsePhone", new() {
            ["Title"] = "Documentation", ["Breadcrumb"] = "SJA Sepanjang › ICT › Documentation",
            ["OnSort"] = EventCallback.Factory.Create<string>(this, value => sort = value),
            ["OnGrid"] = EventCallback.Factory.Create<bool>(this, value => grid = value),
            ["OnSelect"] = EventCallback.Factory.Create(this, () => selected = true)
        });
        Assert.Contains("SJA Sepanjang › ICT › Documentation", cut.Find("nav").TextContent);
        cut.Find("button[aria-label=Sort]").Click();
        Assert.Equal(4, cut.FindAll("button[data-sort]").Count);
        cut.Find("button[data-sort=modified]").Click(); Assert.Equal("modified", sort);
        cut.Find("button[aria-label=Grid]").Click(); Assert.True(grid);
        Assert.Equal("true", cut.Find("button[aria-label=List]").GetAttribute("aria-pressed"));
        cut.Find("button[data-select]").Click(); Assert.True(selected);
    }

    [Fact]
    public async Task Action_dialog_uses_phone_sheet_and_Back_closes_it()
    {
        using var ctx = Context(); bool open = true;
        var cut = Render(ctx, "FilesDialog", new() {
            ["Phone"] = true, ["IsOpen"] = true, ["Title"] = "Rename",
            ["IsOpenChanged"] = EventCallback.Factory.Create<bool>(this, value => open = value)
        });
        Assert.Single(cut.FindAll(".ph-sheet"));
        Assert.Empty(cut.FindAll(".modal-card"));
        await cut.InvokeAsync(() => ctx.Services.GetRequiredService<OverlayStack>().CloseTopAsync());
        Assert.False(open);
    }

    [Fact]
    public void Preview_is_fullscreen_and_dispatches_share_close_without_desktop_controls()
    {
        using var ctx = Context(); bool shared = false, closed = false;
        var cut = Render(ctx, "PreviewPhone", new() {
            ["Name"] = "guide.pdf", ["Url"] = "blob:test", ["Mime"] = "application/pdf",
            ["OnShare"] = EventCallback.Factory.Create(this, () => shared = true),
            ["OnClose"] = EventCallback.Factory.Create(this, () => closed = true)
        });
        Assert.Equal("true", cut.Find("[role=dialog]").GetAttribute("aria-modal"));
        Assert.Equal("blob:test", cut.Find("iframe").GetAttribute("src"));
        cut.Find("button[aria-label=Share]").Click(); Assert.True(shared);
        cut.Find("button[aria-label=Close]").Click(); Assert.True(closed);
        Assert.DoesNotContain("File Explorer", cut.Markup);
    }

    [Fact]
    public void Upload_sheet_has_capture_photos_files_and_dispatches_only_batch_cancel()
    {
        using var ctx = Context(); string? picked = null; bool folder = false, cancelled = false;
        var cut = Render(ctx, "UploadSheet", new() {
            ["Open"] = true, ["Folder"] = "Documentation", ["Uploading"] = true,
            ["OnPick"] = EventCallback.Factory.Create<string>(this, value => picked = value),
            ["OnFolder"] = EventCallback.Factory.Create(this, () => folder = true),
            ["OnCancel"] = EventCallback.Factory.Create(this, () => cancelled = true)
        });
        Assert.Equal("environment", cut.Find("input[data-picker=camera]").GetAttribute("capture"));
        Assert.Equal("image/*", cut.Find("input[data-picker=photos]").GetAttribute("accept"));
        Assert.NotNull(cut.Find("input[data-picker=files][multiple]"));
        cut.Find("button[data-pick=camera]").Click(); Assert.Equal("camera", picked);
        cut.Find("button[data-new-folder]").Click(); Assert.True(folder);
        cut.Find("button[aria-label='Cancel uploads']").Click(); Assert.True(cancelled);
        Assert.DoesNotContain("keep going", cut.Markup);
        Assert.DoesNotContain("s left", cut.Markup);
    }
}
