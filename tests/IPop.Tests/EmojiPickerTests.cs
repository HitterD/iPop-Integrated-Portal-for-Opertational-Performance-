using System.Text.Json;
using Bunit;
using IPop.Web.Components;
using IPop.Web.Services;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using Xunit;

namespace IPop.Tests;

/// Shared full-Unicode emoji picker: dataset, catalog search/tones, per-account prefs, component.
public class EmojiPickerTests
{
    // ---- dataset (the real shipped file) ----

    private static readonly Lazy<string> DatasetJson = new(() =>
        File.ReadAllText(Path.Combine(RepoRoot(), "src", "IPop.Web", "wwwroot", "data", "emoji", "emoji-18.0.json")));

    private static EmojiCatalog RealCatalog()
    {
        var c = new EmojiCatalog(_ => Task.FromResult(DatasetJson.Value));
        c.EnsureLoadedAsync().GetAwaiter().GetResult();
        return c;
    }

    [Fact]
    public void Dataset_is_complete_versioned_and_licensed()
    {
        using var doc = JsonDocument.Parse(DatasetJson.Value);
        Assert.Equal(EmojiCatalog.DatasetVersion, doc.RootElement.GetProperty("version").GetString());
        Assert.Equal(EmojiCategory.All.Select(c => c.Id), doc.RootElement.GetProperty("categories").EnumerateArray().Select(e => e.GetString()));

        var all = EmojiCatalog.Parse(DatasetJson.Value);
        Assert.True(all.Count > 1900, $"only {all.Count} base emoji");
        for (var i = 0; i < EmojiCategory.All.Count; i++) Assert.Contains(all, e => e.Category == i);
        Assert.Equal(all.Count, all.Select(e => e.Glyph).Distinct().Count());
        Assert.All(all, e => Assert.False(string.IsNullOrWhiteSpace(e.Name)));
        Assert.All(all.Where(e => e.Tones is not null), e =>
            Assert.All(e.Tones!, t => Assert.Contains(t.EnumerateRunes(), r => r.Value is >= 0x1F3FB and <= 0x1F3FF)));

        var license = File.ReadAllText(Path.Combine(RepoRoot(), "src", "IPop.Web", "wwwroot", "data", "emoji", "LICENSE.txt"));
        Assert.Contains("UNICODE LICENSE V3", license);
        Assert.Contains("emoji-test.txt", license);
    }

    [Fact]
    public void Catalog_covers_familiar_emoji_with_valid_tones_and_flags()
    {
        var c = RealCatalog();
        var thumbs = c.Find("👍")!.Value.Entry;
        Assert.Equal("thumbs up", thumbs.Name);
        Assert.Equal("👍🏽", thumbs.WithTone(3));
        Assert.Equal("thumbs up: medium skin tone", thumbs.LabelWithTone(3));
        Assert.Equal((thumbs, 5), c.Find("👍🏿")!.Value);

        var heart = c.Find("❤️")!.Value.Entry;
        Assert.False(heart.HasTones);
        Assert.Equal("❤️", heart.WithTone(4));                 // tone never invents a sequence
        Assert.Equal(heart, c.Find("❤")!.Value.Entry);          // stored text without U+FE0F

        Assert.Contains(c.InCategory(7), e => e.Glyph == "🇮🇩");
        Assert.Contains(c.InCategory(0), e => e.Name == "grinning face");
    }

    [Fact]
    public void Search_matches_english_names_and_keywords_best_first()
    {
        var c = RealCatalog();
        Assert.Equal("thumbs up", c.Search("thumbs up")[0].Name);
        Assert.Equal("red heart", c.Search("red heart")[0].Name);
        Assert.Contains(c.Search("happy"), e => e.Glyph == "😀");      // CLDR keyword
        Assert.Contains(c.Search("indonesia"), e => e.Glyph == "🇮🇩");
        Assert.Contains(c.Search("PIZ"), e => e.Name == "pizza");        // prefix, case-insensitive
        Assert.Empty(c.Search("zzqqxx"));
        Assert.Empty(c.Search("   "));
        Assert.True(c.Search("face", limit: 20).Count <= 20);
    }

    [Fact]
    public void Newer_emoji_are_hidden_by_default_but_reachable()
    {
        var c = RealCatalog();
        var newer = c.All.Where(e => e.Version > EmojiCatalog.DefaultMaxVersion).ToList();
        Assert.NotEmpty(newer);
        var sample = newer[^1];
        Assert.DoesNotContain(c.InCategory(sample.Category), e => e == sample);
        Assert.Contains(c.InCategory(sample.Category, maxVersion: null), e => e == sample);
        Assert.DoesNotContain(c.Search(sample.Name), e => e == sample);
        Assert.Contains(c.Search(sample.Name, maxVersion: null), e => e == sample);
    }

    // ---- catalog loading ----

    [Fact]
    public async Task Failed_sync_load_is_retried_and_concurrent_loads_share_one_fetch()
    {
        var calls = 0;
        var c = new EmojiCatalog(_ =>
        {
            calls++;
            if (calls == 1) throw new HttpRequestException("offline");    // synchronous failure
            if (calls == 2) return Task.FromResult("{not json");
            return Task.FromResult(DatasetJson.Value);
        });
        Assert.False(await c.EnsureLoadedAsync());
        Assert.True(c.LoadFailed);
        Assert.False(await c.EnsureLoadedAsync());
        Assert.True(await c.EnsureLoadedAsync());
        Assert.True(await c.EnsureLoadedAsync());
        Assert.Equal(3, calls);
        Assert.False(c.LoadFailed);

        var gate = new TaskCompletionSource<string>();
        var shared = 0;
        var c2 = new EmojiCatalog(_ => { shared++; return gate.Task; });
        var a = c2.EnsureLoadedAsync();
        var b = c2.EnsureLoadedAsync();
        gate.SetResult(DatasetJson.Value);
        Assert.True(await a);
        Assert.True(await b);
        Assert.Equal(1, shared);
    }

    [Fact]
    public async Task Cancelled_wait_does_not_poison_the_shared_load()
    {
        var gate = new TaskCompletionSource<string>();
        var c = new EmojiCatalog(_ => gate.Task);
        using var cts = new CancellationTokenSource();
        var waiting = c.EnsureLoadedAsync(cts.Token);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        gate.SetResult(DatasetJson.Value);
        Assert.True(await c.EnsureLoadedAsync());
    }

    // ---- prefs ----

    [Fact]
    public void Prefs_decode_tolerates_corrupt_storage()
    {
        foreach (var raw in new[] { null, "", "garbage", "[]", "42", "{\"recent\":\"x\"}", "{\"recent\":[1,null,{}],\"tone\":\"3\"}", "{\"tone\":99}" })
        {
            var (recent, tone, newer) = EmojiPrefs.Decode(raw);
            Assert.Empty(recent);
            Assert.Equal(0, tone);
            Assert.False(newer);
        }
        var junk = JsonSerializer.Serialize(new { recent = new object[] { "😀", "😀", "abc", new string('x', 500), "<script>", "👍🏽" }, tone = 2, newer = true });
        var (r, t, n) = EmojiPrefs.Decode(junk);
        Assert.Equal(["😀", "👍🏽"], r);
        Assert.Equal(2, t);
        Assert.True(n);

        var many = EmojiPrefs.Encode(Enumerable.Range(0, 50).Select(i => char.ConvertFromUtf32(0x1F600 + i)), 1);
        Assert.Equal(EmojiPrefs.MaxRecent, EmojiPrefs.Decode(many).Recent.Count);
    }

    [Fact]
    public async Task Recents_are_distinct_newest_first_capped_and_per_account()
    {
        var store = new FakeStorage();
        var auth = Auth("100");
        var prefs = new EmojiPrefs(store, auth);

        for (var i = 0; i < 35; i++) await prefs.AddRecentAsync(char.ConvertFromUtf32(0x1F600 + i));
        await prefs.AddRecentAsync("😀");
        await prefs.AddRecentAsync("not an emoji");
        await prefs.SetToneAsync(4);

        Assert.Equal(EmojiPrefs.MaxRecent, prefs.Recent.Count);
        Assert.Equal("😀", prefs.Recent[0]);
        Assert.Equal(char.ConvertFromUtf32(0x1F600 + 34), prefs.Recent[1]);
        Assert.Equal(prefs.Recent.Count, prefs.Recent.Distinct().Count());
        Assert.True(store.Items.ContainsKey(EmojiPrefs.KeyFor("100")));

        // Another account on the same browser starts empty; switching back restores the first.
        SetMe(auth, "200");
        await prefs.EnsureLoadedAsync();
        Assert.Empty(prefs.Recent);
        Assert.Equal(0, prefs.Tone);
        SetMe(auth, "100");
        await prefs.EnsureLoadedAsync();
        Assert.Equal("😀", prefs.Recent[0]);
        Assert.Equal(4, prefs.Tone);

        // A fresh session (new scope) reads the same per-account data.
        var again = new EmojiPrefs(store, Auth("100"));
        await again.EnsureLoadedAsync();
        Assert.Equal(prefs.Recent, again.Recent);
        Assert.Equal(4, again.Tone);
    }

    [Fact]
    public async Task Full_or_blocked_storage_keeps_picks_in_memory()
    {
        var store = new FakeStorage { QuotaBytes = 60 };     // only the empty-list payload fits
        var prefs = new EmojiPrefs(store, Auth("100"));
        for (var i = 0; i < 12; i++) await prefs.AddRecentAsync(char.ConvertFromUtf32(0x1F600 + i));
        Assert.Equal(12, prefs.Recent.Count);
        Assert.True(store.Items[EmojiPrefs.KeyFor("100")].Length <= 60);

        var blocked = new EmojiPrefs(new FakeStorage { Blocked = true }, Auth("100"));
        await blocked.AddRecentAsync("🎉");
        Assert.Equal(["🎉"], blocked.Recent);
    }

    [Fact]
    public async Task Sign_out_unloads_memory_but_keeps_storage()
    {
        var store = new FakeStorage();
        var auth = Auth("100");
        var prefs = new EmojiPrefs(store, auth);
        await prefs.AddRecentAsync("🎉");
        await auth.SignOutAsync();
        Assert.Empty(prefs.Recent);
        Assert.True(store.Items.ContainsKey(EmojiPrefs.KeyFor("100")));
    }

    // ---- component ----

    [Fact]
    public void Opens_on_first_category_without_history_with_all_tabs()
    {
        using var ctx = Context(out _);
        var cut = ctx.Render<EmojiPicker>();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".ep-emoji")));
        Assert.Equal(
            ["recent", "smileys-people", "animals-nature", "food-drink", "activities", "travel-places", "objects", "symbols", "flags"],
            cut.FindAll("[role=tab]").Select(t => t.GetAttribute("data-tab")));
        Assert.Equal(["Recent", "Smileys & People", "Animals & Nature", "Food & Drink", "Activities", "Travel & Places", "Objects", "Symbols", "Flags"],
            cut.FindAll("[role=tab]").Select(t => t.GetAttribute("aria-label")));
        Assert.Equal("smileys-people", cut.Find("[role=tabpanel]").GetAttribute("data-panel"));
        Assert.Equal("true", cut.Find("[data-tab=smileys-people]").GetAttribute("aria-selected"));
        Assert.Equal("dialog", cut.Find(".emoji-picker").GetAttribute("role"));
        Assert.Equal("grinning face", cut.Find(".ep-emoji").GetAttribute("aria-label"));
        // Bounded: rows go through Virtualize (bUnit has no viewport so it renders them all here;
        // in the browser only the visible window plus overscan is in the DOM).
        Assert.NotEmpty(cut.FindComponents<Microsoft.AspNetCore.Components.Web.Virtualization.Virtualize<EmojiPicker.Cell[]>>());
        Assert.All(cut.FindAll(".ep-row"), r => Assert.True(r.QuerySelectorAll(".ep-emoji").Length <= 8));
    }

    [Fact]
    public void Picking_reports_the_glyph_records_a_recent_and_opens_on_recent_next_time()
    {
        using var ctx = Context(out var store);
        var picked = new List<string>();
        var cut = ctx.Render<EmojiPicker>(p => p.Add(x => x.OnPick, g => picked.Add(g)));
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".ep-emoji")));

        cut.Find(".ep-emoji[aria-label='grinning face']").Click();

        Assert.Equal(["😀"], picked);
        Assert.Equal(["😀"], EmojiPrefs.Decode(store.Items[EmojiPrefs.KeyFor("100")]).Recent);

        var next = ctx.Render<EmojiPicker>();
        next.WaitForAssertion(() => Assert.Equal("recent", next.Find("[role=tabpanel]").GetAttribute("data-panel")));
        Assert.Equal("😀", next.Find(".ep-emoji").TextContent);
    }

    [Fact]
    public void Search_and_tabs_switch_the_grid()
    {
        using var ctx = Context(out _);
        var cut = ctx.Render<EmojiPicker>();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".ep-emoji")));

        cut.Find("[data-tab=flags]").Click();
        Assert.Equal("flags", cut.Find("[role=tabpanel]").GetAttribute("data-panel"));
        Assert.Contains(cut.FindAll(".ep-emoji"), b => b.TextContent == "🏁");

        cut.Find("input[aria-label='Search emoji']").Input("pizza");
        Assert.Empty(cut.FindAll("[role=tab]"));
        Assert.Equal("search", cut.Find(".ep-body").GetAttribute("data-panel"));
        Assert.Equal("🍕", cut.Find(".ep-emoji").TextContent);

        cut.Find("input[aria-label='Search emoji']").Input("zzqqxx");
        Assert.Contains("No emoji match", cut.Find(".ep-empty").TextContent);
    }

    [Fact]
    public void Chosen_tone_applies_only_to_emoji_with_valid_tone_variants_and_persists()
    {
        using var ctx = Context(out var store);
        var picked = new List<string>();
        var cut = ctx.Render<EmojiPicker>(p => p.Add(x => x.OnPick, g => picked.Add(g)));
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".ep-emoji")));

        cut.Find(".ep-tone-btn").Click();
        Assert.Equal(6, cut.FindAll("[role=radio]").Count);
        cut.Find("[role=radio][aria-label='Dark skin tone']").Click();
        Assert.Empty(cut.FindAll(".ep-tones"));

        cut.Find("input[aria-label='Search emoji']").Input("thumbs up");
        cut.Find(".ep-emoji").Click();
        cut.Find("input[aria-label='Search emoji']").Input("red heart");
        cut.Find(".ep-emoji").Click();

        Assert.Equal(["👍🏿", "❤️"], picked);
        Assert.Contains("\"tone\":5", store.Items[EmojiPrefs.KeyFor("100")]);
    }

    [Fact]
    public void Escape_calls_OnClose_after_closing_the_tone_menu()
    {
        using var ctx = Context(out _);
        var closed = 0;
        var cut = ctx.Render<EmojiPicker>(p => p.Add(x => x.OnClose, () => closed++));

        cut.Find(".ep-tone-btn").Click();
        cut.Find(".emoji-picker").KeyDown(new KeyboardEventArgs { Key = "Escape" });
        Assert.Equal(0, closed);
        Assert.Empty(cut.FindAll(".ep-tones"));

        cut.Find(".emoji-picker").KeyDown(new KeyboardEventArgs { Key = "Escape" });
        Assert.Equal(1, closed);
    }

    [Fact]
    public void Outside_click_helper_attaches_and_closes()
    {
        using var ctx = Context(out _);
        var module = ctx.JSInterop.SetupModule("./js/emoji-picker.js?v=20261003-compose");
        module.Mode = JSRuntimeMode.Loose;
        var closed = 0;
        var cut = ctx.Render<EmojiPicker>(p => p.Add(x => x.OnClose, () => closed++));

        cut.WaitForAssertion(() => Assert.Single(module.Invocations["attach"]));
        cut.InvokeAsync(() => cut.Instance.OutsidePointerDown()).GetAwaiter().GetResult();
        Assert.Equal(1, closed);
    }

    [Fact]
    public void Mail_compose_emoji_opens_without_a_click_eating_scrim()
    {
        using var ctx = Context(out _);
        var cut = ctx.Render<IPop.Web.Components.Mail.MailRteEditor>();

        // The emoji button sits inside a toolbar group, not alone in its own trailing group.
        Assert.NotNull(cut.Find(".mrte-group .mrte-aa").ParentElement!.ParentElement!.QuerySelector(".mrte-emoji-btn"));

        cut.Find(".mrte-emoji-btn").Click();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".mrte-emoji-pop .emoji-picker")));
        Assert.Empty(cut.FindAll(".mrte-pop-scrim"));
        Assert.Equal("true", cut.Find(".mrte-emoji-btn").GetAttribute("aria-expanded"));

        // Other toolbar popovers keep their scrim.
        cut.Find(".mrte-aa").Click();
        Assert.NotEmpty(cut.FindAll(".mrte-pop-scrim"));
    }

    [Fact]
    public void Compose_emoji_popup_css_is_not_overridden_below_the_editor()
    {
        var css = File.ReadAllText(Path.Combine(RepoRoot(), "src/IPop.Web/wwwroot/css/components/emoji-picker.css"));
        // The old ".mrte-pop:has(> .emoji-picker) { top: calc(100% + 4px) }" rule pushed the picker under
        // the editor where the composer's overflow:hidden clipped it.
        Assert.DoesNotContain(".mrte-pop:has(> .emoji-picker)", css);
        Assert.Matches(@"\.mrte > \.mrte-emoji-pop \{\s*position: fixed;", css);
        var js = File.ReadAllText(Path.Combine(RepoRoot(), "src/IPop.Web/wwwroot/js/emoji-picker.js"));
        Assert.Contains(".mrte-emoji-pop", js);
    }

    [Fact]
    public void Show_newer_emoji_is_an_explicit_persisted_option_with_a_warning()
    {
        using var ctx = Context(out var store);
        var cut = ctx.Render<EmojiPicker>();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".ep-newer input")));
        var newest = ctx.Services.GetRequiredService<EmojiCatalog>().All.Where(e => e.Version > EmojiCatalog.DefaultMaxVersion).First();

        cut.Find("input[aria-label='Search emoji']").Input(newest.Name);
        Assert.DoesNotContain(cut.FindAll(".ep-emoji"), b => b.TextContent == newest.Glyph);
        Assert.Empty(cut.FindAll(".ep-newer small"));

        cut.Find(".ep-newer input").Change(true);

        Assert.Contains(cut.FindAll(".ep-emoji"), b => b.TextContent == newest.Glyph);
        Assert.Contains("may show as boxes", cut.Find(".ep-newer small").TextContent);
        Assert.Contains("\"newer\":true", store.Items[EmojiPrefs.KeyFor("100")]);
    }

    [Fact]
    public void Load_failure_shows_retry_and_recent_still_works()
    {
        var ctx = Context(out var store, failLoad: true);
        using var _ = ctx;
        store.Items[EmojiPrefs.KeyFor("100")] = EmojiPrefs.Encode(["🎉"], 0);
        var cut = ctx.Render<EmojiPicker>();

        Assert.Equal("recent", cut.Find("[role=tabpanel]").GetAttribute("data-panel"));
        Assert.Equal("🎉", cut.Find(".ep-emoji").TextContent);
        cut.Find("[data-tab=objects]").Click();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".ep-retry")));
    }

    // ---- helpers ----

    private static BunitContext Context(out FakeStorage store, bool failLoad = false)
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        store = new FakeStorage();
        var auth = Auth("100");
        ctx.Services.AddSingleton(auth);
        ctx.Services.AddSingleton(new EmojiCatalog(_ => failLoad
            ? Task.FromException<string>(new HttpRequestException("offline"))
            : Task.FromResult(DatasetJson.Value)));
        ctx.Services.AddSingleton(new EmojiPrefs(store, auth));
        return ctx;
    }

    private static AuthState Auth(string nik)
    {
        var auth = new AuthState(new HttpClient { BaseAddress = new Uri("http://localhost/") }, new FakeStorage());
        SetMe(auth, nik);
        return auth;
    }

    private static void SetMe(AuthState auth, string nik)
    {
        var me = JsonSerializer.Deserialize<DirectoryEmployeeDto>($$"""{"nik":"{{nik}}","name":"Test {{nik}}"}""",
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        typeof(AuthState).GetProperty(nameof(AuthState.Me))!.SetValue(auth, me);
    }

    /// From the build output, falling back to this source file (builds with --artifacts-path).
    private static string RepoRoot([System.Runtime.CompilerServices.CallerFilePath] string source = "")
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Path.GetDirectoryName(source) ?? "" })
        {
            var dir = new DirectoryInfo(start);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "IPop.slnx"))) dir = dir.Parent;
            if (dir is not null) return dir.FullName;
        }
        throw new InvalidOperationException("repo root not found");
    }

    /// localStorage stand-in with an optional quota and a "blocked" mode.
    private sealed class FakeStorage : IJSRuntime
    {
        public Dictionary<string, string> Items { get; } = [];
        public int QuotaBytes { get; init; } = int.MaxValue;
        public bool Blocked { get; init; }

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            if (Blocked) throw new JSException("SecurityError: storage disabled");
            switch (identifier)
            {
                case "localStorage.getItem":
                    return new((TValue)(object?)Items.GetValueOrDefault((string)args![0]!)!);
                case "localStorage.setItem":
                    var value = (string)args![1]!;
                    if (value.Length > QuotaBytes) throw new JSException("QuotaExceededError");
                    Items[(string)args[0]!] = value;
                    return new(default(TValue)!);
                default:
                    return new(default(TValue)!);
            }
        }
    }
}
