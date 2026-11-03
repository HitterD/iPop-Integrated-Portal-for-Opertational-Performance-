using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using IPop.Application.Calls;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.JSInterop;

namespace IPop.Web.Services;

/// <summary>
/// Calls state and WebRTC audio controller for Blazor WASM.
/// Manages the /hubs/calls SignalR connection, the call room lifecycle, the peer-to-peer
/// WebRTC audio (ICE servers from /api/calls/config) and the synthesized call tones.
/// </summary>
public partial class CallState : IAsyncDisposable
{
    private readonly AuthState _auth;
    private readonly HttpClient _http;
    private readonly IJSRuntime _js;
    private HubConnection? _hub;
    private DotNetObjectReference<CallState>? _selfRef;
    private Guid? _webRtcActiveCallId;
    // 1:1 media runs only on the device that placed or answered the call; the same
    // account's other tabs/devices stand down, or they would answer the offer twice.
    private Guid? _ownedCallId;
    // The remote hub connection this call negotiates with; offers/answers from any other
    // connection of the peer's account (a second tab, a stale build) are ignored.
    private string? _peerConnection;
    private Guid? _peerCallId;
    private Guid? _elsewhereCallId;
    private readonly SemaphoreSlim _createGate = new(1, 1);
    private JsonElement? _iceServers;
    // TURN credentials expire (12 h, CallEndpoints.TurnCredentialLifetime): re-fetch before a call
    // when the last fetch is older than this, so a tab left open overnight still connects via TURN.
    private DateTime _configLoadedAt = DateTime.MinValue;
    private static readonly TimeSpan ConfigMaxAge = TimeSpan.FromHours(6);

    public CallState(AuthState auth, HttpClient http, IJSRuntime js)
    {
        _auth = auth;
        _http = http;
        _js = js;
        _auth.SignedOut += () => _ = SignOutResetAsync();
    }

    /// Sign-out (the call itself was already ended by <see cref="EndForSignOutAsync"/>): silence
    /// tones and media, forget the call, and drop the hub that still authenticates as the old account.
    private async Task SignOutResetAsync()
    {
        try
        {
            await _js.InvokeVoidAsync("ipopAudio.stopAudioTones");
            await _js.InvokeVoidAsync("ipopAudio.endCall");
            await _js.InvokeVoidAsync("ipopGroupCall.leave");
        }
        catch (JSException) { }
        try { await _js.InvokeVoidAsync("sessionStorage.removeItem", OwnedCallKey); } catch (JSException) { }
        Current = null;
        Waiting = null;
        WebRtcState = null;
        Quality = null;
        StopHeartbeat();
        _webRtcActiveCallId = null;
        _ownedCallId = null;
        _peerConnection = null;
        _peerCallId = null;
        _elsewhereCallId = null;
        _groupCalls.Clear();
        _meetingId = null;
        _groupMediaCallId = null;
        await DisconnectAsync();
    }

    /// Before signing out: hang up (or decline) so the other side isn't left talking to nobody.
    public async Task EndForSignOutAsync()
    {
        if (Current is null) return;
        try
        {
            if (IsIncomingRing) await DeclineCurrentAsync();
            else if (IsOutgoingRing) await CancelCurrentAsync();
            else await EndCurrentAsync();
        }
        catch { /* best effort: the server's ring/idle timeouts close it otherwise */ }
    }

    /// The user's current live call (null = none ringing/active).
    public CallRoomDto? Current { get; private set; }

    public bool IsConnected => _hub?.State == HubConnectionState.Connected;
    public string? WebRtcState { get; private set; }
    public string? LastError { get; private set; }

    public CallParticipantDto? ActiveMe =>
        Current?.Participants.FirstOrDefault(p => p.Nik == _auth.Me?.Nik);

    public CallParticipantDto? RemotePeer =>
        Current?.Participants.FirstOrDefault(p => p.Nik != _auth.Me?.Nik);

    public bool IsIncomingRing =>
        (Current?.Status == CallStatus.Ringing || (IsGroup && Current?.Status == CallStatus.Active))
        && ActiveMe?.Status == CallParticipantStatus.Invited;

    public bool IsOutgoingRing =>
        !IsGroup && Current?.Status == CallStatus.Ringing && ActiveMe is not null && Current.CreatorId == ActiveMe.UserId;

    public bool IsActiveCall =>
        Current?.Status == CallStatus.Active;

    public bool HasLiveCall =>
        Current is not null && (Current.Status == CallStatus.Ringing || Current.Status == CallStatus.Active);

    public string? LastEndReason { get; private set; }

    /// Call waiting: a 1:1 call ringing for me while I'm already in a call (1:1, group or meeting).
    /// Shown as a banner in the call window; the current call is never replaced or cut.
    public CallRoomDto? Waiting { get; private set; }

    /// Measured link of the 1:1 call ("good" | "fair" | "poor"), null until known.
    public string? Quality { get; private set; }

    /// Seconds an unanswered ring lasts before the server marks it missed.
    public int RingTimeoutSeconds { get; private set; } = 45;

    /// Result of ipopAudio.checkSupport(); null until the hub connects.
    public CallSupport? Support { get; private set; }

    /// The microphone cannot be used here (plain http on a LAN address).
    public bool NeedsHttps => Support is { Secure: false };

    public event Action? StateChanged;

    /// Raised (and awaited) right before a 1:1, group or scheduled-meeting call starts using the microphone:
    /// placing a call, accepting one, joining a group call or meeting. MeetingRoomState listens and leaves its
    /// room first, so the two never run side by side. (An event, not a dependency: MeetingRoomState already
    /// depends on CallState.)
    public event Func<Task>? MediaStarting;

    private async Task RaiseMediaStartingAsync()
    {
        if (MediaStarting is not { } handlers) return;
        foreach (var h in handlers.GetInvocationList().Cast<Func<Task>>()) await h();
    }

    /// Raised once a call closes (any reason) so history lists can refresh.
    public event Action? CallEnded;

    public async Task ConnectAsync()
    {
        if (_hub is not null || !_auth.IsAuthenticated || _auth.IsSuspended) return;
        var hubUrl = new Uri(new Uri(_http.BaseAddress!, "/"), "hubs/calls");
        _hub = new HubConnectionBuilder()
            .WithUrl(hubUrl, o => o.AccessTokenProvider = () => Task.FromResult(_auth.AccessToken))
            .WithAutomaticReconnect()
            .Build();

        _selfRef ??= DotNetObjectReference.Create(this);
        try { await _js.InvokeVoidAsync("ipopNative.bindCalls", _selfRef); } catch (JSException) { }

        _hub.On<CallRoomDto>("callStateChanged", async room =>
        {
            await HandleCallRoomChangedAsync(room);
        });

        _hub.On("callEnded", async () =>
        {
            if (Current is null) return;
            // A scheduled meeting has no server call room; a call-room "ended" is never about it.
            if (IsMeeting) return;
            await HandleCallEndedAsync();
        });

        _hub.On<WebRtcSignalDto>("webrtcSignalReceived", async signal =>
        {
            // Only the device that owns the call negotiates media; the account's other
            // tabs would otherwise answer the same offer and break the caller's session.
            if (Current is not null && Current.Id == signal.CallId && _ownedCallId == signal.CallId)
            {
                if (_peerCallId != signal.CallId) { _peerCallId = signal.CallId; _peerConnection = null; }
                _selfRef ??= DotNetObjectReference.Create(this);
                if (signal.Type == "rejoin")
                {
                    // The peer reloaded or reconnected: whatever tab it is now is the peer, and
                    // our old peer connection is dead. Rebuild ours and answer its next offer.
                    // Both sides came back at once (an API restart): both sent "rejoin" and both
                    // would wait for an offer. The tab with the lower id offers, the other answers.
                    _peerConnection = signal.From;
                    WebRtcState = "reconnecting";
                    StateChanged?.Invoke();
                    if (_rejoinSentFor == signal.CallId && string.CompareOrdinal(_tabId, StripTab(signal.From)) < 0)
                    {
                        _rejoinSentFor = null;
                        await _js.InvokeVoidAsync("ipopAudio.endCall");
                        await _js.InvokeVoidAsync("ipopAudio.initCall", signal.CallId, true, _selfRef, _iceServers);
                        if (ActiveMe?.IsMuted == true) await _js.InvokeVoidAsync("ipopAudio.setMute", true);
                        return;
                    }
                    _rejoinSentFor = null;
                    await _js.InvokeVoidAsync("ipopAudio.resetPeer", signal.CallId);
                    if (ActiveMe?.IsMuted == true) await _js.InvokeVoidAsync("ipopAudio.setMute", true);
                    return;
                }
                if (signal.From is not null)
                {
                    if (_peerConnection is null && signal.Type is "offer" or "answer") _peerConnection = signal.From;
                    else if (_peerConnection is not null && _peerConnection != signal.From) return;
                }
                await _js.InvokeVoidAsync("ipopAudio.handleSignal", signal.CallId, signal.Type, signal.Payload);
            }
        });

        _hub.Reconnecting += ex =>
        {
            // Only signaling dropped: a 1:1 call's peer-to-peer audio usually keeps flowing, so the
            // badge follows the media (OnConnectionStateChanged), not the hub.
            if (IsActiveCall && IsGroup) WebRtcState = "reconnecting";
            StateChanged?.Invoke();
            return Task.CompletedTask;
        };
        _hub.Reconnected += connId => { _ = RefreshAsync(rejoin: true); return Task.CompletedTask; };
        // Auto-reconnect gave up: keep retrying so incoming calls still ring without a refresh.
        var hub = _hub;
        _hub.Closed += ex =>
        {
            StateChanged?.Invoke();
            _ = HubRetry.RunAsync(hub, () => _hub == hub && _auth.IsAuthenticated && !_auth.IsSuspended, () => _ = RefreshAsync(rejoin: true));
            return Task.CompletedTask;
        };

        try
        {
            await LoadConfigAsync();
            _tabId ??= await LoadTabIdAsync();
            await _hub.StartAsync();
            await RefreshAsync(rejoin: true);
        }
        catch
        {
            StateChanged?.Invoke();
            _ = HubRetry.RunAsync(hub, () => _hub == hub && _auth.IsAuthenticated && !_auth.IsSuspended,
                () => _ = RefreshAsync(rejoin: true));
        }
    }

    public Task RefreshAsync() => RefreshAsync(rejoin: false);

    /// rejoin: the hub (re)connected. A call this tab was in before a refresh or a network drop
    /// is taken up again: the same LiveKit room with a fresh token, or a new P2P peer connection.
    private async Task RefreshAsync(bool rejoin)
    {
        var ringing = await _auth.GetAsync<CallRoomDto?>("/api/calls/ringing");
        if (ringing is not null && rejoin && Current is null && !IsMeeting
            && await _auth.GetAsync<CallRoomDto?>("/api/calls/active") is { } mine && mine.Id != ringing.Id)
        {
            // Reloaded mid-call while someone else rings: back into my call, the ring waits.
            await RejoinAsync(mine);
            await HandleCallRoomChangedAsync(ringing);
        }
        else if (ringing is not null)
        {
            await HandleCallRoomChangedAsync(ringing);
        }
        else if (IsMeeting)
        {
            // A meeting has no server call room to re-fetch; LiveKit reconnects on its own.
            StateChanged?.Invoke();
        }
        else if (Current is null)
        {
            if (rejoin && await _auth.GetAsync<CallRoomDto?>("/api/calls/active") is { } active)
                await RejoinAsync(active);
            else
                StateChanged?.Invoke();
        }
        else
        {
            // /ringing only reports calls where I am invited. For my outgoing ring (or
            // an active call), re-fetch the room rather than erasing it after reconnect.
            try
            {
                using var res = await _auth.SendAsync(HttpMethod.Get, $"/api/calls/{Current.Id}");
                if (res.IsSuccessStatusCode)
                {
                    var room = await res.Content.ReadFromJsonAsync<CallRoomDto>();
                    // Same tab, new hub connection after a drop: renegotiate the media.
                    if (room is not null && rejoin && room.Status == CallStatus.Active && Current?.Id == room.Id
                        && (IsGroup ? _groupMediaCallId == room.Id : _ownedCallId == room.Id))
                        await RejoinAsync(room);
                    else if (room is not null) await HandleCallRoomChangedAsync(room);
                }
                else if (res.StatusCode == HttpStatusCode.NotFound)
                    await HandleCallEndedAsync();
            }
            catch (HttpRequestException)
            {
                // Retain the current state while transport is unavailable.
            }
        }
    }

    /// ICE servers (STUN + LAN TURN, with credentials) come from the API with the JWT —
    /// a relative fetch from JS would hit the Web host, not the API.
    private async Task LoadConfigAsync()
    {
        try
        {
            Support = await _js.InvokeAsync<CallSupport>("ipopAudio.checkSupport");
            var cfg = await _auth.GetAsync<CallConfigDto>("/api/calls/config");
            if (cfg is not null)
            {
                _iceServers = cfg.IceServers;
                _configLoadedAt = DateTime.UtcNow;
                if (cfg.RingTimeoutSeconds > 0) RingTimeoutSeconds = cfg.RingTimeoutSeconds;
                await _js.InvokeVoidAsync("ipopAudio.configure", cfg.IceServers);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or JSException or JsonException)
        {
            // Fall back to the JS default (public STUN); LAN calls still work without TURN.
        }
    }

    /// Fresh ICE servers before media starts when the cached TURN credential is getting old.
    private async Task EnsureFreshConfigAsync()
    {
        if (DateTime.UtcNow - _configLoadedAt < ConfigMaxAge) return;
        await LoadConfigAsync();
    }

    private static string? StripTab(string? from) => from is { } f && f.StartsWith("tab:", StringComparison.Ordinal) ? f[4..] : from;

    /// The call I'm in and the ringing one are different: the new one waits (banner), never replaces.
    private bool IsWaitingCandidate(CallRoomDto room)
    {
        if (Current is null || Current.Id == room.Id) return false;
        if (!(HasLiveCall && (IsActiveCall || IsOutgoingRing || IsInGroupCall || IsMeeting))) return false;
        var mine = room.Participants.FirstOrDefault(p => p.Nik == _auth.Me?.Nik);
        return mine?.Status == CallParticipantStatus.Invited;
    }

    private async Task HandleWaitingAsync(CallRoomDto room)
    {
        if (room.Status == CallStatus.Ringing)
        {
            var isNew = Waiting?.Id != room.Id;
            Waiting = room;
            // A soft beep once, not the full ringtone over the conversation.
            if (isNew && !_auth.IsBusy) { try { await _js.InvokeVoidAsync("ipopSound.play", "tick"); } catch (JSException) { } }
        }
        else if (Waiting?.Id == room.Id) Waiting = null;   // caller gave up, timed out, or answered elsewhere
        StateChanged?.Invoke();
    }

    private async Task HandleCallRoomChangedAsync(CallRoomDto room)
    {
        // Call waiting: a 1:1 ring (or its end) while I'm in another call.
        if (room.Kind == CallKind.User && (Waiting?.Id == room.Id || IsWaitingCandidate(room)))
        {
            await HandleWaitingAsync(room);
            return;
        }
        // Someone added a person to my 1:1 call: it moved to LiveKit for good (never back to P2P).
        if (room.Kind == CallKind.AdHoc && Current?.Id == room.Id && Current.Kind == CallKind.User)
        {
            await UpgradeToLiveKitAsync(room);
            return;
        }
        // Group and ad-hoc calls have their own flow (LiveKit media, per-member decline/leave, chat banners).
        if (CallKind.IsLiveKit(room.Kind))
        {
            // Another device of mine owns this call; only a fresh ring for me is news here.
            if (_elsewhereCallId == room.Id
                && room.Participants.FirstOrDefault(p => p.Nik == _auth.Me?.Nik)?.Status != CallParticipantStatus.Invited)
                return;
            await HandleGroupRoomAsync(room);
            return;
        }
        // Busy in a group call or meeting: anything else about another 1:1 call is not for the screen.
        if (IsGroup && HasLiveCall && Current!.Id != room.Id) return;

        // A closed room is only news for the call on screen: the hub echo of our own
        // hang-up (already handled) or a stale call must not end/toast a second time.
        if (room.Status is CallStatus.Ended or CallStatus.Declined or CallStatus.Missed && Current?.Id != room.Id)
            return;

        if (_elsewhereCallId == room.Id) return;
        var mine = room.Participants.FirstOrDefault(p => p.Nik == _auth.Me?.Nik);
        bool mineIsCaller = mine is not null && room.CreatorId == mine.UserId;
        if (_ownedCallId != room.Id)
        {
            // Placing a call: the hub echo can beat the POST response, so claim it here.
            if (mineIsCaller && room.Status == CallStatus.Ringing && _createGate.CurrentCount == 0)
                await RememberOwnedCallAsync(room.Id);
            else if (room.Status == CallStatus.Active || (mineIsCaller && room.Status == CallStatus.Ringing))
            {
                await StandDownAsync(room, mineIsCaller);
                return;
            }
        }

        Current = room;
        LastError = null;

        var me = ActiveMe;
        bool isCaller = me is not null && room.CreatorId == me.UserId;

        if (room.Status == CallStatus.Ringing)
        {
            if (isCaller)
            {
                await _js.InvokeVoidAsync("ipopAudio.playDialTone");
                StartHeartbeat(room.Id);
            }
            else if (me?.Status == CallParticipantStatus.Invited && !_auth.IsBusy)
            {
                // Busy: the incoming call still shows, it just does not ring.
                await _js.InvokeVoidAsync("ipopAudio.playRingtone");
            }
        }
        else if (room.Status == CallStatus.Active)
        {
            await _js.InvokeVoidAsync("ipopAudio.stopAudioTones");

            if (_webRtcActiveCallId != room.Id)
            {
                _webRtcActiveCallId = room.Id;
                // Native Android app: "Sedang menelepon" notification with hang up / mute (no-op in browsers).
                try { await _js.InvokeVoidAsync("ipopNative.callStarted", RemotePeer?.Name ?? room.Title ?? "iPop"); } catch (JSException) { }
                _selfRef ??= DotNetObjectReference.Create(this);
                WebRtcState = "connecting";
                Quality = null;
                await EnsureFreshConfigAsync();
                await _js.InvokeVoidAsync("ipopAudio.initCall", room.Id, isCaller, _selfRef, _iceServers);
                StartHeartbeat(room.Id);
            }
        }
        else if (room.Status is CallStatus.Ended or CallStatus.Declined or CallStatus.Missed)
        {
            if (room.Status == CallStatus.Declined && !isCaller && _decliningCallId != room.Id)
            {
                await DismissQuietlyAsync();
                return;
            }
            await HandleCallEndedAsync(EndReason(room, isCaller));
            return;
        }

        StateChanged?.Invoke();
    }

    private string EndReason(CallRoomDto room, bool isCaller) => room.Status switch
    {
        CallStatus.Declined when room.EndReason == "busy" && isCaller =>
            $"{FirstNameOf(room.Participants.FirstOrDefault(p => p.UserId != room.CreatorId)?.Name ?? "They")} is on another call",
        CallStatus.Declined => "Call declined",
        CallStatus.Missed when isCaller => room.EndedAt is { } end && end - room.StartedAt >= TimeSpan.FromSeconds(RingTimeoutSeconds - 1)
            ? "No answer" : "Call canceled",
        CallStatus.Missed => "Missed call",
        _ => room.EstablishedAt is { } est && room.EndedAt is { } ended
            ? $"Call ended · {FormatDuration(ended - est)}"
            : "Call ended",
    };

    /// Another device of this account placed or answered the call: close it here quietly.
    private async Task StandDownAsync(CallRoomDto room, bool isCaller)
    {
        // A reload of the owning tab keeps ownership (sessionStorage is per tab).
        try
        {
            var saved = await _js.InvokeAsync<string?>("sessionStorage.getItem", OwnedCallKey);
            if (Guid.TryParse(saved, out var id) && id == room.Id)
            {
                _ownedCallId = id;
                await HandleCallRoomChangedAsync(room);
                return;
            }
        }
        catch (JSException) { }

        _elsewhereCallId = room.Id;
        var wasShowing = Current?.Id == room.Id;
        if (wasShowing)
        {
            Current = null;
            WebRtcState = null;
            StopHeartbeat();
            _webRtcActiveCallId = null;
            try
            {
                await _js.InvokeVoidAsync("ipopAudio.stopAudioTones");
                await _js.InvokeVoidAsync("ipopAudio.endCall");
                await _js.InvokeVoidAsync("ipopNative.callEnded");
            }
            catch (JSException) { }
            LastEndReason = isCaller ? "Call continued on another device" : "Call answered on another device";
            StateChanged?.Invoke();
        }
    }

    private const string OwnedCallKey = "ipop.ownedCall";
    private const string TabIdKey = "ipop.callTab";

    /// This tab's id for P2P signaling, kept in sessionStorage so a refreshed tab is the same peer.
    private string? _tabId;

    private async Task<string> LoadTabIdAsync()
    {
        try
        {
            var id = await _js.InvokeAsync<string?>("sessionStorage.getItem", TabIdKey);
            if (string.IsNullOrEmpty(id))
            {
                id = Guid.NewGuid().ToString("N");
                await _js.InvokeVoidAsync("sessionStorage.setItem", TabIdKey, id);
            }
            return id;
        }
        catch (JSException) { return Guid.NewGuid().ToString("N"); }
    }

    /// Takes up a call this tab was in before a refresh or a dropped connection. Another device
    /// of mine that owns the call keeps it: sessionStorage is per tab, so only the tab that held
    /// the call before the refresh claims it back.
    private async Task RejoinAsync(CallRoomDto room)
    {
        if (CallKind.IsLiveKit(room.Kind))
        {
            await RejoinGroupAsync(room);
            return;
        }
        if (room.Status != CallStatus.Active)
        {
            await HandleCallRoomChangedAsync(room);   // my own outgoing ring: show it again
            return;
        }
        string? saved = null;
        try { saved = await _js.InvokeAsync<string?>("sessionStorage.getItem", OwnedCallKey); } catch (JSException) { }
        if (_ownedCallId != room.Id && !(Guid.TryParse(saved, out var owned) && owned == room.Id))
        {
            await HandleCallRoomChangedAsync(room);   // another device's call: stand down as before
            return;
        }
        _ownedCallId = room.Id;
        _elsewhereCallId = null;
        var wasMuted = room.Participants.FirstOrDefault(p => p.Nik == _auth.Me?.Nik)?.IsMuted == true;
        // Only the hub blipped (same tab, audio still flowing peer to peer): keep the media as it is.
        try
        {
            if (Current?.Id == room.Id && _webRtcActiveCallId == room.Id
                && await _js.InvokeAsync<bool>("ipopAudio.isHealthy", room.Id))
            {
                Current = room;
                StartHeartbeat(room.Id);
                StateChanged?.Invoke();
                return;
            }
        }
        catch (JSException) { }
        Current = room;
        LastError = null;
        LastEndReason = null;
        WebRtcState = "reconnecting";
        StateChanged?.Invoke();
        try
        {
            await _js.InvokeVoidAsync("ipopAudio.stopAudioTones");
            await _js.InvokeVoidAsync("ipopAudio.endCall");   // a dead peer connection from before the drop
            await EnsureFreshConfigAsync();
            // Tell the peer first, so it drops its dead connection and waits for our offer.
            _rejoinSentFor = room.Id;
            if (_hub?.State == HubConnectionState.Connected)
                await _hub.SendAsync("SendWebRtcSignal", new WebRtcSignalDto(room.Id, "rejoin", "", Tab: _tabId));
            _peerCallId = room.Id;
            _peerConnection = null;
            _webRtcActiveCallId = room.Id;
            _selfRef ??= DotNetObjectReference.Create(this);
            // Whoever comes back makes the offer, whichever side placed the call.
            await _js.InvokeVoidAsync("ipopAudio.initCall", room.Id, true, _selfRef, _iceServers);
            if (wasMuted) await _js.InvokeVoidAsync("ipopAudio.setMute", true);
            StartHeartbeat(room.Id);
        }
        catch (JSException ex)
        {
            WebRtcState = "failed";
            LastError = "Couldn't reconnect the call: " + ex.Message;
        }
        StateChanged?.Invoke();
    }

    /// Set while my "rejoin" is out: if the peer's "rejoin" crosses it, the lower tab id offers.
    private Guid? _rejoinSentFor;

    private async Task RememberOwnedCallAsync(Guid callId)
    {
        _ownedCallId = callId;
        try { await _js.InvokeVoidAsync("sessionStorage.setItem", OwnedCallKey, callId.ToString()); }
        catch (JSException) { }
    }

    public static string FormatDuration(TimeSpan d) =>
        d.TotalHours >= 1 ? $"{(int)d.TotalHours}:{d.Minutes:D2}:{d.Seconds:D2}" : $"{d.Minutes:D2}:{d.Seconds:D2}";

    private async Task HandleCallEndedAsync(string? reason = null)
    {
        var hadCall = Current is not null;
        if (IsGroup) await LeaveGroupMediaAsync();
        _webRtcActiveCallId = null;
        LastEndReason = reason ?? (Current?.Status switch
        {
            CallStatus.Declined => "Call declined",
            CallStatus.Missed => "No answer",
            _ => "Call ended"
        });
        Current = null;
        WebRtcState = null;
        Quality = null;
        StopHeartbeat();
        try
        {
            await _js.InvokeVoidAsync("ipopAudio.endCall");
            if (hadCall) await _js.InvokeVoidAsync("ipopAudio.playEndTone");
            await _js.InvokeVoidAsync("ipopNative.callEnded");
        }
        catch (JSException) { }
        StateChanged?.Invoke();
        if (hadCall) CallEnded?.Invoke();
        await PromoteWaitingAsync();
    }

    /// The call I was in ended while another one was waiting: it rings normally now.
    private async Task PromoteWaitingAsync()
    {
        if (Waiting is not { } next || Current is not null) return;
        Waiting = null;
        try
        {
            using var res = await _auth.SendAsync(HttpMethod.Get, $"/api/calls/{next.Id}");
            if (res.IsSuccessStatusCode && await res.Content.ReadFromJsonAsync<CallRoomDto>() is { Status: CallStatus.Ringing } fresh)
                await HandleCallRoomChangedAsync(fresh);
            else StateChanged?.Invoke();
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException) { StateChanged?.Invoke(); }
    }

    public void DismissEndReason()
    {
        LastEndReason = null;
        StateChanged?.Invoke();
    }

    /// Set while practising: returns true when the call was simulated instead of placed.
    public Func<string, bool>? PracticeCall { get; set; }

    public async Task<CallRoomDto?> CreateAsync(string peerNik)
    {
        if (PracticeCall?.Invoke(peerNik) == true) return null;
        if (HasLiveCall)
        {
            LastError = "Another call is still in progress.";
            StateChanged?.Invoke();
            return null;
        }
        if (!await _createGate.WaitAsync(0)) return null;
        try
        {
            await RaiseMediaStartingAsync();
            await EnsureFreshConfigAsync();
            // Pre-call check: ask for the microphone BEFORE ringing anyone, so a blocked mic
            // is explained here instead of producing a silent call on the other side.
            var mic = await _js.InvokeAsync<MicCheck>("ipopAudio.prepareMic", (string?)null);
            if (!mic.Ok && mic.Code != "no-device")
            {
                await ReleaseMicAsync();
                LastError = mic.Message;
                StateChanged?.Invoke();
                return null;
            }
            LastError = mic.Ok ? null : mic.Message;

            using var res = await _auth.SendAsync(HttpMethod.Post, "/api/calls", new { peerNik });
            if (!res.IsSuccessStatusCode)
            {
                // Nobody is being rung: the microphone opened by the pre-call check goes off again.
                await ReleaseMicAsync();
                if (res.StatusCode == HttpStatusCode.TooManyRequests)
                    LastError = "Too many calls in a short time. Wait a minute, then try again.";
                else
                {
                    try
                    {
                        var err = await res.Content.ReadFromJsonAsync<CallApiError>();
                        LastError = err?.Message ?? $"Server returned {(int)res.StatusCode}";
                    }
                    catch
                    {
                        LastError = $"Server returned {(int)res.StatusCode}";
                    }
                }
                StateChanged?.Invoke();
                return null;
            }
            var room = await res.Content.ReadFromJsonAsync<CallRoomDto>();
            if (room is not null)
            {
                await RememberOwnedCallAsync(room.Id);
                await HandleCallRoomChangedAsync(room);
            }
            return Current;
        }
        catch (Exception ex)
        {
            await ReleaseMicAsync();
            LastError = "Couldn't start the call: " + ex.Message;
            StateChanged?.Invoke();
            return null;
        }
        finally
        {
            _createGate.Release();
        }
    }

    private async Task ReleaseMicAsync()
    {
        try { await _js.InvokeVoidAsync("ipopAudio.endCall"); } catch (JSException) { }
    }

    public async Task<CallRoomDto?> SignalAsync(Guid callId, string action, bool? muted = null, bool? screen = null)
    {
        try
        {
            var res = await _auth.SendAsync(HttpMethod.Post, "/api/calls/signal",
                new { callId, action, isMuted = muted, isScreenSharing = screen });
            if (!res.IsSuccessStatusCode)
            {
                // The call already closed server-side (timeout, other side hung up): sync to that.
                if (res.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Conflict or HttpStatusCode.BadRequest
                    or HttpStatusCode.UnprocessableEntity
                    && Current?.Id == callId && action is "end" or "decline" or "leave")
                {
                    await HandleCallEndedAsync();
                    return null;
                }
                // Accept lost the race (answered on my other device, or the caller hung up first):
                // this device stands down quietly with the server's view of the call.
                if (action == "accept" && Current?.Id == callId
                    && res.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.UnprocessableEntity)
                {
                    await ResyncAfterLostAcceptAsync(callId);
                    return null;
                }
                LastError = $"Call action failed ({(int)res.StatusCode}).";
                StateChanged?.Invoke();
                return null;
            }
            var room = await res.Content.ReadFromJsonAsync<CallRoomDto>();
            if (room is not null)
            {
                await HandleCallRoomChangedAsync(room);
            }
            return Current;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            LastError = "Can't reach the call server. Check your connection.";
            StateChanged?.Invoke();
            return null;
        }
    }

    /// My accept lost: forget ownership, release media, and show whatever the server says now.
    private async Task ResyncAfterLostAcceptAsync(Guid callId)
    {
        _ownedCallId = null;
        try { await _js.InvokeVoidAsync("sessionStorage.removeItem", OwnedCallKey); } catch (JSException) { }
        await ReleaseMicAsync();
        CallRoomDto? room = null;
        try
        {
            using var get = await _auth.SendAsync(HttpMethod.Get, $"/api/calls/{callId}");
            if (get.IsSuccessStatusCode) room = await get.Content.ReadFromJsonAsync<CallRoomDto>();
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException) { }
        if (room is null) { await HandleCallEndedAsync(); return; }
        if (room.Status == CallStatus.Active) await StandDownAsync(room, isCaller: false);
        else await HandleCallRoomChangedAsync(room);
    }

    public async Task<CallRoomDto?> AcceptCurrentAsync()
    {
        if (Current is null) return null;
        if (IsGroup) return await JoinGroupAsync(Current.Id);
        await RaiseMediaStartingAsync();
        await RememberOwnedCallAsync(Current.Id);
        await _js.InvokeVoidAsync("ipopAudio.stopAudioTones");
        return await SignalAsync(Current.Id, "accept");
    }

    /// The ring was declined on this tab (vs. on another device of mine, which closes it quietly).
    private Guid? _decliningCallId;

    private async Task DismissQuietlyAsync()
    {
        Current = null;
        WebRtcState = null;
        StopHeartbeat();
        try
        {
            await _js.InvokeVoidAsync("ipopAudio.stopAudioTones");
            await _js.InvokeVoidAsync("ipopNative.callEnded");
        }
        catch (JSException) { }
        StateChanged?.Invoke();
        CallEnded?.Invoke();
        await PromoteWaitingAsync();
    }

    public async Task<CallRoomDto?> DeclineCurrentAsync()
    {
        if (Current is null) return null;
        if (IsGroup) return await DeclineGroupAsync();
        // Only an incoming ring is declined here: the media of any other call is never touched.
        await _js.InvokeVoidAsync("ipopAudio.stopAudioTones");
        var id = Current.Id;
        _decliningCallId = id;
        var r = await SignalAsync(id, "decline");
        if (Current?.Id == id && r is null) await HandleCallEndedAsync("Call declined");
        return r;
    }

    // ── Call waiting ──

    /// "Decline": the caller hears that I'm on another call; my current call goes on untouched.
    public async Task DeclineWaitingAsync()
    {
        if (Waiting is not { } w) return;
        Waiting = null;
        StateChanged?.Invoke();
        try { using var _ = await _auth.SendAsync(HttpMethod.Post, "/api/calls/signal", new { callId = w.Id, action = "busy" }); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { }
    }

    /// "End & answer": hang up (or leave) the current call, then answer the waiting one.
    public async Task<CallRoomDto?> EndAndAnswerWaitingAsync()
    {
        if (Waiting is not { } w) return null;
        Waiting = null;
        if (Current is not null)
        {
            if (IsOutgoingRing) await CancelCurrentAsync();
            else await EndCurrentAsync();
        }
        // The hang-up may already have promoted it; otherwise show it and answer.
        if (Current?.Id != w.Id)
        {
            using var res = await _auth.SendAsync(HttpMethod.Get, $"/api/calls/{w.Id}");
            var fresh = res.IsSuccessStatusCode ? await res.Content.ReadFromJsonAsync<CallRoomDto>() : null;
            if (fresh is not { Status: CallStatus.Ringing })
            {
                LastEndReason = "The other call already ended";
                StateChanged?.Invoke();
                return null;
            }
            await HandleCallRoomChangedAsync(fresh);
        }
        return Current?.Id == w.Id ? await AcceptCurrentAsync() : null;
    }

    /// Decline and send the caller a quick chat reply ("Tolak & kirim pesan").
    /// Returns false when the call was declined but the message could not be sent.
    public async Task<bool> DeclineWithMessageAsync(string text)
    {
        var caller = RemotePeer;
        await DeclineCurrentAsync();
        if (caller is null || string.IsNullOrWhiteSpace(text)) return false;
        try
        {
            using var dm = await _auth.SendAsync(HttpMethod.Post, "/api/chat/rooms/dm", new { recipientId = caller.UserId });
            if (!dm.IsSuccessStatusCode) return false;
            var room = await dm.Content.ReadFromJsonAsync<IdOnly>();
            if (room is null) return false;
            using var sent = await _auth.SendAsync(HttpMethod.Post, $"/api/chat/rooms/{room.Id}/messages",
                new { content = text.Trim(), idempotencyKey = Guid.NewGuid(), replyToId = (Guid?)null, type = (string?)null, roomId = room.Id });
            return sent.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException)
        {
            return false;
        }
    }

    public async Task<CallRoomDto?> CancelCurrentAsync()
    {
        if (Current is null) return null;
        if (IsGroup) return await LeaveGroupAsync();
        await _js.InvokeVoidAsync("ipopAudio.endCall");
        return await SignalAsync(Current.Id, "end");
    }

    public async Task<CallRoomDto?> EndCurrentAsync()
    {
        if (Current is null) return null;
        if (IsGroup) return await LeaveGroupAsync();
        await _js.InvokeVoidAsync("ipopAudio.endCall");
        return await SignalAsync(Current.Id, "end");
    }

    // ── Devices (overlay picker + "Tes perangkat" dialog) ──

    public Task<AudioDevices> ListDevicesAsync() => _js.InvokeAsync<AudioDevices>("ipopAudio.listDevices").AsTask();

    public async Task<bool> SetInputDeviceAsync(string? deviceId)
    {
        _groupInputDeviceId = string.IsNullOrEmpty(deviceId) ? null : deviceId;
        if (IsGroup) return await _js.InvokeAsync<bool>("ipopGroupCall.setInputDevice", deviceId ?? "");
        var r = await _js.InvokeAsync<MicCheck>("ipopAudio.setInputDevice", deviceId ?? "");
        return r.Ok;
    }

    public async Task<bool> SetOutputDeviceAsync(string? deviceId)
    {
        // Both paths learn the choice, so a later group/1:1 call uses the same speaker.
        await _js.InvokeAsync<bool>("ipopGroupCall.setOutputDevice", deviceId ?? "");
        return await _js.InvokeAsync<bool>("ipopAudio.setOutputDevice", deviceId ?? "");
    }

    public async Task ToggleMuteAudioAsync()
    {
        if (Current is null) return;
        if (IsMeeting) { await ToggleMeetingMuteAsync(); return; }
        var me = ActiveMe;
        bool newMuted = !(me?.IsMuted ?? false);
        if (IsGroup) await _js.InvokeVoidAsync("ipopGroupCall.setMuted", newMuted);
        else await _js.InvokeVoidAsync("ipopAudio.setMute", newMuted);
        if (await SignalAsync(Current.Id, "update", muted: newMuted) is null && Current is not null
            && ActiveMe?.IsMuted != newMuted)
        {
            // The server didn't take it: put the microphone back the way the screen shows it,
            // so "unmuted" on screen never hides a muted mic (or the reverse).
            if (IsGroup) await _js.InvokeVoidAsync("ipopGroupCall.setMuted", !newMuted);
            else await _js.InvokeVoidAsync("ipopAudio.setMute", !newMuted);
        }
    }

    /// Native Android app: "Tutup" / "Bisukan" on the ongoing-call notification (js/native-app.js).
    [JSInvokable]
    public async Task OnNativeCallAction(string action)
    {
        if (Current is null) return;
        if (action == "hangup") await EndCurrentAsync();
        else if (action == "mute") await ToggleMuteAudioAsync();
        StateChanged?.Invoke();
    }

    [JSInvokable]
    public async Task OnOfferGenerated(Guid callId, string sdp)
    {
        if (_hub?.State == HubConnectionState.Connected)
        {
            await _hub.SendAsync("SendWebRtcSignal", new WebRtcSignalDto(callId, "offer", sdp, Tab: _tabId));
        }
    }

    [JSInvokable]
    public async Task OnAnswerGenerated(Guid callId, string sdp)
    {
        if (_hub?.State == HubConnectionState.Connected)
        {
            await _hub.SendAsync("SendWebRtcSignal", new WebRtcSignalDto(callId, "answer", sdp, Tab: _tabId));
        }
    }

    [JSInvokable]
    public async Task OnIceCandidateGenerated(Guid callId, string candidateJson)
    {
        if (_hub?.State == HubConnectionState.Connected)
        {
            await _hub.SendAsync("SendWebRtcSignal", new WebRtcSignalDto(callId, "candidate", candidateJson, Tab: _tabId));
        }
    }

    /// "connecting" | "connected" | "reconnecting" | "failed".
    [JSInvokable]
    public void OnConnectionStateChanged(string state)
    {
        if (Current is null) return;
        WebRtcState = state;
        if (state == "connected") LastError = null;
        StateChanged?.Invoke();
    }

    [JSInvokable]
    public void OnWebRtcError(string error)
    {
        LastError = error;
        StateChanged?.Invoke();
    }

    /// js getStats: "good" | "fair" | "poor" (only on change). Sent with the next heartbeat too.
    [JSInvokable]
    public void OnQualityChanged(string quality)
    {
        if (Current is null) return;
        Quality = quality;
        _pendingQuality = quality is "fair" or "poor" ? quality : _pendingQuality;
        StateChanged?.Invoke();
    }

    /// The headset in use was unplugged: the call moved to the default microphone.
    [JSInvokable]
    public void OnDeviceFallback(string why)
    {
        DeviceNotice = why == "unplugged" ? "Headset unplugged. Using the default microphone." : null;
        StateChanged?.Invoke();
    }

    /// One-line notice under the call (device fell back), cleared on the next call.
    public string? DeviceNotice { get; private set; }

    /// "Reconnect audio" on a failed 1:1 call: a fresh ICE round, or a full rejoin if that can't run.
    public async Task ReconnectAudioAsync()
    {
        if (Current is not { } room || IsGroup) return;
        LastError = null;
        WebRtcState = "reconnecting";
        StateChanged?.Invoke();
        try
        {
            await EnsureFreshConfigAsync();
            if (await _js.InvokeAsync<bool>("ipopAudio.reconnect")) return;
        }
        catch (JSException) { }
        await RejoinAsync(room);
    }

    // ── Heartbeat: tells the server this tab still holds the call's media ──
    private CancellationTokenSource? _beatCts;
    private Guid? _beatCallId;
    private string? _pendingQuality;
    private static readonly TimeSpan BeatEvery = TimeSpan.FromSeconds(20);

    private void StartHeartbeat(Guid callId)
    {
        if (_beatCallId == callId && _beatCts is not null) return;
        StopHeartbeat();
        _beatCallId = callId;
        var cts = _beatCts = new CancellationTokenSource();
        _ = BeatLoopAsync(callId, cts.Token);
    }

    private void StopHeartbeat()
    {
        _beatCts?.Cancel();
        _beatCts?.Dispose();
        _beatCts = null;
        _beatCallId = null;
        _pendingQuality = null;
        DeviceNotice = null;
    }

    private async Task BeatLoopAsync(Guid callId, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var q = _pendingQuality;
                using var res = await _auth.SendAsync(HttpMethod.Post, $"/api/calls/{callId}/heartbeat", new { quality = q });
                if (res.IsSuccessStatusCode && q == _pendingQuality) _pendingQuality = null;
                // 404: the call is over (or I'm not in it any more) and the hub update was missed.
                if (res.StatusCode == HttpStatusCode.NotFound && Current?.Id == callId && !IsMeeting)
                {
                    using var get = await _auth.SendAsync(HttpMethod.Get, $"/api/calls/{callId}");
                    var room = get.IsSuccessStatusCode ? await get.Content.ReadFromJsonAsync<CallRoomDto>() : null;
                    if (room is null || room.Status is not (CallStatus.Ringing or CallStatus.Active))
                    {
                        if (room is not null) await HandleCallRoomChangedAsync(room);
                        else await HandleCallEndedAsync();
                        return;
                    }
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException) { }
            try { await Task.Delay(BeatEvery, ct); } catch (TaskCanceledException) { return; }
        }
    }

    public void Clear()
    {
        Current = null;
        _webRtcActiveCallId = null;
        StateChanged?.Invoke();
    }

    /// Session moved to another tab (SessionLockState): stop ringing here; ConnectAsync resumes.
    public async Task DisconnectAsync()
    {
        var hub = _hub;
        _hub = null;
        if (hub is not null) await hub.DisposeAsync();
        // A ring shown here must stop sounding: this tab no longer hears whether it was answered.
        if (IsIncomingRing)
        {
            try { await _js.InvokeVoidAsync("ipopAudio.stopAudioTones"); } catch (JSException) { }
            if (!IsGroup) Current = null;
        }
        Waiting = null;
        StateChanged?.Invoke();
    }

    public async ValueTask DisposeAsync()
    {
        StopHeartbeat();
        if (_hub is not null) await _hub.DisposeAsync();
        _hub = null;
        _selfRef?.Dispose();
        _selfRef = null;
    }
}

public record CallApiError(string? Code, string? Message);
public record CallConfigDto(JsonElement IceServers, int RingTimeoutSeconds);
public record CallSupport(bool Secure, bool MediaDevices, bool WebRtc, bool OutputSelect);
public record MicCheck(bool Ok, string? Code, string? Message, string? Label = null);
public record AudioDevice(string Id, string Label);
public record AudioDevices(List<AudioDevice> Inputs, List<AudioDevice> Outputs);
internal record IdOnly(Guid Id);
