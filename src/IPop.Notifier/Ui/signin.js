(function () {
  const $ = s => document.querySelector(s);
  document.body.dataset.esc = 'off';
  $('[data-min]').innerHTML = ICON.min; $('[data-close]').innerHTML = ICON.x;
  $('[data-min]').onclick = () => app.post({ type: 'minimize' });
  $('[data-close]').onclick = () => app.post({ type: 'close' });
  $('#auto').onclick = e => { e.preventDefault(); $('#auto').classList.toggle('on'); };
  $('#adv').onclick = () => document.body.classList.toggle('showsrv');
  app.on('init', m => { $('#server').value = m.server; });
  app.on('error', m => {
    $('#go').disabled = false; $('#go').textContent = 'Sign in';
    const e = $('#err'); e.textContent = m.message || 'Couldn\'t sign in.'; e.classList.remove('shake'); void e.offsetWidth; e.classList.add('shake');
    $('#pw').select();
  });
  // "Sign in with browser" (1.4.2): one attempt at a time. An iPop tab that is already open shows a "Connect iPop
  // Notifier on this PC?" card, or the app opens exactly one tab with it; clicking again while it waits does nothing.
  // After 20 s "Open browser again" opens one tab. Cancel in the browser / expiry / errors end the wait.
  const HINT = {
    asking: 'Looking for iPop in your browser…',
    tab: 'Switch to iPop in your browser and click Connect. This window closes by itself.',
    opened: 'iPop opened in your browser. Sign in there if it asks, then click Connect. This window closes by itself.',
    approving: 'Connecting…',
  };
  const DEFAULT_HINT = $('#hint').textContent;
  const idle = note => {
    const b = $('#browser');
    clearTimeout(againTimer);
    b.disabled = false; b.textContent = 'Sign in with browser';
    $('#again').hidden = true;
    $('#hint').textContent = note || DEFAULT_HINT;
  };
  let againTimer = 0;
  const waiting = () => {
    const b = $('#browser');
    b.disabled = true; b.textContent = 'Waiting for browser…';
    $('#again').hidden = true;
    clearTimeout(againTimer);
    againTimer = setTimeout(() => { $('#again').hidden = false; }, 20000);
  };
  $('#browser').onclick = () => {
    if ($('#browser').disabled) return;
    waiting();
    app.post({ type: 'withBrowser', server: $('#server').value.trim() });
  };
  $('#again').onclick = () => {
    waiting();
    app.post({ type: 'withBrowser', again: true, server: $('#server').value.trim() });
  };
  app.on('browserState', m => {
    if (m.state === 'asking') { const b = $('#browser'); if (!b.disabled) waiting(); }
    if (HINT[m.state]) $('#hint').textContent = HINT[m.state];
    if (m.state === 'denied' || m.state === 'expired' || m.state === 'failed') idle(m.message);
  });
  $('#f').onsubmit = e => {
    e.preventDefault();
    const nik = $('#nik').value.trim(), pw = $('#pw').value;
    if (!nik) return $('#nik').focus();
    if (!pw) return $('#pw').focus();
    $('#err').textContent = ''; $('#go').disabled = true; $('#go').textContent = 'Signing in…';
    app.post({ type: 'signIn', nik, password: pw, server: $('#server').value.trim(), autostart: $('#auto').classList.contains('on') });
  };
  app.ready();
})();
