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
  // "Sign in with browser" (1.4.3, OAuth loopback): the app opens ONE tab with "iPop Notifier wants to connect";
  // Authorize there sends the browser back to this app on 127.0.0.1. Clicking again while it waits does nothing;
  // after 20 s "Open browser again" opens one fresh tab. Decline / timeout / errors end the wait. When the browser
  // can't reach the app, the page shows a short code: "Paste code from browser" takes it here.
  const HINT = {
    opened: 'iPop opened in your browser. Sign in there if it asks, then click Authorize. This window closes by itself.',
    approving: 'Connecting…',
  };
  const DEFAULT_HINT = $('#hint').textContent;
  let againTimer = 0;
  const showPaste = on => {
    document.body.classList.toggle('pasting', on);
    $('#pasteLink').hidden = on || !$('#browser').disabled;
    if (on) { $('#perr').textContent = ''; $('#pgo').disabled = false; $('#pcode').focus(); }
  };
  const idle = note => {
    const b = $('#browser');
    clearTimeout(againTimer);
    b.disabled = false; b.textContent = 'Sign in with browser';
    $('#again').hidden = true;
    showPaste(false);
    $('#pasteLink').hidden = true;
    $('#pcode').value = '';
    $('#hint').textContent = note || DEFAULT_HINT;
  };
  const waiting = () => {
    const b = $('#browser');
    b.disabled = true; b.textContent = 'Waiting for browser…';
    $('#again').hidden = true;
    if (!document.body.classList.contains('pasting')) $('#pasteLink').hidden = false;
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
    showPaste(false); $('#pcode').value = '';
    $('#pasteLink').hidden = false;
    app.post({ type: 'withBrowser', again: true, server: $('#server').value.trim() });
  };
  $('#pasteLink').onclick = () => showPaste(true);
  const sendCode = () => {
    const code = $('#pcode').value.trim();
    if (!code) return $('#pcode').focus();
    $('#perr').textContent = ''; $('#pgo').disabled = true; $('#pgo').textContent = '…';
    app.post({ type: 'pasteCode', code });
  };
  $('#pgo').onclick = sendCode;
  $('#pcode').onkeydown = e => { if (e.key === 'Enter') { e.preventDefault(); sendCode(); } };
  app.on('pasteResult', m => {
    $('#pgo').disabled = false; $('#pgo').textContent = 'Connect';
    const e = $('#perr'); e.textContent = m.message || "That code didn't work."; e.classList.remove('shake'); void e.offsetWidth; e.classList.add('shake');
    $('#pcode').select();
  });
  app.on('browserState', m => {
    if (m.state === 'opened' && !$('#browser').disabled) waiting();
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
