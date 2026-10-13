using System.Net;
using System.Text;
using System.Text.Json;
using Bunit;
using IPop.Web.Components.Meetings;
using IPop.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IPop.Tests;

/// Meeting scheduling v3 §4 (mockup screen 5): the invite picker component (the API side is InvitePickerTests).
[Collection("AvatarPhotos")]
public class InvitePickerUiTests
{
    private static readonly TimeZoneInfo Wib = TimeZoneInfo.CreateCustomTimeZone("WIB", TimeSpan.FromHours(7), "WIB", "WIB");
    private static DateTime At(int h, int m = 0) => new DateTime(2026, 10, 13, h, m, 0, DateTimeKind.Utc).AddHours(-7);
    private static readonly Guid IctGroup = Guid.Parse("aaaaaaaa-0000-4000-8000-000000000001");
    private static readonly Guid Finance = Guid.Parse("aaaaaaaa-0000-4000-8000-000000000002");

    private static InvitePersonDto P(string key, string name, string dept = "ICT") => new(key, name, dept);
    private static readonly InvitePersonDto Alphin = P("NIK900001", "Alphin Santoso"), Beny = P("NIK900002", "Beny Saputra"),
        Jason = P("NIK900003", "Jason Kristanto"), Yudi = P("NIK900004", "Yudi Arta"), Rina = P("NIK900005", "Rina Wulandari", "HR & GA"),
        Fina = P("NIK900006", "Fina Oktaviani", "Finance");

    private static Api Fixture() => new()
    {
        Suggestions = new([Alphin, Beny, Jason, Rina], [Yudi, Alphin], [new("chat", IctGroup, "ICT IPOP", 4, "#2e9e5b")],
            [new("department", Finance, "Finance", 24, null)]),
        Busy = { [Jason.Key] = ("busy", At(10), At(11)), [Rina.Key] = ("unknown", null, null) },
        Expand = { [IctGroup] = [Alphin, Beny, Yudi, P("NIK900007", "Dimas Prakoso")] },
    };

    private static IRenderedComponent<InvitePicker> Render(BunitContext ctx, List<InvitePersonDto>? selected = null, int? capacity = null,
        Action<List<InvitePersonDto>>? changed = null, Action? findTime = null) =>
        ctx.Render<InvitePicker>(p => p.Add(c => c.Selected, selected ?? []).Add(c => c.From, At(10)).Add(c => c.To, At(11))
            .Add(c => c.RoomCapacity, capacity).Add(c => c.RoomName, capacity is null ? null : "Kalosi").Add(c => c.Zone, Wib)
            .Add(c => c.SearchDelay, TimeSpan.Zero)
            .Add(c => c.SelectedChanged, v => changed?.Invoke(v))
            .Add(c => c.OnFindTime, () => findTime?.Invoke()));

    [Fact]
    public void Focus_before_typing_shows_the_four_suggestion_sections()
    {
        var api = Fixture();
        using var ctx = Context(api);
        var cut = Render(ctx);
        Assert.Empty(cut.FindAll(".ip-drop"));
        cut.Find(".ip-in").Focus();

        cut.WaitForAssertion(() => Assert.Equal(["Suggested · people you meet often", "Your department · ICT", "Chat groups", "Departments"],
            cut.FindAll(".ip-sec").Select(s => s.TextContent.Trim()).ToArray()));
        // Alphin is in Frequent and Department: listed once.
        Assert.Single(cut.FindAll(".ip-row b").Where(b => b.TextContent == "Alphin Santoso"));
        Assert.Equal("listbox", cut.Find(".ip-drop-in").GetAttribute("role"));
        var opts = cut.FindAll("[role=option]");
        Assert.Equal("true", opts[0].GetAttribute("aria-selected"));
        Assert.All(opts.Skip(1), o => Assert.Equal("false", o.GetAttribute("aria-selected")));
        Assert.Equal(1, api.SuggestionGets);
    }

    [Fact]
    public void Rows_show_free_busy_with_times_and_unknown()
    {
        var api = Fixture();
        using var ctx = Context(api);
        var cut = Render(ctx);
        cut.Find(".ip-in").Focus();
        cut.WaitForAssertion(() => Assert.Equal("Busy 10:00–11:00", Row(cut, "Jason Kristanto").QuerySelector(".fbs")!.TextContent));
        Assert.Contains("busy", Row(cut, "Jason Kristanto").QuerySelector(".fbs")!.ClassList);
        Assert.Equal("Free", Row(cut, "Alphin Santoso").QuerySelector(".fbs")!.TextContent);
        Assert.Equal("Unknown", Row(cut, "Rina Wulandari").QuerySelector(".fbs")!.TextContent);
        Assert.Contains("Free/busy for", cut.Find(".ip-drop-f").TextContent);
        // One batched request for the rows.
        Assert.Single(api.BusyBodies);
        var keys = JsonDocument.Parse(api.BusyBodies[0]).RootElement.GetProperty("keys").EnumerateArray().Select(k => k.GetString()).ToList();
        Assert.Equal(5, keys.Count);
    }

    [Fact]
    public void Typing_searches_and_highlights_the_match()
    {
        var api = Fixture();
        api.SearchResult = new([Fina, Rina with { Name = "Arifin Hidayat", Key = "NIK900008" }], [new("department", Finance, "Finance", 24, null)]);
        using var ctx = Context(api);
        var cut = Render(ctx);
        cut.Find(".ip-in").Input("fin");
        cut.WaitForAssertion(() => Assert.Equal("fin", api.Queries.Single()));
        cut.WaitForAssertion(() => Assert.Equal("People · name or department matches “fin”", cut.FindAll(".ip-sec")[0].TextContent.Trim()));
        Assert.Equal("Fin", Row(cut, "Fina Oktaviani").QuerySelector("mark")!.TextContent);
        Assert.Equal("Departments", cut.FindAll(".ip-sec")[1].TextContent.Trim());

        cut.Find(".ip-in").Input("f");                 // one character: back to suggestions, no search
        cut.WaitForAssertion(() => Assert.Equal("Suggested · people you meet often", cut.FindAll(".ip-sec")[0].TextContent.Trim()));
        Assert.Single(api.Queries);
    }

    [Fact]
    public void Group_asks_first_with_the_count_then_adds_without_duplicates()
    {
        var api = Fixture();
        using var ctx = Context(api);
        List<InvitePersonDto>? sel = null;
        var cut = Render(ctx, [Alphin], changed: v => sel = v);
        cut.Find(".ip-in").Focus();
        cut.WaitForAssertion(() => Assert.NotNull(Row(cut, "ICT IPOP")));
        Row(cut, "ICT IPOP").Click();

        cut.WaitForAssertion(() => Assert.Contains("Add 4 people from ICT IPOP?", cut.Find(".ip-toast").TextContent));
        Assert.Contains("1 is already invited", cut.Find(".ip-toast").TextContent);
        Assert.Equal("Add 3", cut.Find(".ip-toast-yes").TextContent.Trim());
        Assert.Null(sel);                                // nothing added before confirming

        cut.Find(".ip-toast-yes").Click();
        Assert.Equal(["Alphin Santoso", "Beny Saputra", "Yudi Arta", "Dimas Prakoso"], sel!.Select(p => p.Name).ToArray());
        Assert.Empty(cut.FindAll(".ip-toast"));
        Assert.Equal("Added", Row(cut, "ICT IPOP").QuerySelector(".addall")!.TextContent);
        Assert.Equal(4, cut.FindAll(".ip-chip").Count);
    }

    [Fact]
    public void Group_cancel_adds_nobody()
    {
        var api = Fixture();
        using var ctx = Context(api);
        List<InvitePersonDto>? sel = null;
        var cut = Render(ctx, changed: v => sel = v);
        cut.Find(".ip-in").Focus();
        cut.WaitForAssertion(() => Assert.NotNull(Row(cut, "ICT IPOP")));
        Row(cut, "ICT IPOP").Click();
        cut.WaitForAssertion(() => cut.Find(".ip-toast-no").Click());
        Assert.Null(sel);
        Assert.Empty(cut.FindAll(".ip-toast"));
    }

    [Fact]
    public void Chips_summary_counts_and_find_a_time()
    {
        var api = Fixture();
        using var ctx = Context(api);
        var found = 0;
        var cut = Render(ctx, [Alphin, Beny, Yudi, Jason], findTime: () => found++);
        cut.WaitForAssertion(() => Assert.Equal("3 free · 1 busy · Find a time", Squash(cut.Find(".ip-fb").TextContent)));
        Assert.Contains("busy", cut.FindAll(".ip-chip")[3].ClassList);
        Assert.Equal("4 invited", cut.Find(".ip-cnt").TextContent);
        cut.Find(".ip-find").Click();
        Assert.Equal(1, found);
    }

    [Fact]
    public void Summary_without_busy_people_says_free_at_this_time()
    {
        using var ctx = Context(Fixture());
        var cut = Render(ctx, [Alphin, Beny]);
        cut.WaitForAssertion(() => Assert.Equal("2 free at this time", Squash(cut.Find(".ip-fb").TextContent)));
    }

    [Fact]
    public void Chip_remove_and_row_toggle_update_the_selection()
    {
        using var ctx = Context(Fixture());
        List<InvitePersonDto>? sel = null;
        var cut = Render(ctx, [Alphin, Beny], changed: v => sel = v);
        cut.FindAll(".ip-chip .xx")[0].Click();
        Assert.Equal(["Beny Saputra"], sel!.Select(p => p.Name).ToArray());
        cut.Find(".ip-in").Focus();
        cut.WaitForAssertion(() => Assert.NotNull(Row(cut, "Jason Kristanto")));
        Row(cut, "Jason Kristanto").Click();
        Assert.Equal(["Beny Saputra", "Jason Kristanto"], sel!.Select(p => p.Name).ToArray());
        Assert.Contains("on", Row(cut, "Jason Kristanto").QuerySelector(".addb")!.ClassList);
    }

    [Fact]
    public void Keyboard_moves_the_active_option_enter_adds_and_escape_closes()
    {
        using var ctx = Context(Fixture());
        List<InvitePersonDto>? sel = null;
        var cut = Render(ctx, changed: v => sel = v);
        var input = cut.Find(".ip-in");
        input.Focus();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[role=option]")));
        input.KeyDown("ArrowDown");
        Assert.Equal("true", cut.FindAll("[role=option]")[1].GetAttribute("aria-selected"));
        Assert.Equal(cut.FindAll("[role=option]")[1].Id, cut.Find(".ip-in").GetAttribute("aria-activedescendant"));
        cut.Find(".ip-in").KeyDown("ArrowUp");
        cut.Find(".ip-in").KeyDown("ArrowUp");                 // wraps to the last option
        Assert.Equal("true", cut.FindAll("[role=option]")[^1].GetAttribute("aria-selected"));
        cut.Find(".ip-in").KeyDown("ArrowDown");               // back to the first
        cut.Find(".ip-in").KeyDown("Enter");
        Assert.Equal(["Alphin Santoso"], sel!.Select(p => p.Name).ToArray());
        cut.Find(".ip-in").KeyDown("Escape");
        Assert.Empty(cut.FindAll(".ip-drop"));
        Assert.Equal("false", cut.Find(".ip-in").GetAttribute("aria-expanded"));
    }

    [Fact]
    public void Capacity_warning_above_the_room_size()
    {
        using var ctx = Context(Fixture());
        var twenty = Enumerable.Range(1, 20).Select(i => P($"NIK91{i:0000}", $"Person {i}")).ToList();
        var cut = Render(ctx, twenty, capacity: 20);
        // 20 invited + the organizer = 21.
        Assert.Contains("21 people — Kalosi fits 20", cut.Find(".ip-warn").TextContent);
        Assert.Contains("Remove one person", cut.Find(".ip-warn").TextContent);

        var fits = Render(ctx, twenty.Take(19).ToList(), capacity: 20);
        Assert.Empty(fits.FindAll(".ip-warn"));
        var voice = Render(ctx, twenty);
        Assert.Empty(voice.FindAll(".ip-warn"));
    }

    [Fact]
    public void No_key_ever_appears_in_the_markup()
    {
        var api = Fixture();
        using var ctx = Context(api);
        var cut = Render(ctx, [Alphin, Jason]);
        cut.Find(".ip-in").Focus();
        cut.WaitForAssertion(() => Assert.NotNull(Row(cut, "ICT IPOP")));
        Row(cut, "ICT IPOP").Click();
        cut.WaitForAssertion(() => Assert.NotNull(cut.Find(".ip-toast")));
        Assert.DoesNotContain("NIK9", cut.Markup);
    }

    private static AngleSharp.Dom.IElement Row(IRenderedComponent<InvitePicker> cut, string name) =>
        cut.FindAll(".ip-row").First(r => r.QuerySelector(".n b")!.TextContent == name);

    private static string Squash(string s) => System.Text.RegularExpressions.Regex.Replace(s, "\\s+", " ").Trim();

    private static BunitContext Context(Api api)
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        var http = new HttpClient(api, disposeHandler: false) { BaseAddress = new Uri("http://localhost/") };
        var auth = new AuthState(http, ctx.JSInterop.JSRuntime);
        ctx.Services.AddSingleton(auth);
        ctx.Services.AddSingleton(http);
        ctx.Services.AddSingleton(new ToastService());
        ctx.Services.AddSingleton(new InviteApi(auth));
        ctx.Services.AddScoped<PhotoViewerState>();
        return ctx;
    }

    internal sealed class Api : HttpMessageHandler
    {
        private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
        public InviteSuggestionsDto Suggestions { get; set; } = new([], [], [], []);
        public InviteSearchDto SearchResult { get; set; } = new([], []);
        public Dictionary<string, (string State, DateTime? From, DateTime? To)> Busy { get; } = [];
        public Dictionary<Guid, List<InvitePersonDto>> Expand { get; } = [];
        public List<string> BusyBodies { get; } = [];
        public List<string> Queries { get; } = [];
        private int suggestionGets;
        public int SuggestionGets => Volatile.Read(ref suggestionGets);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            var q = System.Web.HttpUtility.ParseQueryString(request.RequestUri.Query);
            if (path == "/api/invite/suggestions") { Interlocked.Increment(ref suggestionGets); return Json(Suggestions); }
            if (path == "/api/invite/search") { lock (Queries) Queries.Add(q["q"]!); return Json(SearchResult); }
            if (path == "/api/invite/expand")
                return Expand.TryGetValue(Guid.Parse(q["id"]!), out var people) ? Json(people) : new HttpResponseMessage(HttpStatusCode.NotFound);
            if (path == "/api/invite/busy")
            {
                var body = await request.Content!.ReadAsStringAsync(ct);
                lock (BusyBodies) BusyBodies.Add(body);
                var keys = JsonDocument.Parse(body).RootElement.GetProperty("keys").EnumerateArray().Select(k => k.GetString()!).ToList();
                return Json(keys.Select(k => Busy.TryGetValue(k, out var b) ? new PersonBusyDto(k, b.State, b.From, b.To) : new PersonBusyDto(k, "free", null, null)).ToList());
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage Json<T>(T body) =>
            new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(body, Web), Encoding.UTF8, "application/json") };
    }
}
