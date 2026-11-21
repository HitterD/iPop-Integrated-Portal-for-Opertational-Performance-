// The Hub: after each render, flash and scroll to the post that just arrived or was just sent.
window.ipopHub = {
  after(postId) {
    if (!postId) return;
    const el = document.getElementById('hub-p-' + postId);
    const box = document.getElementById('hub-scroll');
    if (!el || !box) return;
    const top = el.getBoundingClientRect().top - box.getBoundingClientRect().top + box.scrollTop;
    box.scrollTo({ top: Math.max(0, top - 90), behavior: matchMedia('(prefers-reduced-motion: reduce)').matches ? 'auto' : 'smooth' });
    el.classList.remove('flash'); void el.offsetWidth; el.classList.add('flash');
  }
};
