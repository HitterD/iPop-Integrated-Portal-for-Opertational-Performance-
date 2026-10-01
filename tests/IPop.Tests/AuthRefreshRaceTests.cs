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
