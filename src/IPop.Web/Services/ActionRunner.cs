using System.Net.Http.Json;

namespace IPop.Web.Services;

public sealed record ActionOutcome(bool Success, int? StatusCode, string? ErrorCode, string? Message);

public sealed class ActionRunner(AuthState auth, ToastService toasts)
{
    private readonly HashSet<string> _busy = [];
    public bool IsBusy(string key) => _busy.Contains(key);

    public async Task<ActionOutcome> RunAsync(string actionKey, HttpMethod method, string url, object? body = null, TimeSpan? timeout = null, Func<Task>? onSuccess = null, Action? rollback = null, string? successToast = null)
    {
        if (_busy.Contains(actionKey)) return new ActionOutcome(false, null, "busy", null);
        _busy.Add(actionKey);
        try
        {
            using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(10));
            HttpResponseMessage res;
            try
            {
                // AuthState.SendAsync has no cancellation overload — use timeout via Task.WhenAny if needed; for now just call directly
                res = await auth.SendAsync(method, url, body);
            }
            catch (OperationCanceledException)
            {
                toasts.Show("Request timed out.", ToastKind.Error);
                rollback?.Invoke();
                return new ActionOutcome(false, null, "timeout", "Request timed out");
            }
            catch (Exception)
            {
                toasts.Show("Network error.", ToastKind.Error);
                rollback?.Invoke();
                return new ActionOutcome(false, null, "network", "Network error");
            }
            if (res.IsSuccessStatusCode)
            {
                if (onSuccess != null) await onSuccess();
                if (successToast != null) toasts.Show(successToast, ToastKind.Success);
                return new ActionOutcome(true, (int)res.StatusCode, null, null);
            }
            var msg = await TryGetMessage(res) ?? ((int)res.StatusCode).ToString();
            var code = await TryGetCode(res);
            if (msg.Length > 200) msg = msg[..200];
            toasts.Show(msg, ToastKind.Error);
            rollback?.Invoke();
            return new ActionOutcome(false, (int)res.StatusCode, code, msg);
        }
        finally
        {
            _busy.Remove(actionKey);
        }
    }

    private static async Task<string?> TryGetMessage(HttpResponseMessage res)
    {
        try { var d = await res.Content.ReadFromJsonAsync<Dictionary<string, string>>(); if (d != null && d.TryGetValue("message", out var m) && !string.IsNullOrWhiteSpace(m)) return m; } catch { }
        return null;
    }
    private static async Task<string?> TryGetCode(HttpResponseMessage res)
    {
        try { var d = await res.Content.ReadFromJsonAsync<Dictionary<string, string>>(); if (d != null && d.TryGetValue("code", out var c)) return c; } catch { }
        return null;
    }
}
