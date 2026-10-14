// Mail read receipts (spec 2026-10-14 §3). A message is "seen" only when its body is shown in the
// reading pane (thread item expanded), it is on screen, the tab is visible AND the window focused,
// all continuously for 2 s. Any break cancels that message's timer; it is reported once per session.
// Same attention rule as chat (ipop.watchAttention): visibilityState === "visible" && hasFocus().
//
// Markup contract: an element carrying data-mail-seen="<mailId>" is the shown body of one message.
// Mail.razor / MailThreadStack only render the attribute for a message that is open, loaded, and not
// the reader's own (the server ignores the sender's copy anyway).
(function () {
  const SEEN_DELAY_MS = 2000;

  /// Pure timing core (unit-tested in tests/mail-seen.test.cjs).
  function createSeenTracker({ setTimer, clearTimer, onSeen, delay = SEEN_DELAY_MS }) {
    let attentive = false;
    const shown = new Set();
    const timers = new Map();
    const done = new Set();

    function arm(id) {
      if (done.has(id) || timers.has(id) || !attentive || !shown.has(id)) return;
      timers.set(id, setTimer(() => {
        timers.delete(id);
        if (!attentive || !shown.has(id) || done.has(id)) return;
        done.add(id);
        onSeen(id);
      }, delay));
    }
    function disarm(id) {
      const t = timers.get(id);
      if (t !== undefined) { clearTimer(t); timers.delete(id); }
    }

    return {
      setAttention(on) {
        on = !!on;
        if (on === attentive) return;
        attentive = on;
        if (on) shown.forEach(arm); else [...timers.keys()].forEach(disarm);
      },
      setShown(id, on) {
        if (!id) return;
        if (on) { if (!shown.has(id)) { shown.add(id); arm(id); } }
        else { shown.delete(id); disarm(id); }
      },
      /// Ids currently tracked as shown (to drop ones whose element went away).
      shownIds: () => [...shown],
      wasSeen: id => done.has(id),
      /// The report failed: allow a later display to try again.
      forget(id) { done.delete(id); },
      reset() { [...timers.keys()].forEach(disarm); shown.clear(); },
    };
  }

  /// visiblePx of an element of height elHeight inside a pane viewportHeight tall. On screen = half the
  /// message, or (for a message taller than the pane) half the pane filled with it.
  function isOnScreen(visiblePx, elHeight, viewportHeight) {
    if (!(elHeight > 0) || !(viewportHeight > 0)) return false;
    return visiblePx >= Math.min(elHeight, viewportHeight) / 2;
  }

  const api = { createSeenTracker, isOnScreen, SEEN_DELAY_MS };
  if (typeof module !== 'undefined') { module.exports = api; return; }

  // ── Browser binding ──
  let state = null;

  function visiblePart(el) {
    const r = el.getBoundingClientRect();
    if (r.width <= 0 || r.height <= 0) return { vis: 0, h: 0, vp: 0 };
    const pane = el.closest('.detail-scroll-area');
    const p = pane ? pane.getBoundingClientRect() : { top: 0, bottom: window.innerHeight };
    const top = Math.max(p.top, 0), bottom = Math.min(p.bottom, window.innerHeight);
    const vis = Math.max(0, Math.min(r.bottom, bottom) - Math.max(r.top, top));
    return { vis, h: r.height, vp: Math.max(0, bottom - top) };
  }

  function scan() {
    if (!state) return;
    state.raf = 0;
    const present = new Set();
    state.root.querySelectorAll('[data-mail-seen]').forEach(el => {
      const id = el.getAttribute('data-mail-seen');
      if (!id) return;
      present.add(id);
      const { vis, h, vp } = visiblePart(el);
      state.tracker.setShown(id, isOnScreen(vis, h, vp));
    });
    state.tracker.shownIds().forEach(id => { if (!present.has(id)) state.tracker.setShown(id, false); });
  }
  function schedule() { if (state && !state.raf) state.raf = requestAnimationFrame(scan); }
  function attention() {
    if (!state) return;
    state.tracker.setAttention(document.visibilityState === 'visible' && document.hasFocus());
  }

  // One tracker per tab: "once per message per session" survives leaving and reopening /mail.
  let tracker = null;
  let dotnetRef = null;
  function getTracker() {
    return tracker ??= createSeenTracker({
      setTimer: (fn, ms) => setTimeout(fn, ms),
      clearTimer: t => clearTimeout(t),
      onSeen: id => {
        if (!dotnetRef) { tracker.forget(id); return; }
        // .NET answers false when the post failed: the next display may try again.
        dotnetRef.invokeMethodAsync('OnMailSeen', id).then(ok => { if (ok === false) tracker.forget(id); }, () => tracker.forget(id));
      },
    });
  }

  window.ipopMailSeen = {
    ...api,
    /// rootSelector: the Mail page container. dotnet: object with [JSInvokable] OnMailSeen(string id).
    bind(rootSelector, dotnet) {
      window.ipopMailSeen.unbind();
      const root = document.querySelector(rootSelector);
      if (!root) return false;
      dotnetRef = dotnet;
      const t = getTracker();
      const later = () => setTimeout(attention, 0);   // focus moving into an iframe blurs the window
      state = { root, tracker: t, raf: 0, later };
      state.mo = new MutationObserver(schedule);
      state.mo.observe(root, { subtree: true, childList: true, attributes: true, attributeFilter: ['data-mail-seen', 'class', 'style'] });
      document.addEventListener('scroll', schedule, { capture: true, passive: true });
      window.addEventListener('resize', schedule, { passive: true });
      window.addEventListener('focus', attention);
      window.addEventListener('blur', later);
      document.addEventListener('visibilitychange', attention);
      attention();
      scan();
      return true;
    },
    unbind() {
      if (!state) return;
      state.mo.disconnect();
      if (state.raf) cancelAnimationFrame(state.raf);
      document.removeEventListener('scroll', schedule, { capture: true });
      window.removeEventListener('resize', schedule);
      window.removeEventListener('focus', attention);
      window.removeEventListener('blur', state.later);
      document.removeEventListener('visibilitychange', attention);
      state.tracker.reset();
      state.tracker.setAttention(false);
      state = null;
      dotnetRef = null;
    },
    /// The server did not take it (network): let the next display retry.
    forget(id) { if (tracker) tracker.forget(id); },
  };
})();
