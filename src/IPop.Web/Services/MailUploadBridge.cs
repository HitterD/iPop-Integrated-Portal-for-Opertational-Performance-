using Microsoft.JSInterop;

namespace IPop.Web.Services;

/// Mail attachments up to <see cref="MaxFileBytes"/> each. The browser uploads them itself
/// (ipop.mailUploadInput, one XHR per file) so a big file is streamed from disk rather than copied
/// through Blazor's memory; this object gives it the bearer token and receives byte progress.
public sealed class MailUploadBridge(AuthState auth, HttpClient http, IJSRuntime js) : IDisposable
{
    /// Mirrors MailEndpoints.MaxUploadFiles / MaxUploadFileBytes on the API.
    public const int MaxFiles = 5;
    public const long MaxFileBytes = 500L * 1024 * 1024;

    public sealed record Result(MailAttachment[] Files, string[] Errors);

    private DotNetObjectReference<MailUploadBridge>? self;
    private Action<long, long>? progress;

    /// Uploads the files currently selected in the InputFile inside <paramref name="inputOrWrapper"/>
    /// (an element or a CSS selector). <paramref name="onProgress"/> gets (sent, total) bytes.
    public async Task<Result> UploadInputAsync(object inputOrWrapper, Action<long, long>? onProgress = null)
    {
        progress = onProgress;
        self ??= DotNetObjectReference.Create(this);
        var url = new Uri(http.BaseAddress!, "/api/mail/attachments/upload").ToString();
        var r = await js.InvokeAsync<Result?>("ipop.mailUploadInput", TimeSpan.FromHours(2),
            inputOrWrapper, url, self, MaxFiles, MaxFileBytes);
        return r ?? new Result([], ["Upload failed."]);
    }

    [JSInvokable]
    public async Task<string?> Token(bool refresh)
    {
        if (refresh) await auth.TryRestoreAsync();
        return auth.AccessToken;
    }

    [JSInvokable]
    public void Progress(long sent, long total) => progress?.Invoke(sent, total);

    public static string Percent(long sent, long total) =>
        total <= 0 ? "" : $"{Math.Clamp(sent * 100 / total, 0, 100)}%";

    public void Dispose() => self?.Dispose();
}
