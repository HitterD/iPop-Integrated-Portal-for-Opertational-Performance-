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
  },
  focus(id) { const el = document.getElementById(id); if (el) el.focus(); },
  // Files live behind /api/attachments, which wants the bearer token: fetch, then open (pictures, PDF) or save.
  async open(apiBase, token, id, name, inTab) {
    const base = String(apiBase || '').replace(/\/+$/, '');
    // Open the tab now, while the click still counts as a user gesture; fill it once the file is here.
    const tab = inTab ? window.open('', '_blank') : null;
    const res = await fetch(`${base}/api/attachments/${id}`, { headers: token ? { Authorization: 'Bearer ' + token } : {} });
    if (!res.ok) { if (tab) tab.close(); throw new Error('download failed ' + res.status); }
    const url = URL.createObjectURL(await res.blob());
    if (tab) { tab.location.href = url; }
    else { const a = document.createElement('a'); a.href = url; a.download = name || 'file'; document.body.appendChild(a); a.click(); a.remove(); }
    setTimeout(() => URL.revokeObjectURL(url), 60000);
  }
};
