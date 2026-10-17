using System.Net;
using System.Net.Http.Json;
using Bunit;
using IPop.Web.Components;
using IPop.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IPop.Tests;
public class PhoneSearchTests
{
    private sealed class Api : HttpMessageHandler
    {
        public readonly Guid HistoryId = Guid.NewGuid();
        public List<string> Deleted = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            if (r.Method == HttpMethod.Delete) Deleted.Add(r.RequestUri!.AbsolutePath);
            object payload = r.RequestUri!.AbsolutePath switch {
                "/api/search/history" => new[] { new CommandPalette.SearchHistoryItemDto(HistoryId, "Maintenance", DateTime.UtcNow) },
                "/api/search/modules" => new[] { new CommandPalette.SearchModuleInfoDto("notes", "Notes", "notebook", 1) },
                "/api/search/modules/notes" => new CommandPalette.SearchPageDto("notes", "ready", [new("n1", "notes", "Maintenance plan", null, "UPS", "/notes?id=n1", "notebook", null, "title")], 1, 1, 5, false),
                _ => Array.Empty<object>() };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(payload, payload.GetType()) });
        }
    }
    [Fact]
    public void Phone_search_uses_existing_history_and_progressive_provider_and_Back()
    {
        using var ctx = new BunitContext(); ctx.JSInterop.Mode = JSRuntimeMode.Loose; var api = new Api();
        ctx.Services.AddSingleton(new AuthState(new HttpClient(api) { BaseAddress = new("http://localhost/") }, ctx.JSInterop.JSRuntime));
        var phone = new PhoneState(ctx.JSInterop.JSRuntime); phone.OnPhoneChanged(true); ctx.Services.AddSingleton(phone);
        var palette = new CommandPaletteState(); ctx.Services.AddSingleton(palette); ctx.Services.AddSingleton(new PeopleLinks()); ctx.Services.AddSingleton(new OverlayStack());
        var cut = ctx.Render<CommandPalette>(); cut.InvokeAsync(palette.Open);
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".m-search")));
        cut.WaitForAssertion(() => Assert.True(cut.Markup.Contains("Maintenance"), cut.Markup));
        cut.Find("button[aria-label='Remove Maintenance']").Click();
        cut.WaitForAssertion(() => Assert.Contains("/api/search/history/" + api.HistoryId, api.Deleted));
        cut.Find("input[aria-label='Search']").Input("UPS");
        cut.WaitForAssertion(() => Assert.Contains("Maintenance plan", cut.Markup));
        cut.InvokeAsync(() => ctx.Services.GetRequiredService<OverlayStack>().CloseTopAsync());
        cut.WaitForAssertion(() => Assert.False(palette.IsOpen));
    }
}
