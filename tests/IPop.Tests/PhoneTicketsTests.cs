using Bunit;
using IPop.Application.Tickets;
using IPop.Web.Components.Phone.Tickets;
using IPop.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IPop.Tests;
public class PhoneTicketsTests
{
    private static TicketListItemDto Row(string status, string title) => new(Guid.NewGuid(), "ICT-42", null, "IDESK", status, "HIGH", "it-support", "IT Support", "NETWORK", title, DateTime.UtcNow, DateTime.UtcNow, 1);
    [Fact]
    public void List_groups_needs_you_open_and_closed_only_under_all_and_dispatches_real_selection()
    {
        using var ctx = new BunitContext(); ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        var rows = new[] { Row("RESOLVED", "Confirm wifi"), Row("TODO", "Repair laptop"), Row("CLOSED", "Old ticket") };
        Guid? opened = null;
        var cut = ctx.Render<TicketsPhone>(p => p.Add(x => x.Items, rows).Add(x => x.OnOpen, id => opened = id));
        Assert.Contains("Needs you", cut.Markup); Assert.Contains("Open", cut.Markup);
        Assert.DoesNotContain("Old ticket", cut.Markup);
        cut.Find($"button[data-ticket='{rows[0].Id}']").Click(); Assert.Equal(rows[0].Id, opened);
        Assert.Contains("New reply", cut.Markup);
        cut.Render(p => p.Add(x => x.Items, rows).Add(x => x.ShowAll, true));
        Assert.Contains("Old ticket", cut.Markup); Assert.DoesNotContain("NIK", cut.Markup);
    }
    [Fact]
    public void Error_is_actionable_and_does_not_leave_a_loading_skeleton()
    {
        using var ctx = new BunitContext(); ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        var retried = false;
        var cut = ctx.Render<TicketsPhone>(p => p.Add(x => x.Error, "Could not load tickets").Add(x => x.OnSync, () => retried = true));
        Assert.NotNull(cut.Find("[role=alert]"));
        cut.Find("button[aria-label='Retry tickets']").Click(); Assert.True(retried);
    }
    [Fact]
    public void Phone_form_uses_live_queues_category_before_templates_and_template_sets_real_fields()
    {
        using var ctx = PageContext(true);
        var cut = ctx.Render<IPop.Web.Pages.TicketNew>();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[role=radio]")));
        Assert.NotNull(cut.Find(".ph-sheet-full"));
        var category = cut.Find("[aria-label=Category]");
        var templates = cut.Find("[aria-label='Quick templates']");
        Assert.True(cut.Markup.IndexOf("aria-label=\"Category\"", StringComparison.Ordinal) < cut.Markup.IndexOf("aria-label=\"Quick templates\"", StringComparison.Ordinal), cut.Markup);
        cut.FindAll(".tk-tpl").Single(b => b.TextContent.Trim() == "No Network").Click();
        Assert.Equal("Koneksi internet bermasalah", cut.Find("#tk-title").GetAttribute("value"));
        Assert.Contains("Tidak ada koneksi", ((AngleSharp.Html.Dom.IHtmlTextAreaElement)cut.Find("#tk-desc")).Value);
        Assert.Equal("true", cut.Find("[aria-label=Priority] button:nth-child(3)").GetAttribute("aria-checked"));
        Assert.DoesNotContain("SECRET-NIK", cut.Find(".ph-sheet-body").TextContent);
    }
    [Fact]
    public void Desktop_form_keeps_original_root_without_phone_sheet()
    {
        using var ctx = PageContext(false);
        var cut = ctx.Render<IPop.Web.Pages.TicketNew>();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".tk-mod")));
        Assert.NotNull(cut.Find(".tk-formwrap > .tk-form-h")); Assert.Empty(cut.FindAll(".ph-sheet"));
    }
    private static BunitContext PageContext(bool phone)
    {
        var ctx = new BunitContext(); ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        var http = new HttpClient(new ApiHandler()) { BaseAddress = new Uri("http://localhost/") };
        var auth = new AuthState(http, ctx.JSInterop.JSRuntime);
        typeof(AuthState).GetProperty(nameof(AuthState.Me))!.SetValue(auth, new DirectoryEmployeeDto("SECRET-NIK", "Jane Smith", null, null, null, null, null, null, "jane@example.test", null, null, "on"));
        ctx.Services.AddSingleton(auth); ctx.Services.AddSingleton(http);
        ctx.Services.AddSingleton<TicketApi>(); ctx.Services.AddSingleton<PageCache>();
        ctx.Services.AddSingleton<BadgeState>(); ctx.Services.AddSingleton<CalendarLinks>();
        ctx.Services.AddSingleton<ToastService>(); ctx.Services.AddSingleton<ConfirmService>(); ctx.Services.AddSingleton<OverlayStack>();
        var state = new PhoneState(ctx.JSInterop.JSRuntime); state.OnPhoneChanged(phone); ctx.Services.AddSingleton(state);
        return ctx;
    }
    private sealed class ApiHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            object body = req.RequestUri!.AbsolutePath.EndsWith("modules") ? new[] { new TicketModuleDto("it-support", "IT Support", null, ["NETWORK", "HARDWARE_INSTALLATION"], "NETWORK", ["LOW", "MEDIUM", "HIGH", "CRITICAL"], true) } : new TicketListDto([], null, false, false);
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = System.Net.Http.Json.JsonContent.Create(body) });
        }
    }
    [Fact]
    public void More_sheet_keeps_follow_up_and_pending_actions_on_existing_overlay_stack()
    {
        using var ctx = new BunitContext(); ctx.JSInterop.Mode = JSRuntimeMode.Loose; ctx.Services.AddScoped<OverlayStack>();
        var discarded = false;
        var cut = ctx.Render<TicketMoreSheet>(p => p.Add(x => x.Open, true).Add(x => x.Pending, true)
            .Add(x => x.Id, Guid.NewGuid()).Add(x => x.FollowUpUrl, "/calendar?new=1").Add(x => x.OnDiscard, () => discarded = true));
        Assert.Equal("/calendar?new=1", cut.Find("a[data-action=follow-up]").GetAttribute("href"));
        Assert.Single(cut.FindComponents<IPop.Web.Components.OverlayHook>());
        cut.Find("button[data-action=discard]").Click(); Assert.True(discarded);
    }
}
