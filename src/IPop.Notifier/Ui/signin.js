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
  // "Sign in with browser" (1.4.0): one attempt at a time. The app re-uses an iPop tab that is already open, or opens
  // exactly one; clicking again while it waits does nothing. After 20 s "Open browser again" opens one tab.
  const HINT = {
    asking: 'Looking for iPop in your browser…',
    tab: 'Switch to iPop in your browser and choose “Open iPop Notifier” if it asks. This window closes by itself.',
    opened: 'iPop opened in your browser. Sign in there if it asks, then choose “Open iPop Notifier”. This window closes by itself.',
    approving: 'Connecting…',
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
    app.post({ type: 'withBrowser' });
  };
  $('#again').onclick = () => {
    waiting();
    app.post({ type: 'withBrowser', again: true });
  };
  app.on('browserState', m => {
    if (m.state === 'asking') { const b = $('#browser'); if (!b.disabled) waiting(); }
    if (HINT[m.state]) $('#hint').textContent = HINT[m.state];
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
