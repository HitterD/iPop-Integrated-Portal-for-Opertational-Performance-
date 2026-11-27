// Remote window › Session history (BIGIS #8 v3, mockup png/02): who, PC, when, length, mode, result, files; search,
// result filter, person filter (Admins), Export (CSV to Downloads). ICT people see only their own sessions; Admins
// see everyone's and the Admin changes to PCs ("Settings changed" rows). Kept for the policy's retention.
(function () {
  const { api, esc, post } = window.app;
  const RES = { done: 'Done', declined: 'Declined', expired: 'No answer', denied: 'Wrong password', locked: 'Locked', dropped: 'Connection lost', cfg: 'Settings changed' };
  const CLS = { done: 'on', declined: '', expired: '', denied: 'bad', locked: 'bad', dropped: 'warn' };
  let rows = [], f = 'all', who = 'all', qs = '', retention = 365, host = null, me = {};
  const when = iso => new Date(iso).toLocaleString('en-GB', { weekday: 'short', day: 'numeric', month: 'short', hour: '2-digit', minute: '2-digit', hourCycle: 'h23' });
  const dur = s => s == null ? '—' : s < 60 ? Math.round(s) + ' s' : s < 3600 ? Math.round(s / 60) + ' min' : Math.floor(s / 3600) + ' h ' + Math.round((s % 3600) / 60) + ' min';

  const line = h => h.result === 'cfg'
    ? `<tr class="cfg"><td>${when(h.at)}</td><td>${esc(h.who)}</td><td><b>${esc(h.pc)}</b></td><td colspan="4" style="color:var(--ink-3)">${IC('settings', 'lucide')} ${esc(h.what)}</td></tr>`
    : `<tr><td>${when(h.at)}</td><td>${esc(h.who)}</td><td><b>${esc(h.pc)}</b>${h.ua ? ' <span class="ua">UA</span>' : ''}</td><td>${dur(h.seconds)}</td><td><span class="pill">${h.mode === 'view' ? 'View only' : 'Control'}</span></td><td><span class="res ${CLS[h.result] || ''}">${RES[h.result] || esc(h.result)}</span></td><td class="fl">${esc((h.files || '').split('\n').filter(Boolean).join(', ') || '—')}</td></tr>`;

  function render() {
    const mine = !me.isAdmin;
    const list = rows.filter(h => (f === 'all' || h.result === f) && (who === 'all' || h.who === who) && (!qs || (h.pc + ' ' + h.who).toLowerCase().includes(qs.toLowerCase())));
    const people = [...new Set(rows.map(h => h.who))].sort();
    host.innerHTML = `<div class="hbar"><label class="srch" style="margin:0;flex:1">${IC('search')}<input id="hq" placeholder="Search PC or person…" value="${esc(qs)}"></label>
<select id="hf"><option value="all">All results</option>${Object.entries(RES).filter(([k]) => !mine || k !== 'cfg').map(([k, v]) => `<option value="${k}"${f === k ? ' selected' : ''}>${v}</option>`).join('')}</select>
${mine ? '' : `<select id="hw"><option value="all">Everyone</option>${people.map(p => `<option${who === p ? ' selected' : ''}>${esc(p)}</option>`).join('')}</select>`}
<button class="btn" id="hx" title="Export as CSV">${IC('download')}Export</button></div>
<div class="hsum">${list.length} session${list.length === 1 ? '' : 's'}${mine ? ' · yours only' : ''} · kept ${retention} days</div>
<table class="log"><tr><th>When</th><th>ICT</th><th>PC</th><th>Length</th><th>Mode</th><th>Result</th><th>Files</th></tr>${list.map(line).join('') || '<tr><td colspan="7" class="empty" style="padding:30px">No sessions match</td></tr>'}</table>`;
    host.querySelector('#hf').onchange = e => { f = e.target.value; render(); };
    const hw = host.querySelector('#hw'); if (hw) hw.onchange = e => { who = e.target.value; render(); };
    const hq = host.querySelector('#hq'); hq.oninput = () => { qs = hq.value; render(); const i = host.querySelector('#hq'); i.focus(); i.setSelectionRange(qs.length, qs.length); };
    host.querySelector('#hx').onclick = () => post({ type: 'download', id: 'remote-history', path: 'api/notifier/remote/history?csv=true', name: 'remote-sessions.csv' });
  }

  async function show(el, who_) {
    host = el; me = who_ || {};
    const [r, p] = await Promise.all([api('GET', 'api/notifier/remote/history'), api('GET', 'api/notifier/remote/policy')]);
    rows = r.ok ? r.json : [];
    if (p.ok) retention = p.json.policy.retentionDays;
    document.querySelector('#nHist').textContent = rows.filter(h => h.result !== 'cfg').length;
    render();
  }
  app.on('downloadDone', m => { if (m.id === 'remote-history') app.snack(`Exported ${rows.length} sessions to Downloads\\${m.name || 'remote-sessions.csv'}`); });

  window.bookHistory = { show };
})();
