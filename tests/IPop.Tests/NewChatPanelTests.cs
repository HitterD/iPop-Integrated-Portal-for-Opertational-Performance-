using System.Net;
using System.Net.Http.Json;
using Bunit;
using IPop.Web.Components.Chat;
using IPop.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IPop.Tests;

/// WhatsApp-style "New chat" panel that replaces the old New message / New group modal.
[Collection("AvatarPhotos")]
public class NewChatPanelTests
{
    private static readonly List<NewChatPerson> Directory = Enumerable.Range(1, 12)
        .Select(i => new NewChatPerson(Guid.NewGuid(), $"N{i:000}", $"Person{i:00} Surname", null, "Operator", "Production", "off"))
        .ToList();

    /// Answers /api/chat/users/directory with the fixed staff list (filtered by ?q=).
    private sealed class DirectoryHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var q = System.Web.HttpUtility.ParseQueryString(request.RequestUri!.Query)["q"] ?? "";
            var rows = Directory.Where(p => q.Length == 0 || p.Name.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(rows) });
        }
    }

    private static BunitContext NewContext()
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        var http = new HttpClient(new DirectoryHandler()) { BaseAddress = new Uri("http://localhost/") };
        ctx.Services.AddSingleton(new AuthState(http, ctx.JSInterop.JSRuntime));
        ctx.Services.AddSingleton(new ToastService());
        ctx.Services.AddScoped<PhotoViewerState>();
        return ctx;
    }

    private static IRenderedComponent<NewChatPanel> RenderPanel(BunitContext ctx, bool group = false,
        Action<NewChatPerson>? onDm = null, Func<NewGroupDraft, Task<bool>>? onCreate = null, Action? onClose = null)
    {
        var cut = ctx.Render<NewChatPanel>(p => p
            .Add(c => c.GroupMode, group)
            .Add(c => c.OnStartDm, (NewChatPerson x) => onDm?.Invoke(x))
            .Add(c => c.OnClose, () => onClose?.Invoke())
            .Add(c => c.OnCreateGroup, onCreate));
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".nc-list .nc-row:not(.nc-new-group)")), TimeSpan.FromSeconds(3));
        return cut;
    }

    private static void ClickPerson(IRenderedComponent<NewChatPanel> cut, string name) =>
        cut.FindAll(".nc-list .nc-row").First(r => r.TextContent.Contains(name)).Click();

    [Fact]
    public void Chat_step_shows_New_group_row_and_a_click_starts_the_DM_immediately()
    {
        using var ctx = NewContext();
        NewChatPerson? dm = null;
        var cut = RenderPanel(ctx, onDm: p => dm = p);

        Assert.Equal("New chat", cut.Find(".nc-title").TextContent);
        Assert.NotNull(cut.Find(".nc-new-group"));
        Assert.Empty(cut.FindAll(".nc-check"));   // no radio/checkbox in DM mode
        Assert.Empty(cut.FindAll(".nc-next"));    // no confirm button either

        ClickPerson(cut, "Person03");

        Assert.Equal("N003", dm?.Nik);
    }

    [Fact]
    public void Search_input_stays_present_and_full_width_after_selecting_six_members()
    {
        using var ctx = NewContext();
        var cut = RenderPanel(ctx);
        cut.Find(".nc-new-group").Click();
        Assert.Equal("Add group members", cut.Find(".nc-title").TextContent);

        for (var i = 1; i <= 6; i++) ClickPerson(cut, $"Person{i:00}");

        Assert.Equal(6, cut.FindAll(".nc-chip").Count);
        // Chips live in their own row; the search is a sibling after it, not squeezed inside.
        var input = cut.Find(".nc-search .nc-search-input");
        Assert.Null(input.Closest(".nc-chips"));
        Assert.Empty(cut.FindAll(".nc-chips input"));
        Assert.Equal("nc-chips", cut.Find(".nc-search").PreviousElementSibling?.ClassName);
        // And it still works: typing filters the list.
        input.Input("Person1");
        cut.WaitForAssertion(() => Assert.All(
            cut.FindAll(".nc-list .nc-row:not(.on)"),
            r => Assert.Contains("Person1", r.TextContent)), TimeSpan.FromSeconds(3));
        Assert.Equal("6 selected", cut.Find(".nc-sub").TextContent);
    }

    [Fact]
    public void Backspace_on_empty_search_removes_the_last_chip()
    {
        using var ctx = NewContext();
        var cut = RenderPanel(ctx, group: true);
        ClickPerson(cut, "Person01");
        ClickPerson(cut, "Person02");

        cut.Find(".nc-search-input").KeyDown("Backspace");

        var chip = Assert.Single(cut.FindAll(".nc-chip"));
        Assert.Contains("Person01", chip.TextContent);
    }

    [Fact]
    public async Task Next_needs_two_members_then_details_step_creates_the_group()
    {
        using var ctx = NewContext();
        NewGroupDraft? created = null;
        var cut = RenderPanel(ctx, group: true, onCreate: d => { created = d; return Task.FromResult(true); });

        Assert.True(cut.Find(".nc-next").HasAttribute("disabled"));
        ClickPerson(cut, "Person01");
        Assert.True(cut.Find(".nc-next").HasAttribute("disabled"));
        ClickPerson(cut, "Person02");
        Assert.False(cut.Find(".nc-next").HasAttribute("disabled"));

        cut.Find(".nc-next").Click();
        Assert.Equal("New group", cut.Find(".nc-title").TextContent);
        Assert.True(cut.Find(".nc-primary").HasAttribute("disabled"));   // name still empty

        cut.Find(".nc-name-input").Input("Line 3 crew");
        await cut.InvokeAsync(() => cut.Find(".nc-primary").Click());

        Assert.NotNull(created);
        Assert.Equal("Line 3 crew", created!.Name);
        Assert.Equal(["N001", "N002"], created.Members.Select(m => m.Nik));
        Assert.Null(created.Photo);
    }

    [Fact]
    public void Back_walks_one_step_at_a_time_then_closes()
    {
        using var ctx = NewContext();
        var closed = 0;
        var cut = RenderPanel(ctx, onClose: () => closed++);
        cut.Find(".nc-new-group").Click();
        ClickPerson(cut, "Person01");
        ClickPerson(cut, "Person02");
        cut.Find(".nc-next").Click();

        cut.Find(".nc-back").Click();
        Assert.Equal("Add group members", cut.Find(".nc-title").TextContent);
        Assert.Equal(2, cut.FindAll(".nc-chip").Count);   // selection survives going back

        cut.Find(".nc-back").Click();
        Assert.Equal("New chat", cut.Find(".nc-title").TextContent);
        Assert.Equal(0, closed);

        cut.Find(".nc-back").Click();
        Assert.Equal(1, closed);
    }

    [Fact]
    public void Group_mode_preselects_people_passed_in()
    {
        using var ctx = NewContext();
        var cut = ctx.Render<NewChatPanel>(p => p
            .Add(c => c.GroupMode, true)
            .Add(c => c.Preselected, Directory.Take(3).ToList()));

        Assert.Equal(3, cut.FindAll(".nc-chip").Count);
        Assert.False(cut.Find(".nc-next").HasAttribute("disabled"));
    }
}
