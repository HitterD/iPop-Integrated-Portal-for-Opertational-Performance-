using System.Net;

namespace IPop.Web.Services;

/// Sits on top of PracticeGate. A JSON read (GET/HEAD under /api) that has not answered within
/// <see cref="AttemptTimeout"/> is abandoned and sent again, at most <see cref="MaxRetries"/>
/// times; so are network errors and 502/503/504 from the proxy. Without this a request that
/// stalled server-side (a hung DB connect once took 7.5 s, and nothing capped it below the
/// 100 s HttpClient default) left the page on skeletons until the user reloaded.
///
/// Writes are never retried: a POST that timed out may still have sent the mail or the message.
/// Downloads, exports and media streams are left alone; they are legitimately slow.
public sealed class ResilientHandler : DelegatingHandler
{
    public static readonly TimeSpan AttemptTimeout = TimeSpan.FromSeconds(15);
    public const int MaxRetries = 2;
    private static readonly TimeSpan[] Backoff = [TimeSpan.FromMilliseconds(400), TimeSpan.FromMilliseconds(1200)];

    /// Path fragments of GETs that stream files or run long on purpose.
    private static readonly string[] LongRunning =
    [
        "/download", "/export", "/videos/", "/attachments/", "/files/preview", "/stream", "/thumb",
    ];

    public ResilientHandler(HttpMessageHandler inner) : base(inner) { }

    /// Test seam: the delay between attempts.
    public Func<TimeSpan, CancellationToken, Task> Delay { get; set; } = Task.Delay;

    public static bool IsRetryable(HttpRequestMessage request)
    {
        if (request.Method != HttpMethod.Get && request.Method != HttpMethod.Head) return false;
        var path = request.RequestUri is { IsAbsoluteUri: true } u ? u.AbsolutePath : request.RequestUri?.OriginalString ?? "";
        if (!path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase)) return false;
        foreach (var frag in LongRunning)
            if (path.Contains(frag, StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (!IsRetryable(request)) return await base.SendAsync(request, ct);

        for (var attempt = 0; ; attempt++)
        {
            var last = attempt >= MaxRetries;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(AttemptTimeout);
            try
            {
                var res = await base.SendAsync(attempt == 0 ? request : Clone(request), timeout.Token);
                if (last || !IsTransient(res.StatusCode)) return res;
                res.Dispose();
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Our own per-attempt timeout, not the caller giving up.
                if (last) throw new TimeoutException($"{request.RequestUri} did not answer in {AttemptTimeout.TotalSeconds:0} s.");
            }
            catch (HttpRequestException) when (!last && !ct.IsCancellationRequested) { }

            await Delay(Backoff[Math.Min(attempt, Backoff.Length - 1)], ct);
        }
    }

    private static bool IsTransient(HttpStatusCode code)
        => code is HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout;

    /// A request message can only be sent once; GET/HEAD carry no body, so headers are all there is.
    private static HttpRequestMessage Clone(HttpRequestMessage src)
    {
        var copy = new HttpRequestMessage(src.Method, src.RequestUri) { Version = src.Version };
        foreach (var h in src.Headers) copy.Headers.TryAddWithoutValidation(h.Key, h.Value);
        foreach (var o in src.Options) ((IDictionary<string, object?>)copy.Options)[o.Key] = o.Value;
        return copy;
    }
}
