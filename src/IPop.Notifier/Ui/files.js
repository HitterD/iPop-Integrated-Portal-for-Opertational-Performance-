// Files in any Notifier page (1.5.0, mockup docs/mockups/notifier/priority.html screens 4–5): the .fm card with
// Preview + Download, the download progress ring → tick → "Saved to Downloads · Open" snack, and the viewer request.
// The app does the work (Core/Downloads.cs, Windows/ViewerWindow.cs); bridge messages:
//   download {id, path, name} → downloadProgress {id, p (0–100, -1 unknown)} → downloadDone {id, file, name} | downloadFailed {id, message}
//   reveal {file} · openFile {file} · preview {files:[{name, path, size, from?}], index}
// A file here = { name, path, size, from? } where path is an allow-listed iPop route
// (api/mail/attachments/{id}/download, api/chat/attachments/{id}/download, api/attachments/{id}).
(function () {
  const { post, esc } = window.app;
  // pdf | image | video | audio | office | none — same as PreviewCache.KindOf (C#).
  const EXT = {
    pdf: 'pdf', png: 'image', jpg: 'image', jpeg: 'image', gif: 'image', webp: 'image', bmp: 'image',
    mp4: 'video', webm: 'video', mov: 'video', m4v: 'video', mp3: 'audio', wav: 'audio', m4a: 'audio', ogg: 'audio', aac: 'audio',
    doc: 'office', docx: 'office', rtf: 'office', odt: 'office', pptx: 'office', xlsx: 'office', xls: 'office', xlsm: 'office', csv: 'office',
  };
  const ext = name => (String(name || '').match(/\.([A-Za-z0-9]{1,6})$/) || [])[1]?.toLowerCase() || '';
  const kind = name => EXT[ext(name)] || 'none';
  // Badge colour class (files.css .ft.*) and its short label.
  function badge(name) {
    const e = ext(name);
    const cls = e === 'pdf' ? 'pdf' : /^(xlsx?|xlsm|csv)$/.test(e) ? 'xlsx' : /^pptx?$/.test(e) ? 'pptx' : /^(docx?|rtf|odt)$/.test(e) ? 'docx'
      : kind(name) === 'image' ? 'jpg' : kind(name) === 'video' ? 'vid' : kind(name) === 'audio' ? 'aud' : 'zip';
    return { cls, label: (e || 'file').slice(0, 4).toUpperCase() };
  }
  const typeName = name => ({ pdf: 'PDF', image: 'Picture', video: 'Video', audio: 'Audio' })[kind(name)]
    || ({ doc: 'Word', docx: 'Word', rtf: 'Word', odt: 'Word', ppt: 'PowerPoint', pptx: 'PowerPoint', xls: 'Excel', xlsx: 'Excel', xlsm: 'Excel', csv: 'CSV', zip: 'ZIP' })[ext(name)]
    || (ext(name) ? ext(name).toUpperCase() : 'File');
  const fmtSize = b => !b ? '' : b >= 1048576 ? (b / 1048576).toFixed(1) + ' MB' : Math.max(1, Math.round(b / 1024)) + ' KB';
  const CHECK = '<svg class="lucide tk" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.4" stroke-linecap="round" stroke-linejoin="round"><path d="M20 6 9 17l-5-5"/></svg>';

  // One card per file; the list keeps every file so Preview can move between them.
  function cards(list) {
    if (!list || !list.length) return '';
    return `<div class="fmini" data-files>${list.map((f, i) => {
      const b = badge(f.name), can = kind(f.name) !== 'none';
      const meta = [typeName(f.name), fmtSize(f.size)].filter(Boolean).join(' · ');
      return `<div class="fm" style="animation-delay:${Math.min(i, 6) * 40}ms"><span class="ft ${b.cls}">${esc(b.label)}</span><div class="mid"><div class="fn" title="${esc(f.name)}">${esc(f.name)}</div><div class="fs">${esc(meta)}</div></div>`
        + `<button class="ib ${can ? '' : 'na'}" ${can ? `data-fp="${i}" title="Preview"` : 'title="No preview for this type" aria-disabled="true"'}>${IC(can ? 'eye' : 'eye-off')}</button>`
        + `<button class="ib" data-fd="${i}" title="Download">${IC('download')}</button></div>`;
    }).join('')}</div>`;
  }

  // Click handling for cards inside `box`; `list` = the same array given to cards().
  function wire(box, list) {
    box.addEventListener('click', e => {
      const p = e.target.closest('[data-fp]');
      if (p && box.contains(p)) { e.stopPropagation(); return preview(list, +p.dataset.fp); }
      const d = e.target.closest('[data-fd]');
      if (d && box.contains(d)) { e.stopPropagation(); return download(list[+d.dataset.fd], d); }
    });
  }

  function preview(list, index) {
    post({ type: 'preview', files: list.map(f => ({ name: f.name, path: f.path, size: f.size || 0, from: f.from || null })), index: index || 0 });
  }

  // ── downloads ─────────────────────────────────────────────────────────────
  const pending = new Map();
  let seq = 0;
  app.on('downloadProgress', m => pending.get(m.id)?.progress(m.p));
  app.on('downloadDone', m => { const w = pending.get(m.id); pending.delete(m.id); w?.done(m); });
  app.on('downloadFailed', m => { const w = pending.get(m.id); pending.delete(m.id); w?.failed(m); });

  // Starts a download; `btn` (optional) shows the ring → tick. Resolves with the saved file path, or null.
  // opts.labeled: the button keeps a text label ("Saving…" / "Saved"); opts.quiet: no snack; opts.then(file).
  function download(f, btn, opts) {
    opts = opts || {};
    if (!f || (btn && btn.classList.contains('busy'))) return Promise.resolve(null);
    const id = 'd' + (++seq) + '-' + Date.now();
    const orig = btn ? btn.innerHTML : '';
    const labeled = opts.labeled ?? (btn && btn.hasAttribute('data-label'));
    if (btn) { btn.classList.add('busy'); btn.innerHTML = `<span class="ring ind"></span>${labeled ? '<span>Saving…</span>' : ''}`; }
    return new Promise(resolve => {
      pending.set(id, {
        progress: p => {
          const r = btn && btn.querySelector('.ring'); if (!r) return;
          if (p < 0) r.classList.add('ind'); else { r.classList.remove('ind'); r.style.setProperty('--p', p); }
        },
        done: m => {
          if (btn) {
            btn.classList.add('done'); btn.innerHTML = CHECK + (labeled ? '<span>Saved</span>' : '');
            setTimeout(() => { btn.classList.remove('busy', 'done'); btn.innerHTML = orig; window.icons && window.icons(); }, 2400);
          }
          if (!opts.quiet) {
            const narrow = window.innerWidth < 600;
            snack(`${CHECK}<span>${narrow ? 'Saved to <b>Downloads</b>' : `Saved <b>${esc(m.name || f.name)}</b> to Downloads`}</span>`,
              narrow ? [['Open', () => post({ type: 'openFile', file: m.file })]]
                : [['Show in folder', () => post({ type: 'reveal', file: m.file })], ['Open', () => post({ type: 'openFile', file: m.file })]]);
          }
          if (opts.then) opts.then(m.file);
          resolve(m.file);
        },
        failed: m => {
          if (btn) { btn.classList.remove('busy'); btn.innerHTML = orig; window.icons && window.icons(); }
          snack(`${IC('circle-alert')}<span>${esc(m.message || "Couldn't download the file.")}</span>`);
          resolve(null);
        },
      });
      post({ type: 'download', id, path: f.path, name: f.name });
    });
  }

  // Snackbar with optional actions: [[label, fn], …]. Stays 6 s (longer while hovered).
  let snackTimer;
  function snack(html, actions) {
    document.querySelectorAll('.wsnack').forEach(s => s.remove());
    const s = document.createElement('div'); s.className = 'wsnack'; s.setAttribute('role', 'status');
    s.innerHTML = html + (actions || []).map((a, i) => `<button data-sa="${i}">${esc(a[0])}</button>`).join('');
    document.body.appendChild(s);
    window.icons && window.icons();
    requestAnimationFrame(() => s.classList.add('on'));
    const hide = () => { s.classList.remove('on'); setTimeout(() => s.remove(), 400); };
    s.onclick = e => { const b = e.target.closest('[data-sa]'); if (b) { actions[+b.dataset.sa][1](); hide(); } };
    clearTimeout(snackTimer);
    const arm = () => { clearTimeout(snackTimer); snackTimer = setTimeout(hide, 6000); };
    s.onmouseenter = () => clearTimeout(snackTimer); s.onmouseleave = arm;
    arm();
  }

  window.files = { kind, ext, badge, typeName, fmtSize, cards, wire, preview, download, snack, CHECK };
})();
