// Maximized popup = "full size per source" (Notifier 1.5.1, mockup docs/mockups/notifier/priority.html screen 7).
// popup.js loads the notification's real data (view.src) and, while the window is maximized (html.max), asks this
// file for the layout of that source: Mail reads like Outlook, an iDesk ticket shows its steps and conversation, Chat
// becomes a conversation, a Meeting an event card, News an article, a Projects task a task card, Notes a page with
// its thread, an Approval its facts, and iPop's own / other systems' notices a centred info card.
// Only data the API gives is shown; anything the mockup shows that iPop doesn't know is left out (never faked).
(function () {
  const esc = s => String(s ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
  const ic = n => (typeof IC === 'function' ? IC(n) : '');
  const I = n => `style="--i:${n}"`;
  const ini = n => (typeof initials === 'function' ? initials(n) : String(n || '?').slice(0, 2).toUpperCase());
  const col = n => (typeof avatarColor === 'function' ? avatarColor(n) : '#5B6B78');
  const av = (name, cls) => `<span class="av ${cls || ''}" style="background:${col(name)}">${esc(ini(name))}</span>`;
  const first = n => String(n || '').trim().split(/\s+/)[0] || '';

  // ── pure helpers (tests/notifier-popup-full.test.cjs) ─────────────────────
  const hm = d => new Date(d).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit', hourCycle: 'h23' });
  const dayKey = d => { const x = new Date(d); return x.getFullYear() + '-' + x.getMonth() + '-' + x.getDate(); };
  // "Today", "Yesterday", "Wednesday" (within 6 days), else "Mon 17 Nov".
  function dayLabel(d, now) {
    const x = new Date(d), n = now ? new Date(now) : new Date();
    const a = new Date(x.getFullYear(), x.getMonth(), x.getDate()), b = new Date(n.getFullYear(), n.getMonth(), n.getDate());
    const days = Math.round((b - a) / 864e5);
    if (days === 0) return 'Today';
    if (days === 1) return 'Yesterday';
    if (days > 1 && days < 7) return x.toLocaleDateString('en-GB', { weekday: 'long' });
    return x.toLocaleDateString('en-GB', { weekday: 'short', day: 'numeric', month: 'short' });
  }
  // "Today 10:05", "Yesterday 16:40", "Fri 14 Nov 09:00".
  const when = (d, now) => d ? dayLabel(d, now) + ' ' + hm(d) : '';

  // Body lines "Key: value" → a fact table. Only when there are at least two such lines (one "Note: …" sentence stays
  // text). Keys are short labels; a value that opens with a quote is a chat line ("Rizky: “…”"), not a fact.
  function keyValues(text) {
    const lines = String(text || '').split(/\r?\n/);
    const pairs = [], rest = [];
    for (const l of lines) {
      const m = /^\s*([A-Za-z][A-Za-z0-9 ./()&-]{0,30}?)\s*:\s+(\S.{0,200})$/.exec(l);
      if (m && !/^[“"']/.test(m[2])) pairs.push({ k: m[1].trim(), v: m[2].trim() }); else rest.push(l);
    }
    if (pairs.length < 2) return { pairs: [], rest: String(text || '') };
    return { pairs, rest: rest.join('\n').trim() };
  }

  const WORDS = { epop: 'EPop', hris: 'HRIS', spp: 'SPP', ict: 'ICT', idesk: 'iDesk', ipop: 'iPop', bod: 'BOD', hc: 'HC', nas: 'NAS', sso: 'SSO', pdf: 'PDF' };
  const human = s => String(s || '').split(/[_\-.\s]+/).filter(Boolean)
    .map((w, i) => WORDS[w.toLowerCase()] || (i === 0 ? w[0].toUpperCase() + w.slice(1).toLowerCase() : w.toLowerCase())).join(' ');
  // Where a notice comes from, for the chip on the info card: "From iPop · EPop import", "From Payroll · Slip ready".
  function sourceLabel(eventType) {
    const parts = String(eventType || '').split('.').filter(Boolean);
    if (!parts.length) return 'From iPop';
    const head = parts[0].toLowerCase();
    const what = parts.slice(1).join(' ');
    if (head === 'system') return 'From iPop' + (what ? ' · ' + human(what) : '');
    if (head === 'approval') return 'Approval' + (what ? ' · ' + human(what) : '');
    return 'From ' + human(head) + (what ? ' · ' + human(what) : '');
  }

  // The primary button on an info card names where it goes: "/mail" → "Open in Mail".
  const PAGES = { mail: 'Mail', chat: 'Chat', calendar: 'Calendar', calls: 'Calls', news: 'News', projects: 'Projects', notes: 'Notes',
    tickets: 'Tickets', idesk: 'iDesk', files: 'Files', directory: 'Directory', hub: 'The Hub', notifications: 'Notifications', profile: 'Profile',
    dashboard: 'Dashboard', settings: 'Settings', admin: 'Admin', hall: 'SJA Hall', whiteboard: 'Whiteboard' };
  function openLabel(url) {
    const u = String(url || '');
    if (!u) return 'Open in iPop';
    if (/^https?:\/\//i.test(u)) { try { return 'Open ' + new URL(u).hostname.replace(/^www\./, ''); } catch { return 'Open link'; } }
    const seg = (u.match(/^\/?([a-z-]+)/i) || [])[1];
    return PAGES[(seg || '').toLowerCase()] ? 'Open in ' + PAGES[seg.toLowerCase()] : 'Open in iPop';
  }

  // iDesk ticket steps Open → Assigned → In progress → Resolved → Closed: index of the current one, null = unknown.
  const STEPS = ['Open', 'Assigned', 'In progress', 'Resolved', 'Closed'];
  function ticketStep(status, assigned) {
    switch (String(status || '').toUpperCase()) {
      case 'TODO': return assigned ? 1 : 0;
      case 'IN_PROGRESS': case 'WAITING_VENDOR': return 2;
      case 'RESOLVED': return 3;
      case 'CLOSED': return 4;
      default: return null; // SENDING / FAILED / CANCELLED: no progress line
    }
  }
  // Technician side (no iPop copy of the ticket): the step the notification itself reports, if any.
  function ticketStepOfEvent(eventType, sentence) {
    if (/^Reopened/i.test(sentence || '')) return 2;
    return ({ 'ticket.assigned': 1, 'ticket.reopened': 2, 'ticket.resolved': 3, 'ticket.closed': 4 })[String(eventType || '').toLowerCase()] ?? null;
  }
  // "2 h 41 m left" / "1 h 5 m overdue" (null when there is no target or the ticket is no longer open).
  function slaText(target, now) {
    if (!target) return null;
    const ms = new Date(target) - (now ? new Date(now) : new Date());
    const m = Math.round(Math.abs(ms) / 60000), h = Math.floor(m / 60), mm = m % 60;
    const t = h >= 48 ? Math.round(h / 24) + ' days' : (h ? h + ' h ' : '') + (h && !mm ? '' : mm + ' m');
    return ms >= 0 ? `SLA ${t.trim()} left` : `SLA ${t.trim()} overdue`;
  }
  const duration = (s, e) => { const m = Math.round((new Date(e) - new Date(s)) / 60000); if (m <= 0) return ''; const h = Math.floor(m / 60), r = m % 60;
    return h && r ? `${h} h ${r} min` : h ? (h === 1 ? '1 hour' : `${h} hours`) : `${r} min`; };
  // Agenda written as lines "1. …" / "- …" → list items; anything else stays paragraphs.
  function agendaItems(text) {
    const lines = String(text || '').split(/\r?\n/).map(s => s.trim()).filter(Boolean);
    if (lines.length < 2 || !lines.every(l => /^(\d+[.)]|[-•*])\s+/.test(l))) return null;
    return lines.map(l => l.replace(/^(\d+[.)]|[-•*])\s+/, ''));
  }
  const dueText = (d, now) => { if (!d) return null; const n = now ? new Date(now) : new Date();
    const days = Math.round((new Date(new Date(d).toDateString()) - new Date(n.toDateString())) / 864e5);
    return days === 0 ? 'today' : days === 1 ? 'tomorrow' : days > 1 ? `in ${days} days` : days === -1 ? 'yesterday' : `${-days} days ago`; };
  const paras = t => String(t || '').split(/\n{2,}/).map(p => p.trim()).filter(Boolean).map(p => `<p>${esc(p).replace(/\n/g, '<br>')}</p>`).join('');

  // ── shared pieces ─────────────────────────────────────────────────────────
  function bubbles(list, me) {
    // list: [{ name, me, text, at, atts:[html], sys, reacts }] sorted by time; day separators between days.
    let out = '', day = null;
    for (const x of list) {
      if (x.at && dayKey(x.at) !== day) { day = dayKey(x.at); out += `<div class="daysep">${esc(dayLabel(x.at))}</div>`; }
      if (x.sys) { out += `<div class="sysline">${ic(x.icon || 'circle-dot')}${esc(x.text)}${x.at ? ' · ' + esc(hm(x.at)) : ''}</div>`; continue; }
      const who = x.me ? (me || 'You') : x.name;
      out += `<div class="cm ${x.me ? 'me' : ''}">${av(who)}<div class="bb sel">${x.me ? '' : `<b>${esc(first(x.name) || 'iPop')}</b>`}${esc(x.text)}`
        + (x.atts && x.atts.length ? `<div class="attn">${x.atts.join('')}</div>` : '')
        + `<div class="t">${x.at ? esc(hm(x.at)) : ''}${x.status ? ' · ' + esc(x.status) : ''}</div>`
        + (x.reacts && x.reacts.length ? `<span class="reacts">${x.reacts.map(r => esc(r.emoji) + (r.count > 1 ? `<small>${r.count}</small>` : '')).join(' ')}</span>` : '')
        + `</div></div>`;
    }
    return out;
  }
  const composer = (ph, extra, opts) => `<div class="factions"><div class="composer2">${opts && opts.attach ? '<div class="attq"></div>' : ''}<div class="box">`
    + (opts && opts.attach ? `<button class="ib" data-attach title="Attach">${ic('paperclip')}</button>` : '')
    + `<input data-reply placeholder="${esc(ph)}" aria-label="${esc(ph)}"></div><button class="btn p" data-fsend disabled>${ic('send')}Send</button>${extra || ''}</div>`
    + (opts && opts.attach ? '<input type="file" hidden multiple data-file>' : '') + `</div>`;
  const actions = (primary, rest) => `<div class="factions"><div class="in">${primary || ''}<span class="sp"></span>${rest || ''}</div></div>`;
  const btn = (key, icon, label, cls) => `<button class="btn ${cls || ''}" data-fa="${key}">${ic(icon)}${esc(label)}</button>`;
  const sect = (icon, label, link) => `<div class="sect2">${ic(icon)}${esc(label)}${link || ''}</div>`;
  const chip = (cls, icon, text) => `<span class="fchip ${cls}">${icon ? ic(icon) : ''}${esc(text)}</span>`;
  const leaderChip = N => N.leader ? chip('lead', 'crown', N.leader === 'VIP' ? 'VIP' : 'Your leader · ' + N.leader) : '';
  const skel = `<div class="fbody"><div class="fcol pane"><div class="skel" style="height:30px;width:60%"></div><div class="skel" style="height:16px;width:40%;margin-top:18px"></div>`
    + `<div class="skel" style="height:14px;margin-top:28px"></div><div class="skel" style="height:14px;margin-top:10px;width:92%"></div><div class="skel" style="height:14px;margin-top:10px;width:80%"></div></div></div>`;

  // ── per source ────────────────────────────────────────────────────────────
  // env: { N, view, me, nik, files (window.files) }. Returns the HTML between the title bar and the end of the window.
  const R = {};

  R.mail = env => {
    const { N, view } = env, s = view.src, d = s.d;
    const isMe = r => (r.nik || '').toLowerCase() === (env.nik || '').toLowerCase() && !!env.nik;
    const people = t => (d.recipients || []).filter(r => r.recipientType === t).sort((a, b) => isMe(b) - isMe(a));
    const pill = r => `<span class="p">${esc(isMe(r) ? 'You' : r.name)}</span>`;
    const row = (label, list, key) => {
      if (!list.length) return '';
      const shown = list.slice(0, 3).map(pill).join(''), rest = list.slice(3);
      return `<div class="line">${label} <span class="rcpt" data-rcpt="${key}">${shown}${rest.length ? `<span class="more" data-more-rcpt="${key}">+${rest.length} more</span>` : ''}</span></div>`;
    };
    s.hidden = { to: people('to').slice(3).map(pill).join(''), cc: people('cc').slice(3).map(pill).join('') };
    const groups = (d.recipientGroups || []).map(g => `<span class="p">${esc(g.name)}${g.count ? ' · ' + g.count : ''}</span>`).join('');
    const unseen = Math.max(0, (d.recipientTotal || 0) - (d.recipients || []).length);
    const rcpt = d.hideRecipients
      ? `<div class="line">To <span class="rcpt"><span class="p">Hidden list${d.recipientTotal ? ' · ' + d.recipientTotal + ' people' : ''}</span></span></div>`
      : row('To', people('to'), 'to') + row('Cc', people('cc'), 'cc')
        + (groups ? `<div class="line">Groups <span class="rcpt">${groups}</span></div>` : '')
        + (unseen ? `<div class="line"><span class="muted">and ${unseen} more ${unseen === 1 ? 'person' : 'people'}</span></div>` : '');
    const atts = d.attachments || [];
    const pics = atts.filter(a => (a.mimeType || '').startsWith('image/'));
    const total = atts.reduce((n, a) => n + (a.fileSize || 0), 0);
    const filesHtml = atts.length ? `<div ${I(3)}>${sect('paperclip', `${atts.length} attachment${atts.length === 1 ? '' : 's'}${total ? ' · ' + env.files.fmtSize(total) : ''}`,
      atts.length > 1 ? `<button class="lnk" data-dlall>${ic('download')}Download all</button>` : '')}${env.files.grid(view.files)}</div>` : '';
    const chips = [leaderChip(N), d.importance === 'high' ? chip('act', 'circle-alert', 'High importance') : '', d.isMass ? chip('mod', 'users', 'Mass mail') : ''].filter(Boolean).join('');
    return `<div class="fbody"><div class="fcol pane">
      <div class="msubj sel" ${I(0)}>${esc(d.subject || '(no subject)')}</div>
      <div class="mmeta" ${I(1)}>${av(d.senderName, 'lg')}<div class="who2"><b>${esc(d.senderName)}</b>${s.dept ? `<span class="dept"> · ${esc(s.dept)}</span>` : ''}${rcpt}</div>
        <div class="mwhen">${esc(when(d.sentAt || N.at))}${chips ? `<div class="mchips">${chips}</div>` : ''}</div></div>
      <div class="mailbody" ${I(2)}><iframe data-mail sandbox="allow-same-origin" title="Message"></iframe>${pics.map(a => `<img class="img" data-img data-src="api/mail/attachments/${a.id}/download?inline=true" alt="${esc(a.fileName)}">`).join('')}</div>
      ${filesHtml}
    </div></div>
    ${composer(`Reply to ${first(d.senderName) || 'the sender'}…`,
      (s.canAll ? `<button class="btn tog" data-fa="replyall" aria-pressed="false">${ic('reply-all')}Reply all</button>` : '')
      + btn('forward', 'forward', 'Forward') + `<button class="btn ib2" data-fa="read" title="Mark as read">${ic('check')}</button>`, { attach: true })}`;
  };

  R.ticket = env => {
    const { N, view } = env, s = view.src;
    const t = s.t, x = s.x;
    const step = t ? ticketStep(t.status, !!t.assignee) : ticketStepOfEvent(N.eventType, x.sentence);
    const open = t ? !/^(RESOLVED|CLOSED|CANCELLED)$/i.test(t.status || '') : true;
    const sla = t && open ? slaText(t.slaTarget) : null;
    const number = t ? (t.ticketNumber ? '#' + t.ticketNumber : null) : x.number;
    const chips = [number && chip('mod', '', number), t && (t.moduleName || t.category) && chip('mod', '', [t.moduleName, t.category].filter(Boolean).join(' · ')),
      t && t.priority && /HIGH|CRITICAL/i.test(t.priority) && chip('act', 'flame', human(t.priority) + ' priority'),
      t && !STEPS.includes(human(t.status)) && step == null && chip('mod', '', human(t.status)),
      sla && `<span class="sla ${/overdue/.test(sla) ? 'late' : ''}">${ic('timer')}${esc(sla)}</span>`].filter(Boolean).join('');
    const steps = step == null ? '' : `<div class="steps" ${I(2)}>${STEPS.map((nm, i) => {
      const done = i < step || (i === step && step === 4), cur = i === step && step !== 4;
      return (i ? `<div class="stepline ${i <= step ? 'fin' : ''}" style="--i:${i - 1}"></div>` : '')
        + `<div class="step ${done ? 'fin' : cur ? 'cur' : ''}"><i>${done ? '✓' : i + 1}</i>${nm}</div>`;
    }).join('')}</div>`;
    // People: my own ticket → me (reporter) + the PIC; technician side → the reporter + me.
    const pc = (label, name, sub, avName) => `<div class="pc">${av(avName || name)}<div><small>${label}</small><b>${esc(name)}</b>${sub ? `<span>${esc(sub)}</span>` : ''}</div></div>`;
    const people = t
      ? pc('Reported by', 'You' + (env.me ? ` (${env.me})` : ''), null, env.me || 'You') + (t.assignee ? pc('PIC', t.assignee.name, t.moduleName || 'ICT') : pc('PIC', 'Not assigned yet', t.moduleName || 'ICT'))
      : (x.who ? pc('Reported by', x.who.name, x.who.sub) : '') + pc('PIC', 'You' + (env.me ? ` (${env.me})` : ''), null, env.me || 'You');
    // Conversation: what happened (events) and the ticket chat, oldest first.
    let list = [];
    if (t) {
      const evText = e => ({ created: 'Ticket created', assigned: e.assignee ? `Assigned to ${e.assignee}` : 'Assigned', resolved: 'Marked as resolved', closed: 'Closed',
        visit_changed: 'Visit changed', sent: 'Sent to iDesk', send_failed: "Couldn't reach iDesk",
        status_changed: e.toStatus ? `Status: ${human(e.toStatus)}` : 'Status changed' })[e.kind];
      const evIcon = k => ({ created: 'circle-plus', assigned: 'user-check', resolved: 'circle-check', closed: 'lock', visit_changed: 'calendar', sent: 'send', send_failed: 'circle-alert' })[k] || 'circle-dot';
      for (const e of t.events || []) { const tx = evText(e); if (tx) list.push({ sys: true, text: tx, icon: evIcon(e.kind), at: e.occurredAt }); }
      if (t.description) list.push({ me: true, text: t.description, at: t.createdAt, atts: (t.attachments || []).map(a => `<span>${ic('file')}${esc(a.fileName)}</span>`) });
      for (const m of s.msgs || []) list.push({ me: m.mine, name: m.sender || 'ICT', text: m.content, at: m.createdAt, atts: (m.attachments || []).map(a => `<span>${ic('file')}${esc(a)}</span>`) });
      list.sort((a, b) => new Date(a.at) - new Date(b.at));
    } else {
      const c = s.c;
      if (c) list.push({ name: x.who?.name || c.from, text: c.text, at: N.at });
      else list.push({ sys: true, text: x.sentence || N.title, icon: /^Reopened/.test(x.sentence || '') ? 'rotate-ccw' : 'user-check', at: N.at });
    }
    const reply = view.reply;
    const to = t ? (t.assignee?.name || 'ICT') : (x.who?.name || 'the reporter');
    const extra = (s.confirm ? btn('confirm', 'check', 'Confirm in iDesk') : '') + btn('idesk', 'external-link', 'Open in iDesk');
    return `<div class="fbody"><div class="fcol pane">
      ${chips ? `<div class="chiprow" ${I(0)}>${chips}</div>` : ''}
      <div class="ttitle sel" ${I(1)}>${esc((t && t.title) || x.title || N.title)}</div>
      ${steps}
      <div class="people2" ${I(3)}>${people}</div>
      <div ${I(4)}>${sect('messages-square', 'Conversation')}<div class="convo" data-convo>${bubbles(list, env.me)}</div></div>
    </div></div>
    ${reply ? composer(`Reply to ${first(to)}…`, extra) : actions(btn('idesk', 'external-link', 'Open in iDesk', 'p'), btn('read', 'check', 'Mark as read'))}`;
  };

  R.chat = env => {
    const { N, view } = env, s = view.src;
    const items = (s.items || []).filter(x => !x.isDeleted && x.type !== 'system').slice().sort((a, b) => new Date(a.createdAt) - new Date(b.createdAt));
    const mine = x => !!env.nik && (x.senderNik || '').toLowerCase() === env.nik.toLowerCase();
    const them = items.filter(x => !mine(x)).slice(-1)[0];
    const room = s.room;
    const group = room && room.type === 'group';
    const other = room && !group ? (room.members || []).find(m => (m.nik || '').toLowerCase() !== (env.nik || '').toLowerCase()) : null;
    const name = group ? (room.name || 'Group chat') : (other?.name || them?.senderName || N.title);
    const pres = other && ({ on: ['on', 'Online'], away: ['away', 'Away'], busy: ['busy', 'Busy'] })[other.presenceStatus];
    const sub = group ? `${room.memberCount || (room.members || []).length} members` : s.dept || '';
    let fi = 0;
    const list = items.map(x => ({ me: mine(x), name: x.senderName, at: x.createdAt, status: mine(x) && x.deliveryStatus === 'read' ? 'Read' : '',
      text: (window.app && app.plainChat ? app.plainChat(x.content) : x.content) || '',
      reacts: x.reactions || [],
      atts: (x.attachments || []).map(a => `<button data-fp="${fi++}">${ic(/^image\//.test(a.mimeType || '') ? 'image' : 'file-text')}${esc(a.fileName)}</button>`) }));
    return `<div class="fbody"><div class="fcol pane">
      <div class="chathead" ${I(0)}>${av(name, 'lg')}<div><b>${esc(name)}</b><div class="s">${esc(sub)}${sub && pres ? ' · ' : ''}${pres ? `<span class="presence ${pres[0]}"><i></i>${pres[1]}</span>` : ''}</div></div>${N.leader ? `<span style="margin-left:auto">${leaderChip(N)}</span>` : ''}</div>
      <div class="convo" data-convo ${I(1)}>${bubbles(list, env.me)}</div>
      <div class="quick" ${I(2)}>${['On it 👍', 'Will do', 'Thank you'].map(q => `<button data-quick="${esc(q)}">${esc(q)}</button>`).join('')}</div>
    </div></div>
    ${composer(group ? `Message ${name}…` : `Message ${first(name)}…`, btn('open', 'arrow-up-right', 'Open chat') + `<button class="btn ib2" data-fa="read" title="Mark as read">${ic('check')}</button>`)}`;
  };

  R.meeting = env => {
    const { N, view } = env, s = view.src, src = s.mt || s.ev;
    if (!src) return R.system(env);
    const st = new Date(src.startAt), en = new Date(src.endAt);
    const room = src.roomName || src.location;
    const rows = [`${ic('clock')}${esc(hm(st))} – ${esc(hm(en))}${duration(st, en) ? ' · ' + esc(duration(st, en)) : ''}`,
      room && `${ic('map-pin')}${esc(room)}`,
      s.key && `${ic('video')}On an iPop meeting room`,
      !s.key && s.link && `${ic('link')}<span class="sel lk">${esc(s.link)}</span>`,
      (s.mt?.organizerName || s.ev?.ownerName) && `${ic('user')}Organised by ${esc(s.mt?.organizerName || s.ev?.ownerName)}`].filter(Boolean);
    const mins = (st - Date.now()) / 60000;
    const live = Date.now() >= +st && Date.now() < +en;
    const cd = mins > 0 && mins <= 60 ? `<div class="countdown">${ic('bell-ring')}Starts in <span data-cd="${st.toISOString()}">${fmtCd(st)}</span></div>`
      : live ? `<div class="countdown live">${ic('radio')}Happening now · until ${esc(hm(en))}</div>` : '';
    const agendaText = s.mt?.agenda || s.mt?.description || s.ev?.notes;
    const items = agendaItems(agendaText);
    const agenda = agendaText ? `<div ${I(1)}>${sect('list-ordered', 'Agenda')}${items ? `<ol class="agenda">${items.map(a => `<li>${esc(a)}</li>`).join('')}</ol>` : `<div class="agtext sel">${paras(agendaText)}</div>`}</div>` : '';
    const RS = { accept: ['y', 'Going'], maybe: ['m', 'Maybe'], decline: ['n', 'Declined'], pending: ['m', 'No reply'] };
    const people = (src.attendees || []).map(a => ({ name: a.name || a.displayName, nik: a.nik, rsvp: a.rsvpStatus || a.rsvp || 'pending', req: a.required }));
    const orgNik = (s.mt?.organizerNik || s.ev?.ownerNik || '').toLowerCase();
    const going = people.filter(p => p.rsvp === 'accept').length;
    const isMe = p => !!env.nik && (p.nik || '').toLowerCase() === env.nik.toLowerCase();
    const att = people.length ? `<div ${I(2)}>${sect('users', `${people.length} ${people.length === 1 ? 'person' : 'people'} · ${going} going`)}<div class="attd">${people.map(p => {
      const r = RS[p.rsvp] || RS.pending;
      const role = (p.nik || '').toLowerCase() === orgNik ? 'Organiser' : p.req === false ? 'Optional' : p.req === true ? 'Required' : '';
      return `<div class="pc">${av(p.name)}<div><b>${esc(isMe(p) ? 'You' : p.name)}</b>${role ? `<span>${role}</span>` : ''}</div><span class="rsvp ${r[0]}" ${isMe(p) ? 'data-myrsvp' : ''}>${r[1]}</span></div>`;
    }).join('')}</div></div>` : '';
    const my = s.mt && s.invited ? s.mt.myResponse : null;
    const seg = s.mt && s.invited ? `<div class="seg3" data-rsvpseg><span class="thumb"></span>${[['accept', 'check', 'Accept'], ['maybe', 'circle-help', 'Maybe'], ['decline', 'x', 'Decline']]
      .map(r => `<button data-r="${r[0]}" class="${my === r[0] ? 'on' : ''}">${ic(r[1])}${r[2]}</button>`).join('')}</div>` : '';
    return `<div class="fbody"><div class="fcol pane">
      <div class="evcard" ${I(0)}><div class="datecard"><div class="mo">${esc(st.toLocaleDateString('en-GB', { month: 'short' }))}</div><div class="dd">${st.getDate()}</div><div class="wd">${esc(st.toLocaleDateString('en-GB', { weekday: 'long' }))}</div></div>
        <div><div class="evt sel">${esc(src.title)}</div>${rows.map(r => `<div class="evrow">${r}</div>`).join('')}${cd}</div></div>
      ${agenda}${att}
    </div></div>
    ${actions((s.canJoin ? btn('join', 'video', 'Join now', 'p') : '') + seg + (!s.canJoin && !seg ? btn('open', 'arrow-up-right', 'Open in Calendar', 'p') : ''),
      btn('snooze', 'alarm-clock', 'Snooze') + (s.canJoin || seg ? btn('open', 'arrow-up-right', 'Open') : '') + btn('read', 'check', 'Mark as read'))}`;
  };
  function fmtCd(st) { const s = Math.max(0, Math.round((new Date(st) - Date.now()) / 1000)); return `${String(Math.floor(s / 60)).padStart(2, '0')}:${String(s % 60).padStart(2, '0')}`; }

  R.news = env => {
    const { N, view } = env, s = view.src, p = s.p;
    const kind = p.type === 'memo' ? 'Memo' : p.type === 'condolence' ? 'Condolence' : 'News';
    const words = String(p.body || '').replace(/<[^>]+>/g, ' ').split(/\s+/).filter(Boolean).length;
    const read = words ? Math.max(1, Math.round(words / 200)) + ' min read' : '';
    const ack = p.requiresAcknowledgement;
    const pct = p.ackTotal ? Math.min(100, Math.round(p.ackCount / p.ackTotal * 1000) / 10) : 0;
    const ackbar = ack ? `<div class="ackbar" ${I(2)}><div class="tx">${p.ackTotal ? `<b data-ackn>${(p.ackCount || 0).toLocaleString('en-US')} of ${p.ackTotal.toLocaleString('en-US')}</b> people acknowledged<div class="prog"><i data-ackbar style="--w:${pct}%"></i></div>` : (p.isAcknowledged ? 'You acknowledged this.' : 'Please read and acknowledge.')}</div>`
      + (p.isAcknowledged ? `<button class="btn" disabled>${ic('check')}Acknowledged</button>` : `<button class="btn p" data-fa="ack">${ic('check')}Acknowledge</button>`) + `</div>` : '';
    const files = (p.attachments || []).length ? `<div ${I(3)}>${sect('paperclip', `${p.attachments.length} attachment${p.attachments.length === 1 ? '' : 's'}`)}${env.files.grid(view.fullFiles)}</div>` : '';
    const coverStyle = s.coverUrl ? ` style="background-image:linear-gradient(transparent 35%,rgba(0,0,0,.55)),url('${esc(s.coverUrl)}')"` : '';
    return `<div class="fbody"><div class="pane">
      <div class="cover ${s.coverUrl ? 'img' : ''}"${coverStyle}>${s.coverUrl ? '' : '<div class="shine"></div>'}<div class="ct"><small>${esc(kind)}${ack ? ' · Please acknowledge' : ''}</small><h1 class="sel">${esc(p.title)}</h1></div></div>
      <div class="fcol">
        <div class="byline" ${I(0)}>${av(p.authorName)}<div><b>${esc(p.authorName)}</b>${s.dept ? ' · ' + esc(s.dept) : ''}<br>${esc(when(p.publishedAt || N.at))}${read ? ' · ' + read : ''}</div></div>
        <div class="article sel" ${I(1)}>${view.html ? '<iframe data-mail data-article sandbox="allow-same-origin" title="Post"></iframe>' : paras(p.body)}</div>
        ${ackbar}${files}
      </div></div></div>
    ${actions(btn('open', 'arrow-up-right', 'Open in iPop', ack && !p.isAcknowledged ? '' : 'p'), btn('read', 'check', 'Mark as read'))}`;
  };

  const ST = { todo: ['To do', 'todo'], in_progress: ['In progress', 'prog'], review: ['In review', 'rev'], done: ['Done', 'dn'] };
  R.project = env => {
    const { N, view } = env, s = view.src, t = s.t;
    const done = t.status === 'done';
    const stn = ST[t.status] || [t.status || 'Task', 'todo'];
    const subs = s.subs || [];
    const nSub = subs.length || t.subtaskCount || 0, nDone = subs.length ? subs.filter(x => x.status === 'done').length : t.subtaskDoneCount || 0;
    const due = t.dueDate ? `${esc(new Date(t.dueDate).toLocaleDateString('en-GB', { weekday: 'short', day: 'numeric', month: 'short' }))} · <span class="${!done && new Date(t.dueDate) < new Date(new Date().toDateString()) ? 'late' : 'soon'}">${esc(dueText(t.dueDate))}</span>` : '';
    const names = (t.assignees || []).map(a => (env.nik && (a.nik || '').toLowerCase() === env.nik.toLowerCase()) ? 'You' : a.name);
    const props = [
      [ic('circle-dot') + 'Status', `<div><span class="stpill ${stn[1]}" data-stpill>${esc(stn[0])}</span></div>`],
      names.length && [ic('users') + 'Assignees', `<div class="avs"><div class="row2">${(t.assignees || []).slice(0, 5).map(a => av(a.name)).join('')}</div><span>${esc(names.join(', '))}</span></div>`],
      due && [ic('calendar') + 'Due', `<div>${due}</div>`],
      t.priority && [ic('flag') + 'Priority', `<div>${esc(human(t.priority))}</div>`],
      nSub && [ic('gauge') + 'Progress', `<div class="prog2"><div class="bar2"><i data-pbar style="--w:${Math.round(nDone / nSub * 100)}%"></i></div><span data-ptxt>${nDone} of ${nSub}</span></div>`],
    ].filter(Boolean);
    const check = '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="3" stroke-linecap="round" stroke-linejoin="round"><path d="M20 6 9 17l-5-5"/></svg>';
    const checklist = subs.length ? `<div ${I(2)}>${sect('list-checks', 'Checklist')}<ul class="checklist">${subs.map(c => `<li class="${c.status === 'done' ? 'ok' : ''}" data-sub="${esc(c.id)}" data-was="${esc(c.status === 'done' ? 'todo' : c.status)}"><span class="chk ${c.status === 'done' ? 'on' : ''}">${check}</span><span>${esc(c.title)}</span></li>`).join('')}</ul></div>` : '';
    const comments = (s.comments || []).map(c => ({ me: !!env.nik && (c.authorNik || '').toLowerCase() === env.nik.toLowerCase(), name: c.authorName, text: c.content, at: c.createdAt }));
    return `<div class="fbody"><div class="fcol pane">
      <div class="taskhead ${done ? 'isdone' : ''}" data-taskhead ${I(0)}><button class="chk ${done ? 'on' : ''}" data-fa="taskdone" title="${done ? 'Done' : 'Mark done'}">${check}</button><div>${s.crumb ? `<div class="crumb">${esc(s.crumb)}</div>` : ''}<h2 class="sel">${esc(t.title || N.title)}</h2></div></div>
      <div class="props" ${I(1)}>${props.map(p => `<span>${p[0]}</span>${p[1]}`).join('')}</div>
      ${t.description ? `<div class="desc sel" ${I(2)}>${paras(t.description)}</div>` : ''}
      ${checklist}
      <div ${I(3)}>${sect('message-square', comments.length ? `Comments · ${comments.length}` : 'Comments')}<div class="convo" data-convo>${comments.length ? bubbles(comments, env.me) : `<div class="sysline">${ic('message-square')}${esc(N.body || 'No comments yet')}</div>`}</div></div>
    </div></div>
    ${composer('Comment on this task…', btn('open', 'arrow-up-right', 'Open project'))}`;
  };

  R.notes = env => {
    const { N, view } = env, s = view.src, th = s.th;
    const page = (/ in (.+)$/.exec(N.title || '') || [])[1] || 'a page';
    const ctx = th?.quote || N.body;
    const list = th ? th.comments.map(c => ({ me: !!env.nik && (c.authorNik || '').toLowerCase() === env.nik.toLowerCase(), name: c.authorName, text: c.body, at: c.createdAt })) : [];
    return `<div class="fbody"><div class="fcol pane">
      <div class="nlead" ${I(0)}>${esc(N.title)}</div>
      <div class="notepage" ${I(1)}><div class="np-h"><span class="emo">${ic('notebook-pen')}</span><div><b>${esc(page)}</b>${th ? `<br>${th.quote ? 'Comment on the highlighted text' : 'Page comment'}` : ''}</div></div>
        ${ctx ? `<div class="np-b sel"><p><mark>${esc(ctx)}</mark></p></div>` : ''}</div>
      ${th ? `<div ${I(2)}>${sect('messages-square', `Thread · ${th.comments.length} ${th.comments.length === 1 ? 'comment' : 'comments'}`)}<div class="convo" data-convo>${bubbles(list, env.me)}</div></div>` : ''}
    </div></div>
    ${view.reply ? composer('Reply in the thread…', btn('open', 'arrow-up-right', 'Open page')) : actions(btn('open', 'arrow-up-right', 'Open page', 'p'), btn('read', 'check', 'Mark as read'))}`;
  };

  R.approval = env => {
    const { N } = env;
    const kv = keyValues(N.body);
    const amount = kv.pairs.find(p => /^(total|amount|nilai|jumlah)$/i.test(p.k));
    const facts = kv.pairs.filter(p => p !== amount);
    return `<div class="fbody"><div class="fcol pane">
      <div class="apphead" ${I(0)}><div><span class="fchip mod">${esc(sourceLabel(N.eventType))}</span><div class="ttitle sel" style="margin:10px 0 4px">${esc(N.title)}</div>
        <div class="appsub">${N.actorName ? 'From ' + esc(N.actorName) + ' · ' : ''}${esc(when(N.at))}</div></div>
        ${amount || N.priority === 'high' ? `<div class="amt">${amount ? `<small>${esc(amount.k)}</small><b class="sel">${esc(amount.v)}</b>` : ''}${N.priority === 'high' ? `<div>${chip('act', 'alarm-clock', 'High priority')}</div>` : ''}</div>` : ''}</div>
      ${facts.length ? `<div ${I(1)}>${sect('list', 'Details')}<table class="lines kv sel"><tbody>${facts.map((p, i) => `<tr style="--i:${i}"><th>${esc(p.k)}</th><td>${esc(p.v)}</td></tr>`).join('')}</tbody></table></div>` : ''}
      ${kv.rest ? `<div class="apptext sel" ${I(2)}>${paras(kv.rest)}</div>` : ''}
    </div></div>
    ${actions(N.targetUrl ? btn('open', 'external-link', 'Open to approve', 'p') : '', btn('snooze', 'alarm-clock', 'Snooze') + btn('read', 'check', 'Mark as read'))}`;
  };

  // iPop's own notices, other systems (Gateway apps) and anything without a richer layout.
  const SYS_ICON = { epop_import: 'archive-restore', hris: 'user-cog', backup: 'hard-drive', password: 'key-round', security: 'shield', update: 'download', maintenance: 'wrench' };
  R.system = env => {
    const { N, view } = env;
    const kv = keyValues(N.body);
    const sub = String(N.eventType || '').split('.').slice(1).join('_').toLowerCase();
    const icon = N.module === 'calls' ? 'phone' : Object.keys(SYS_ICON).find(k => sub.includes(k)) ? SYS_ICON[Object.keys(SYS_ICON).find(k => sub.includes(k))]
      : N.module && N.module !== 'system' ? ({ mail: 'mail', calendar: 'calendar', news: 'newspaper', projects: 'kanban', notes: 'notebook-pen', chat: 'message-circle', tickets: 'life-buoy' })[N.module] || 'bell' : 'bell';
    const src = N.module === 'calls' ? 'Calls' : sourceLabel(N.eventType);
    const primary = N.module === 'calls' ? btn('callback', 'phone', 'Call back', 'p') : N.targetUrl ? btn('open', 'arrow-up-right', openLabel(N.targetUrl), 'p') : '';
    return `<div class="fbody"><div class="pane"><div class="syscard">
      <div class="big">${ic(icon)}</div>
      <span class="srcchip">${ic('server')}${esc(src)}</span>
      <h1 class="sel">${esc(N.title)}</h1>
      ${kv.rest ? `<div class="systext sel">${paras(kv.rest)}</div>` : ''}
      ${kv.pairs.length ? `<div class="statgrid" style="--n:${Math.min(3, kv.pairs.length)}">${kv.pairs.map(p => `<div><b class="sel">${esc(p.v)}</b><span>${esc(p.k)}</span></div>`).join('')}</div>` : ''}
      <div class="syswhen">${esc(when(N.at))}</div>
    </div></div></div>
    ${actions(primary, btn('read', 'check', 'Mark as read'))}`;
  };

  // Which layout: approvals always by their kind; otherwise what the loader found; else the info card.
  function kindOf(N, view) {
    if (N.kind === 'approval' || /^approval\./i.test(N.eventType || '')) return 'approval';
    return view.src && R[view.src.kind] ? view.src.kind : 'system';
  }
  function render(env) {
    if (!env.loaded) return skel;
    return R[kindOf(env.N, env.view)](env);
  }

  // Header chip in the full title bar: Approval / Your leader / Needs action / Message / For your info.
  function tierChip(N) {
    if (N.kind === 'approval') return `<span class="fchip appr hc">${ic('stamp')}Approval</span>`;
    if (N.leader) return `<span class="fchip lead hc" title="${esc(N.leader)}">${ic('crown')}${N.leader === 'VIP' ? 'VIP' : 'Your leader'}</span>`;
    if (N.tier === 'act') return `<span class="fchip act hc">Needs action</span>`;
    if (N.tier === 'info') return `<span class="fchip mod hc">For your info</span>`;
    return `<span class="fchip thr hc">Message</span>`;
  }

  window.popupFull = { render, kindOf, tierChip, bubbles, fmtCd, when,
    // exposed for tests
    keyValues, sourceLabel, openLabel, ticketStep, ticketStepOfEvent, slaText, dayLabel, agendaItems, duration, dueText, human };
})();
