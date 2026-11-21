// Priority tiers (Notifier 1.5.0, docs/notifier/PRIORITY-150.md): the JS twin of NotifierSettings.KindOf /
// DefaultTier / AlwaysAct / TierFor (src/IPop.Application/Notifier/NotifierSettings.cs). Both are checked against the
// same table, tests/notifier-tier-cases.json (tests/notifier-tiers.test.cjs and NotifierTierTests.cs).
// Pages get the effective settings tiers, leaders and VIPs in the app's context: ctx.tiers, ctx.leaders, ctx.vips.
(function (root) {
  const ACT = 'act', MSG = 'msg', INFO = 'info';
  const KINDS = ['approval', 'chat', 'mail', 'mailmass', 'ticketassigned', 'ticketchat', 'tickets', 'meetingsoon', 'calendar',
    'projects', 'projectwatch', 'notes', 'calls', 'news', 'system'];
  const lower = s => String(s == null ? '' : s).trim().toLowerCase();

  function kindOf(eventType, priority, targetUrl) {
    const e = lower(eventType);
    const head = e.split('.')[0];
    const low = lower(priority) === 'low';
    switch (head) {
      case 'approval': return 'approval';
      case 'chat': return 'chat';
      case 'mail': return e === 'mail.new' && low ? 'mailmass' : 'mail';
      case 'ticket': case 'tickets': case 'idesk': {
        if (e === 'ticket.message_created') return 'ticketchat';
        if (e === 'ticket.assigned' || e === 'ticket.reopened') return 'ticketassigned';
        const u = String(targetUrl || '').toLowerCase();
        if (targetUrl != null && u.startsWith('/idesk/tickets/') && u.endsWith('#update')) return 'ticketassigned';
        return 'tickets';
      }
      case 'meeting': case 'calendar':
        return e === 'meeting.reminder' || e === 'calendar.reminder' ? 'meetingsoon' : 'calendar';
      case 'project': case 'task':
        return e === 'project.task_assigned' || e === 'project.task_due_reminder' || e === 'project.task_due_tomorrow'
          || e.includes('comment') || e.includes('mention') ? 'projects' : 'projectwatch';
      case 'note': case 'notes': return 'notes';
      case 'call': case 'calls': return 'calls';
      case 'news': case 'memo': return 'news';
      default: return 'system';
    }
  }

  function defaultTier(kind) {
    if (kind === 'approval' || kind === 'ticketassigned' || kind === 'meetingsoon') return ACT;
    if (['chat', 'mail', 'ticketchat', 'tickets', 'calendar', 'projects', 'notes', 'calls'].includes(kind)) return MSG;
    return INFO;
  }

  function alwaysAct(eventType, priority, fromLeaderOrVip) {
    return !!fromLeaderOrVip || lower(eventType) === 'chat.emergency' || lower(eventType) === 'memo.mandatory'
      || (lower(priority) === 'high' && lower(eventType) !== 'chat.mention'); // a colleague's @mention stays a Message
  }

  // An iDesk ticket assigned / reopened to me is Needs action only while unread and younger than this (1.5.1).
  const ASSIGNED_ACT_MS = 24 * 3600e3;

  // tiers = the settings' Tiers map (kind → act|msg|info); missing/unknown → the kind's default.
  // at (ISO / Date / ms, optional), read and now (default: the clock) only matter for "ticketassigned".
  function tierFor(kind, eventType, priority, fromLeaderOrVip, tiers, at, read, now) {
    if (alwaysAct(eventType, priority, fromLeaderOrVip)) return ACT;
    const t = tiers && tiers[kind];
    const tier = t === ACT || t === MSG || t === INFO ? t : defaultTier(kind);
    if (tier === ACT && kind === 'ticketassigned') {
      if (read) return MSG;
      const a = at == null ? NaN : new Date(at).getTime();
      const n = now == null ? Date.now() : new Date(now).getTime();
      if (!isNaN(a) && n - a >= ASSIGNED_ACT_MS) return MSG;
    }
    return tier;
  }

  // Why someone counts: leader reason ("Manager", "BOD / GM level"…), "VIP", or null. ctx = the app's context.
  function leaderReason(actorId, ctx) {
    if (!actorId || !ctx) return null;
    const id = String(actorId).toLowerCase();
    const l = (ctx.leaders || []).find(x => String(x.id).toLowerCase() === id);
    if (l) return l.reason || 'Leader';
    return (ctx.vips || []).some(x => String(x.id || x).toLowerCase() === id) ? 'VIP' : null;
  }

  // An Inbox row / notice → { kind, tier, leader }. Rows carry eventType (servers since 1.5.0) or only the coarse type.
  // item.at + item.isRead feed the 24 h rule for tickets assigned to me; now = optional clock (tests).
  function classify(item, ctx, now) {
    const eventType = item.eventType || item.type;
    const kind = kindOf(eventType, item.priority, item.targetUrl);
    const leader = leaderReason(item.actorId, ctx);
    return { kind, tier: tierFor(kind, eventType, item.priority, !!leader, ctx && ctx.tiers, item.at, !!item.isRead, now), leader };
  }

  const api = { ACT, MSG, INFO, KINDS, ASSIGNED_ACT_MS, kindOf, defaultTier, alwaysAct, tierFor, leaderReason, classify };
  if (typeof module !== 'undefined' && module.exports) module.exports = api;
  else root.tiers = api;
})(typeof window !== 'undefined' ? window : globalThis);
