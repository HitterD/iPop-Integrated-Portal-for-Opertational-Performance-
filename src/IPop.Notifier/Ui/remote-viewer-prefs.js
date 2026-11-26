// ICT viewer preferences (BIGIS #8 v3, Settings › Remote › "My viewer"): starting quality, screen size Fit / 1:1 /
// Fill, hide the toolbar automatically, shortcuts (Ctrl+Alt+Del, full screen, toolbar), chat sound, save folder.
// The app sends them with "start" (ui-prefs "notifier" › remote); defaults are the mockup README's.
(function () {
  const R = window.remote, { post } = window.app;
  const $ = s => document.querySelector(s);
  const stage = $('#stage');
  let P = { quality: 'auto', fit: 'fit', autoHide: false, chatSound: true, saveDir: 'Downloads\\iPop Remote', keys: { cad: 'Ctrl+Alt+End', fs: 'F11', toolbar: 'Ctrl+Shift+T' } };

  app.on('start', m => { if (m.prefs) P = Object.assign(P, m.prefs, { keys: Object.assign({}, P.keys, m.prefs.keys || {}) }); fit(P.fit); setQ(P.quality); });

  // ── screen size: Fit (whole screen) · 1:1 (pixels, scrollbars) · Fill (cover the window) ──
  function fit(v) {
    P.fit = v;
    document.querySelectorAll('#fitSeg button').forEach(b => b.classList.toggle('on', b.dataset.f === v));
    stage.classList.toggle('scroll', v === 'actual');
    const vid = window.viewer?.video;
    if (!vid) return;
    if (v === 'actual') { vid.style.width = vid.videoWidth + 'px'; vid.style.height = vid.videoHeight + 'px'; vid.style.objectFit = 'none'; }
    else { vid.style.width = ''; vid.style.height = ''; vid.style.objectFit = v === 'fill' ? 'cover' : 'contain'; }
  }
  $('#fitSeg').onclick = e => { const b = e.target.closest('button'); if (b) { fit(b.dataset.f); post({ type: 'remotePref', key: 'fit', value: b.dataset.f }); } };
  new MutationObserver(() => fit(P.fit)).observe(stage, { childList: true });

  // ── starting quality ──
  function setQ(v) { document.querySelectorAll('#quality button').forEach(b => b.classList.toggle('on', b.dataset.q === v)); }
  function applyQuality(r) { if (P.quality !== 'auto') r.sendReliable({ t: 'quality', preset: P.quality }); setQ(P.quality); }

  // ── hide the toolbar automatically (move to the top to bring it back) ──
  document.addEventListener('mousemove', e => {
    if (!P.autoHide || document.body.classList.contains('fs')) { document.body.classList.remove('tbhide'); return; }
    document.body.classList.toggle('tbhide', e.clientY > 110);
  });

  // ── shortcuts ──
  const combo = e => [e.ctrlKey && 'Ctrl', e.altKey && 'Alt', e.shiftKey && 'Shift'].filter(Boolean).concat(e.key.length === 1 ? e.key.toUpperCase() : e.key).join('+');
  document.addEventListener('keydown', e => {
    if (['Control', 'Alt', 'Shift', 'Meta'].includes(e.key)) return;
    const k = combo(e);
    if (k === P.keys.cad) { e.preventDefault(); e.stopPropagation(); R.inputReliable({ t: 'combo', k: 'cad' }); app.snack('Ctrl + Alt + Del sent'); }
    else if (k === P.keys.toolbar) { e.preventDefault(); e.stopPropagation(); P.autoHide = !P.autoHide; document.body.classList.toggle('tbhide', P.autoHide); }
    else if (k === P.keys.fs && k !== 'F11') { e.preventDefault(); e.stopPropagation(); $('#fs').click(); }
  }, true);

  // ── chat sound ──
  function chatSound() {
    if (!P.chatSound) return;
    try { const a = new AudioContext(), o = a.createOscillator(), g = a.createGain(); o.frequency.value = 880; g.gain.value = .04; o.connect(g); g.connect(a.destination); o.start(); setTimeout(() => { o.stop(); a.close(); }, 140); } catch { }
  }

  // Keys menu shows the Ctrl+Alt+Del shortcut next to it (mockup png/04).
  new MutationObserver(() => {
    const b = document.querySelector('.menu button[data-k=cad]');
    if (b && !b.querySelector('small')) { b.innerHTML = b.innerHTML.replace('Ctrl + Alt + Del', '<span style="flex:1">Ctrl + Alt + Del</span>') + `<small style="color:var(--ink-3)">${P.keys.cad}</small>`; }
  }).observe(document.body, { childList: true });

  window.viewerPrefs = { get: () => P, applyQuality, chatSound, fit };
})();
