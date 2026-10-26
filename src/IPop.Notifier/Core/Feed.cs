using System.Net.Http.Json;
using Microsoft.AspNetCore.SignalR.Client;

namespace IPop.Notifier.Core;

/// Live feed from iPop: /hubs/notification ("notificationReceived" for every module incl. iDesk, "desktopToast" for
/// chat messages). After a (re)connect it catches up from the inbox, so a notification that arrived while the PC
/// was asleep or offline still pops up once — and every one stays in the Inbox anyway.
public sealed class Feed(IPopClient api, LocalState state) : IAsyncDisposable
{
    private HubConnection? hub;
    public event Action<Notice>? Received;
    public event Action<ToastPayload>? ChatToast;
    /// A ticket's chat was read or answered (in iPop, iDesk or here): hide its toast. Refs = ticket ids.
    public event Action<IReadOnlyList<string>>? TicketChatRead;
    /// The bell changed (read/unread/deleted): the unread count is stale.
    public event Action? ReadElsewhere;
    /// Remote help: "remoteRequest" (this PC is asked), "remoteAnswer" (to the helper), "remoteEnded" (either side).
    public event Action<string, System.Text.Json.Nodes.JsonNode>? Remote;
    public event Action<bool>? ConnectionChanged;
    /// A Notifier release was published (or released to all): check for the update now.
    public event Action? ReleasePublished;
    public bool Connected => hub?.State == HubConnectionState.Connected;

    public async Task StartAsync(CancellationToken ct)
    {
        await StopAsync();
        hub = new HubConnectionBuilder()
            .WithUrl(new Uri(api.Server, "hubs/notification"), o => o.AccessTokenProvider = () => api.AccessTokenAsync())
            .WithAutomaticReconnect(new ForeverRetry())
            .Build();
        hub.On<Delivery>("notificationReceived", d =>
        {
            // Read/acknowledged elsewhere (the state echo of a read) is not a new notification.
            if (d.Status is "read" or "acknowledged") return;
            QaLog("notificationReceived " + d.EventType + " " + d.TargetUrl);
            Received?.Invoke(Notice.From(d));
        });
        hub.On<ToastPayload>("desktopToast", t => { QaLog("desktopToast " + t.Kind + " " + t.Url); ChatToast?.Invoke(t); });
        hub.On<List<string>>("ticketChatRead", refs => { QaLog("ticketChatRead " + string.Join(',', refs)); TicketChatRead?.Invoke(refs); });
        // Read, unread, deleted or "mark all read" anywhere (iPop tab, phone, this Inbox): only the count changes.
        hub.On("notificationsChanged", () => ReadElsewhere?.Invoke());
        hub.On<System.Text.Json.JsonElement>("notifierUpdate", _ => { Log.Info("hub", "notifierUpdate"); ReleasePublished?.Invoke(); });
        foreach (var ev in new[] { "remoteRequest", "remoteAnswer", "remoteEnded" })
            hub.On<System.Text.Json.JsonElement>(ev, e => { Log.Info("hub", ev); if (System.Text.Json.Nodes.JsonNode.Parse(e.GetRawText()) is { } n) Remote?.Invoke(ev, n); });
        hub.Reconnected += async _ => { ConnectionChanged?.Invoke(true); await CatchUpAsync(CancellationToken.None); };
        hub.Reconnecting += _ => { ConnectionChanged?.Invoke(false); return Task.CompletedTask; };
        var mine = hub;
        hub.Closed += async _ =>
        {
            if (!ReferenceEquals(hub, mine)) return; // an old connection we replaced
            ConnectionChanged?.Invoke(false);
            // Closed only after the retry policy gives up or the server refuses; try again quietly.
            await Task.Delay(TimeSpan.FromSeconds(30));
            if (api.IsSignedIn) { try { await StartAsync(CancellationToken.None); } catch { } }
        };

        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await hub.StartAsync(ct);
                break;
            }
            catch when (!ct.IsCancellationRequested && api.IsSignedIn)
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(60, 5 * (attempt + 1))), ct);
            }
        }
        ConnectionChanged?.Invoke(true);
        Log.Info("hub", "connected to " + api.Server);
        await CatchUpAsync(ct);
    }

    /// While the PC was off or offline: everything still unread that arrived after the app was last running and that
    /// this PC never showed, oldest first, any age. The very first run on a PC only records "now" (no history flood;
    /// it is all in the Inbox). Read elsewhere = not unread any more = not shown.
    public event Action<IReadOnlyList<Notice>>? Backlog;

    public async Task CatchUpAsync(CancellationToken ct)
    {
        if (state.LastShownAt is null)
        {
            state.LastShownAt = DateTime.UtcNow;
            state.Save();
            return;
        }
        var since = state.LastShownAt.Value;
        var items = new List<InboxItem>();
        // Newest first from the server: page until the items are older than the last one this PC saw.
        for (var page = 1; page <= 20; page++)
        {
            var p = await InboxAsync("unread", page, 100, ct);
            if (p is null) return;
            items.AddRange(p.Items);
            if (p.Items.Count < 100 || p.Items.Min(i => i.At.ToUniversalTime()) <= since) break;
        }
        var pending = items.Where(i => i.At.ToUniversalTime() > since && !state.Shown.Contains(i.Id))
            .OrderBy(i => i.At).Select(i => Notice.From(i)).ToList();
        if (pending.Count > 0) Backlog?.Invoke(pending);
        // Caught up to now: the next gap starts here (live ones move it forward too, in PopupManager.Show).
        var newest = items.Count > 0 ? items.Max(i => i.At.ToUniversalTime()) : since;
        if (newest > since) { state.LastShownAt = newest; state.Save(); }
    }

    public async Task<InboxPage?> InboxAsync(string? status, int page, int size, CancellationToken ct = default)
    {
        try
        {
            using var res = await api.SendAsync(HttpMethod.Get, $"api/notifications/v1/inbox?page={page}&pageSize={size}" + (status is null ? "" : "&status=" + status), null, ct);
            return res.IsSuccessStatusCode ? await res.Content.ReadFromJsonAsync<InboxPage>(IPopClient.Json, ct) : null;
        }
        catch { return null; }
    }

    public async Task StopAsync()
    {
        if (hub is null) return;
        var h = hub;
        hub = null;
        try { await h.DisposeAsync(); } catch { }
    }

    public ValueTask DisposeAsync() => new(StopAsync());

    /// Debug QA runs only: what arrived, so a test can check the feed (never in Release, never content).
    [System.Diagnostics.Conditional("DEBUG")]
    private static void QaLog(string line)
    {
        if (Environment.GetEnvironmentVariable("IPOP_NOTIFIER_QA_DIR") is not { Length: > 0 }) return;
        try { File.AppendAllText(Path.Combine(SessionStore.Folder, "qa-events.log"), $"{DateTime.Now:HH:mm:ss} {line}{Environment.NewLine}"); } catch { }
    }

    private sealed class ForeverRetry : IRetryPolicy
    {
        public TimeSpan? NextRetryDelay(RetryContext ctx) => TimeSpan.FromSeconds(Math.Min(60, 2 + ctx.PreviousRetryCount * 5));
    }
}
