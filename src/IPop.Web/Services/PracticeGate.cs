namespace IPop.Web.Services;

/// Sits under the app's HttpClient. While practice mode is on, PracticeSandbox answers the
/// module's API calls here so no page needs to know about it; otherwise requests pass through.
public sealed class PracticeGate : DelegatingHandler
{
    public PracticeGate() : base(new HttpClientHandler()) { }

    public Func<HttpRequestMessage, Task<HttpResponseMessage?>>? Intercept { get; set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (Intercept is { } intercept && await intercept(request) is { } fake)
        {
            fake.RequestMessage = request;
            return fake;
        }
        return await base.SendAsync(request, ct);
    }
}
