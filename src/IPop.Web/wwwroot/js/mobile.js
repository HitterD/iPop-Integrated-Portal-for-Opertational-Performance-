// Mobile helpers (<= 767px). See .scratch/backup-mobile/CONTRACT.md.
(function () {
  const ipop = window.ipop = window.ipop || {};
  const mq = window.matchMedia('(max-width: 767px)');
  // One open detail per page: its .NET callback runs when the browser/phone Back pops our entry.
  let detail = null;

  window.addEventListener('popstate', () => {
    if (!detail) return;
    const d = detail; detail = null;
    d.ref.invokeMethodAsync(d.method).catch(() => {});
  });

  ipop.mobile = {
    isMobile: () => mq.matches,
    hideTabbar: on => document.body.classList.toggle('m-hide-tabbar', !!on),
    /** Push a history entry for an opened detail; Back calls ref.method() to close it. */
    pushDetail: (ref, method) => {
      if (!mq.matches) return false;
      if (detail) { detail = { ref, method }; return true; } // already pushed: just retarget
      detail = { ref, method };
      history.pushState({ ipopDetail: true }, '', location.href);
      return true;
    },
    /** Close via the in-app back button: drop our history entry without calling back. */
    popDetail: () => {
      if (!detail) return;
      detail = null;
      if (history.state && history.state.ipopDetail) history.back();
    },
    /** Forget the entry without navigating (e.g. the page is being disposed). */
    clearDetail: () => { detail = null; },
  };
})();
