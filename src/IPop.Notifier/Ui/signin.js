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
  $('#browser').onclick = () => {
    app.post({ type: 'withBrowser' });
    $('#hint').textContent = 'iPop opened in your browser. Sign in there if it asks; this window closes by itself.';
  };
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
