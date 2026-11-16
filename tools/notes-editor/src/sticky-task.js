// Turning a sticky's text into a task: title, the person (@name) and a due date. Pure, so it is tested without a browser.
//   "Order 4 switches @Yudi 25 Nov"  ->  { title: "Order 4 switches", who: "Yudi", due: { iso: "2026-11-25T00:00:00Z", label } }
// Dates understood: 2026-11-25, 25 Nov / 25 November / 25 Okt (Indonesian month names too), 25/11. Without a year, the next
// time that day comes (a date already past this year means next year).
const EN = ['jan', 'feb', 'mar', 'apr', 'may', 'jun', 'jul', 'aug', 'sep', 'oct', 'nov', 'dec'];
const ID = ['jan', 'feb', 'mar', 'apr', 'mei', 'jun', 'jul', 'agu', 'sep', 'okt', 'nov', 'des'];

function toDue(y, m, d) {
  const dt = new Date(Date.UTC(y, m, d));
  if (Number.isNaN(dt.getTime()) || dt.getUTCMonth() !== m || dt.getUTCDate() !== d) return null;
  return { iso: `${y}-${String(m + 1).padStart(2, '0')}-${String(d).padStart(2, '0')}T00:00:00Z`, label: dt.toLocaleDateString(undefined, { day: 'numeric', month: 'short', timeZone: 'UTC' }) };
}
const monthIndex = word => { const k = word.slice(0, 3).toLowerCase(); const a = EN.indexOf(k); return a >= 0 ? a : ID.indexOf(k); };

export function parseSticky(text, today = new Date()) {
  let t = String(text ?? '').trim();
  let who = null, due = null;
  t = t.replace(/(^|\s)@([\p{L}][\p{L}.'-]{0,30})/u, (_, pre, n) => { who = n.replace(/[.'-]+$/, ''); return pre; });
  const yearFor = (m, d) => { const y = today.getFullYear(); const thisYear = new Date(Date.UTC(y, m, d)); const t0 = Date.UTC(today.getFullYear(), today.getMonth(), today.getDate()); return thisYear.getTime() < t0 ? y + 1 : y; };
  // 2026-11-25
  t = t.replace(/\b(\d{4})-(\d{1,2})-(\d{1,2})\b/, (m0, y, m, d) => { const r = toDue(+y, +m - 1, +d); if (!r) return m0; due = r; return ''; });
  // 25 Nov / 25 November — the first "number + month name" pair, skipping "4 new" and the like
  if (!due) t = t.replace(/\b(\d{1,2})\s+([A-Za-z]{3,9})\b/g, (m0, d, mon) => {
    if (due) return m0;
    const i = monthIndex(mon); if (i < 0) return m0;
    const r = toDue(yearFor(i, +d), i, +d); if (!r) return m0; due = r; return '';
  });
  // 25/11
  if (!due) t = t.replace(/\b(\d{1,2})\/(\d{1,2})\b/, (m0, d, m) => { const r = toDue(yearFor(+m - 1, +d), +m - 1, +d); if (!r) return m0; due = r; return ''; });
  t = t.replace(/\s+(before|by|sebelum|due|on|tgl|tanggal)\s*$/i, '').replace(/[ \t]{2,}/g, ' ').trim();
  return { title: t.split('\n')[0].trim().slice(0, 300), who, due };
}
