// ICT viewer: automatic reconnect (BIGIS #8 v3, mockup png/08, 09, 25, 26). When LiveKit drops, the viewer rejoins
// with a fresh token from /join: N tries spread over the policy's window (default 6 in 60 s). The overlay says why
// (network / PC asleep / PC locked / Notifier restarting) and keeps the session open; "End session" ends it now.
// After the last try: "Connection lost" (logged as such). The user's PC reports sleep/lock/crash on the ctl topic.
(function () {
  const R = window.remote, { api } = window.app;
  const $ = s => document.querySelector(s);
  const CAUSE = {
    network: ['wifi-off', 'The network dropped'],
    asleep: ['moon', 'PC is asleep — waiting'],
    locked: ['lock', 'The user\'s PC is locked — waiting'],
    crash: ['rotate-cw', 'Notifier on the PC stopped and is restarting'],
  };
  let attempts = 6, seconds = 60, run = 0, cause = 'network', active = false;

  $('#stage').insertAdjacentHTML('beforeend', `<div class="recon" id="recon" hidden><div class="rc"><span id="rcIc">${IC('wifi-off')}</span><b id="rcT">Reconnecting…</b><span id="rcCause"></span><div class="rbar"><i id="rcBar"></i></div><span id="rcSub"></span><button class="fx" id="rcEnd">End session</button></div></div>`);

  function show(a, el) {
    const C = CAUSE[cause] || CAUSE.network;
    $('#recon').hidden = false;
    $('#rcIc').innerHTML = IC(C[0]);
    $('#rcT').textContent = cause === 'network' ? 'Reconnecting…' : C[1];
    $('#rcCause').textContent = cause === 'network' ? C[1] : 'Reconnecting automatically…';
    $('#rcSub').textContent = `Attempt ${a} of ${attempts} · ${el} of ${seconds} s · your session stays open`;
    $('#rcBar').style.width = Math.min(100, (el / seconds) * 100) + '%';
    window.icons && window.icons();
  }
  function hide() { $('#recon').hidden = true; }

  /// Starts (or keeps) reconnecting. join(info) = the viewer's own connect with the new token; resolves true/false.
  async function start(sessionId, why, join, onGiveUp, onBack) {
    if (why) cause = why;
    if (active) return; active = true;
    const me = ++run, t0 = performance.now();
    const elapsed = () => Math.floor((performance.now() - t0) / 1000);
    viewerFiles.paused(true);
    const tick = setInterval(() => { if (me === run && active) show(Math.min(attempts, cur), Math.min(seconds, elapsed())); }, 1000);
    let cur = 1;
    try {
      for (; cur <= attempts && me === run; cur++) {
        show(cur, elapsed());
        const due = (seconds * 1000 * (cur - 1)) / attempts - (performance.now() - t0);
        if (due > 0) await new Promise(r => setTimeout(r, due));
        if (me !== run) return;
        const r = await api('GET', `api/notifier/remote/sessions/${sessionId}/join`);
        if (r.status === 404 || r.status === 409) { stop(); return onGiveUp('over'); }
        if (r.ok && await join(r.json)) { stop(); viewerFiles.paused(false); app.snack('Reconnected · the session continues'); return onBack(); }
      }
      while (me === run && elapsed() < seconds) await new Promise(r => setTimeout(r, 500));
      if (me === run) { stop(); onGiveUp('lost'); }
    } finally { clearInterval(tick); }
  }
  function stop() { run++; active = false; hide(); }
  /// The user's PC says why the picture stopped (before or during a drop).
  function hint(state) {
    if (state === 'asleep') cause = 'asleep';
    else if (state === 'locked') cause = 'locked';
    else if (state === 'crash') cause = 'crash';
    else if (state === 'awake' || state === 'unlocked') cause = 'network';
    if (active) show(1, 0);
  }
  function setPolicy(info) { attempts = info.reconnectAttempts || attempts; seconds = Math.min(60, info.reconnectSeconds || seconds); } // ≤ 60 s: under the server's 90 s heartbeat window

  (window.__qa = window.__qa || {}).recon = (c, a, el) => { cause = c; show(a, el); }; // screenshots/QA
  window.viewerRecon = { start, stop, hint, setPolicy, get active() { return active; }, endButton: $('#rcEnd') };
})();
