using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using Bunit;
using IPop.Web.Services;
using Microsoft.JSInterop;
using Xunit;

namespace IPop.Tests;

/// Two tabs of one browser share localStorage but not AuthState, so the per-tab refresh gate cannot serialize them.
/// The server rotates refresh tokens: when both tabs present the same token at once, one wins and the other gets a 401
/// for a token that is merely stale. That tab must adopt the rotated token another tab just stored, not wipe it.
[Collection(nameof(RefreshRetryDelaysCollection))]
public class AuthTwoTabRefreshTests
{
    /// One browser's storage, shared by every AuthState built on it.
    private sealed class SharedStorageJs : IJSRuntime
    {
        public readonly ConcurrentDictionary<string, string> Local = new();
        public readonly List<string> Removed = [];

        public ValueTask<T> InvokeAsync<T>(string identifier, object?[]? args)
        {
            object? result = null;
            switch (identifier)
            {
                case "localStorage.getItem":
                    result = Local.TryGetValue((string)args![0]!, out var v) ? v : null;
                    break;
                case "localStorage.setItem":
                    Local[(string)args![0]!] = (string)args[1]!;
                    break;
                case "localStorage.removeItem":
                    lock (Removed) Removed.Add((string)args![0]!);
                    Local.TryRemove((string)args[0]!, out _);
                    break;
            }
            return ValueTask.FromResult((T)result!);
        }

        public ValueTask<T> InvokeAsync<T>(string identifier, CancellationToken cancellationToken, object?[]? args)
            => InvokeAsync<T>(identifier, args);
    }

    private sealed class RotatingHandler : HttpMessageHandler
    {
        private readonly ConcurrentDictionary<string, byte> _spent = new();
        private readonly object _gate = new();
        private int _issued;
        public int Refreshes;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri!.AbsolutePath != "/api/auth/refresh")
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new { status = "on" }) };
            Interlocked.Increment(ref Refreshes);
            var presented = (await request.Content!.ReadFromJsonAsync<Body>(cancellationToken: ct))!.RefreshToken;
            await Task.Delay(40, ct);   // both tabs have read the stored token before either answer arrives
            lock (_gate)
            {
                if (!_spent.TryAdd(presented, 1)) return new HttpResponseMessage(HttpStatusCode.Unauthorized);
                _issued++;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new
                    {
                        accessToken = $"access-{_issued}", refreshToken = $"refresh-{_issued}",
                        accessTokenExpiresAt = DateTime.UtcNow.AddMinutes(15), mustChangePassword = false,
                    }),
                };
            }
        }

        private sealed record Body(string RefreshToken);
    }

    [Fact]
    public async Task Two_tabs_refreshing_at_once_both_stay_signed_in_and_the_rotated_token_survives()
    {
        var saved = AuthState.RefreshRetryDelays;
        AuthState.RefreshRetryDelays = [TimeSpan.Zero];
        try
        {
            var js = new SharedStorageJs();
            js.Local["ipop.refresh"] = "refresh-0";
            var handler = new RotatingHandler();
            AuthState Tab() => new(new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") }, js);
            var tabA = Tab(); var tabB = Tab();
            var ended = 0;
            tabA.SessionEnded += () => Interlocked.Increment(ref ended);
            tabB.SessionEnded += () => Interlocked.Increment(ref ended);

            var results = await Task.WhenAll(tabA.TryRestoreAsync(), tabB.TryRestoreAsync());

            Assert.Equal([true, true], results);
            Assert.Equal(0, ended);
            Assert.Empty(js.Removed);
            Assert.True(js.Local.TryGetValue("ipop.refresh", out var stored) && stored.StartsWith("refresh-"), "a live refresh token must remain stored");
        }
        finally { AuthState.RefreshRetryDelays = saved; }
    }
}

/// SignalR calls its token provider on every (re)connect. After a laptop sleep the cached access token is long dead,
/// and handing it over made each reconnect fail at negotiate until some unrelated request happened to refresh.
[Collection(nameof(RefreshRetryDelaysCollection))]
public class AuthHubTokenTests
{
    private sealed class CountingHandler(TimeSpan firstLifetime) : HttpMessageHandler
    {
        public int Refreshes;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri!.AbsolutePath != "/api/auth/refresh")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new { status = "on" }) });
            var n = Interlocked.Increment(ref Refreshes);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new
                {
                    accessToken = $"access-{n}", refreshToken = $"refresh-{n}",
                    accessTokenExpiresAt = DateTime.UtcNow + (n == 1 ? firstLifetime : TimeSpan.FromMinutes(15)),
                    mustChangePassword = false,
                }),
            });
        }
    }

    [Fact]
    public async Task Hub_token_provider_refreshes_a_token_that_is_expired_or_about_to_expire()
    {
        using var ctx = new Bunit.BunitContext();
        ctx.JSInterop.Mode = Bunit.JSRuntimeMode.Loose;
        ctx.JSInterop.Setup<string?>("localStorage.getItem", _ => true).SetResult("refresh-0");
        var handler = new CountingHandler(TimeSpan.FromMinutes(-5));   // the first token is already dead
        var auth = new AuthState(new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") }, ctx.JSInterop.JSRuntime);
        Assert.True(await auth.TryRestoreAsync());

        var token = await auth.HubAccessTokenAsync();

        Assert.Equal("access-2", token);
        Assert.Equal(2, handler.Refreshes);
    }

    [Fact]
    public async Task Hub_token_provider_reuses_a_healthy_token_without_a_network_call()
    {
        using var ctx = new Bunit.BunitContext();
        ctx.JSInterop.Mode = Bunit.JSRuntimeMode.Loose;
        ctx.JSInterop.Setup<string?>("localStorage.getItem", _ => true).SetResult("refresh-0");
        var handler = new CountingHandler(TimeSpan.FromMinutes(15));
        var auth = new AuthState(new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") }, ctx.JSInterop.JSRuntime);
        Assert.True(await auth.TryRestoreAsync());

        Assert.Equal("access-1", await auth.HubAccessTokenAsync());
        Assert.Equal(1, handler.Refreshes);
    }
}
