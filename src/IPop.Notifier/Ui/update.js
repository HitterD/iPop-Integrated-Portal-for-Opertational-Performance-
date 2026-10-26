(function () {
  const $ = s => document.querySelector(s);
  $('[data-min]').innerHTML = ICON.min; $('[data-close]').innerHTML = ICON.x;
  $('[data-min]').onclick = () => app.post({ type: 'minimize' });
  // Closing the window is the same as Later.
  $('[data-close]').onclick = () => app.post({ type: 'later' });
  $('#later').onclick = () => app.post({ type: 'later' });
  $('#now').onclick = () => {
    $('#now').disabled = true; $('#later').disabled = true;
    $('#now').innerHTML = IC('loader-circle', 'spin') + ' Updating…';
    app.post({ type: 'now' });
  };

  // "- line" / "* line" markdown bullets from the release notes; anything else is one line each.
  function lines(md) {
    return String(md || '').split(/\r?\n/).map(l => l.replace(/^\s*[-*]\s+/, '').trim()).filter(Boolean).slice(0, 6);
  }

  app.on('update', ({ info: m }) => {
    $('#title').textContent = `iPop Notifier ${m.version} is ready`;
    $('#sub').textContent = `You have ${m.current}.`;
    const l = lines(m.notes);
    $('#notes').hidden = l.length === 0;
    $('#list').innerHTML = l.map(x => `<li>${app.esc(x)}</li>`).join('');
    $('#now').focus();
  });
  app.ready();
})();
