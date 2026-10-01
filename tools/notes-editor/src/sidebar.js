// Notes sidebar gestures that Blazor can't express: drag & drop between rows (onto a row = make it a
// sub-page, near its top/bottom edge = place before/after it, onto Trash = delete), right-click and
// long-press menus. Rows opt in with data-id; draggable rows also carry data-drag. Touch screens get
// the long-press menu instead of dragging.

export function sidebar(root, net) {
  const fine = matchMedia('(pointer: fine)');
  let dragId = null, mark = null, press = null, lastMenu = 0;

  const rowOf = t => t?.closest?.('.nt-row[data-id], .nt-row[data-drop]');
  const clear = () => { mark?.classList.remove('drop-before', 'drop-after', 'drop-in'); mark = null; };
  const menu = (row, x, y) => {
    if (!row?.dataset.id || Date.now() - lastMenu < 700) return;
    lastMenu = Date.now();
    net.invokeMethodAsync('OnRowMenu', row.dataset.id, Math.round(x), Math.round(y)).catch(() => {});
  };
  const arm = () => root.querySelectorAll('.nt-row[data-drag]').forEach(r => { r.draggable = fine.matches; });

  const zone = (row, e) => {
    if (row.dataset.drop === 'trash') return 'trash';
    const r = row.getBoundingClientRect(), f = (e.clientY - r.top) / r.height;
    if (row.dataset.sort !== undefined && f < .28) return 'before';
    if (row.dataset.sort !== undefined && f > .72) return 'after';
    return row.dataset.nest !== undefined ? 'inside' : null;
  };

  const on = {
    dragstart: e => {
      const row = rowOf(e.target);
      if (!row?.dataset.drag) return;
      dragId = row.dataset.id;
      e.dataTransfer.effectAllowed = 'move';
      e.dataTransfer.setData('text/plain', row.querySelector('.t')?.textContent || '');
      row.classList.add('dragging');
    },
    dragover: e => {
      const row = rowOf(e.target);
      if (!dragId || !row || row.dataset.id === dragId) { clear(); return; }
      const z = zone(row, e);
      if (!z) { clear(); return; }
      e.preventDefault();
      e.dataTransfer.dropEffect = 'move';
      if (mark !== row) clear();
      mark = row;
      row.classList.toggle('drop-before', z === 'before');
      row.classList.toggle('drop-after', z === 'after');
      row.classList.toggle('drop-in', z === 'inside' || z === 'trash');
    },
    dragleave: e => { if (mark && !mark.contains(e.relatedTarget)) clear(); },
    drop: e => {
      const row = rowOf(e.target), id = dragId, z = row && zone(row, e);
      clear();
      if (!id || !row || !z || row.dataset.id === id) return;
      e.preventDefault();
      net.invokeMethodAsync('OnRowDrop', id, row.dataset.id || null, z).catch(() => {});
    },
    dragend: () => { clear(); root.querySelector('.dragging')?.classList.remove('dragging'); dragId = null; },
    contextmenu: e => {
      const row = rowOf(e.target);
      if (!row?.dataset.id || e.target.closest('input')) return;
      e.preventDefault();
      menu(row, e.clientX, e.clientY);
    },
    pointerdown: e => {
      if (e.pointerType !== 'touch') return;
      const row = rowOf(e.target);
      if (!row?.dataset.id) return;
      press = { x: e.clientX, y: e.clientY, t: setTimeout(() => { press = null; menu(row, e.clientX, e.clientY); }, 500) };
    },
    pointermove: e => { if (press && Math.hypot(e.clientX - press.x, e.clientY - press.y) > 8) { clearTimeout(press.t); press = null; } },
    pointerup: () => { if (press) { clearTimeout(press.t); press = null; } },
  };
  on.pointercancel = on.pointerup;
  for (const [k, f] of Object.entries(on)) root.addEventListener(k, f);
  const watch = new MutationObserver(arm);
  watch.observe(root, { childList: true, subtree: true });
  fine.addEventListener?.('change', arm);
  arm();

  return {
    dispose() {
      for (const [k, f] of Object.entries(on)) root.removeEventListener(k, f);
      watch.disconnect();
      fine.removeEventListener?.('change', arm);
    },
  };
}

/// Keeps a fixed-position menu opened at the pointer inside the viewport.
export function place(el) {
  if (!el) return;
  const r = el.getBoundingClientRect(), pad = 8;
  if (r.right > innerWidth - pad) el.style.left = Math.max(pad, innerWidth - pad - r.width) + 'px';
  if (r.bottom > innerHeight - pad) el.style.top = Math.max(pad, innerHeight - pad - r.height) + 'px';
}

export function openTab(url) { window.open(url, '_blank', 'noopener'); }

export function focusSelect(el) { el?.focus(); el?.select?.(); }
