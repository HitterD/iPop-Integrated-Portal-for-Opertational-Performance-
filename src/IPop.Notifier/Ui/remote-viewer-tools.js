// Viewer additions (1.8.0, mockup ipop-remote-v2 prop-viewer / prop-err-*): the remote ID next to the PC name, the
// Monitor picker, what the user allowed (chip on the picture; disallowed buttons greyed — the user's PC enforces it
// anyway), and the screens for a connect that didn't start: offline, declined, wrong password / locked, not installed.
(function () {
  const R = window.remote, { post } = window.app;
  const $ = s => document.querySelector(s);
  const fmtId = id => id && id.length === 9 ? `${id.slice(0, 3)} ${id.slice(3, 6)} ${id.slice(6)}` : (id || '');
  let monitors = { current: 0, count: 1, switchable: false, items: [] };

  function start(m) { if (m.remoteId) $('#rid').textContent = fmtId(m.remoteId); }

  function joined(info) {
    if (info.remoteId) $('#rid').textContent = fmtId(info.remoteId);
    perms(info.permissions, info.mode);
  }

  // "control,clipboard,files" → chip text + toolbar state.
  function perms(list, mode) {
    const p = new Set((list || '').split(',').filter(Boolean));
    const names = [p.has('control') && mode !== 'view' ? 'control' : null, p.has('clipboard') ? 'clipboard' : null, p.has('files') ? 'files' : null].filter(Boolean);
    $('#permsTxt').textContent = names.length ? 'Allowed by the user: ' + names.join(', ') : 'Allowed by the user: view only';
    $('#perms').hidden = false;
    const ctl = $('#mode [data-m=control]');
    ctl.disabled = !p.has('control');
    ctl.title = p.has('control') ? '' : 'The user did not allow mouse and keyboard';
    if (!p.has('control')) viewer.setMode('view');
    const clip = $('#clip');
    clip.disabled = !p.has('clipboard'); clip.classList.toggle('on', p.has('clipboard'));
    clip.title = p.has('clipboard') ? 'Clipboard sync: text, images and files' : 'The user did not allow the clipboard';
    document.querySelectorAll('[data-p=files]').forEach(b => { b.title = p.has('files') ? '' : 'The user did not allow file transfer'; });
    window.viewerPerms = p;
  }

  function fromHost(m) {
    if (m.t === 'perms') return perms(m.list, m.mode);
    monitors = m;
    const cur = m.items.find(i => i.index === m.current) || m.items[0];
    $('#monName').textContent = cur ? (cur.index === -1 ? 'All' : cur.name.replace(' · Main', '')) : 'Monitor 1';
    $('#monOf').textContent = m.current === -1 ? `${m.count}/${m.count}` : `${(m.current || 0) + 1}/${m.count}`;
    $('#mon').disabled = !m.switchable;
    $('#mon').title = m.switchable ? 'Choose monitor' : 'The user\'s PC has one monitor';
  }

  $('#mon').onclick = e => {
    e.stopPropagation();
    document.querySelectorAll('.pop.monpop,.menu').forEach(x => x.remove());
    if (!monitors.switchable) return;
    const b = $('#mon').getBoundingClientRect();
    const pop = document.createElement('div'); pop.className = 'pop monpop'; pop.style.cssText = `left:${b.left}px;top:${b.bottom + 6}px`;
    const th = i => i === -1 ? 'background:linear-gradient(135deg,#123528,#2e9e5b)' : i === 0 ? '' : 'filter:hue-rotate(40deg) brightness(.8)';
    pop.innerHTML = '<div class="lbl" style="margin:0 0 8px;font-size:11px;font-weight:800;color:var(--ink-3);text-transform:uppercase">Choose monitor</div><div class="mons">'
      + monitors.items.map(i => `<button class="mon${i.index === monitors.current ? ' on' : ''}" data-i="${i.index}"><div class="th" style="${th(i.index)}"></div>${i.name}<small>${i.size}</small></button>`).join('') + '</div>';
    document.body.appendChild(pop);
    $('#mon').classList.add('on');
    pop.onclick = ev => {
      const m = ev.target.closest('.mon'); if (!m) return;
      R.sendReliable({ t: 'monitor', index: +m.dataset.i });
      pop.remove(); $('#mon').classList.remove('on');
      document.querySelector('#stage').focus();
    };
    setTimeout(() => document.addEventListener('click', () => { pop.remove(); $('#mon').classList.remove('on'); }, { once: true }), 0);
  };

  // ── a connect that didn't start ────────────────────────────────────────
  function ended(icon, color, title, sub, retry) {
    viewer.showEnded(title, sub);
    $('#wait').style.display = 'none';
    $('#perms').hidden = true;
    $('#ghost').hidden = true;
    $('#fps').textContent = '–'; $('#ping').textContent = '–'; $('#res').textContent = ''; $('#inp').innerHTML = '';
    const e = $('#ended');
    e.querySelector('i,svg').outerHTML = `<i data-lucide="${icon}" style="width:42px;height:42px;color:${color}"></i>`;
    e.querySelectorAll('[data-retry]').forEach(b => b.remove());
    if (retry) { e.insertAdjacentHTML('beforeend', '<button data-retry>Try again</button>'); e.querySelector('[data-retry]').onclick = () => post({ type: 'retry' }); }
    window.icons && window.icons();
  }
  const agoLong = iso => { const s = (Date.now() - new Date(iso)) / 1000; return s < 3600 ? Math.max(1, Math.round(s / 60)) + ' minutes' : s < 86400 ? Math.round(s / 3600) + ' hours' : Math.round(s / 86400) + ' days'; };
  const left = iso => { const s = Math.max(0, Math.round((new Date(iso) - Date.now()) / 1000)); return Math.floor(s / 60) + ':' + String(s % 60).padStart(2, '0'); };

  function problem(p) {
    const b = p.body || {};
    if (b.machine) $('#pc').textContent = b.machine;
    if (b.user) $('#who').textContent = '· ' + b.user;
    if (b.remoteId) $('#rid').textContent = fmtId(b.remoteId);
    switch (b.code) {
      case 'offline':
        return ended('monitor-off', '#aeaeb2', `${b.machine} is offline`, `Last seen ${b.lastSeenAt ? agoLong(b.lastSeenAt) + ' ago' : 'a while ago'}. The Notifier on that PC is not answering.`, true);
      case 'notinstalled':
        return ended('package-x', '#aeaeb2', `Notifier is not installed on ${b.machine}`, 'This PC is in Active Directory but cannot be controlled yet. Ask the user to install iPop Notifier.');
      case 'locked': {
        const tick = () => { if (!$('#ended').classList.contains('on')) return; $('#endedSub').textContent = `Too many wrong passwords. Try again in ${left(b.lockedUntil)}, or ask an Administrator to unlock it in Devices.`; };
        ended('shield-alert', '#ff453a', `Access to ${b.machine} is locked`, '');
        tick(); const t = setInterval(() => { if (new Date(b.lockedUntil) <= Date.now()) { clearInterval(t); return; } tick(); }, 1000);
        return;
      }
      default:
        return ended('circle-alert', '#ff9f0a', 'Couldn\'t connect', b.message || 'Try again in a moment.', true);
    }
  }

  window.viewerTools = { start, joined, fromHost, problem };
})();
