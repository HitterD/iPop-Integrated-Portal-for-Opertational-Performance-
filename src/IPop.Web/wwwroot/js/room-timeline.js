// Meeting scheduling v3 §2 (mockup screen 2): the room day timeline. Pointer drag moves the selected block, its
// edges resize it, in 15-minute steps, clamped to the day window (and never before "now"), 15 min – 8 h.
// Times are local minutes of the day. The .NET side (RoomTimeline.razor) owns state: every snapped change is sent
// with OnDrag(start, end, done); the block is also moved here directly so dragging feels immediate.
(function () {
  const MIN = 15, MAX = 480;

  /// Pixel offset within the track → minutes of the day.
  function toMinutes(px, width, win) {
    if (!(width > 0)) return win.start;
    return win.start + (px / width) * (win.end - win.start);
  }

  /// Round to the nearest step (default 15); never -0.
  function snap(m, step = 15) {
    return Math.round(m / step) * step || 0;
  }

  function lowest(win) { return Math.max(win.start, win.floor ?? win.start); }

  /// Move [s, e) by dm minutes, same length, inside the window and not before its floor.
  function clampMove(s, e, dm, win) {
    const d = e - s;
    const start = Math.min(Math.max(s + dm, lowest(win)), win.end - d);
    return { s: start, e: start + d };
  }

  /// Drag the left ('l') or right ('r') edge by dm minutes: 15 min – 8 h, inside the window and its floor.
  function clampResize(s, e, edge, dm, win) {
    if (edge === 'l') return { s: Math.min(Math.max(s + dm, lowest(win), e - MAX), e - MIN), e };
    if (edge === 'r') return { s, e: Math.max(Math.min(e + dm, win.end, s + MAX), s + MIN) };
    return { s, e };
  }

  /// Phone (v3 Task 10): the track is wider than its sideways scroller. The scrollLeft that shows the selection
  /// [left, left + width) px: unchanged when it is fully visible, else the selection starts `pad` px in, clamped to the content.
  function revealLeft(left, width, scroll, view, content, pad = 90) {
    const max = Math.max(0, content - view);
    if (left >= scroll && left + width <= scroll + view) return Math.min(scroll, max);
    return Math.min(Math.max(0, left - pad), max);
  }

  /// Scrolls the track's .tl-scroll so the selected block is in view (no-op when nothing overflows).
  function reveal(track) {
    const sc = track && track.closest('.tl-scroll'), sel = track && track.querySelector('[data-sel]');
    if (!sc || !sel || sc.scrollWidth <= sc.clientWidth) return;
    const x = sel.getBoundingClientRect().left - track.getBoundingClientRect().left;
    const pad = parseFloat(getComputedStyle(sc).paddingLeft) || 0;
    sc.scrollLeft = revealLeft(x + pad, sel.offsetWidth, sc.scrollLeft, sc.clientWidth, sc.scrollWidth);
  }

  const pct = (m, win) => ((m - win.start) / (win.end - win.start) * 100).toFixed(3) + '%';

  /// Wires one track. opts: { start, end, floor, sel: [s, e] } in minutes. Returns a handle to update or dispose.
  function bind(track, ref, opts) {
    if (!track) return null;
    const state = { win: { start: opts.start, end: opts.end, floor: opts.floor }, s: opts.s, e: opts.e };
    let drag = null;
    function paint() {
      const sel = track.querySelector('[data-sel]');
      if (!sel) return;
      sel.style.left = pct(state.s, state.win);
      sel.style.width = pct(state.e - state.s + state.win.start, state.win);
    }
    function down(ev) {
      const sel = ev.target.closest('[data-sel]');
      if (!sel || ev.button > 0) return;
      ev.preventDefault();
      drag = { mode: ev.target.dataset.h || 'move', x0: ev.clientX, s0: state.s, e0: state.e, w: track.getBoundingClientRect().width, last: null };
      sel.classList.add('drag');
      try { track.setPointerCapture(ev.pointerId); } catch { }
    }
    function move(ev) {
      if (!drag) return;
      const dm = snap(toMinutes(ev.clientX - drag.x0, drag.w, { start: 0, end: state.win.end - state.win.start }));
      const r = drag.mode === 'move' ? clampMove(drag.s0, drag.e0, dm, state.win) : clampResize(drag.s0, drag.e0, drag.mode, dm, state.win);
      if (r.s === state.s && r.e === state.e) return;
      state.s = r.s; state.e = r.e;
      paint();
      ref.invokeMethodAsync('OnDrag', r.s, r.e, false).catch(() => { });
    }
    function up() {
      if (!drag) return;
      track.querySelector('[data-sel]')?.classList.remove('drag');
      drag = null;
      ref.invokeMethodAsync('OnDrag', state.s, state.e, true).catch(() => { });
    }
    requestAnimationFrame(() => reveal(track));
    track.addEventListener('pointerdown', down);
    track.addEventListener('pointermove', move);
    track.addEventListener('pointerup', up);
    track.addEventListener('pointercancel', up);
    return {
      update(o) {
        const moved = !drag && (o.s !== state.s || o.e !== state.e || o.start !== state.win.start || o.end !== state.win.end);
        state.win = { start: o.start, end: o.end, floor: o.floor };
        if (!drag) { state.s = o.s; state.e = o.e; }
        // A chip, Try or a new day moved the block from .NET: bring it into view (a drag already follows the finger).
        if (moved) requestAnimationFrame(() => reveal(track));
      },
      dispose() {
        track.removeEventListener('pointerdown', down);
        track.removeEventListener('pointermove', move);
        track.removeEventListener('pointerup', up);
        track.removeEventListener('pointercancel', up);
      },
    };
  }

  const api = { toMinutes, snap, clampMove, clampResize, revealLeft, reveal, bind };
  if (typeof module !== 'undefined') { module.exports = api; return; }
  window.ipopRoomTimeline = api;
})();
