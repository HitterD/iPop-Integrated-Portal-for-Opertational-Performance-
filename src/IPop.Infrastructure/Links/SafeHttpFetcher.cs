using System.Net;
using System.Net.Sockets;

namespace IPop.Infrastructure.Links;

public interface IDnsResolver
{
    Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct);
}

public sealed class SystemDnsResolver : IDnsResolver
{
    public Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct) => Dns.GetHostAddressesAsync(host, ct);
}

/// Outcome codes are coarse on purpose: callers log them with the host, never the full URL.
public enum FetchOutcome { Ok, BadUrl, Blocked, DnsFailed, HttpError, WrongType, TooLarge, TooManyRedirects, Timeout, Failed }

public sealed record FetchResult(FetchOutcome Outcome, Uri? FinalUri = null, string? MediaType = null, string? Charset = null, byte[]? Body = null, bool Truncated = false)
{
    public bool Ok => Outcome == FetchOutcome.Ok;
}

/// AllowTruncate: keep the first MaxBytes (HTML head is all we need); otherwise oversize = TooLarge.
public sealed record FetchRequest(Uri Url, Func<string, bool> AcceptMediaType, int MaxBytes, bool AllowTruncate, string Accept);

/// Server-side fetch that cannot be steered into the internal network:
/// - only http/https on 80/443 (<see cref="SsrfGuard.IsAllowedUri"/>);
/// - DNS is resolved here and every address must be public; the socket then connects to that
///   vetted address (ConnectCallback), so a second, rebinding lookup never happens;
/// - redirects are followed by hand (max 3), each hop re-validated and re-resolved;
/// - 5 s total, size-capped body, no cookies, no proxy, no credentials.
public sealed class SafeHttpFetcher : IDisposable
{
    public const string UserAgent = "iPopLinkPreview/1.0 (+https://ipop.santos.co.id)";
    public const int MaxRedirects = 3;
    public static readonly TimeSpan TotalTimeout = TimeSpan.FromSeconds(5);

    internal static readonly HttpRequestOptionsKey<IPAddress[]> VettedAddresses = new("ipop.vetted-addresses");

    private readonly IDnsResolver _dns;
    private readonly HttpClient _http;
    private readonly TimeSpan _timeout;

    public SafeHttpFetcher(IDnsResolver dns, HttpMessageHandler? transport = null, TimeSpan? timeout = null)
    {
        _dns = dns;
        _timeout = timeout ?? TotalTimeout;
        _http = new HttpClient(transport ?? CreateTransport(), disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public static SocketsHttpHandler CreateTransport() => new()
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        UseProxy = false,
        Credentials = null,
        PreAuthenticate = false,
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
        ConnectTimeout = TimeSpan.FromSeconds(4),
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        MaxResponseHeadersLength = 64, // KB
        ConnectCallback = ConnectToVettedAddressAsync,
    };

    /// Connects only to an address this fetcher vetted for the request — never resolves on its own.
    internal static async ValueTask<Stream> ConnectToVettedAddressAsync(SocketsHttpConnectionContext ctx, CancellationToken ct)
    {
        if (!ctx.InitialRequestMessage.Options.TryGetValue(VettedAddresses, out var addrs) || addrs is not { Length: > 0 })
            throw new HttpRequestException("No vetted address for this request.");
        Exception? last = null;
        foreach (var ip in addrs)
        {
            if (!SsrfGuard.IsPublic(ip)) continue; // belt and braces
            var socket = new Socket(ip.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(ip, ctx.DnsEndPoint.Port), ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (SocketException ex)
            {
                socket.Dispose();
                last = ex;
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }
        throw new HttpRequestException("Could not connect.", last);
    }

    public async Task<FetchResult> FetchAsync(FetchRequest req, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(_timeout);
        try
        {
            return await FetchCoreAsync(req, cts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new FetchResult(FetchOutcome.Timeout);
        }
        catch (HttpRequestException)
        {
            return new FetchResult(FetchOutcome.Failed);
        }
        catch (IOException)
        {
            return new FetchResult(FetchOutcome.Failed);
        }
    }

    private async Task<FetchResult> FetchCoreAsync(FetchRequest req, CancellationToken ct)
    {
        var uri = req.Url;
        for (var hop = 0; ; hop++)
        {
            if (!SsrfGuard.IsAllowedUri(uri)) return new FetchResult(hop == 0 ? FetchOutcome.BadUrl : FetchOutcome.Blocked);

            var addrs = await ResolveAsync(uri.IdnHost, ct);
            if (addrs is null) return new FetchResult(FetchOutcome.DnsFailed);
            // Any private answer poisons the whole set — no "try the public one" games.
            if (addrs.Length == 0 || addrs.Any(a => !SsrfGuard.IsPublic(a))) return new FetchResult(FetchOutcome.Blocked);

            using var msg = new HttpRequestMessage(HttpMethod.Get, uri);
            msg.Options.Set(VettedAddresses, addrs);
            msg.Headers.UserAgent.ParseAdd(UserAgent);
            msg.Headers.Accept.ParseAdd(req.Accept);
            msg.Headers.AcceptLanguage.ParseAdd("id,en;q=0.8");

            using var res = await _http.SendAsync(msg, HttpCompletionOption.ResponseHeadersRead, ct);
            var code = (int)res.StatusCode;
            if (code is >= 300 and < 400 && code != 304)
            {
                if (hop >= MaxRedirects) return new FetchResult(FetchOutcome.TooManyRedirects);
                var loc = res.Headers.Location;
                if (loc is null) return new FetchResult(FetchOutcome.HttpError);
                uri = loc.IsAbsoluteUri ? loc : new Uri(uri, loc);
                continue;
            }
            if (!res.IsSuccessStatusCode) return new FetchResult(FetchOutcome.HttpError);

            var media = res.Content.Headers.ContentType?.MediaType?.ToLowerInvariant();
            if (media is null || !req.AcceptMediaType(media)) return new FetchResult(FetchOutcome.WrongType);
            if (res.Content.Headers.ContentLength > req.MaxBytes && !req.AllowTruncate) return new FetchResult(FetchOutcome.TooLarge);

            var (body, truncated) = await ReadCappedAsync(res.Content, req.MaxBytes, ct);
            if (truncated && !req.AllowTruncate) return new FetchResult(FetchOutcome.TooLarge);
            return new FetchResult(FetchOutcome.Ok, uri, media, res.Content.Headers.ContentType?.CharSet, body, truncated);
        }
    }

    private async Task<IPAddress[]?> ResolveAsync(string host, CancellationToken ct)
    {
        if (IPAddress.TryParse(host.Trim('[', ']'), out var literal)) return [literal];
        try { return await _dns.ResolveAsync(host, ct); }
        catch (SocketException) { return null; }
        catch (ArgumentException) { return null; }
    }

    private static async Task<(byte[] Body, bool Truncated)> ReadCappedAsync(HttpContent content, int max, CancellationToken ct)
    {
        await using var s = await content.ReadAsStreamAsync(ct);
        var buf = new byte[81920];
        using var ms = new MemoryStream();
        int n;
        while ((n = await s.ReadAsync(buf, ct)) > 0)
        {
            var room = max - (int)ms.Length;
            if (n > room)
            {
                ms.Write(buf, 0, room);
                return (ms.ToArray(), true);
            }
            ms.Write(buf, 0, n);
        }
        return (ms.ToArray(), false);
    }

    public void Dispose() => _http.Dispose();
}
