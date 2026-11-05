using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using Bunit;
using IPop.Web.Services;
using Xunit;

namespace IPop.Tests;

/// The server rotates refresh tokens (AuthWorkflow.RefreshAsync): a successful refresh revokes the
/// token it was given. Replaying a revoked token is a 401, and AuthState treats that 401 as "the
/// session is gone" and wipes the stored refresh token.
///
/// So when the 15-minute access token expires while several requests are in flight — Dashboard
/// alone fans out seven parallel GETs — every one of them 401s and every one of them tries to
/// refresh. Exactly one may do so; the rest must wait for that result, not race it.
public class AuthRefreshRaceTests
{
    /// Stands in for the API: 401 on the protected route while the caller carries the expired
    /// access token, and a rotating /api/auth/refresh that 401s on any token it has already spent.
    private sealed class RotatingAuthHandler : HttpMessageHandler
    {
        private readonly ConcurrentDictionary<string, byte> _spent = new();
        private readonly object _gate = new();
        private int _issued;

        public int RefreshCalls;
        public int RevokedTokenReplays;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri!.AbsolutePath == "/api/auth/refresh")
            {
                Interlocked.Increment(ref RefreshCalls);
                var body = await request.Content!.ReadFromJsonAsync<RefreshBody>(cancellationToken: ct);
                var presented = body!.RefreshToken;

                // Give the losers of the race a real window in which to present the stale token.
                await Task.Delay(30, ct);

                lock (_gate)
                {
                    if (!_spent.TryAdd(presented, 1))
                    {
                        // Rotation already spent this one: exactly what the server returns.
                        Interlocked.Increment(ref RevokedTokenReplays);
                        return new HttpResponseMessage(HttpStatusCode.Unauthorized);
                    }
                    _issued++;
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = JsonContent.Create(new
                        {
                            accessToken = $"access-{_issued}",
                            refreshToken = $"refresh-{_issued}",
                            accessTokenExpiresAt = DateTime.UtcNow.AddMinutes(15),
                            mustChangePassword = false,
                        }),
                    };
                }
            }

            // Protected route: 401 until the caller presents a token minted by a refresh.
            var token = request.Headers.Authorization?.Parameter;
            if (token is null || token == "expired")
                return new HttpResponseMessage(HttpStatusCode.Unauthorized);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new Payload(true)) };
        }

        private sealed record RefreshBody(string RefreshToken);
    }

    private sealed record Payload(bool Ok);

    [Fact]
    public async Task ConcurrentUnauthorized_RefreshesOnce_AndNeverReplaysARevokedToken()
    {
        var handler = new RotatingAuthHandler();
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };

        using var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.JSInterop.Setup<string?>("localStorage.getItem", _ => true).SetResult("refresh-0");

        var auth = new AuthState(http, ctx.JSInterop.JSRuntime);

        // Eight callers hit protected endpoints at the instant the access token goes stale:
        // the Dashboard fan-out plus the presence heartbeat.
        var calls = Enumerable.Range(0, 8)
            .Select(_ => auth.GetAsync<Payload>("/api/protected"))
            .ToArray();
        await Task.WhenAll(calls);

        Assert.Equal(0, handler.RevokedTokenReplays);
        Assert.Equal(1, handler.RefreshCalls);
    }

    /// Always-401 refresh: the session really is over (90-day idle expiry, admin revoke, deactivation).
    private sealed class DeadSessionHandler : HttpMessageHandler
    {
        public int RefreshCalls;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri!.AbsolutePath == "/api/auth/refresh")
                Interlocked.Increment(ref RefreshCalls);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
        }
    }

    [Fact]
    public async Task RefusedRefreshToken_RaisesSessionEnded_SoTheShellCanLeave()
    {
        var handler = new DeadSessionHandler();
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };

        using var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.JSInterop.Setup<string?>("localStorage.getItem", _ => true).SetResult("refresh-dead");

        var auth = new AuthState(http, ctx.JSInterop.JSRuntime);
        var ended = 0;
        auth.SessionEnded += () => Interlocked.Increment(ref ended);

        Assert.False(await auth.TryRestoreAsync());

        // Before the fix nothing fired here, so the shell stayed put and the heartbeat kept
        // beating against a session the server had already thrown away.
        Assert.Equal(1, ended);
        Assert.False(auth.IsAuthenticated);
    }
}

/// An API restart (502/503 from the proxy, or no answer) must not end the session: only a refused token does.
[Collection(nameof(RefreshRetryDelaysCollection))]
public class AuthRefreshTransientTests
{
    private sealed class FlakyRefreshHandler(params HttpStatusCode?[] answers) : HttpMessageHandler
    {
        private int _i;
        public int RefreshCalls;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref RefreshCalls);
            var a = answers[Math.Min(_i++, answers.Length - 1)];
            if (a is null) throw new HttpRequestException("connection refused");
            if (a != HttpStatusCode.OK) return Task.FromResult(new HttpResponseMessage(a.Value));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new
                {
                    accessToken = "access-1", refreshToken = "refresh-1",
                    accessTokenExpiresAt = DateTime.UtcNow.AddMinutes(15), mustChangePassword = false,
                }),
            });
        }
    }

    private static (AuthState Auth, BunitContext Ctx) Make(HttpMessageHandler h)
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.JSInterop.Setup<string?>("localStorage.getItem", _ => true).SetResult("refresh-0");
        return (new AuthState(new HttpClient(h) { BaseAddress = new Uri("http://localhost/") }, ctx.JSInterop.JSRuntime), ctx);
    }

    [Fact]
    public async Task Proxy_errors_during_a_restart_are_retried_and_the_session_survives()
    {
        var saved = AuthState.RefreshRetryDelays;
        AuthState.RefreshRetryDelays = [TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero];
        try
        {
            var h = new FlakyRefreshHandler(HttpStatusCode.BadGateway, null, HttpStatusCode.ServiceUnavailable, HttpStatusCode.OK);
            var (auth, ctx) = Make(h);
            using var _ = ctx;
            var ended = 0;
            auth.SessionEnded += () => ended++;

            Assert.True(await auth.TryRestoreAsync());
            Assert.Equal(4, h.RefreshCalls);
            Assert.Equal(0, ended);
            Assert.DoesNotContain(ctx.JSInterop.Invocations, i => i.Identifier == "localStorage.removeItem");
        }
        finally { AuthState.RefreshRetryDelays = saved; }
    }

    [Fact]
    public async Task Api_still_down_after_the_retries_keeps_the_stored_token()
    {
        var saved = AuthState.RefreshRetryDelays;
        AuthState.RefreshRetryDelays = [TimeSpan.Zero, TimeSpan.Zero];
        try
        {
            var h = new FlakyRefreshHandler(HttpStatusCode.GatewayTimeout);
            var (auth, ctx) = Make(h);
            using var _ = ctx;
            var ended = 0;
            auth.SessionEnded += () => ended++;

            Assert.False(await auth.TryRestoreAsync());
            Assert.Equal(3, h.RefreshCalls);
            Assert.Equal(0, ended);
            Assert.DoesNotContain(ctx.JSInterop.Invocations, i => i.Identifier == "localStorage.removeItem");
        }
        finally { AuthState.RefreshRetryDelays = saved; }
    }

    [Theory]
    [InlineData(HttpStatusCode.BadGateway, true)]
    [InlineData(HttpStatusCode.ServiceUnavailable, true)]
    [InlineData(HttpStatusCode.TooManyRequests, true)]
    [InlineData(HttpStatusCode.InternalServerError, true)]
    [InlineData(HttpStatusCode.Unauthorized, false)]
    [InlineData(HttpStatusCode.Forbidden, false)]
    [InlineData(HttpStatusCode.BadRequest, false)]
    [InlineData(HttpStatusCode.NotFound, false)]
    [InlineData(HttpStatusCode.RequestTimeout, true)]
    public void Only_a_refused_token_is_final(HttpStatusCode code, bool transient) =>
        Assert.Equal(transient, AuthState.IsTransientRefreshFailure(code));
}

[CollectionDefinition(nameof(RefreshRetryDelaysCollection), DisableParallelization = true)]
public class RefreshRetryDelaysCollection;
