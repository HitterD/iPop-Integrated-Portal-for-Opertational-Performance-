// Unattended access password (1.8.0). The password goes to the app, which sends it to iPop once for this connect;
// iPop checks it against the hash (5 wrong = the PC locks for 15 minutes). Nothing is stored on disk.
(function () {
  const { post, esc } = window.app;
  const $ = s => document.querySelector(s);
  let remember = true;
  $('[data-close]').innerHTML = ICON.x;
  $('[data-close]').onclick = () => post({ type: 'close' });
  const fmtId = id => id && id.length === 9 ? `${id.slice(0, 3)} ${id.slice(3, 6)} ${id.slice(6)}` : (id || '');

  app.on('show', m => {
    const wrong = !!m.wrong;
    $('#ic').style.background = wrong ? 'var(--cal)' : '#6f8290';
    $('#title').textContent = wrong ? 'Wrong password' : 'Enter access password';
    const left = m.triesLeft ?? 5;
    $('#text').innerHTML = `<b>${esc(m.machine)}</b> <span class="mono">${esc(fmtId(m.remoteId))}</span> `
      + (wrong ? `did not accept that password. ${left} ${left === 1 ? 'try' : 'tries'} left, then this PC locks for ${m.lockMinutes || 15} minutes.`
        : 'can be reached without asking the user. Enter the password set by an Administrator.');
    $('#fld').className = 'fld ' + (wrong ? 'e' : 'f');
    $('#eye').innerHTML = IC(wrong ? 'circle-alert' : 'eye');
    $('#logged').textContent = `This connection is logged under ${m.helper || 'you'}`;
    $('#go').disabled = false;
    if (wrong) $('#pw').select(); else $('#pw').value = '';
    $('#pw').focus();
  });
  $('#pw').oninput = () => { if ($('#fld').classList.contains('e')) { $('#fld').className = 'fld f'; $('#eye').innerHTML = IC('eye'); } };
  $('#eye').onclick = () => { const p = $('#pw'); p.type = p.type === 'password' ? 'text' : 'password'; p.focus(); };
  $('#remember').onclick = () => { remember = !remember; const c = $('#remember .cb'); c.classList.toggle('off', !remember); c.innerHTML = remember ? IC('check') : ''; };
  const go = () => { const v = $('#pw').value; if (!v) return $('#pw').focus(); $('#go').disabled = true; post({ type: 'password', password: v, remember }); };
  $('#go').onclick = go;
  $('#pw').onkeydown = e => { if (e.key === 'Enter') go(); };
  $('#cancel').onclick = () => post({ type: 'close' });
  $('#askUser').onclick = () => post({ type: 'askUser' });
  app.ready();
})();
