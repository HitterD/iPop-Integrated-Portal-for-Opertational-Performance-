// One notification window (mockup v2) or card (mockup v1). Loads the detail from iPop by the notification's target
// and offers the module's quick actions; anything bigger opens iPop (already signed in there in the browser).
(function () {
  const { api, image, esc, post, snack } = window.app;
  const G = '([0-9a-fA-F-]{36})';
  let N, STYLE, ME, NIK, MORE = 0, root, view = {};

  app.on('notice', m => { N = m.notice; STYLE = m.style; ME = m.me; NIK = m.nik; MORE = m.more || 0; render(); });
  app.on('more', m => { MORE = m.count; showMore(); });
  function showMore() { const r = document.querySelector('.more-row'); if (!r) return; r.classList.toggle('on', MORE > 0); r.querySelector('b').textContent = MORE; size(); }
  app.ready();

  // ── layout ────────────────────────────────────────────────────────────────
  function render() {
    const mod = MOD[N.module] || MOD.system;
    document.body.className = STYLE === 'stack' ? 'card' : 'win';
    root = document.getElementById('root');
    root.style.setProperty('--m', mod.c);
    view = { tools: [], chips: [], title: N.title, who: null, body: `<p class="txt sel">${esc(N.body)}</p>`, done: 'Done' };
    shell();
    load().catch(() => { }).finally(() => { shell(); });
  }

  function shell() {
    const mod = MOD[N.module] || MOD.system;
    const time = timeShort(N.at);
    const label = t => STYLE === 'stack' ? t.label.replace('Open in iPop ', 'Open ').replace('Open in iDesk ', 'iDesk ') : t.label;
    const tools = view.tools.map((t, i) => `<button class="${STYLE === 'stack' ? 'btn' : 'tb2'} ${t.cls || ''}" data-t="${i}" ${t.disabled ? 'disabled' : ''}>${label(t)}</button>`);
    const sp = view.tools.findIndex(t => t.right);
    if (sp >= 0) tools.splice(sp, 0, '<span class="sp"></span>');
    const who = view.who ? `<div class="who"><span class="av" style="background:${avatarColor(view.who.name)}" ${view.who.photo ? `data-photo="${esc(view.who.photo)}"` : ''}>${esc(initials(view.who.name))}</span><span class="nm"><b>${esc(view.who.name)}</b><small>${esc(view.who.sub || '')}</small></span>${STYLE === 'stack' ? '' : `<span class="when">${esc(time)}</span>`}</div>` : '';
    const chips = view.chips.length ? `<div class="chips">${view.chips.map(c => `<span class="chip ${c.st ? 'st' : ''}">${esc(c.t)}</span>`).join('')}</div>` : '';
    const reply = view.reply ? replyBox() : '';
    const done = `<div class="done"><span class="ck">${ICON.check}</span><span id="doneText">${esc(view.done)}</span></div>`;
    const wasReplying = root.firstElementChild?.classList.contains('replying');
    const draft = root.querySelector('.ed')?.innerHTML;

    if (STYLE === 'stack') {
      root.innerHTML = `<div class="pop" data-drag><i class="bar"></i>
        <div class="hd"><span class="ico">${ICON[N.module] || ICON.system}</span><span class="src"><b>${esc(mod.n)}</b><small>${esc(time)}</small></span><button class="x" data-close title="Close">${ICON.x}</button></div>
        <div class="bd"><div class="ttl2">${esc(view.title)}</div>${view.who ? `<div class="from">${who}</div>` : ''}${chips}<div class="msg sel">${view.body}</div></div>
        ${reply}
        ${view.tools.length ? `<div class="acts">${tools.join('')}</div>` : ''}
        ${done}
        <div class="more-row"><span>+<b>0</b> more waiting</span></div></div>`;
    } else {
      root.innerHTML = `<div class="win">
        <div class="ttl" data-drag><span class="ic">${ICON[N.module] || ICON.system}</span><b>${esc(N.title)}</b><small>${esc(time)}</small><button class="wc" data-min title="Minimize">${ICON.min}</button><button class="wc x" data-close title="Close">${ICON.x}</button></div>
        ${view.tools.length ? `<div class="tools">${tools.join('')}</div>` : ''}
        <div class="hdr"><h3 class="sel">${esc(view.title)}</h3>${who}${chips}</div>
        <div class="body">${view.body}</div>
        ${reply}${done}</div>`;
    }
    const box = root.firstElementChild;
    if (wasReplying && view.reply) { box.classList.add('replying'); const ed = box.querySelector('.ed'); if (ed && draft) ed.innerHTML = draft; }
    wire(box);
    hydrate(box);
    if (STYLE === 'stack') showMore();
    size();
  }

  function replyBox() {
    const r = view.reply;
    const rich = r.rich ? `<div class="rt"><button data-cmd="bold" title="Bold">B</button><button data-cmd="italic" title="Italic"><i>I</i></button><button data-cmd="underline" title="Underline"><u>U</u></button><button data-cmd="insertUnorderedList" title="List">•</button>${r.attach ? '<button data-attach title="Attach">' + IC('paperclip') + '</button>' : ''}</div>` : '';
    return `<div class="reply"><div class="rte">${rich}<div class="ed" contenteditable="true" data-ph="${esc(r.placeholder || 'Write a reply…')}"></div><div class="att"></div>
      <div class="err" hidden></div><div class="rf"><small id="replyTo">${esc(r.hint || '')}</small><button class="${STYLE === 'stack' ? 'btn' : 'tb2'}" data-cancel>Cancel</button><button class="${STYLE === 'stack' ? 'btn' : 'tb2'} p" data-send>Send</button></div></div>
      <input type="file" hidden multiple data-file></div>`;
  }

  function wire(box) {
    box.addEventListener('click', async e => {
      const b = e.target.closest('button,[data-img],[data-nas],.more-link'); if (!b) return;
      if (b.hasAttribute('data-close')) return closeOut();
      if (b.hasAttribute('data-min')) return post({ type: 'minimize' });
      if (b.hasAttribute('data-cancel')) { box.classList.remove('replying'); return size(); }
      if (b.hasAttribute('data-send')) return send(box, b);
      if (b.hasAttribute('data-cmd')) { e.preventDefault(); document.execCommand(b.dataset.cmd); return; }
      if (b.hasAttribute('data-attach')) return box.querySelector('[data-file]').click();
      if (b.hasAttribute('data-img')) return viewer(b.src);
      if (b.hasAttribute('data-nas')) return nasMenu(b, b.dataset.nas, b.dataset.path);
      if (b.classList.contains('more-link')) return openIPop(N.targetUrl);
      if (b.dataset.t != null) {
        const t = view.tools[+b.dataset.t];
        if (!t) return;
        if (t.reply) { view.replyAll = !!t.all; box.classList.add('replying'); updateReplyHint(); size(); setTimeout(() => box.querySelector('.ed')?.focus(), 30); post({ type: 'focus' }); return; }
        b.disabled = true;
        try { await t.run(b); } finally { if (document.body.contains(b)) b.disabled = false; }
      }
    });
    const file = box.querySelector('[data-file]');
    if (file) file.addEventListener('change', () => attach(box, file));
    box.querySelectorAll('.ed').forEach(ed => ed.addEventListener('keydown', e => {
      if (e.key === 'Enter' && (e.ctrlKey || e.metaKey)) { e.preventDefault(); send(box, box.querySelector('[data-send]')); }
      if (e.key === 'Escape') { e.stopPropagation(); box.classList.remove('replying'); size(); }
    }));
    document.body.dataset.esc = 'on';
    if (STYLE === 'stack') { box.addEventListener('mouseenter', size); box.addEventListener('mouseleave', size); }
  }

  // Images, avatars and the mail iframe are filled in after the HTML is on screen.
  function hydrate(box) {
    box.querySelectorAll('[data-photo]').forEach(async el => {
      const url = await image('api/photos/' + el.dataset.photo);
      if (url) { el.style.backgroundImage = `url("${url}")`; el.textContent = ''; }
    });
    box.querySelectorAll('img[data-src]').forEach(async img => {
      const url = await image(img.dataset.src);
      if (url) { img.src = url; img.onload = size; } else img.remove();
    });
    const frame = box.querySelector('iframe[data-mail]');
    if (frame && view.html) mailFrame(frame, view.html);
  }

  function size() {
    requestAnimationFrame(() => {
      const box = root.firstElementChild; if (!box) return;
      let h = 0;
      for (const el of box.children) {
        if (getComputedStyle(el).display === 'none' || el.classList.contains('bar')) continue;
        // The scroll area asks for its full content; .bd (v1 card) asks for what its capped message shows.
        if (el.classList.contains('more-row')) continue;
        const scroll = el.classList.contains('body');
        h += scroll ? Math.min(el.scrollHeight, 2000) : el.classList.contains('bd') ? el.scrollHeight : el.offsetHeight;
      }
      post({ type: 'size', height: Math.ceil(h) + 2 });
    });
  }

  function closeOut(action) {
    const box = root.firstElementChild;
    box.classList.add('out');
    setTimeout(() => post(action ? { type: 'done', action } : { type: 'close' }), 260);
  }

  function finish(text, action) {
    const box = root.firstElementChild;
    document.getElementById('doneText').textContent = text;
    box.classList.remove('replying'); box.classList.add('sent');
    size();
    setTimeout(() => closeOut(action || 'done'), 1400);
  }

  const openIPop = path => post({ type: 'openIPop', path: path || '/notifications' });
  const markRead = () => api('POST', `api/notifications/v1/notifications/${N.id}/read`);
  const openTool = (label, path) => ({ label: label + ' ' + IC('arrow-up-right'), right: true, run: async () => { openIPop(path || N.targetUrl); await markRead(); closeOut('open'); } });
  const readTool = { label: IC('check') + 'Mark read', run: async () => { await markRead(); closeOut('read'); } };
  const id = re => (N.targetUrl || '').match(re)?.[1];

  // ── per module ────────────────────────────────────────────────────────────
  async function load() {
    const loaders = { mail, tickets, calendar, news, projects, notes, calls };
    const fn = loaders[N.module];
    if (fn && await fn() !== false) return;
    view.tools = [readTool, openTool('Open in iPop')];
  }

  async function mail() {
    const mid = id(new RegExp('mailId=' + G));
    if (!mid) return false;
    const r = await api('GET', `api/mail/${mid}`);
    if (!r.ok) return false;
    const d = r.json; view.mail = d;
    view.title = d.subject || '(no subject)';
    const to = (d.recipients || []).filter(x => x.recipientType === 'to').map(x => x.name);
    const cc = (d.recipients || []).filter(x => x.recipientType === 'cc').map(x => x.name);
    const more = Math.max(0, (d.recipientTotal || 0) - to.length - cc.length);
    view.who = { name: d.senderName, sub: 'to ' + [...to.slice(0, 3)].join('; ') + (to.length > 3 ? ` +${to.length - 3}` : '') + (cc.length ? ` · cc ${cc.slice(0, 2).join('; ')}${cc.length > 2 ? ` +${cc.length - 2}` : ''}` : '') + (more ? ` +${more}` : '') };
    if (d.importance === 'high') view.chips.push({ t: 'High importance', st: true });
    view.html = d.isHtml ? d.body : `<p style="white-space:pre-wrap">${esc(d.bodyText || d.body)}</p>`;
    if (d.signature) view.html += `<div style="margin-top:12px;color:#6e6e73">${d.signature}</div>`;
    const atts = (d.attachments || []);
    const pics = atts.filter(a => (a.mimeType || '').startsWith('image/')).slice(0, 2);
    view.body = `<iframe data-mail sandbox="allow-same-origin" title="Message"></iframe>`
      + pics.map(a => `<img class="img" data-img data-src="api/mail/attachments/${a.id}/download?inline=true" alt="${esc(a.fileName)}">`).join('')
      + (atts.length ? `<div class="files">${atts.map(a => `<span class="file" title="${esc(a.fileName)}"><span class="fi">${esc(ext(a.fileName))}</span><span>${esc(a.fileName)} · ${fmtSize(a.fileSize)}</span></span>`).join('')}</div>` : '')
      + `<a class="more-link">Show full message ${IC('arrow-up-right')}</a>`;
    const canAll = d.canReplyAll && replyPlan(d, true).count > replyPlan(d, false).count;
    view.reply = { rich: true, attach: true, placeholder: 'Write your reply…' };
    view.tools = [
      { label: IC('reply') + 'Reply', cls: 'p', reply: true },
      ...(canAll ? [{ label: IC('reply-all') + 'Reply all', reply: true, all: true }] : []),
      { label: IC('forward') + 'Forward', run: async () => { openIPop(N.targetUrl + '&forward=1'); closeOut('open'); } },
      { label: IC('check') + 'Mark read', right: true, run: async () => { await api('POST', `api/mail/${mid}/read`); await markRead(); closeOut('read'); } },
      openTool('Open in iPop'),
    ];
    view.send = async (html, attIds) => {
      const plan = replyPlan(d, view.replyAll);
      if (!plan.to.length) return plan.notice || 'There is no one to send this reply to.';
      const res = await api('POST', 'api/mail/send', {
        subject: /^re:/i.test(d.subject || '') ? d.subject : `Re: ${d.subject || ''}`,
        body: html + quoteOf(d), to: plan.to, cc: plan.cc, bcc: [], attachmentIds: attIds.length ? attIds : null,
        inReplyToId: d.id, threadRootId: d.threadRootId || d.id,
      });
      if (res.ok && res.json?.success !== false) { await api('POST', `api/mail/${mid}/read`); await markRead(); return null; }
      return res.json?.error || res.json?.message || 'Couldn\'t send the reply. Try again.';
    };
    view.done = 'Reply sent';
  }

  // Same rules as the web (IPop.Web/Services/MailAddressing.ForReply).
  function replyPlan(d, all) {
    const me = (NIK || '').toLowerCase(), isMe = n => (n || '').toLowerCase() === me;
    const seen = new Set([me]), to = [], cc = [];
    const of = t => (d.recipients || []).filter(r => r.recipientType === t && !isMe(r.nik));
    let notice = null;
    if (isMe(d.senderNik)) {
      const toP = of('to'), primary = toP.length ? toP : of('cc');
      primary.forEach(r => { if (!seen.has(r.nik.toLowerCase())) { seen.add(r.nik.toLowerCase()); to.push(r.nik); } });
      if (all && toP.length) of('cc').forEach(r => { if (!seen.has(r.nik.toLowerCase())) { seen.add(r.nik.toLowerCase()); cc.push(r.nik); } });
      return { to, cc, count: to.length + cc.length, notice: to.length ? null : 'This mail has no other recipients to reply to.' };
    }
    if (d.senderIsActive) { seen.add((d.senderNik || '').toLowerCase()); to.push(d.senderNik); }
    else notice = `${d.senderName} doesn't have an active iPop account, so a reply can't reach them. Reply in iPop and add who should get it.`;
    if (all && d.canReplyAll) {
      of('to').forEach(r => { if (!seen.has(r.nik.toLowerCase())) { seen.add(r.nik.toLowerCase()); to.push(r.nik); } });
      of('cc').forEach(r => { if (!seen.has(r.nik.toLowerCase())) { seen.add(r.nik.toLowerCase()); cc.push(r.nik); } });
    }
    return { to, cc, count: to.length + cc.length, notice };
  }
  function quoteOf(d) {
    const stamp = d.sentAt ? new Date(d.sentAt).toLocaleString([], { weekday: 'short', day: '2-digit', month: 'short', year: 'numeric', hour: '2-digit', minute: '2-digit' }) : 'earlier';
    return `<details class="mail-quote"><summary class="mail-quote-toggle" title="Show quoted message">⋯</summary><div class="mail-quote-inner"><div class="mail-quote-attr">On ${esc(stamp)}, ${esc(d.senderName)} wrote:</div><blockquote>${d.body}</blockquote></div></details>`;
  }
  function updateReplyHint() {
    const el = document.getElementById('replyTo'); if (!el) return;
    if (!view.mail) return;
    const p = replyPlan(view.mail, view.replyAll);
    el.textContent = (view.replyAll ? 'Reply all' : 'Reply') + (p.notice ? ' · ' + p.notice : ` · to ${p.to.length + p.cc.length} ${p.to.length + p.cc.length === 1 ? 'person' : 'people'}`);
  }

  async function tickets() {
    const tid = id(new RegExp('^/tickets/' + G));
    if (!tid) return false;
    const [r, m] = await Promise.all([api('GET', `api/tickets/${tid}`), api('GET', `api/tickets/${tid}/messages`)]);
    if (!r.ok) return false;
    const t = r.json;
    view.title = `${t.ticketNumber ? '#' + t.ticketNumber + ' · ' : ''}${t.title}`;
    const msgs = (m.ok && m.json?.messages) || [];
    const last = msgs.filter(x => !x.mine).slice(-1)[0];
    view.who = last ? { name: last.sender, sub: 'replied to your ticket' } : (t.assignee ? { name: t.assignee, sub: 'ICT' } : null);
    view.chips = [{ t: t.status, st: true }, t.priority && { t: t.priority }, (t.moduleName || t.category) && { t: [t.moduleName, t.category].filter(Boolean).join(' · ') }].filter(Boolean);
    view.body = msgs.length
      ? `<div class="msg-list">${msgs.slice(-4).map(x => `<div class="msg-it sel"><b>${esc(x.mine ? 'You' : x.sender)}</b><small>${esc(timeShort(x.createdAt))}</small><p>${esc(x.content)}</p></div>`).join('')}</div>`
      : `<p class="txt sel">${esc(t.description || N.body)}</p>`;
    const url = t.url || m.json?.url;
    view.tools = [
      { label: IC('reply') + 'Reply in iDesk', cls: 'p', run: async () => { if (url) post({ type: 'openUrl', url }); else openIPop(N.targetUrl); await markRead(); closeOut('open'); } },
      { label: IC('check') + 'Mark read', run: async () => { await markRead(); closeOut('read'); } },
      { label: 'Open in iDesk ' + IC('arrow-up-right'), right: true, run: async () => { if (url) post({ type: 'openUrl', url }); else openIPop(N.targetUrl); await markRead(); closeOut('open'); } },
    ];
  }

  async function calendar() {
    const url = N.targetUrl || '';
    const meetingId = id(new RegExp('meeting=' + G));
    const eventId = id(new RegExp('event=' + G));
    const joinKey = (url.match(/join=([A-Za-z0-9_-]+)/) || [])[1];
    let mt = null, ev = null;
    if (meetingId) { const r = await api('GET', `api/meetings/${meetingId}`); if (r.ok) mt = r.json; }
    if (!mt && eventId) {
      const r = await api('GET', `api/calendar/events/${eventId}`);
      if (r.ok) { ev = r.json; if (ev.meetingId) { const r2 = await api('GET', `api/meetings/${ev.meetingId}`); if (r2.ok) mt = r2.json; } }
    }
    const src = mt || ev;
    if (src) {
      view.title = src.title;
      const s = new Date(src.startAt), e = new Date(src.endAt);
      const hm = d => d.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
      view.chips = [{ t: `${s.toLocaleDateString([], { weekday: 'short', day: 'numeric', month: 'short' })} · ${hm(s)} – ${hm(e)}`, st: true },
        (src.roomName || src.location) && { t: src.roomName || src.location },
        src.attendees?.length && { t: `${src.attendees.length} people` }].filter(Boolean);
      const notes = mt?.description || mt?.agenda || ev?.notes;
      view.body = `<p class="txt sel">${esc(notes || N.body)}</p>`;
      view.who = src.ownerName || mt?.organizerName ? { name: src.ownerName || mt.organizerName, sub: 'Organizer' } : null;
    }
    const key = joinKey || mt?.roomKey || ev?.roomKey;
    const link = mt?.link || ev?.link;
    const canJoin = key || link;
    const invited = mt && mt.myResponse !== undefined && mt.myResponse !== 'organizer';
    view.tools = [
      ...(canJoin ? [{ label: IC('video') + 'Join', cls: 'p', run: async () => { if (key) openIPop(`/calls?tab=rooms&join=${key}`); else post({ type: 'openUrl', url: link }); await markRead(); finish('Joining…', 'join'); } }] : []),
      ...(mt && invited && mt.myResponse !== 'accept' && !canJoin ? [{ label: IC('check') + 'Accept', cls: 'p', run: () => rsvp(mt.id, 'accept', 'Accepted') }] : []),
      { label: IC('alarm-clock') + 'Snooze 5 min', run: async () => { const r = await api('POST', `api/notifications/v1/notifications/${N.id}/snooze`, { until: new Date(Date.now() + 5 * 60000).toISOString() }); if (r.ok) finish('Snoozed for 5 minutes', 'snooze'); else snack('Couldn\'t snooze. Try again.'); } },
      ...(mt && invited && mt.myResponse !== 'decline' ? [{ label: IC('x') + 'Decline', cls: 'r', run: () => rsvp(mt.id, 'decline', 'Declined') }] : []),
      openTool('Open', N.targetUrl),
    ];
    view.done = 'Done';
  }
  async function rsvp(mid, response, text) {
    const r = await api('POST', `api/meetings/${mid}/rsvp`, { response });
    if (r.ok) { await markRead(); finish(text, response); } else snack(r.json?.message || 'Couldn\'t send your answer. Try again.');
  }

  async function news() {
    const pid = id(new RegExp('^/news/' + G)) || id(new RegExp('post=' + G));
    if (!pid) return false;
    const r = await api('GET', `api/news/${pid}`);
    if (!r.ok) return false;
    const p = r.json;
    view.title = p.title;
    view.who = { name: p.authorName, sub: p.type === 'memo' ? 'Memo' : p.type === 'condolence' ? 'Condolence' : 'News' };
    if (p.requiresAcknowledgement) view.chips.push({ t: p.isAcknowledged ? 'Acknowledged' : 'Please acknowledge', st: true });
    let cover = '';
    if (p.coverMediaId) {
      const m = await api('GET', `api/news/composer/posts/${pid}/media`);
      const it = m.ok && (m.json || []).find(x => x.mediaId === p.coverMediaId);
      const src = it && (it.posterUrl || it.streamUrl);
      if (src) cover = `<img class="img" data-img data-src="${esc(src.replace(/^\//, ''))}" alt="">`;
    }
    view.html = p.bodyFormat === 'html' ? p.body : null;
    view.body = (view.html ? '<iframe data-mail sandbox="allow-same-origin" title="Post"></iframe>' : `<p class="txt sel">${esc(p.body)}</p>`) + cover + '<a class="more-link">Read in iPop ' + IC('arrow-up-right') + '</a>';
    view.tools = [
      ...(p.requiresAcknowledgement && !p.isAcknowledged ? [{ label: IC('badge-check') + 'Acknowledge', cls: 'p', run: async () => {
        const a = await api('POST', `api/news/${pid}/acknowledge?contentVersion=${p.contentVersion ?? 1}`);
        if (a.ok) { await api('POST', `api/notifications/v1/notifications/${N.id}/acknowledge`); finish('Acknowledged', 'ack'); }
        else snack(a.status === 409 ? 'The memo was updated — open it in iPop to read the new version.' : 'Couldn\'t acknowledge. Try again.');
      } }] : [readTool]),
      openTool('Open in iPop'),
    ];
  }

  async function projects() {
    const tid = id(new RegExp('task=' + G));
    if (!tid) return false;
    const r = await api('GET', `api/projects/tasks/${tid}`);
    if (!r.ok) return false;
    const t = r.json.task || r.json;
    view.title = t.title || N.title;
    view.chips = [{ t: statusName(t.status), st: true }, t.dueDate && { t: 'Due ' + new Date(t.dueDate).toLocaleDateString([], { day: 'numeric', month: 'short' }) }, t.priority && { t: t.priority }].filter(Boolean);
    view.body = `<p class="txt sel">${esc(N.body)}</p>` + (t.description ? `<p class="txt sel" style="margin-top:10px;color:var(--ink-3)">${esc(t.description.slice(0, 600))}</p>` : '');
    view.reply = { rich: false, placeholder: 'Write a comment…', hint: 'Comment on the task' };
    view.send = async text => {
      const res = await api('POST', `api/projects/tasks/${tid}/comments`, { content: text });
      if (res.ok) { await markRead(); return null; }
      return res.json?.message || 'Couldn\'t add the comment. Try again.';
    };
    view.done = 'Comment added';
    view.tools = [
      { label: IC('reply') + 'Reply', cls: 'p', reply: true },
      ...(t.status !== 'done' ? [{ label: IC('circle-check') + 'Mark done', run: async () => {
        const res = await api('POST', `api/projects/tasks/${tid}/move`, { status: 'done' });
        if (res.ok) { await markRead(); finish('Marked done', 'done'); } else snack(res.json?.message || 'Couldn\'t update the task.');
      } }] : []),
      openTool('Open in iPop'),
    ];
  }
  const statusName = s => ({ todo: 'To do', in_progress: 'In progress', review: 'In review', done: 'Done' }[s] || s || 'Task');

  async function notes() {
    const pid = id(new RegExp('^/notes/' + G));
    if (!pid) return false;
    view.tools = [readTool, openTool('Open in iPop')];
    if (!/comment|mention/.test(N.eventType)) return;
    const r = await api('GET', `api/notes/${pid}/threads`);
    const threads = (r.ok && r.json) || [];
    const open = threads.filter(t => !t.resolvedAt && t.comments?.length)
      .sort((a, b) => new Date(b.comments[b.comments.length - 1].createdAt) - new Date(a.comments[a.comments.length - 1].createdAt));
    const th = open[0];
    if (!th) return;
    const last = th.comments[th.comments.length - 1];
    view.who = { name: last.authorName, sub: th.quote ? `on “${th.quote.slice(0, 60)}”` : 'commented' };
    view.body = `<div class="msg-list">${th.comments.slice(-3).map(c => `<div class="msg-it sel"><b>${esc(c.authorName)}</b><small>${esc(timeShort(c.createdAt))}</small><p>${esc(c.body)}</p></div>`).join('')}</div>`;
    view.reply = { rich: false, placeholder: 'Reply to the comment…', hint: 'Reply in the comment thread' };
    view.send = async text => {
      const res = await api('POST', `api/notes/${pid}/threads/${th.id}/replies`, { body: text });
      if (res.ok) { await markRead(); return null; }
      return res.json?.message || 'Couldn\'t reply. Try again.';
    };
    view.done = 'Reply added';
    view.tools = [{ label: IC('reply') + 'Reply', cls: 'p', reply: true }, readTool, openTool('Open in iPop')];
  }

  async function calls() {
    view.tools = [
      { label: IC('phone') + 'Call back', cls: 'p', run: async () => { openIPop('/calls?tab=recents'); await markRead(); closeOut('call'); } },
      readTool, openTool('Open in iPop', '/calls?tab=recents'),
    ];
  }

  // ── reply ─────────────────────────────────────────────────────────────────
  async function send(box, btn) {
    if (!view.send) return;
    const ed = box.querySelector('.ed');
    const text = ed.innerText.trim();
    const err = box.querySelector('.err');
    if (!text) { ed.focus(); return; }
    btn.disabled = true; btn.textContent = 'Sending…'; err.hidden = true;
    const ids = [...box.querySelectorAll('.att span[data-id]')].map(s => s.dataset.id);
    const fail = await (view.reply.rich ? view.send(clean(ed), ids) : view.send(text));
    btn.disabled = false; btn.textContent = 'Send';
    if (fail) { err.textContent = fail; err.hidden = false; size(); return; }
    finish(view.done, 'reply');
  }

  // Typed HTML only: bold/italic/underline/lists/line breaks. Pasted formatting and anything else is dropped.
  function clean(ed) {
    const ok = new Set(['B', 'STRONG', 'I', 'EM', 'U', 'UL', 'OL', 'LI', 'BR', 'P', 'DIV']);
    const walk = n => [...n.childNodes].map(c => {
      if (c.nodeType === 3) return esc(c.textContent);
      if (c.nodeType !== 1) return '';
      const inner = walk(c);
      if (!ok.has(c.tagName)) return inner;
      const tag = c.tagName === 'DIV' ? 'p' : c.tagName.toLowerCase();
      return tag === 'br' ? '<br/>' : `<${tag}>${inner}</${tag}>`;
    }).join('');
    const html = walk(ed).trim();
    return /^<(p|ul|ol)>/.test(html) ? html : `<p>${html}</p>`;
  }

  async function attach(box, input) {
    const list = box.querySelector('.att');
    for (const f of [...input.files].slice(0, 5)) {
      if (f.size > 25 * 1024 * 1024) { snack(`${f.name} is too large here — attach it in iPop.`); continue; }
      const chip = document.createElement('span'); chip.innerHTML = IC('loader-circle', 'spin') + esc(f.name); list.appendChild(chip); size();
      const data = await new Promise(res => { const r = new FileReader(); r.onload = () => res(String(r.result).split(',')[1]); r.readAsDataURL(f); });
      const r = await new Promise(res => { const idn = Math.random(); const h = m => { if (m.ref === idn) res(m); }; app.on('uploadResult', h); post({ type: 'upload', ref: idn, name: f.name, mime: f.type || 'application/octet-stream', data }); });
      if (r.id) { chip.dataset.id = r.id; chip.innerHTML = IC('paperclip') + esc(f.name); }
      else { chip.remove(); snack(r.error || `Couldn't attach ${f.name}.`); }
    }
    input.value = '';
  }

  // ── mail HTML: isolated, no scripts, no remote loads; inline images and NAS links handled here ──
  async function mailFrame(frame, html) {
    const dark = document.documentElement.classList.contains('dark');
    frame.srcdoc = `<!doctype html><html><head><meta charset="utf-8"><base target="_blank"><style>
      html,body{margin:0;padding:0;background:transparent;color:${dark ? '#d1d1d6' : '#3c3c43'};font:14px/1.55 "Segoe UI Variable Text","Segoe UI",system-ui,sans-serif;word-break:break-word;overflow:hidden}
      img{max-width:100%;height:auto;border-radius:8px;cursor:zoom-in} a{color:${dark ? '#30d158' : '#2e9e5b'}} table{max-width:100%}
      blockquote{margin:8px 0;padding-left:10px;border-left:3px solid rgba(120,120,128,.35);color:#8e8e93}
      details.mail-quote>summary{cursor:pointer;color:#8e8e93;list-style:none}
      </style></head><body>${html}</body></html>`;
    frame.onload = async () => {
      const doc = frame.contentDocument; if (!doc) return;
      const fit = () => { frame.style.height = doc.documentElement.scrollHeight + 'px'; size(); };
      for (const img of doc.querySelectorAll('img')) {
        const src = img.getAttribute('src') || '';
        const m = src.match(/\/?(api\/attachments\/[0-9a-fA-F-]{36})/);
        if (m) { const url = await image(m[1]); if (url) img.src = url; else img.remove(); }
        else if (!src.startsWith('data:')) img.remove();
        img.onload = fit;
        img.addEventListener('click', () => viewer(img.src));
      }
      doc.addEventListener('click', e => {
        const a = e.target.closest('a'); if (!a) return;
        e.preventDefault();
        const href = a.getAttribute('href') || '';
        const nas = nasOf(href);
        if (nas) return nasMenu(a, nas.server, nas.path, frame);
        if (/^https?:|^mailto:/i.test(href)) post({ type: 'openUrl', url: href });
        else if (href.startsWith('/')) openIPop(href);
      });
      fit();
      setTimeout(fit, 300);
    };
  }

  // iPop Files links (/files?server=spj&path=…) and \\NAS paths → the Open in File Explorer menu.
  function nasOf(href) {
    try {
      const u = new URL(href, 'https://x/');
      if (/\/files$/.test(u.pathname) && u.searchParams.get('server')) return { server: u.searchParams.get('server'), path: u.searchParams.get('path') || '/' };
    } catch { }
    const unc = href.replace(/^file:\/*/i, '\\\\').replace(/\//g, '\\');
    const m = unc.match(/^\\\\(192\.168\.2\.16|10\.12\.2\.6|10\.11\.2\.6|10\.14\.2\.16|192\.168\.2\.17)\\(.+)$/);
    if (m) return { unc: decodeURIComponent(unc) };
    return null;
  }
  const NAS = { spj: '192.168.2.16', krw: '10.12.2.6', jtb: '10.11.2.6', smg: '10.14.2.16', ict: '192.168.2.17' };
  function nasMenu(anchor, server, path, frame) {
    document.querySelectorAll('.menu').forEach(m => m.remove());
    const nas = typeof server === 'object' ? server : { server, path };
    const unc = nas.unc || (NAS[nas.server] ? '\\\\' + NAS[nas.server] + (nas.path || '').replace(/\//g, '\\') : null);
    const r = anchor.getBoundingClientRect(), off = frame ? frame.getBoundingClientRect() : { left: 0, top: 0 };
    const m = document.createElement('div'); m.className = 'menu';
    m.style.left = Math.min(window.innerWidth - 260, r.left + off.left) + 'px';
    m.style.top = Math.min(window.innerHeight - 160, r.bottom + off.top + 6) + 'px';
    m.innerHTML = `${unc ? '<button data-k="exp">' + IC('folder-open') + 'Open in File Explorer</button>' : ''}${nas.server ? '<button data-k="web">' + IC('globe') + 'Open in browser</button>' : ''}${unc ? '<button data-k="copy">' + IC('copy') + 'Copy network path</button>' : ''}<small>${esc(unc || nas.path)}</small>`;
    document.body.appendChild(m);
    m.onclick = e => {
      const k = e.target.closest('button')?.dataset.k; m.remove();
      if (k === 'exp') post({ type: 'explorer', unc });
      if (k === 'web') openIPop(`/files?server=${encodeURIComponent(nas.server)}&path=${encodeURIComponent(nas.path)}`);
      if (k === 'copy') { post({ type: 'copy', text: unc }); snack('Network path copied'); }
    };
    setTimeout(() => document.addEventListener('click', () => m.remove(), { once: true }), 0);
  }

  function viewer(src) {
    if (!src) return;
    const v = document.createElement('div'); v.className = 'viewer'; v.innerHTML = `<img src="${src}" alt="">`;
    v.onclick = () => v.remove(); document.body.appendChild(v);
  }
  const ext = n => (String(n).split('.').pop() || 'FILE').slice(0, 4).toUpperCase();
  const fmtSize = b => b > 1048576 ? (b / 1048576).toFixed(1) + ' MB' : Math.max(1, Math.round(b / 1024)) + ' KB';
})();
