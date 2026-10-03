using IPop.Application.Links;

namespace IPop.Web.Services;

/// Session cache for GET /api/link-preview. One request per URL (in-flight calls are shared),
/// at most <see cref="MaxConcurrent"/> requests at once so opening a long history never fires a
/// burst. 204 / any failure resolves to null (no card) and is cached too, so it isn't retried
/// on every re-render. Never throws.
public sealed class LinkPreviewService
{
    public const int MaxConcurrent = 3;
    private const int MaxEntries = 500;

    private readonly Func<string, CancellationToken, Task<LinkPreviewDto?>> _fetch;
    private readonly Dictionary<string, Task<LinkPreviewDto?>> _cache = new(StringComparer.Ordinal);
    private readonly Queue<string> _order = new();
    private readonly SemaphoreSlim _gate = new(MaxConcurrent, MaxConcurrent);

    public LinkPreviewService(AuthState auth) : this((url, ct) => FetchAsync(auth, url, ct))
        => auth.SignedOut += Clear;

    internal LinkPreviewService(Func<string, CancellationToken, Task<LinkPreviewDto?>> fetch) => _fetch = fetch;

    /// Clears every cached preview (sign-out: previews of internal links are per-viewer).
    public void Clear()
    {
        _cache.Clear();
        _order.Clear();
    }

    /// The finished preview if it is already cached, without starting a request.
    public bool TryGetCached(string url, out LinkPreviewDto? preview)
    {
        preview = null;
        if (!_cache.TryGetValue(url, out var t) || !t.IsCompletedSuccessfully) return false;
        preview = t.Result;
        return true;
    }

    public Task<LinkPreviewDto?> GetAsync(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return Task.FromResult<LinkPreviewDto?>(null);
        if (_cache.TryGetValue(url, out var existing)) return existing;
        var task = LoadAsync(url);
        _cache[url] = task;
        _order.Enqueue(url);
        while (_order.Count > MaxEntries) _cache.Remove(_order.Dequeue());
        return task;
    }

    private async Task<LinkPreviewDto?> LoadAsync(string url)
    {
        await _gate.WaitAsync();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            return await _fetch(url, cts.Token);
        }
        catch
        {
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static async Task<LinkPreviewDto?> FetchAsync(AuthState auth, string url, CancellationToken ct)
    {
        var r = await auth.GetResultAsync<LinkPreviewDto>($"/api/link-preview?url={Uri.EscapeDataString(url)}", ct);
        if (r.Status != GetStatus.Ok || r.Value is null) return null;
        var p = r.Value;
        // Nothing worth a card.
        if (p.Internal is null && string.IsNullOrWhiteSpace(p.Title) && string.IsNullOrWhiteSpace(p.Description)) return null;
        return p;
    }
}
