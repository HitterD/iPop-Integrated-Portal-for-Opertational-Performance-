// Inbox model (Notifier 1.5.0, docs/notifier/PRIORITY-150.md "Inbox small/maximized"): pure helpers, no DOM, so
// tests/notifier-inbox-model.test.cjs can check them. Rows = GET api/notifications/v1/inbox items
// ({id, type, eventType, title, body, at, isRead, priority, targetUrl, actorId, actorName…}); ctx = the app's context.
(function (root) {
  const G = '[0-9a-fA-F-]{36}';
  const RANK = { act: 3, msg: 2, info: 1 };

  // Module for the icon/colour and the sidebar "Modules" list. The event type decides first: a system notice
  // ("system.epop_import", linking to /mail) is iPop's own, not Mail.
  function moduleOf(i) {
    const u = String(i.targetUrl || '');
    const e = String(i.eventType || '').toLowerCase();
    if (e.startsWith('system.')) return 'system';
    if (e.startsWith('approval.')) return 'approval';
    if (/^(meeting|calendar)\./.test(e)) return 'calendar';
    if (u.startsWith('/tickets') || u.startsWith('/idesk/tickets') || /^(ticket|tickets|idesk)\./.test(e)) return 'tickets';
    if (u.startsWith('/calendar') || u.includes('tab=scheduled') || u.includes('join=') || /^(meeting|calendar)\./.test(e)) return 'calendar';
    if (u.startsWith('/calls') || /^calls?\./.test(e)) return 'calls';
    if (u.startsWith('/notes') || /^notes?\./.test(e)) return 'notes';
    if (u.startsWith('/chat') || e.startsWith('chat.')) return 'chat';
    return { mail: 'mail', chat: 'chat', news: 'news', project: 'projects' }[i.type] || 'system';
  }

  // One row per conversation: the same chat room, ticket or project task. Everything else stands alone.
  function threadKey(i) {
    const u = String(i.targetUrl || '');
    let m = new RegExp('^/chat/(' + G + ')').exec(u); if (m) return 'chat:' + m[1].toLowerCase();
    m = new RegExp('^/(?:idesk/)?tickets/(' + G + ')').exec(u); if (m) return 'ticket:' + m[1].toLowerCase();
    m = new RegExp('^/projects/' + G + '\\?(?:.*&)?task=(' + G + ')').exec(u); if (m) return 'task:' + m[1].toLowerCase();
    return null;
  }

  // Ticket notification body (TicketNotificationText): "Person · Role · Unit\nwhat happened\n#number".
  function ticketParts(body) {
    const lines = String(body || '').split('\n').map(s => s.trim()).filter(Boolean);
    const number = lines.length > 1 && lines[lines.length - 1].startsWith('#') ? lines.pop() : null;
    if (lines.length < 2) return { who: null, text: lines.join(' '), number };
    return { who: lines[0].split(' · ')[0], whoSub: lines[0].split(' · ').slice(1).join(' · '), text: lines.slice(1).join(' '), number };
  }

  // iDesk ticket status chip (1.5.1, mockup .tchip): 'new' (assigned / reopened / created / sent), 'rep' (a reply in
  // the ticket chat), 'res' (resolved / closed), or null. Event names: TicketService; sentences: TicketNotificationText.
  function ticketStatus(i) {
    const e = String(i.eventType || '').toLowerCase().replace(/^(tickets|idesk)\./, 'ticket.');
    const text = ticketParts(i.body).text || '';
    if (e === 'ticket.message_created') return 'rep';
    if (e === 'ticket.resolved' || e === 'ticket.closed') return 'res';
    if (e === 'ticket.assigned' || e === 'ticket.reopened' || e === 'ticket.created' || e === 'ticket.sent') return 'new';
    if (e === 'ticket.status_changed') {
      if (/^Reopened\b/.test(text)) return 'new';
      if (/→ (Resolved|Closed)\.$|^Status: (Resolved|Closed)\.$/.test(text)) return 'res';
      return null;
    }
    if (!e.startsWith('ticket.')) {
      // Older servers send only the coarse type: read the sentence.
      if (/^(Assigned to you|Reopened)\b/.test(text)) return 'new';
      if (/^Marked as resolved|closed\.$|^Closed\b|Confirmed as fixed/i.test(text)) return 'res';
      if (/^[^:\s]+: “/.test(text)) return 'rep';
    }
    return null;
  }

  // "Assigned to you" (technician): iDesk's status line for my own ticket queue (/idesk/tickets/{id}#update).
  function assignedToMe(i) {
    const e = String(i.eventType || '').toLowerCase();
    const u = String(i.targetUrl || '');
    if (e === 'ticket.assigned' && /^\/idesk\/tickets\//i.test(u)) return true;
    return /^\/idesk\/tickets\/.*#update$/i.test(u) && /^Assigned to you\b/.test(ticketParts(i.body).text || '');
  }

  // What a row shows under its title: the person (if known) and one line of text; plus the ticket number chip.
  function lineOf(i) {
    const mod = moduleOf(i);
    if (mod === 'tickets') {
      const t = ticketParts(i.body);
      return { who: i.actorName || t.who, text: t.text, tag: t.number };
    }
    const text = String(i.body || '').replace(/\s+/g, ' ').trim();
    // A chat notification is titled with the sender already.
    return { who: mod === 'chat' ? null : i.actorName || null, text, tag: null };
  }

  // Rows (newest first) → threads: { key, head, items (newest first), ids, unread, tier, kind, leader, mod }.
  // The head is the newest; the thread is unread if any is, and takes the highest tier of its unread updates
  // (of all updates when everything is read), so a leader's message inside a busy ticket still counts.
  // now = optional clock (tests): a ticket assigned to me leaves Needs action after 24 h (tiers.js).
  function group(rows, ctx, now) {
    const tiers = root.tiers || (typeof require === 'function' ? require('./tiers.js') : null);
    const out = [], byKey = new Map();
    for (const r of rows || []) {
      const c = tiers.classify(r, ctx, now);
      const it = Object.assign({}, r, { kind: c.kind, tier: c.tier, leader: c.leader });
      const key = threadKey(r);
      const t = key && byKey.get(key);
      if (t) { t.items.push(it); continue; }
      const th = { key: key || 'n:' + r.id, items: [it] };
      if (key) byKey.set(key, th);
      out.push(th);
    }
    for (const t of out) {
      const head = t.items[0];
      const unread = t.items.filter(x => !x.isRead);
      const pool = unread.length ? unread : t.items;
      const top = pool.reduce((a, b) => (RANK[b.tier] > RANK[a.tier] ? b : a), pool[0]);
      t.head = head;
      t.id = head.id;
      t.ids = t.items.map(x => x.id);
      t.unread = unread.length > 0;
      t.tier = top.tier;
      t.kind = top.tier === head.tier ? head.kind : top.kind;
      t.leader = (t.items.find(x => x.leader) || {}).leader || null;
      t.approval = t.items.some(x => x.kind === 'approval');
      t.soon = t.items.some(x => x.kind === 'meetingsoon');
      t.mod = moduleOf(head);
      t.at = head.at;
      t.line = lineOf(head);
      t.status = t.mod === 'tickets' ? ticketStatus(head) : null;
      // "Assigned to you" while that assignment is what still counts (Needs action, < 24 h, unread).
      t.assigned = t.mod === 'tickets' && t.tier === 'act' && t.items.some(x => x.kind === 'ticketassigned' && assignedToMe(x));
    }
    return out;
  }

  // Needs action = an act-tier thread that is still unread. Once read it is an ordinary row of its day (still in
  // All / its module, shown as read). The summary card, the Act chip, the section and the tray all use this rule.
  const needsAction = t => t.tier === 'act' && !!t.unread;
  // Which list group a thread is drawn in: 'act' (unread Needs action), 'msg' or 'info'. A read act thread is a Message.
  const groupOf = t => needsAction(t) ? 'act' : t.tier === 'info' ? 'info' : 'msg';

  // Unread threads per tier, leaders, approvals, iDesk tickets waiting on me (Needs action) and module.
  function counts(threads) {
    const c = { all: 0, act: 0, msg: 0, info: 0, lead: 0, appr: 0, tix: 0, mod: {} };
    for (const t of threads) {
      if (!t.unread) continue;
      c.all++; c[t.tier]++;
      if (t.leader) c.lead++;
      if (t.approval) c.appr++;
      if (t.tier === 'act' && t.mod === 'tickets' && t.kind === 'ticketassigned') c.tix++;
      c.mod[t.mod] = (c.mod[t.mod] || 0) + 1;
    }
    return c;
  }

  // Sidebar / chip filter: all | act | msg | info | lead | appr | <module key>.
  function inView(t, f) {
    if (!f || f === 'all') return true;
    if (f === 'act') return needsAction(t);
    if (f === 'msg' || f === 'info') return t.tier === f;
    if (f === 'lead') return !!t.leader;
    if (f === 'appr') return t.approval;
    return t.mod === f;
  }

  function matches(t, q) {
    q = String(q || '').trim().toLowerCase();
    if (!q) return true;
    return t.items.some(i => [i.title, i.body, i.actorName].some(s => String(s || '').toLowerCase().includes(q)));
  }

  // Whole days between the item and now, in local time (0 = today).
  function daysAgo(iso, now) {
    const d = new Date(iso), n = now ? new Date(now) : new Date();
    const day = x => new Date(x.getFullYear(), x.getMonth(), x.getDate()).getTime();
    return Math.round((day(n) - day(d)) / 864e5);
  }

  // Group headers: "Today" | "Yesterday" | "Mon 17 Nov" (en-GB order; the UI is English).
  function dayLabel(iso, now) {
    const diff = daysAgo(iso, now);
    if (diff <= 0) return 'Today';
    if (diff === 1) return 'Yesterday';
    return new Date(iso).toLocaleDateString('en-GB', { weekday: 'short', day: 'numeric', month: 'short' });
  }

  // 24 h clock in the PC's own style ("16.38" in Indonesian Windows, "16:38" elsewhere). locale = optional (tests).
  function clock(iso, locale) {
    return new Date(iso).toLocaleTimeString(locale || [], { hour: '2-digit', minute: '2-digit', hourCycle: 'h23' });
  }

  // Row time (1.5.1): today → "16.38", yesterday → "Yesterday", within 6 days → "Mon", older → "17 Nov".
  function relTime(iso, now, locale) {
    const diff = daysAgo(iso, now);
    if (diff <= 0) return clock(iso, locale);
    if (diff === 1) return 'Yesterday';
    const d = new Date(iso);
    if (diff <= 6) return d.toLocaleDateString('en-GB', { weekday: 'short' });
    return d.toLocaleDateString('en-GB', { day: 'numeric', month: 'short' });
  }

  // Summary line (small Inbox): "2 approvals · 2 leaders · 1 ticket · 4 messages", zero parts left out.
  // [count key, number, label, chip it jumps to].
  function summaryParts(c) {
    const n = (v, one, many) => (v === 1 ? one : many);
    return [
      ['appr', c.appr, n(c.appr, 'approval', 'approvals'), 'act'],
      ['lead', c.lead, n(c.lead, 'leader', 'leaders'), 'act'],
      ['tix', c.tix, n(c.tix, 'ticket', 'tickets'), 'act'],
      ['msg', c.msg, n(c.msg, 'message', 'messages'), 'msg'],
    ].filter(p => p[1] > 0);
  }

  const api = { needsAction, groupOf, moduleOf, threadKey, ticketParts, ticketStatus, assignedToMe, lineOf, group, counts, inView, matches, dayLabel, clock, relTime, summaryParts };
  if (typeof module !== 'undefined' && module.exports) module.exports = api;
  else root.inboxModel = api;
})(typeof window !== 'undefined' ? window : globalThis);
