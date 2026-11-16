// Whiteboard round 4 (Visio, network, ClickUp): pure helpers with no DOM, so they are tested without a browser.
// Data graphics rules, re-layout styles, the shape report (CSV / Excel), PowerPoint export, version diff,
// find by IP, org chart from Directory, diagram steps to Projects tasks, and a tiny ZIP writer for xlsx/pptx.
import { STENCIL, isNetDevice, NET_FIELDS, rackSlots, rackUnits, lanesOf } from './stencils.js';
import { graph, flowOrder, stepNumbers } from './flow.js';

const field = (el, re) => (el?.data || []).find(d => re.test(d.name || ''))?.value ?? '';
const firstLine = el => String(el.text || el.title || el.name || STENCIL[el.type]?.label || el.type || '').split('\n')[0].replace(/\*\*|~~/g, '').trim();
const isNodeEl = e => e && !e.isDeleted && e.type !== 'connector' && e.type !== 'path' && e.type !== 'frame' && e.type !== 'swimlane' && e.type !== 'groupBox'
  && e.type !== 'listBox' && e.type !== 'rack' && e.type !== 'fpRoom' && e.type !== 'umlPackage' && e.type !== 'callout';

// ── Data graphics ─────────────────────────────────────────────────────────────
// Rules live on the board (one per board): { show: 'bar'|'icon'|'color'|'off', field: 'Progress', lateDays: 3 }.
// Status words, a progress % and a duration in days decide the colour: Done green, Late red, In progress orange.
export const DEFAULT_RULES = { show: 'off', lateDays: 3 };
const DONE = /\b(done|selesai|complete|completed|ok|closed)\b/i, LATE = /\b(late|terlambat|blocked|stuck|overdue)\b/i, DOING = /\b(progress|doing|proses|ongoing|working)\b/i;
export function daysOf(v) {
  const s = String(v || '').toLowerCase(); const n = parseFloat(s.replace(',', '.'));
  if (!Number.isFinite(n)) return null;
  if (/min|menit/.test(s)) return n / 480;
  if (/\bh\b|hour|jam/.test(s)) return n / 8;
  if (/week|minggu/.test(s)) return n * 5;
  return n;   // days by default
}
export function graphicsFor(el, rules) {
  if (!rules || !rules.show || rules.show === 'off' || !el?.data?.length) return null;
  const status = field(el, /status/i), pctRaw = field(el, /progress|%|percent/i), dur = daysOf(field(el, /duration|durasi|days|hari/i));
  const pct = pctRaw !== '' ? Math.max(0, Math.min(100, parseFloat(String(pctRaw).replace('%', '')) || 0)) : (DONE.test(status) ? 100 : null);
  if (!status && pct == null && dur == null) return null;
  const late = LATE.test(status) || (dur != null && rules.lateDays != null && dur > rules.lateDays && !DONE.test(status) && pct !== 100);
  const kind = DONE.test(status) || pct === 100 ? 'done' : late ? 'late' : DOING.test(status) || (pct != null && pct > 0) ? 'doing' : 'todo';
  const color = { done: '#34C759', late: '#FF3B30', doing: '#FF9500', todo: '#8E8E93' }[kind];
  if (rules.show === 'bar') return { kind, color, bar: (pct ?? (kind === 'done' ? 100 : 0)) / 100 };
  if (rules.show === 'icon') return { kind, color, icon: { done: '✓', late: '!', doing: '…', todo: '·' }[kind] };
  if (rules.show === 'color') return { kind, color, fill: `${color}33` };
  return null;
}

// ── Re-layout styles ─────────────────────────────────────────────────────────
/// Parent → children from connectors (first parent wins), roots = nodes nobody points to.
function forest(els, ids) {
  const g = graph(els);
  const nodes = g.nodes.filter(n => !ids || ids.has(n.id));
  const inSet = new Set(nodes.map(n => n.id));
  const kids = new Map(nodes.map(n => [n.id, []])), parent = new Map();
  const order = flowOrder(g).filter(n => inSet.has(n.id));
  const rank = new Map([...order, ...nodes].map((n, i) => [n.id, i]));
  const edges = g.edges.filter(e => inSet.has(e.from) && inSet.has(e.to)).sort((a, b) => (rank.get(a.from) - rank.get(b.from)) || (rank.get(a.to) - rank.get(b.to)));
  for (const e of edges) if (!parent.has(e.to) && e.to !== e.from && !isAncestor(e.to, e.from)) { parent.set(e.to, e.from); kids.get(e.from).push(e.to); }
  function isAncestor(a, b) { for (let p = b; p != null; p = parent.get(p)) if (p === a) return true; return false; }
  const roots = nodes.filter(n => !parent.has(n.id)).sort((a, b) => a.y - b.y || a.x - b.x);
  return { nodes, kids, parent, roots, byId: g.byId, edges };
}
/// Hierarchy (tree): parents centred above their children, top to bottom. Returns Map(id -> {x,y}).
export function layoutTree(els, { ids, gapX = 40, gapY = 80, origin } = {}) {
  const f = forest(els, ids); if (!f.nodes.length) return new Map();
  const ox = origin?.x ?? Math.min(...f.nodes.map(n => n.x)), oy = origin?.y ?? Math.min(...f.nodes.map(n => n.y));
  const width = new Map();
  const wOf = id => { if (width.has(id)) return width.get(id); const n = f.byId.get(id); const k = f.kids.get(id) || []; const w = Math.max(n.w, k.reduce((s, c) => s + wOf(c), 0) + Math.max(0, k.length - 1) * gapX); width.set(id, w); return w; };
  const levelH = [];
  const depth = (id, d) => { levelH[d] = Math.max(levelH[d] || 0, f.byId.get(id).h); for (const c of f.kids.get(id) || []) depth(c, d + 1); };
  f.roots.forEach(r => depth(r.id, 0));
  const top = []; let acc = oy; for (let d = 0; d < levelH.length; d++) { top[d] = acc; acc += levelH[d] + gapY; }
  const pos = new Map();
  const place = (id, left, d) => {
    const n = f.byId.get(id), w = wOf(id);
    pos.set(id, { x: Math.round(left + (w - n.w) / 2), y: Math.round(top[d] + (levelH[d] - n.h) / 2) });
    let x = left + (w - ((f.kids.get(id) || []).reduce((s, c) => s + wOf(c), 0) + Math.max(0, (f.kids.get(id) || []).length - 1) * gapX)) / 2;
    for (const c of f.kids.get(id) || []) { place(c, x, d + 1); x += wOf(c) + gapX; }
  };
  let x = ox; for (const r of f.roots) { place(r.id, x, 0); x += wOf(r.id) + gapX * 2; }
  return pos;
}
/// Radial: the root in the middle, each level on a ring around it.
export function layoutRadial(els, { ids, ring = 220 } = {}) {
  const f = forest(els, ids); if (!f.nodes.length) return new Map();
  const root = f.roots.length === 1 ? f.roots[0] : [...f.nodes].sort((a, b) => (f.kids.get(b.id).length - f.kids.get(a.id).length))[0];
  const cx = root.x + root.w / 2, cy = root.y + root.h / 2;
  const leaves = new Map();
  const count = id => { if (leaves.has(id)) return leaves.get(id); const k = f.kids.get(id) || []; const n = k.length ? k.reduce((s, c) => s + count(c), 0) : 1; leaves.set(id, n); return n; };
  const pos = new Map(), seen = new Set([root.id]);
  const place = (id, a0, a1, d) => {
    const k = (f.kids.get(id) || []).filter(c => !seen.has(c)); const total = k.reduce((s, c) => s + count(c), 0) || 1;
    let a = a0;
    for (const c of k) {
      seen.add(c);
      const span = (a1 - a0) * count(c) / total, mid = a + span / 2, n = f.byId.get(c), r = ring * d;
      pos.set(c, { x: Math.round(cx + Math.cos(mid) * r - n.w / 2), y: Math.round(cy + Math.sin(mid) * r * 0.75 - n.h / 2) });
      place(c, a, a + span, d + 1); a += span;
    }
  };
  place(root.id, -Math.PI / 2, Math.PI * 1.5, 1);
  // the other roots (separate trees) go on the outer ring
  const rest = f.nodes.filter(n => n.id !== root.id && !pos.has(n.id));
  rest.forEach((n, i) => { const a = (i / Math.max(1, rest.length)) * Math.PI * 2; const r = ring * 3; pos.set(n.id, { x: Math.round(cx + Math.cos(a) * r - n.w / 2), y: Math.round(cy + Math.sin(a) * r * 0.75 - n.h / 2) }); });
  return pos;
}
/// Organic: a small force layout (connected shapes pull together, all shapes push apart). Deterministic.
export function layoutOrganic(els, { ids, iterations = 220, ideal = 220 } = {}) {
  const f = forest(els, ids); if (!f.nodes.length) return new Map();
  const P = new Map(f.nodes.map(n => [n.id, { x: n.x + n.w / 2, y: n.y + n.h / 2 }]));
  // a spread start (circle) so shapes on top of each other separate the same way every time
  f.nodes.forEach((n, i) => { const p = P.get(n.id), a = (i / f.nodes.length) * Math.PI * 2; p.x += Math.cos(a) * 1; p.y += Math.sin(a) * 1; });
  const edges = f.edges;
  for (let it = 0; it < iterations; it++) {
    const t = 30 * (1 - it / iterations) + 1;
    const D = new Map(f.nodes.map(n => [n.id, { x: 0, y: 0 }]));
    for (let i = 0; i < f.nodes.length; i++) for (let j = i + 1; j < f.nodes.length; j++) {
      const a = P.get(f.nodes[i].id), b = P.get(f.nodes[j].id); let dx = a.x - b.x, dy = a.y - b.y; const d = Math.max(1, Math.hypot(dx, dy));
      const k = (ideal * ideal) / d; dx /= d; dy /= d;
      D.get(f.nodes[i].id).x += dx * k; D.get(f.nodes[i].id).y += dy * k; D.get(f.nodes[j].id).x -= dx * k; D.get(f.nodes[j].id).y -= dy * k;
    }
    for (const e of edges) {
      const a = P.get(e.from), b = P.get(e.to); if (!a || !b) continue; let dx = a.x - b.x, dy = a.y - b.y; const d = Math.max(1, Math.hypot(dx, dy));
      const k = (d * d) / ideal; dx /= d; dy /= d;
      D.get(e.from).x -= dx * k; D.get(e.from).y -= dy * k; D.get(e.to).x += dx * k; D.get(e.to).y += dy * k;
    }
    for (const n of f.nodes) { const d = D.get(n.id), l = Math.max(1, Math.hypot(d.x, d.y)), p = P.get(n.id); p.x += d.x / l * Math.min(l, t); p.y += d.y / l * Math.min(l, t); }
  }
  // keep the picture where it was (same top-left)
  const minX = Math.min(...f.nodes.map(n => n.x)), minY = Math.min(...f.nodes.map(n => n.y));
  const nx = Math.min(...f.nodes.map(n => P.get(n.id).x - n.w / 2)), ny = Math.min(...f.nodes.map(n => P.get(n.id).y - n.h / 2));
  return new Map(f.nodes.map(n => [n.id, { x: Math.round(P.get(n.id).x - n.w / 2 - nx + minX), y: Math.round(P.get(n.id).y - n.h / 2 - ny + minY) }]));
}
/// Network tidy-up: Internet → firewall → core → access → devices, as a tree.
const NET_RANK = { netCloud: 0, netISP: 0, netFirewall: 1, netVPN: 1, netRouter: 1, netLB: 2, netCoreSwitch: 2, netSwitch: 3, netAccessSwitch: 3, netCluster: 3 };
export function layoutNetwork(els, opts = {}) {
  // point every connector from the higher-level device to the lower one, then lay out as a tree
  const rank = e => NET_RANK[e?.type] ?? 4;
  const byId = new Map(els.map(e => [e.id, e]));
  const fixed = els.map(e => {
    if (e.type !== 'connector' || !byId.get(e.from?.id) || !byId.get(e.to?.id)) return e;
    return rank(byId.get(e.from.id)) > rank(byId.get(e.to.id)) ? { ...e, from: e.to, to: e.from } : e;
  });
  return layoutTree(fixed, { gapX: 50, gapY: 90, ...opts });
}

// ── Find a device by IP, hostname or name ───────────────────────────────────
export function findDevice(els, q) {
  const s = String(q || '').trim().toLowerCase(); if (!s) return [];
  return els.filter(e => !e.isDeleted && (isNetDevice(e) || (e.data || []).some(d => /^ip$/i.test(d.name || ''))))
    .map(e => {
      const ip = String(field(e, /^ip$/i)).toLowerCase(), host = String(field(e, /host/i)).toLowerCase(), mac = String(field(e, /^mac$/i)).toLowerCase(), name = firstLine(e).toLowerCase();
      const score = ip === s ? 0 : ip.startsWith(s) ? 1 : host.startsWith(s) || name.startsWith(s) ? 2 : mac.includes(s) || host.includes(s) || name.includes(s) || ip.includes(s) ? 3 : 9;
      return { e, score };
    }).filter(x => x.score < 9).sort((a, b) => a.score - b.score || a.e.y - b.e.y || a.e.x - b.e.x).map(x => x.e);
}
/// The data fields a new network device starts with.
export const netFields = () => NET_FIELDS.map(name => ({ name, value: '' }));

// ── Racks ────────────────────────────────────────────────────────────────────
/// The rack a device box is dropped into (its centre inside the rack) and where it snaps: { rack, u, box } or null.
export function rackDrop(dev, els) {
  const cx = dev.x + dev.w / 2, cy = dev.y + dev.h / 2;
  const rack = els.find(r => r.type === 'rack' && !r.isDeleted && r.id !== dev.id && cx >= r.x && cx <= r.x + r.w && cy >= r.y && cy <= r.y + r.h);
  if (!rack) return null;
  const s = rackSlots(rack), size = Math.min(rackUnits(dev), s.units);
  let u = s.uAt(dev.y, size);
  if (!u) u = dev.y < rack.y + rack.h / 2 ? s.units - size + 1 : 1;
  return { rack, u, size, box: s.slot(u, size) };
}
/// What a rack holds, from the top: [{ u, size, el }], and how many U are free.
export function rackContents(rack, els) {
  const s = rackSlots(rack);
  const inside = els.filter(e => !e.isDeleted && e.id !== rack.id && e.type !== 'connector' && e.type !== 'path' && e.x >= rack.x - 1 && e.x + e.w <= rack.x + rack.w + 1 && e.y >= rack.y - 1 && e.y + e.h <= rack.y + rack.h + 1);
  const rows = inside.map(e => { const size = Math.max(1, Math.round(e.h / s.uh)); return { u: s.uAt(e.y, size), size, el: e }; }).filter(r => r.u).sort((a, b) => b.u - a.u);
  const used = new Set(); for (const r of rows) for (let k = 0; k < r.size; k++) used.add(r.u + k);
  return { rows, units: s.units, free: s.units - used.size };
}

// ── Lists (containers that stack their contents) ──────────────────────────────
/// New positions for the shapes inside a list box: stacked under the title, full width, in their current order.
export function stackList(list, els, { gap = 8, head = 44, pad = 12 } = {}) {
  const inside = els.filter(e => isNodeEl(e) && e.id !== list.id && e.x + e.w / 2 >= list.x && e.x + e.w / 2 <= list.x + list.w && e.y + e.h / 2 >= list.y && e.y + e.h / 2 <= list.y + list.h)
    .sort((a, b) => a.y - b.y || a.x - b.x);
  const pos = new Map(); let y = list.y + head;
  for (const e of inside) { pos.set(e.id, { x: list.x + pad, y, w: list.w - pad * 2 }); y += e.h + gap; }
  return { pos, height: Math.max(list.h, y - list.y + pad - gap) };
}

// ── Report: every shape with its data, for Excel / CSV ───────────────────────
export function reportRows(els, { types } = {}) {
  const live = els.filter(e => !e.isDeleted);
  const shapes = live.filter(e => isNodeEl(e) && (e.text || e.data?.length || STENCIL[e.type]) && (!types || types.has(e.type)));
  const steps = stepNumbers(live);
  const lanes = live.filter(e => e.type === 'swimlane');
  const laneOf = e => { const cx = e.x + e.w / 2, cy = e.y + e.h / 2; for (const s of lanes) for (const l of lanesOf(s)) if (cx >= l.box.x && cx <= l.box.x + l.box.w && cy >= l.box.y && cy <= l.box.y + l.box.h) return l.name || ''; return ''; };
  const fieldNames = [];
  for (const e of shapes) for (const d of e.data || []) if (d.name && !fieldNames.includes(d.name)) fieldNames.push(d.name);
  const head = ['Step', 'Shape', 'Text', ...fieldNames, 'Lane'];
  const rows = shapes.sort((a, b) => (steps.get(a.id) ?? 1e9) - (steps.get(b.id) ?? 1e9) || a.y - b.y || a.x - b.x)
    .map(e => [steps.get(e.id) ?? '', STENCIL[e.type]?.label || e.type, String(e.text || e.title || '').replace(/\*\*|~~/g, '').replace(/\n/g, ' / '), ...fieldNames.map(n => (e.data || []).find(d => d.name === n)?.value ?? ''), laneOf(e)]);
  return { head, rows };
}
const csvCell = v => { const s = String(v ?? ''); return /[",;\n]/.test(s) ? `"${s.replace(/"/g, '""')}"` : s; };
/// CSV with a BOM so Excel reads accents; ";" is what Excel uses in Indonesia.
export const toCsv = ({ head, rows }, sep = ';') => '﻿' + [head, ...rows].map(r => r.map(csvCell).join(sep)).join('\r\n') + '\r\n';

// ── A tiny ZIP writer (stored, no compression): enough for .xlsx and .pptx ───
const CRC = (() => { const t = new Uint32Array(256); for (let n = 0; n < 256; n++) { let c = n; for (let k = 0; k < 8; k++) c = c & 1 ? 0xEDB88320 ^ (c >>> 1) : c >>> 1; t[n] = c >>> 0; } return t; })();
export function crc32(bytes) { let c = 0xFFFFFFFF; for (let i = 0; i < bytes.length; i++) c = CRC[(c ^ bytes[i]) & 255] ^ (c >>> 8); return (c ^ 0xFFFFFFFF) >>> 0; }
export function zip(files) {
  const enc = new TextEncoder(), parts = [], central = []; let off = 0;
  for (const [name, data] of files) {
    const nb = enc.encode(name), body = typeof data === 'string' ? enc.encode(data) : data, crc = crc32(body);
    const h = new DataView(new ArrayBuffer(30));
    h.setUint32(0, 0x04034b50, true); h.setUint16(4, 20, true); h.setUint16(6, 0x0800, true); h.setUint16(8, 0, true);
    h.setUint32(14, crc, true); h.setUint32(18, body.length, true); h.setUint32(22, body.length, true); h.setUint16(26, nb.length, true);
    parts.push(new Uint8Array(h.buffer), nb, body);
    const c = new DataView(new ArrayBuffer(46));
    c.setUint32(0, 0x02014b50, true); c.setUint16(4, 20, true); c.setUint16(6, 20, true); c.setUint16(8, 0x0800, true);
    c.setUint32(16, crc, true); c.setUint32(20, body.length, true); c.setUint32(24, body.length, true); c.setUint16(28, nb.length, true); c.setUint32(42, off, true);
    central.push(new Uint8Array(c.buffer), nb);
    off += 30 + nb.length + body.length;
  }
  const size = central.reduce((s, p) => s + p.length, 0);
  const e = new DataView(new ArrayBuffer(22));
  e.setUint32(0, 0x06054b50, true); e.setUint16(8, files.length, true); e.setUint16(10, files.length, true); e.setUint32(12, size, true); e.setUint32(16, off, true);
  const all = [...parts, ...central, new Uint8Array(e.buffer)], out = new Uint8Array(all.reduce((s, p) => s + p.length, 0));
  let at = 0; for (const p of all) { out.set(p, at); at += p.length; }
  return out;
}
const xml = s => String(s ?? '').replace(/[&<>"]/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c])).replace(/[\u0000-\u0008\u000B\u000C\u000E-\u001F]/g, '');
const colName = i => { let s = ''; for (i++; i; i = Math.floor((i - 1) / 26)) s = String.fromCharCode(65 + ((i - 1) % 26)) + s; return s; };

/// A one-sheet .xlsx (inline strings, bold header, frozen first row).
export function toXlsx({ head, rows }, sheet = 'Report') {
  const cell = (v, r, c, s) => (typeof v === 'number' && Number.isFinite(v) ? `<c r="${colName(c)}${r}"${s ? ' s="1"' : ''}><v>${v}</v></c>` : `<c r="${colName(c)}${r}" t="inlineStr"${s ? ' s="1"' : ''}><is><t xml:space="preserve">${xml(v)}</t></is></c>`);
  const sheetXml = `<?xml version="1.0" encoding="UTF-8" standalone="yes"?><worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetViews><sheetView workbookViewId="0"><pane ySplit="1" topLeftCell="A2" activePane="bottomLeft" state="frozen"/></sheetView></sheetViews><cols>${head.map((h, i) => `<col min="${i + 1}" max="${i + 1}" width="${Math.min(60, Math.max(10, ...[h, ...rows.map(r => r[i])].map(v => String(v ?? '').length + 2)))}" customWidth="1"/>`).join('')}</cols><sheetData>${[head, ...rows].map((r, ri) => `<row r="${ri + 1}">${r.map((v, ci) => cell(v, ri + 1, ci, ri === 0)).join('')}</row>`).join('')}</sheetData></worksheet>`;
  return zip([
    ['[Content_Types].xml', '<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/><Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/><Override PartName="/xl/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"/></Types>'],
    ['_rels/.rels', '<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/></Relationships>'],
    ['xl/workbook.xml', `<?xml version="1.0" encoding="UTF-8" standalone="yes"?><workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets><sheet name="${xml(sheet.slice(0, 31))}" sheetId="1" r:id="rId1"/></sheets></workbook>`],
    ['xl/_rels/workbook.xml.rels', '<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/><Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/></Relationships>'],
    ['xl/styles.xml', '<?xml version="1.0" encoding="UTF-8" standalone="yes"?><styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><fonts count="2"><font><sz val="11"/><name val="Calibri"/></font><font><b/><sz val="11"/><name val="Calibri"/></font></fonts><fills count="2"><fill><patternFill patternType="none"/></fill><fill><patternFill patternType="gray125"/></fill></fills><borders count="1"><border/></borders><cellStyleXfs count="1"><xf/></cellStyleXfs><cellXfs count="2"><xf fontId="0"/><xf fontId="1" applyFont="1"/></cellXfs></styleSheet>'],
    ['xl/worksheets/sheet1.xml', sheetXml],
  ]);
}

// ── PowerPoint: each frame is a slide; shapes and text stay editable ──────────
const PRST = {
  rect: 'roundRect', ellipse: 'ellipse', diamond: 'diamond', triangle: 'triangle', star: 'star5', cloud: 'cloud', arrowShape: 'rightArrow', cylinder: 'can', bubble: 'wedgeRoundRectCallout',
  terminator: 'flowChartTerminator', process: 'flowChartProcess', decision: 'flowChartDecision', data: 'flowChartInputOutput', document: 'flowChartDocument', subprocess: 'flowChartPredefinedProcess',
  manualInput: 'flowChartManualInput', preparation: 'flowChartPreparation', delay: 'flowChartDelay', database: 'flowChartMagneticDisk', offPage: 'flowChartOffpageConnector', display: 'flowChartDisplay',
  storage: 'flowChartOnlineStorage', manualOp: 'flowChartManualOperation', multiDoc: 'flowChartMultidocument', bpmnStart: 'ellipse', bpmnEnd: 'ellipse', bpmnTimer: 'ellipse', bpmnMessage: 'ellipse',
  bpmnTask: 'roundRect', bpmnGateway: 'diamond', bpmnParallel: 'diamond', erTable: 'rect', erEntity: 'rect', umlClass: 'rect', umlInterface: 'rect', umlUseCase: 'ellipse', umlState: 'roundRect',
  umlActivity: 'roundRect', umlNote: 'foldedCorner', umlStartNode: 'ellipse', umlEndNode: 'ellipse', orgCard: 'roundRect', sticky: 'rect', callout: 'rect', milestone: 'diamond', phase: 'homePlate',
  vsProcess: 'rect', vsInventory: 'triangle', fpDesk: 'rect', fpMeeting: 'roundRect', fpWall: 'rect', fpWindow: 'rect', fpRoom: 'rect', groupBox: 'roundRect', listBox: 'roundRect',
};
const STICKY_HEX = { yellow: 'FFE58F', green: 'B7EB8F', blue: '91D5FF', pink: 'FFADD2', purple: 'D3ADF7', orange: 'FFC069' };
const hex = (c, fb) => { const m = /^#?([0-9a-f]{6})$/i.exec(String(c || '')); return m ? m[1].toUpperCase() : fb; };
/// slides: [{ title, box: {x,y,w,h}, els: [...], picture?: { bytes(Uint8Array png), box } }]
/// Shapes, stickies, text and straight lines become real PowerPoint shapes; everything else is passed in `picture`.
export function toPptx(slides, { title = 'Whiteboard' } = {}) {
  const W = 12192000, H = 6858000;             // 16:9 in EMU
  const files = [];
  const slideXml = (s, idx) => {
    const k = Math.min(W / Math.max(1, s.box.w), H / Math.max(1, s.box.h)) * 0.94;
    const ox = (W - s.box.w * k) / 2, oy = (H - s.box.h * k) / 2;
    const X = v => Math.round(ox + (v - s.box.x) * k), Y = v => Math.round(oy + (v - s.box.y) * k), L = v => Math.max(1, Math.round(v * k));
    let id = 2; const sp = [];
    if (s.picture) sp.push(`<p:pic><p:nvPicPr><p:cNvPr id="${id++}" name="Drawing"/><p:cNvPicPr/><p:nvPr/></p:nvPicPr><p:blipFill><a:blip r:embed="rIdImg"/><a:stretch><a:fillRect/></a:stretch></p:blipFill><p:spPr><a:xfrm><a:off x="${X(s.picture.box.x)}" y="${Y(s.picture.box.y)}"/><a:ext cx="${L(s.picture.box.w)}" cy="${L(s.picture.box.h)}"/></a:xfrm><a:prstGeom prst="rect"><a:avLst/></a:prstGeom></p:spPr></p:pic>`);
    const byId = new Map(s.els.map(e => [e.id, e]));
    for (const e of s.els) {
      if (e.type === 'connector') {
        const a = byId.get(e.from?.id), b = byId.get(e.to?.id);
        const pa = a ? { x: a.x + a.w / 2, y: a.y + a.h / 2 } : e.from, pb = b ? { x: b.x + b.w / 2, y: b.y + b.h / 2 } : e.to;
        if (!pa || !pb || pa.x == null || pb.x == null) continue;
        const x1 = Math.min(pa.x, pb.x), y1 = Math.min(pa.y, pb.y), flipH = pb.x < pa.x, flipV = pb.y < pa.y;
        sp.push(`<p:cxnSp><p:nvCxnSpPr><p:cNvPr id="${id++}" name="Line"/><p:cNvCxnSpPr/><p:nvPr/></p:nvCxnSpPr><p:spPr><a:xfrm${flipH ? ' flipH="1"' : ''}${flipV ? ' flipV="1"' : ''}><a:off x="${X(x1)}" y="${Y(y1)}"/><a:ext cx="${L(Math.abs(pb.x - pa.x))}" cy="${L(Math.abs(pb.y - pa.y))}"/></a:xfrm><a:prstGeom prst="${e.route === 'elbow' ? 'bentConnector3' : 'straightConnector1'}"><a:avLst/></a:prstGeom><a:ln w="19050"><a:solidFill><a:srgbClr val="${hex(e.stroke, '6E6E73')}"/></a:solidFill>${(e.endHead ?? 'arrow') !== 'none' ? '<a:tailEnd type="triangle"/>' : ''}</a:ln></p:spPr></p:cxnSp>`);
        continue;
      }
      const geom = e.type === 'text' ? 'rect' : PRST[e.type];
      if (!geom) continue;
      const fill = e.type === 'sticky' ? STICKY_HEX[e.color] || STICKY_HEX.yellow : e.type === 'text' ? null : e.fill === 'none' ? null : hex(e.fill, 'FFFFFF');
      const line = e.type === 'sticky' || e.type === 'text' ? null : hex(e.stroke, '1D1D1F');
      const text = String(e.text || e.title || '').replace(/\*\*|~~/g, '');
      const size = Math.round(Math.max(8, Math.min(40, (e.size || (e.type === 'text' ? 20 : 15)) * k / 12700 * 0.75)) * 100);
      const paras = (text ? text.split('\n') : ['']).map(t => `<a:p><a:pPr algn="${e.type === 'text' || e.type === 'sticky' ? 'l' : 'ctr'}"/>${t ? `<a:r><a:rPr lang="en-US" sz="${size}" dirty="0"><a:solidFill><a:srgbClr val="${e.type === 'sticky' ? '1D1D1F' : hex(e.textColor || (e.type === 'text' ? e.stroke : null), '1D1D1F')}"/></a:solidFill></a:rPr><a:t>${xml(t)}</a:t></a:r>` : ''}</a:p>`).join('');
      sp.push(`<p:sp><p:nvSpPr><p:cNvPr id="${id++}" name="${xml(STENCIL[e.type]?.label || e.type)}"/><p:cNvSpPr/><p:nvPr/></p:nvSpPr><p:spPr><a:xfrm><a:off x="${X(e.x)}" y="${Y(e.y)}"/><a:ext cx="${L(e.w)}" cy="${L(e.h)}"/></a:xfrm><a:prstGeom prst="${geom}"><a:avLst/></a:prstGeom>${fill ? `<a:solidFill><a:srgbClr val="${fill}"/></a:solidFill>` : '<a:noFill/>'}${line ? `<a:ln w="19050"><a:solidFill><a:srgbClr val="${line}"/></a:solidFill></a:ln>` : '<a:ln><a:noFill/></a:ln>'}</p:spPr><p:txBody><a:bodyPr wrap="square" anchor="${e.type === 'text' || e.type === 'sticky' ? 't' : 'ctr'}" lIns="72000" rIns="72000"><a:normAutofit/></a:bodyPr><a:lstStyle/>${paras}</p:txBody></p:sp>`);
    }
    if (s.title) sp.push(`<p:sp><p:nvSpPr><p:cNvPr id="${id++}" name="Title"/><p:cNvSpPr txBox="1"/><p:nvPr/></p:nvSpPr><p:spPr><a:xfrm><a:off x="304800" y="152400"/><a:ext cx="${W - 609600}" cy="400000"/></a:xfrm><a:prstGeom prst="rect"><a:avLst/></a:prstGeom><a:noFill/></p:spPr><p:txBody><a:bodyPr/><a:lstStyle/><a:p><a:r><a:rPr lang="en-US" sz="1600" b="1"><a:solidFill><a:srgbClr val="6E6E73"/></a:solidFill></a:rPr><a:t>${xml(s.title)}</a:t></a:r></a:p></p:txBody></p:sp>`);
    return `<?xml version="1.0" encoding="UTF-8" standalone="yes"?><p:sld xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships" xmlns:p="http://schemas.openxmlformats.org/presentationml/2006/main"><p:cSld><p:spTree><p:nvGrpSpPr><p:cNvPr id="1" name=""/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr><p:grpSpPr/>${sp.join('')}</p:spTree></p:cSld><p:clrMapOvr><a:masterClrMapping/></p:clrMapOvr></p:sld>`;
  };
  const n = slides.length;
  files.push(['[Content_Types].xml', `<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Default Extension="png" ContentType="image/png"/><Override PartName="/ppt/presentation.xml" ContentType="application/vnd.openxmlformats-officedocument.presentationml.presentation.main+xml"/><Override PartName="/ppt/slideMasters/slideMaster1.xml" ContentType="application/vnd.openxmlformats-officedocument.presentationml.slideMaster+xml"/><Override PartName="/ppt/slideLayouts/slideLayout1.xml" ContentType="application/vnd.openxmlformats-officedocument.presentationml.slideLayout+xml"/><Override PartName="/ppt/theme/theme1.xml" ContentType="application/vnd.openxmlformats-officedocument.theme+xml"/>${slides.map((_, i) => `<Override PartName="/ppt/slides/slide${i + 1}.xml" ContentType="application/vnd.openxmlformats-officedocument.presentationml.slide+xml"/>`).join('')}<Override PartName="/docProps/core.xml" ContentType="application/vnd.openxmlformats-package.core-properties+xml"/></Types>`]);
  files.push(['_rels/.rels', '<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="ppt/presentation.xml"/><Relationship Id="rId2" Type="http://schemas.openxmlformats.org/package/2006/relationships/metadata/core-properties" Target="docProps/core.xml"/></Relationships>']);
  files.push(['docProps/core.xml', `<?xml version="1.0" encoding="UTF-8" standalone="yes"?><cp:coreProperties xmlns:cp="http://schemas.openxmlformats.org/package/2006/metadata/core-properties" xmlns:dc="http://purl.org/dc/elements/1.1/"><dc:title>${xml(title)}</dc:title><dc:creator>iPop</dc:creator></cp:coreProperties>`]);
  files.push(['ppt/presentation.xml', `<?xml version="1.0" encoding="UTF-8" standalone="yes"?><p:presentation xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships" xmlns:p="http://schemas.openxmlformats.org/presentationml/2006/main"><p:sldMasterIdLst><p:sldMasterId id="2147483648" r:id="rIdM"/></p:sldMasterIdLst><p:sldIdLst>${slides.map((_, i) => `<p:sldId id="${256 + i}" r:id="rIdS${i + 1}"/>`).join('')}</p:sldIdLst><p:sldSz cx="${W}" cy="${H}"/><p:notesSz cx="6858000" cy="9144000"/></p:presentation>`]);
  files.push(['ppt/_rels/presentation.xml.rels', `<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rIdM" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/slideMaster" Target="slideMasters/slideMaster1.xml"/><Relationship Id="rIdT" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/theme" Target="theme/theme1.xml"/>${slides.map((_, i) => `<Relationship Id="rIdS${i + 1}" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/slide" Target="slides/slide${i + 1}.xml"/>`).join('')}</Relationships>`]);
  files.push(['ppt/slideMasters/slideMaster1.xml', '<?xml version="1.0" encoding="UTF-8" standalone="yes"?><p:sldMaster xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships" xmlns:p="http://schemas.openxmlformats.org/presentationml/2006/main"><p:cSld><p:spTree><p:nvGrpSpPr><p:cNvPr id="1" name=""/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr><p:grpSpPr/></p:spTree></p:cSld><p:clrMap bg1="lt1" tx1="dk1" bg2="lt2" tx2="dk2" accent1="accent1" accent2="accent2" accent3="accent3" accent4="accent4" accent5="accent5" accent6="accent6" hlink="hlink" folHlink="folHlink"/><p:sldLayoutIdLst><p:sldLayoutId id="2147483649" r:id="rId1"/></p:sldLayoutIdLst></p:sldMaster>']);
  files.push(['ppt/slideMasters/_rels/slideMaster1.xml.rels', '<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/slideLayout" Target="../slideLayouts/slideLayout1.xml"/><Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/theme" Target="../theme/theme1.xml"/></Relationships>']);
  files.push(['ppt/slideLayouts/slideLayout1.xml', '<?xml version="1.0" encoding="UTF-8" standalone="yes"?><p:sldLayout xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships" xmlns:p="http://schemas.openxmlformats.org/presentationml/2006/main" type="blank"><p:cSld name="Blank"><p:spTree><p:nvGrpSpPr><p:cNvPr id="1" name=""/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr><p:grpSpPr/></p:spTree></p:cSld><p:clrMapOvr><a:masterClrMapping/></p:clrMapOvr></p:sldLayout>']);
  files.push(['ppt/slideLayouts/_rels/slideLayout1.xml.rels', '<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/slideMaster" Target="../slideMasters/slideMaster1.xml"/></Relationships>']);
  files.push(['ppt/theme/theme1.xml', THEME]);
  slides.forEach((s, i) => {
    files.push([`ppt/slides/slide${i + 1}.xml`, slideXml(s, i)]);
    files.push([`ppt/slides/_rels/slide${i + 1}.xml.rels`, `<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/slideLayout" Target="../slideLayouts/slideLayout1.xml"/>${s.picture ? `<Relationship Id="rIdImg" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/image" Target="../media/image${i + 1}.png"/>` : ''}</Relationships>`]);
    if (s.picture) files.push([`ppt/media/image${i + 1}.png`, s.picture.bytes]);
  });
  void n;
  return zip(files);
}
/// What PowerPoint can draw as real shapes (the rest goes into the slide's picture).
export const pptxNative = e => e.type === 'connector' || e.type === 'text' || !!PRST[e.type];
const THEME = '<?xml version="1.0" encoding="UTF-8" standalone="yes"?><a:theme xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main" name="iPop"><a:themeElements><a:clrScheme name="iPop"><a:dk1><a:srgbClr val="1D1D1F"/></a:dk1><a:lt1><a:srgbClr val="FFFFFF"/></a:lt1><a:dk2><a:srgbClr val="3A3A3C"/></a:dk2><a:lt2><a:srgbClr val="F5F5F7"/></a:lt2><a:accent1><a:srgbClr val="0A84FF"/></a:accent1><a:accent2><a:srgbClr val="34C759"/></a:accent2><a:accent3><a:srgbClr val="FF9500"/></a:accent3><a:accent4><a:srgbClr val="AF52DE"/></a:accent4><a:accent5><a:srgbClr val="FF2D55"/></a:accent5><a:accent6><a:srgbClr val="30B0C7"/></a:accent6><a:hlink><a:srgbClr val="0A84FF"/></a:hlink><a:folHlink><a:srgbClr val="5856D6"/></a:folHlink></a:clrScheme><a:fontScheme name="iPop"><a:majorFont><a:latin typeface="Segoe UI"/><a:ea typeface=""/><a:cs typeface=""/></a:majorFont><a:minorFont><a:latin typeface="Segoe UI"/><a:ea typeface=""/><a:cs typeface=""/></a:minorFont></a:fontScheme><a:fmtScheme name="iPop"><a:fillStyleLst><a:solidFill><a:schemeClr val="phClr"/></a:solidFill><a:solidFill><a:schemeClr val="phClr"/></a:solidFill><a:solidFill><a:schemeClr val="phClr"/></a:solidFill></a:fillStyleLst><a:lnStyleLst><a:ln w="9525"><a:solidFill><a:schemeClr val="phClr"/></a:solidFill></a:ln><a:ln w="19050"><a:solidFill><a:schemeClr val="phClr"/></a:solidFill></a:ln><a:ln w="28575"><a:solidFill><a:schemeClr val="phClr"/></a:solidFill></a:ln></a:lnStyleLst><a:effectStyleLst><a:effectStyle><a:effectLst/></a:effectStyle><a:effectStyle><a:effectLst/></a:effectStyle><a:effectStyle><a:effectLst/></a:effectStyle></a:effectStyleLst><a:bgFillStyleLst><a:solidFill><a:schemeClr val="phClr"/></a:solidFill><a:solidFill><a:schemeClr val="phClr"/></a:solidFill><a:solidFill><a:schemeClr val="phClr"/></a:solidFill></a:bgFillStyleLst></a:fmtScheme></a:themeElements></a:theme>';

// ── Version diff: what was added, removed or changed between two copies ──────
const VISIBLE = ['x', 'y', 'w', 'h', 'text', 'title', 'name', 'label', 'fill', 'stroke', 'data', 'points', 'from', 'to', 'lanes', 'color'];
const sig = e => JSON.stringify(VISIBLE.map(k => e[k] ?? null));
export function diffScenes(before, after) {
  const A = new Map(before.filter(e => !e.isDeleted).map(e => [e.id, e])), B = new Map(after.filter(e => !e.isDeleted).map(e => [e.id, e]));
  const added = [...B.values()].filter(e => !A.has(e.id)), removed = [...A.values()].filter(e => !B.has(e.id));
  const changed = [...B.values()].filter(e => A.has(e.id) && sig(A.get(e.id)) !== sig(e));
  return { added, removed, changed };
}
/// A short sentence for the "since you last opened" card: "added 2 shapes and changed 1".
export function diffSummary(d) {
  const label = e => (e.type === 'connector' ? 'line' : e.type === 'path' ? 'drawing' : e.type === 'sticky' ? 'sticky' : 'shape');
  const part = (n, verb, list) => { if (!n) return null; const kinds = [...new Set(list.map(label))]; return `${verb} ${n} ${kinds.length === 1 ? kinds[0] + (n > 1 ? 's' : '') : 'things'}`; };
  return [part(d.added.length, 'added', d.added), part(d.changed.length, 'changed', d.changed), part(d.removed.length, 'removed', d.removed)].filter(Boolean).join(', ').replace(/, ([^,]*)$/, ' and $1');
}

// ── Org chart from Directory ─────────────────────────────────────────────────
/// people: [{ key, name, dept, managerKey }] (no NIK or position for users). Returns cards + elbow lines laid out as a tree.
export function orgChart(people, create, at = { x: 0, y: 0 }) {
  const cards = people.map(p => create('orgCard', { x: at.x, y: at.y, w: 220, h: 76, text: [p.name, p.dept].filter(Boolean).join('\n'), photo: p.photo || undefined }));
  const byKey = new Map(people.map((p, i) => [p.key, cards[i]]));
  const lines = people.filter(p => p.managerKey && byKey.has(p.managerKey) && p.managerKey !== p.key)
    .map(p => create('connector', { from: { id: byKey.get(p.managerKey).id, side: 'b' }, to: { id: byKey.get(p.key).id, side: 't' }, route: 'elbow', endHead: 'none' }));
  const pos = layoutTree([...cards, ...lines], { gapX: 28, gapY: 60, origin: at });
  return [...cards.map(c => ({ ...c, ...(pos.get(c.id) || {}) })), ...lines];
}

// ── Diagram steps → Projects tasks ───────────────────────────────────────────
/// The selected shapes in flow order, with PIC and duration from their data, and chained due dates (working days).
export function tasksFromShapes(els, selected, { start = new Date(), chain = true } = {}) {
  const ids = new Set(selected.map(s => s.id));
  const order = flowOrder(graph(els)).filter(n => ids.has(n.id));
  const rest = selected.filter(s => !order.some(o => o.id === s.id)).sort((a, b) => a.y - b.y || a.x - b.x);
  const list = [...order, ...rest].filter(e => isNodeEl(e) && !/^(terminator|bpmnStart|bpmnEnd|umlStartNode|umlEndNode|decision|bpmnGateway|bpmnParallel)$/.test(e.type));
  let day = new Date(start.getFullYear(), start.getMonth(), start.getDate());
  const addWork = (d, n) => { const x = new Date(d); let left = Math.max(1, Math.ceil(n)); while (left > 0) { x.setDate(x.getDate() + 1); if (x.getDay() !== 0 && x.getDay() !== 6) left--; } return x; };
  return list.map(e => {
    const dur = daysOf(field(e, /duration|durasi|days|hari/i));
    let due = null;
    if (chain) { day = addWork(day, dur ?? 1); due = new Date(day); }
    const iso = due ? `${due.getFullYear()}-${String(due.getMonth() + 1).padStart(2, '0')}-${String(due.getDate()).padStart(2, '0')}` : null;
    return { id: e.id, title: firstLine(e) || 'Step', pic: field(e, /^(pic|owner|who|assignee)$/i), duration: dur, due: iso, description: String(e.text || '').split('\n').slice(1).join('\n') || null };
  });
}
