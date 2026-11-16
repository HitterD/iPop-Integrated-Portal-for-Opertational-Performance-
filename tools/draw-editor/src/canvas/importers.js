// Import diagrams made elsewhere (round 3, grill Q6): Mermaid "flowchart" text and draw.io files (.drawio / .xml, also the
// compressed form). Both become ordinary board elements (stencil shapes + connectors), placed with "tidy up flow".
import { create } from './model.js';
import { STENCIL } from './stencils.js';
import { layoutFlow } from './flow.js';

const size = type => ({ w: STENCIL[type]?.w || 180, h: STENCIL[type]?.h || 72 });

/// Mermaid flowchart text -> elements. Supports `flowchart|graph TD|TB|LR|RL|BT`, node shapes
/// A[box] A(rounded) A([stadium]) A{decision} A[(database)] A[/data/] A[[subprocess]] A((circle)) A>flag]
/// and edges A --> B, A -->|label| B, A -- label --> B, A --- B, A -.-> B, A ==> B, chained A --> B --> C, and `A & B --> C`.
export function fromMermaid(src, { x = 0, y = 0 } = {}) {
  const lines = String(src || '').replace(/\r/g, '').split('\n').map(l => l.replace(/%%.*$/, '').trim()).filter(Boolean);
  if (!lines.length) return { elements: [], error: 'Paste some Mermaid text first.' };
  const head = lines[0].match(/^(flowchart|graph)\s*(TD|TB|LR|RL|BT)?/i);
  const dir = head?.[2]?.toUpperCase() === 'LR' || head?.[2]?.toUpperCase() === 'RL' ? 'LR' : 'TB';
  const body = head ? lines.slice(1) : lines;
  const nodes = new Map(), edges = [];
  const shapeOf = (open, close) => {
    const k = open + close;
    if (k === '([])') return 'terminator';
    if (k === '{}') return 'decision';
    if (k === '[()]') return 'database';
    if (k === '[//]' || k === '[\\\\]') return 'data';
    if (k === '[[]]') return 'subprocess';
    if (k === '(())') return 'ellipse';
    if (k === '()') return 'terminator';
    if (k === '>]') return 'offPage';
    if (k === '{{}}') return 'preparation';
    return 'process';
  };
  const NODE = /^([A-Za-z0-9_.-]+)\s*(\(\[|\[\(|\[\[|\[\/|\[\\|\(\(|\{\{|\[|\(|\{|>)?(.*?)(\]\)|\)\]|\]\]|\/\]|\\\]|\)\)|\}\}|\]|\)|\})?$/;
  const node = raw => {
    const t = raw.trim().replace(/:::\w+$/, '');
    const m = t.match(NODE);
    if (!m) return null;
    const id = m[1];
    if (!nodes.has(id)) nodes.set(id, { id, type: 'process', text: id });
    const n = nodes.get(id);
    if (m[2]) { n.type = shapeOf(m[2], m[4] || ''); n.text = m[3].replace(/^"|"$/g, '').replace(/<br\s*\/?>/gi, '\n').trim() || id; }
    return n;
  };
  const EDGE = /\s*(-->|---|-\.->|==>|--\s+[^-]+?\s+-->|-\.\s+[^.]+?\s+\.->|==\s+[^=]+?\s+==>)\s*(?:\|([^|]*)\|)?\s*/;
  for (const line of body) {
    if (/^(classDef|class|style|linkStyle|subgraph|end|click|direction)\b/i.test(line)) continue;
    const parts = line.split(EDGE);
    // parts = [nodes, op, label, nodes, op, label, nodes …]
    if (parts.length < 4) { for (const one of line.split('&')) node(one); continue; }
    for (let i = 0; i + 3 < parts.length; i += 3) {
      const from = parts[i].split('&').map(node).filter(Boolean);
      const op = parts[i + 1] || '';
      const inline = op.match(/^(?:--|-\.|==)\s+(.+?)\s+(?:-->|\.->|==>)$/);
      const label = (parts[i + 2] || inline?.[1] || '').trim();
      const to = parts[i + 3].split('&').map(node).filter(Boolean);
      for (const a of from) for (const b of to) edges.push({ from: a.id, to: b.id, label, arrow: !/---$/.test(op), dashed: op.includes('.') });
    }
  }
  if (!nodes.size) return { elements: [], error: 'No shapes found. Start with a line like "flowchart TD", then "A --> B".' };
  const els = new Map();
  for (const n of nodes.values()) els.set(n.id, create(n.type, { x, y, ...size(n.type), text: n.text }));
  const conns = edges.map(e => create('connector', { from: { id: els.get(e.from).id }, to: { id: els.get(e.to).id }, route: 'elbow', endHead: e.arrow ? 'arrow' : 'none', startHead: 'none', label: e.label || undefined, dashed: e.dashed || undefined }));
  const all = [...els.values(), ...conns];
  const pos = layoutFlow(all, { dir, origin: { x, y } });
  return { elements: all.map(e => (pos.has(e.id) ? { ...e, ...pos.get(e.id) } : e)), error: null };
}

/// draw.io styles -> our stencil type.
function typeFromStyle(style, value) {
  const s = String(style || '');
  const has = k => new RegExp(`(^|;)${k}(=|;|$)`).test(s) || s.includes(`shape=${k}`) || s.includes(k);
  if (s.includes('swimlane')) return 'groupBox';
  if (has('rhombus') || s.includes('flowchart.decision')) return 'decision';
  if (s.includes('flowchart.terminator') || (s.includes('rounded=1') && s.includes('arcSize=50'))) return 'terminator';
  if (s.includes('flowchart.start') || s.includes('ellipse')) return /end|start/i.test(value) ? 'terminator' : 'ellipse';
  if (s.includes('flowchart.document') || s.includes('shape=document')) return 'document';
  if (s.includes('flowchart.multi-document')) return 'multiDoc';
  if (s.includes('flowchart.predefined_process') || s.includes('shape=process')) return 'subprocess';
  if (s.includes('flowchart.manual_input')) return 'manualInput';
  if (s.includes('flowchart.manual_operation')) return 'manualOp';
  if (s.includes('flowchart.preparation') || s.includes('shape=hexagon')) return 'preparation';
  if (s.includes('flowchart.delay')) return 'delay';
  if (s.includes('flowchart.database') || s.includes('shape=cylinder') || s.includes('shape=datastore')) return 'database';
  if (s.includes('flowchart.off-page_reference') || s.includes('offPageConnector')) return 'offPage';
  if (s.includes('flowchart.display')) return 'display';
  if (s.includes('flowchart.stored_data')) return 'storage';
  if (s.includes('parallelogram') || s.includes('flowchart.data')) return 'data';
  if (s.includes('shape=cloud')) return 'netCloud';
  if (s.includes('text;')) return 'text';
  return 'process';
}
function decodeHtml(v) {
  return String(v || '').replace(/<br\s*\/?>/gi, '\n').replace(/<[^>]+>/g, '').replace(/&nbsp;/g, ' ').replace(/&amp;/g, '&').replace(/&lt;/g, '<').replace(/&gt;/g, '>').replace(/&quot;/g, '"').replace(/&#39;/g, "'").trim();
}

/// The <mxGraphModel> XML of a draw.io file (plain or compressed). `inflate(bytes)` is supplied by the caller (the browser's
/// DecompressionStream), so this stays testable.
export async function drawioModel(xmlText, inflate) {
  const doc = new DOMParser().parseFromString(xmlText, 'text/xml');
  if (doc.querySelector('parsererror')) throw new Error('This is not a draw.io file');
  let model = doc.querySelector('mxGraphModel');
  if (model) return model;
  const diagram = doc.querySelector('diagram');
  if (!diagram) throw new Error('No diagram found in the file');
  const raw = diagram.textContent.trim();
  if (!raw) throw new Error('The diagram is empty');
  const bytes = Uint8Array.from(atob(raw), c => c.charCodeAt(0));
  const text = decodeURIComponent(await inflate(bytes));
  model = new DOMParser().parseFromString(text, 'text/xml').querySelector('mxGraphModel');
  if (!model) throw new Error('Could not read the diagram');
  return model;
}

/// A draw.io <mxGraphModel> -> elements (first page only), placed at (x, y). Groups/containers keep their children's
/// absolute positions.
export function fromDrawioModel(model, { x = 0, y = 0 } = {}) {
  const cells = [...model.querySelectorAll('mxCell')];
  const geo = c => { const g = c.querySelector('mxGeometry'); return g ? { x: +g.getAttribute('x') || 0, y: +g.getAttribute('y') || 0, w: +g.getAttribute('width') || 0, h: +g.getAttribute('height') || 0 } : null; };
  const parentOf = new Map(cells.map(c => [c.getAttribute('id'), c.getAttribute('parent')]));
  const absolute = new Map();
  const abs = id => {
    if (absolute.has(id)) return absolute.get(id);
    const c = cells.find(k => k.getAttribute('id') === id);
    const g = c && c.getAttribute('vertex') === '1' ? geo(c) : null;
    const p = parentOf.get(id);
    const base = p && p !== '0' && p !== '1' ? abs(p) : { x: 0, y: 0 };
    const r = g ? { x: base.x + g.x, y: base.y + g.y } : base;
    absolute.set(id, r);
    return r;
  };
  const vertices = cells.filter(c => c.getAttribute('vertex') === '1');
  if (!vertices.length) return { elements: [], error: 'The draw.io file has no shapes.' };
  const minX = Math.min(...vertices.map(c => abs(c.getAttribute('id')).x)), minY = Math.min(...vertices.map(c => abs(c.getAttribute('id')).y));
  const map = new Map(), out = [];
  for (const c of vertices) {
    const g = geo(c); if (!g) continue;
    const p = abs(c.getAttribute('id'));
    const style = c.getAttribute('style') || '', text = decodeHtml(c.getAttribute('value'));
    const type = typeFromStyle(style, text);
    const fill = style.match(/fillColor=(#[0-9a-fA-F]{6})/)?.[1], stroke = style.match(/strokeColor=(#[0-9a-fA-F]{6})/)?.[1];
    const common = { x: Math.round(x + p.x - minX), y: Math.round(y + p.y - minY), w: Math.max(20, g.w), h: Math.max(20, g.h) };
    const el = type === 'text'
      ? create('text', { ...common, text, size: 16, font: 'ipop' })
      : create(type, { ...common, text, fill: fill && fill.toLowerCase() !== '#ffffff' ? fill : undefined, stroke, dashed: /dashed=1/.test(style) || undefined, ...(type === 'groupBox' ? { title: text, text: '' } : {}) });
    map.set(c.getAttribute('id'), el.id);
    out.push(el);
  }
  for (const c of cells.filter(k => k.getAttribute('edge') === '1')) {
    const a = map.get(c.getAttribute('source')), b = map.get(c.getAttribute('target'));
    if (!a || !b) continue;
    const style = c.getAttribute('style') || '';
    let label = decodeHtml(c.getAttribute('value'));
    if (!label) { const child = cells.find(k => k.getAttribute('parent') === c.getAttribute('id') && k.getAttribute('value')); label = decodeHtml(child?.getAttribute('value')); }
    out.push(create('connector', {
      from: { id: a }, to: { id: b },
      route: /edgeStyle=(orthogonal|elbow)/.test(style) ? 'elbow' : /curved=1/.test(style) ? 'curved' : 'elbow',
      endHead: /endArrow=none/.test(style) ? 'none' : 'arrow', startHead: /startArrow=(classic|block|open)/.test(style) ? 'arrow' : 'none',
      dashed: /dashed=1/.test(style) || undefined, label: label || undefined,
    }));
  }
  return { elements: out, error: null };
}
