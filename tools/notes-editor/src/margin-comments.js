// Comments in the page margin. Each open thread is a card in the right margin, level with the text it is about, all visible at once.
// The page (Blazor) renders the cards; this module only measures: where each highlighted text sits, and the room around the page
// column, and pushes the answers to the cards as plain numbers (--top) so they stack without overlapping and follow edits and scroll.
//
//   margin = createMarginLayout({ scroller, page, cards, gap })
//   margin.layout()            // measure + position now (also runs on resize, on edits through `schedule`, and on card size changes)
//   margin.mode()              // 'margin' (cards in the right margin) | 'bubbles' (narrow screen / full width)
//
// Pure helpers (stack, pickMode) are exported for tests.

/// Puts cards at their wanted tops without overlap: each is at least `gap` below the previous one. The focused card keeps its
/// own spot and pushes the others (earlier ones up as far as they can, later ones down) so the card you clicked sits at its text.
export function stack(items, gap = 8) {
  // items: [{ id, want, height, focus }] in document order
  const out = items.map(it => ({ id: it.id, top: Math.max(0, it.want), height: it.height }));
  const f = items.findIndex(it => it.focus);
  const down = from => { for (let i = from; i < out.length; i++) { const min = i === 0 ? 0 : out[i - 1].top + out[i - 1].height + gap; if (out[i].top < min) out[i].top = min; } };
  if (f < 0) { down(0); return out; }
  // cards above the focused one move up to make room, but never above 0 or above where the previous one needs to be
  out[f].top = Math.max(0, items[f].want);
  for (let i = f - 1; i >= 0; i--) {
    const limit = out[i + 1].top - gap - out[i].height;
    if (out[i].top > limit) out[i].top = limit;
  }
  // if that pushed the first card above 0, shift everything back down instead
  const lift = out.length ? Math.min(0, out[0].top) : 0;
  if (lift < 0) for (const o of out) o.top -= lift;
  down(f + 1);
  down(0);
  return out;
}

/// Margin cards need room: a readable page column (at least 640 px; it shrinks from 820 to make room) plus a 280 px card column
/// and a small gutter each side must fit in the scroller. Full width always uses bubbles.
export const MIN_PAGE = 640, CARD_W = 280;
export function pickMode({ scrollerWidth, fullWidth, cardWidth = CARD_W, gutter = 16, minPage = MIN_PAGE }) {
  if (fullWidth) return 'bubbles';
  return scrollerWidth >= minPage + cardWidth + gutter * 2 ? 'margin' : 'bubbles';
}

export function createMarginLayout({ scroller, page, cards, editorRoot, draftTop, gap = 8, cardWidth = CARD_W, onMode }) {
  let raf = 0, last = '', modeNow = 'bubbles';
  const ro = typeof ResizeObserver === 'undefined' ? null : new ResizeObserver(() => schedule());

  function anchorTop(threadId) {
    if (threadId === 'draft') return draftTop?.() ?? 0;
    const el = editorRoot()?.querySelector(`[data-thread="${CSS.escape(String(threadId))}"]`);
    if (!el) return null;
    const r = el.getBoundingClientRect(), s = scroller.getBoundingClientRect();
    return r.top - s.top + scroller.scrollTop;
  }

  function layout() {
    raf = 0;
    if (!scroller?.isConnected || !page?.isConnected) return;
    const pr = page.getBoundingClientRect(), sr = scroller.getBoundingClientRect();
    const colTop = cards.getBoundingClientRect().top - sr.top + scroller.scrollTop;   // where the margin column starts in scroll coordinates
    const m = pickMode({ scrollerWidth: sr.width, fullWidth: page.classList.contains('wide'), cardWidth });
    if (m !== modeNow) { modeNow = m; onMode?.(m); }
    const els = [...cards.querySelectorAll('[data-card]')];
    const items = els.map(c => ({ id: c.dataset.card, el: c, want: anchorTop(c.dataset.card), height: c.offsetHeight, focus: c.classList.contains('focus') }))
      .filter(i => i.want != null)
      .sort((a, b) => a.want - b.want);
    // cards without a visible anchor (text deleted, anchor lost) go to the end of the column in their DOM order
    const orphans = els.filter(c => !items.some(i => i.id === c.dataset.card));
    const placed = stack(items.map(i => ({ id: i.id, want: i.want - 8 - colTop, height: i.height, focus: i.focus })), gap);
    const key = placed.map(p => `${p.id}:${Math.round(p.top)}`).join('|') + `#${modeNow}@${Math.round(colTop)}`;
    if (key === last) return;
    last = key;
    const byId = new Map(items.map(i => [i.id, i.el]));
    for (const p of placed) byId.get(p.id)?.style.setProperty('--top', `${Math.round(p.top)}px`);
    let tail = placed.length ? placed[placed.length - 1].top + placed[placed.length - 1].height + gap : 0;
    for (const o of orphans) { o.style.setProperty('--top', `${Math.round(tail)}px`); tail += o.offsetHeight + gap; }
    // bubbles sit at the right edge of the page column, level with the first line of the highlighted text
    for (const b of page.querySelectorAll('[data-bubble]')) {
      const t = anchorTop(b.dataset.bubble);
      if (t == null) { b.hidden = true; continue; }
      b.hidden = false;
      b.style.setProperty('--top', `${Math.round(t - (pr.top - sr.top + scroller.scrollTop))}px`);
    }
  }

  function schedule() { if (!raf) raf = requestAnimationFrame(layout); }
  addEventListener('resize', schedule);
  const mo = typeof MutationObserver === 'undefined' ? null : new MutationObserver(schedule);
  mo?.observe(page, { childList: true, subtree: true, characterData: true, attributes: true, attributeFilter: ['class', 'data-thread'] });
  ro?.observe(page);
  ro?.observe(cards);
  schedule();

  return {
    layout: () => { last = ''; schedule(); },
    schedule,
    mode: () => modeNow,
    /// Brings the text a thread is about into view (clicking its card).
    reveal(threadId) {
      const t = anchorTop(threadId);
      if (t == null) return false;
      const top = scroller.scrollTop, h = scroller.clientHeight;
      if (t < top + 80 || t > top + h - 120) scroller.scrollTo({ top: Math.max(0, t - h / 3), behavior: matchMedia('(prefers-reduced-motion: reduce)').matches ? 'auto' : 'smooth' });
      return true;
    },
    destroy() { cancelAnimationFrame(raf); removeEventListener('resize', schedule); mo?.disconnect(); ro?.disconnect(); },
  };
}
