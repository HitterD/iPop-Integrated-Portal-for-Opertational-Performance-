// File viewer (1.5.0, mockup docs/mockups/notifier/priority.html screen 5). The app downloads each file into its
// media cache (https://media.notifier.ipop/) and answers "loaded {id, kind, url, error}"; Office files are converted
// to PDF on iPop once ("preparing {id}" first). ← → / buttons / the list on the right switch files.
(function () {
  const { post, esc } = window.app;
  const F = window.files;
  const $ = s => document.querySelector(s);
  const RM = window.matchMedia && matchMedia('(prefers-reduced-motion: reduce)').matches;
  let list = [], vi = 0, vz = 1, seq = 0, current = null;
  const state = new Map(); // path → { kind, url, error, preparing }
  const waiters = new Map(); // load id → path

  app.on('files', m => {
    list = m.files || [];
    document.body.classList.toggle('one', list.length < 2);
    show(m.index || 0, 0);
  });
  app.on('preparing', m => {
    const path = waiters.get(m.id); if (!path) return;
    const s = state.get(path) || {}; s.preparing = true; state.set(path, s);
    if (current && current.path === path) render(0, true);
  });
  app.on('loaded', m => {
    const path = waiters.get(m.id); waiters.delete(m.id); if (!path) return;
    state.set(path, { kind: m.kind, url: m.url, error: m.error, done: true });
    if (current && current.path === path) render(0, true);
  });
  app.ready();

  function show(i, dir) {
    if (!list.length) return;
    vi = (i + list.length) % list.length; vz = 1;
    current = list[vi];
    const f = current, b = F.badge(f.name);
    $('#vft').className = 'ft ' + b.cls; $('#vft').textContent = b.label;
    $('#vnm').textContent = f.name;
    $('#vmt').textContent = [F.typeName(f.name), F.fmtSize(f.size), list.length > 1 ? `${vi + 1} of ${list.length}` : '', f.from ? 'from ' + f.from : ''].filter(Boolean).join(' · ');
    $('#vz').textContent = '100%';
    document.title = f.name + ' — Preview';
    if (F.kind(f.name) !== 'none' && !state.has(f.path)) {
      const id = 'l' + (++seq);
      waiters.set(id, f.path);
      state.set(f.path, {});
      post({ type: 'load', id, path: f.path, name: f.name });
    }
    render(dir, false);
    thumbs();
  }

  function render(dir, swap) {
    const f = current, st = state.get(f.path) || {};
    const k = F.kind(f.name);
    const cls = swap ? 'inF' : dir > 0 ? 'inR' : dir < 0 ? 'inL' : 'inZ';
    let html;
    if (k === 'none' || st.kind === 'none') html = only(f, st.error);
    else if (!st.done) html = k === 'office' && st.preparing ? skeleton(f) : `<span class="vconv"><span class="spin"></span>Loading ${esc(F.typeName(f.name))}…</span>`;
    else if (st.error || !st.url) html = only(f, st.error || "Couldn't load the file.");
    else if (st.kind === 'pdf' || st.kind === 'office') html = (st.kind === 'office' ? `<span class="vconv">${IC('refresh-cw')}Converted to PDF by iPop · Download gives the original .${esc(F.ext(f.name))}</span>` : '')
      + `<iframe class="pdf" src="${esc(st.url)}" title="${esc(f.name)}"></iframe>`;
    else if (st.kind === 'image') html = `<div class="vimgw"><img class="vimg" src="${esc(st.url)}" alt="${esc(f.name)}" style="--z:${vz}"></div>`;
    else if (st.kind === 'video') html = `<video class="vmed" src="${esc(st.url)}" controls autoplay></video>`;
    else if (st.kind === 'audio') html = `<audio class="vmed" src="${esc(st.url)}" controls autoplay></audio>`;
    else html = only(f, null);
    $('#vstage').innerHTML = `<div class="vcontent ${RM ? '' : cls}">${html}</div>`;
    $('#vstage').scrollTop = 0;
    $('#zoom').hidden = !(st.done && st.kind === 'image');
  }

  function skeleton(f) {
    const what = /^ppt/.test(F.ext(f.name)) ? 'PowerPoint' : /^(xls|csv)/.test(F.ext(f.name)) ? 'Excel' : 'Word';
    return `<span class="vconv"><span class="spin"></span>Preparing preview — converting ${what} to PDF on iPop…</span>`
      + `<div class="skel2 ${what === 'PowerPoint' ? 'wide' : ''}"><div class="sk h"></div><div class="sk s"></div><div class="sk"></div><div class="sk"></div><div class="sk t"></div><div class="sk s"></div><div class="sk"></div></div>`;
  }

  function only(f, why) {
    const b = F.badge(f.name);
    return `<div class="only"><span class="ft ${b.cls}">${esc(b.label)}</span><b>Download only</b><p>${esc(why || `There's no preview for ${F.typeName(f.name)} files. Download it, or open it in its Windows app.`)}</p>`
      + `<div class="row"><button class="vbtn" data-only="open">${IC('app-window')}Open in app</button><button class="vbtn p" data-only="dl">${IC('download')}Download</button></div></div>`;
  }

  function thumbs() {
    $('#vthumbs').innerHTML = `<div class="h">Files in this message</div>` + list.map((x, j) => {
      const b = F.badge(x.name);
      return `<button class="vt ${j === vi ? 'on' : ''}" data-vi="${j}"><span class="ft ${b.cls}">${esc(b.label)}</span><div><div class="n">${esc(x.name)}</div><div class="s">${esc([F.typeName(x.name), F.fmtSize(x.size)].filter(Boolean).join(' · '))}</div></div></button>`;
    }).join('');
    $('#vthumbs .vt.on')?.scrollIntoView({ block: 'nearest' });
  }

  function zoom(d) {
    vz = Math.min(3, Math.max(.25, +(vz + d * .1).toFixed(2)));
    document.querySelectorAll('.vimg').forEach(el => el.style.setProperty('--z', vz));
    const zv = $('#vz'); zv.textContent = Math.round(vz * 100) + '%';
    zv.classList.remove('tick'); void zv.offsetWidth; zv.classList.add('tick');
  }

  const openInApp = () => F.download(current, null, { quiet: true, then: file => post({ type: 'openFile', file }) })
    .then(file => { if (file) F.snack(`${IC('app-window')}<span>Opening <b>${esc(current.name)}</b>…</span>`); });

  document.addEventListener('click', e => {
    const t = e.target;
    if (t.closest('[data-close]')) return post({ type: 'close' });
    if (t.closest('[data-min]')) return post({ type: 'minimize' });
    const v = t.closest('[data-vi]'); if (v) { const j = +v.dataset.vi; if (j !== vi) show(j, j > vi ? 1 : -1); return; }
    const s = t.closest('[data-vs]'); if (s) return show(vi + +s.dataset.vs, +s.dataset.vs);
    const z = t.closest('[data-z]'); if (z) return zoom(+z.dataset.z);
    if (t.closest('#vdl') || t.closest('[data-only="dl"]')) return F.download(current, t.closest('#vdl') || t.closest('[data-only]'), { labeled: true });
    if (t.closest('#vopen') || t.closest('[data-only="open"]')) return openInApp();
  });
  document.addEventListener('keydown', e => {
    if (e.target.closest && e.target.closest('input,textarea,video,audio')) return;
    if (e.key === 'ArrowLeft') { show(vi - 1, -1); e.preventDefault(); }
    else if (e.key === 'ArrowRight') { show(vi + 1, 1); e.preventDefault(); }
    else if ((e.key === '+' || e.key === '=') && !$('#zoom').hidden) zoom(1);
    else if (e.key === '-' && !$('#zoom').hidden) zoom(-1);
  });
  // Ctrl + wheel zooms pictures.
  document.addEventListener('wheel', e => { if (e.ctrlKey && !$('#zoom').hidden) { e.preventDefault(); zoom(e.deltaY < 0 ? 1 : -1); } }, { passive: false });
})();
