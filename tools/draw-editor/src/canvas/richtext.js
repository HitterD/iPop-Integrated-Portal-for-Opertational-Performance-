// Formatted text on the board, kept as plain text with light markers so it stays one string per element (simple to sync,
// search and export): **bold**, *italic*, ~~strike~~, [label](url), and "- " at the start of a line for a bullet.
// The editor (a textarea) shows the markers while typing, like Slack; the board shows the formatting.

/// Splits one line into runs: [{ text, b, i, s, href }]. Markers that are not closed stay as typed.
export function parseLine(line) {
  const runs = [];
  let b = false, i = false, s = false, buf = '';
  const push = (extra = {}) => { if (buf) runs.push({ text: buf, b, i, s, ...extra }); buf = ''; };
  const closes = (from, mark) => line.indexOf(mark, from) > from;
  for (let k = 0; k < line.length; k++) {
    const rest = line.slice(k);
    const link = rest.match(/^\[([^\]\n]{1,200})\]\((https?:\/\/[^\s)]{1,500}|note:[0-9a-f-]{36})\)/i);
    if (link) { push(); buf = link[1]; push({ href: link[2] }); k += link[0].length - 1; continue; }
    if (rest.startsWith('**') && (b || closes(k + 2, '**'))) { push(); b = !b; k++; continue; }
    if (rest.startsWith('~~') && (s || closes(k + 2, '~~'))) { push(); s = !s; k++; continue; }
    if (rest[0] === '*' && rest[1] !== '*' && (i || closes(k + 1, '*'))) { push(); i = !i; continue; }
    buf += rest[0];
  }
  push();
  return runs;
}

/// A whole text: lines with a bullet flag and runs. Plain text (no markers) gives one run per line.
export function parse(text) {
  return String(text ?? '').split('\n').map(raw => {
    const bullet = /^\s*[-•]\s+/.test(raw);
    const line = bullet ? raw.replace(/^\s*[-•]\s+/, '') : raw;
    return { bullet, runs: parseLine(line) };
  });
}

/// The text without any markers (what search, export names and the minimap use).
export function plain(text) {
  return parse(text).map(l => (l.bullet ? '• ' : '') + l.runs.map(r => r.text).join('')).join('\n');
}

/// Whether the text uses any formatting (plain text keeps the old, faster drawing path).
export const hasFormatting = text => /\*\*|~~|(^|[^*])\*[^*\s]|\]\((https?:|note:)|^\s*[-•]\s/m.test(String(text ?? ''));

/// Wraps formatted lines into visual lines that fit `maxWidth`. `measure(str, run)` returns a width for that run's style.
/// Returns [{ bullet, first, runs }] where `first` marks the first visual line of a bulleted line.
export function wrapRuns(lines, maxWidth, measure, bulletIndent = 0) {
  const out = [];
  for (const line of lines) {
    const width = maxWidth - (line.bullet ? bulletIndent : 0);
    // words keep their run style; spaces are their own pieces so a line can break there
    const pieces = [];
    for (const r of line.runs) for (const w of r.text.split(/(\s+)/)) if (w) pieces.push({ ...r, text: w });
    let cur = [], curW = 0, first = true;
    const flush = () => { out.push({ bullet: line.bullet, first, runs: merge(cur) }); first = false; cur = []; curW = 0; };
    for (const p of pieces) {
      const w = measure(p.text, p);
      const isSpace = !p.text.trim();
      if (curW + w > width && cur.length && !isSpace) { flush(); }
      if (isSpace && !cur.length) continue;              // no space at the start of a wrapped line
      if (w > width && !isSpace) {
        // a single word longer than the line is broken by characters
        let chunk = '';
        for (const ch of p.text) { const cw = measure(chunk + ch, p); if (curW + cw > width && (chunk || cur.length)) { if (chunk) cur.push({ ...p, text: chunk }); flush(); chunk = ch; } else chunk += ch; }
        if (chunk) { cur.push({ ...p, text: chunk }); curW += measure(chunk, p); }
        continue;
      }
      cur.push(p); curW += w;
    }
    if (cur.length || first) flush();
  }
  // trailing spaces on each visual line do not count
  for (const l of out) { const last = l.runs.at(-1); if (last && !last.text.trim()) l.runs.pop(); else if (last) last.text = last.text.replace(/\s+$/, ''); }
  return out;
}
const same = (a, b) => a.b === b.b && a.i === b.i && a.s === b.s && a.href === b.href;
function merge(runs) {
  const out = [];
  for (const r of runs) { const p = out.at(-1); if (p && same(p, r)) p.text += r.text; else out.push({ ...r }); }
  return out;
}

/// Toggles a marker around the selection of a textarea value. Returns { value, start, end }.
export function toggleMark(value, start, end, mark) {
  const sel = value.slice(start, end);
  const before = value.slice(0, start), after = value.slice(end);
  if (before.endsWith(mark) && after.startsWith(mark)) return { value: before.slice(0, -mark.length) + sel + after.slice(mark.length), start: start - mark.length, end: end - mark.length };
  if (sel.startsWith(mark) && sel.endsWith(mark) && sel.length >= mark.length * 2) { const inner = sel.slice(mark.length, sel.length - mark.length); return { value: before + inner + after, start, end: start + inner.length }; }
  return { value: before + mark + sel + mark + after, start: start + mark.length, end: end + mark.length };
}

/// Turns the lines touched by the selection into bullets (or back).
export function toggleBullets(value, start, end) {
  const a = value.lastIndexOf('\n', start - 1) + 1, bEnd = value.indexOf('\n', end), b = bEnd < 0 ? value.length : bEnd;
  const lines = value.slice(a, b).split('\n');
  const on = !lines.every(l => /^\s*[-•]\s+/.test(l));
  const next = lines.map(l => on ? (/^\s*[-•]\s+/.test(l) ? l : `- ${l}`) : l.replace(/^\s*[-•]\s+/, '')).join('\n');
  // one line with no selection: the caret goes after the text so typing carries on in the bullet
  if (lines.length === 1 && start === end) return { value: value.slice(0, a) + next + value.slice(b), start: a + next.length, end: a + next.length };
  return { value: value.slice(0, a) + next + value.slice(b), start: a, end: a + next.length };
}

/// Wraps the selection as a link. An empty selection uses the address as its label.
export function makeLink(value, start, end, href) {
  const label = value.slice(start, end) || href.replace(/^https?:\/\//, '').slice(0, 60);
  const md = `[${label.replace(/[[\]]/g, '')}](${href})`;
  return { value: value.slice(0, start) + md + value.slice(end), start: start + md.length, end: start + md.length };
}
