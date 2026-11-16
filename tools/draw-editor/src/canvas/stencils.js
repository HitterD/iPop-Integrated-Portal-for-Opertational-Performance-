// Diagram stencils (round 3: draw.io + Visio). Every shape here is an ordinary box element ({x,y,w,h,text,stroke,fill}),
// so selecting, resizing, colours, text and connectors work the same as for the basic shapes. Pure: outlines are SVG path
// strings, and the meaning of each shape (for the panel's tooltips and the flowchart hints) lives here too.

/// [type, label, category, default w, h, what it means]
export const STENCILS = [
  // Flowchart (the standard set)
  ['terminator', 'Start / End', 'Flowchart', 160, 56, 'Where the flow begins or ends. Every flowchart has one Start and at least one End.'],
  ['process', 'Process', 'Flowchart', 180, 72, 'One step someone does: "Check stock", "Send invoice".'],
  ['decision', 'Decision', 'Flowchart', 180, 110, 'A yes/no question. Give it two lines out: Yes and No.'],
  ['data', 'Data (in / out)', 'Flowchart', 180, 72, 'Information coming in or going out: a form, a report.'],
  ['document', 'Document', 'Flowchart', 180, 80, 'A paper or file that is made or used: PR, PO, invoice.'],
  ['subprocess', 'Subprocess', 'Flowchart', 180, 72, 'A step that has its own flowchart somewhere else.'],
  ['manualInput', 'Manual input', 'Flowchart', 180, 72, 'Something typed or entered by hand.'],
  ['preparation', 'Preparation', 'Flowchart', 180, 72, 'Getting ready before the main step: set up, check tools.'],
  ['delay', 'Wait / Delay', 'Flowchart', 160, 72, 'Waiting time: approval pending, goods in transit.'],
  ['database', 'Database', 'Flowchart', 130, 100, 'Where information is stored: SAP, Excel, a system.'],
  ['offPage', 'Off-page link', 'Flowchart', 80, 80, 'The flow carries on in another diagram or page.'],
  ['display', 'Display', 'Flowchart', 180, 72, 'Information shown on a screen.'],
  ['storage', 'Storage', 'Flowchart', 130, 100, 'Goods or files kept somewhere.'],
  ['manualOp', 'Manual operation', 'Flowchart', 180, 72, 'A step done by hand, not by a system.'],
  ['multiDoc', 'Several documents', 'Flowchart', 180, 90, 'More than one document: copies, a set of forms.'],
  // BPMN
  ['bpmnStart', 'Start event', 'BPMN', 56, 56, 'The process starts here.'],
  ['bpmnEnd', 'End event', 'BPMN', 56, 56, 'The process ends here.'],
  ['bpmnTimer', 'Timer event', 'BPMN', 56, 56, 'Something happens at a time or after a wait.'],
  ['bpmnMessage', 'Message event', 'BPMN', 56, 56, 'A message or email arrives or is sent.'],
  ['bpmnTask', 'Task', 'BPMN', 180, 80, 'Work done by a person or a system.'],
  ['bpmnGateway', 'Gateway (exclusive)', 'BPMN', 70, 70, 'The flow takes one of the ways out.'],
  ['bpmnParallel', 'Gateway (parallel)', 'BPMN', 70, 70, 'The flow splits into ways that run at the same time, or joins them.'],
  // ERD
  ['erTable', 'Table', 'ERD', 200, 150, 'A database table. First line is the name, then one field per line. Join tables with crow\'s foot lines.'],
  ['erEntity', 'Entity', 'ERD', 160, 64, 'A thing the system stores: Customer, Order, Item.'],
  // Network
  ['netPC', 'PC', 'Network', 90, 80, 'A desktop computer.'],
  ['netLaptop', 'Laptop', 'Network', 100, 70, 'A laptop.'],
  ['netServer', 'Server', 'Network', 80, 100, 'A server or NAS.'],
  ['netSwitch', 'Switch', 'Network', 140, 48, 'A network switch.'],
  ['netRouter', 'Router', 'Network', 100, 70, 'A router.'],
  ['netFirewall', 'Firewall', 'Network', 100, 80, 'A firewall.'],
  ['netPrinter', 'Printer', 'Network', 100, 80, 'A printer or scanner.'],
  ['netCloud', 'Internet / Cloud', 'Network', 150, 90, 'The internet or a cloud service.'],
  ['netAP', 'Wi-Fi access point', 'Network', 90, 70, 'A wireless access point.'],
  ['netPhone', 'Phone', 'Network', 60, 90, 'A phone or handheld scanner.'],
  // Timeline
  ['milestone', 'Milestone', 'Timeline', 44, 44, 'An important date on a timeline.'],
  ['phase', 'Phase bar', 'Timeline', 260, 40, 'A period of work: from one date to another.'],
  // Value stream
  ['vsProcess', 'Process box', 'Value stream', 180, 110, 'A production step. Put cycle time and people under the title.'],
  ['vsInventory', 'Inventory', 'Value stream', 80, 72, 'Stock waiting between steps.'],
  ['vsTruck', 'Shipment', 'Value stream', 110, 70, 'Goods delivered by truck.'],
  ['vsSupplier', 'Supplier / Customer', 'Value stream', 160, 100, 'A supplier or a customer.'],
  // Containers
  ['swimlane', 'Swimlane', 'Containers', 900, 420, 'Rows for departments or people. Shapes inside move with their lane.'],
  ['groupBox', 'Group box', 'Containers', 400, 260, 'A box with a title around related shapes.'],
];

export const STENCIL_TYPES = STENCILS.map(s => s[0]);
export const STENCIL = Object.fromEntries(STENCILS.map(([type, label, cat, w, h, tip]) => [type, { type, label, cat, w, h, tip }]));
export const STENCIL_CATS = ['Flowchart', 'Containers', 'BPMN', 'ERD', 'Network', 'Timeline', 'Value stream'];
/// Shapes that make something a flowchart (the guide, hints, step numbers and Check only show up when one is on the board).
export const FLOW_TYPES = new Set(['terminator', 'process', 'decision', 'data', 'document', 'subprocess', 'manualInput', 'preparation', 'delay', 'database', 'offPage', 'display', 'storage', 'manualOp', 'multiDoc', 'bpmnTask', 'bpmnGateway', 'bpmnParallel', 'bpmnStart', 'bpmnEnd']);
/// Shapes that get a step number (things someone does).
export const STEP_TYPES = new Set(['process', 'subprocess', 'manualInput', 'manualOp', 'data', 'document', 'multiDoc', 'preparation', 'bpmnTask', 'vsProcess']);
export const DECISION_TYPES = new Set(['decision', 'bpmnGateway']);
export const isTerminator = el => el?.type === 'terminator' || el?.type === 'bpmnStart' || el?.type === 'bpmnEnd';
export const isEnd = el => el?.type === 'bpmnEnd' || (el?.type === 'terminator' && /\b(end|finish|done|stop|selesai)\b/i.test(el.text || ''));

const f = n => +n.toFixed(2);

/// The outline of a stencil shape: { d, extra } where `d` is the filled outline and `extra` are lines drawn on top
/// (no fill). Returns null for a type that is not a stencil.
export function stencilPath(type, x, y, w, h) {
  const cx = x + w / 2, cy = y + h / 2, r = (...a) => a.map(f).join(' ');
  switch (type) {
    case 'terminator': { const rr = Math.min(h / 2, w / 2); return { d: `M${r(x + rr, y)}H${f(x + w - rr)}A${r(rr, rr)} 0 0 1 ${r(x + w - rr, y + h)}H${f(x + rr)}A${r(rr, rr)} 0 0 1 ${r(x + rr, y)}Z` }; }
    case 'process': return { d: `M${r(x, y)}H${f(x + w)}V${f(y + h)}H${f(x)}Z`, rx: Math.min(8, w / 8, h / 8) };
    case 'decision': case 'bpmnGateway': case 'bpmnParallel': {
      const d = `M${r(cx, y)}L${r(x + w, cy)}L${r(cx, y + h)}L${r(x, cy)}Z`;
      if (type === 'bpmnGateway') { const k = Math.min(w, h) * 0.16; return { d, extra: `M${r(cx - k, cy - k)}L${r(cx + k, cy + k)}M${r(cx + k, cy - k)}L${r(cx - k, cy + k)}`, extraWidth: 3 }; }
      if (type === 'bpmnParallel') { const k = Math.min(w, h) * 0.2; return { d, extra: `M${r(cx - k, cy)}H${f(cx + k)}M${r(cx, cy - k)}V${f(cy + k)}`, extraWidth: 3 }; }
      return { d };
    }
    case 'data': { const s = Math.min(w * 0.15, 24); return { d: `M${r(x + s, y)}H${f(x + w)}L${r(x + w - s, y + h)}H${f(x)}Z` }; }
    case 'document': return { d: `M${r(x, y)}H${f(x + w)}V${f(y + h * 0.85)}C${r(x + w * 0.75, y + h * 0.7, x + w * 0.25, y + h * 1.05, x, y + h * 0.88)}Z` };
    case 'multiDoc': {
      const o = 7, ww = w - o * 2, hh = h - o * 2;
      const page = (px, py) => `M${r(px, py)}H${f(px + ww)}V${f(py + hh * 0.85)}C${r(px + ww * 0.75, py + hh * 0.7, px + ww * 0.25, py + hh * 1.05, px, py + hh * 0.88)}Z`;
      return { d: page(x, y + o * 2), behind: [page(x + o * 2, y), page(x + o, y + o)] };
    }
    case 'subprocess': { const k = Math.min(14, w * 0.08); return { d: `M${r(x, y)}H${f(x + w)}V${f(y + h)}H${f(x)}Z`, extra: `M${r(x + k, y)}V${f(y + h)}M${r(x + w - k, y)}V${f(y + h)}` }; }
    case 'manualInput': return { d: `M${r(x, y + h * 0.25)}L${r(x + w, y)}V${f(y + h)}H${f(x)}Z` };
    case 'manualOp': { const s = w * 0.12; return { d: `M${r(x, y)}H${f(x + w)}L${r(x + w - s, y + h)}H${f(x + s)}Z` }; }
    case 'preparation': { const s = Math.min(w * 0.15, 26); return { d: `M${r(x + s, y)}H${f(x + w - s)}L${r(x + w, cy)}L${r(x + w - s, y + h)}H${f(x + s)}L${r(x, cy)}Z` }; }
    case 'delay': { const rr = Math.min(h / 2, w / 2); return { d: `M${r(x, y)}H${f(x + w - rr)}A${r(rr, h / 2)} 0 0 1 ${r(x + w - rr, y + h)}H${f(x)}Z` }; }
    case 'database': { const ry = Math.min(h * 0.14, 14); return { d: `M${r(x, y + ry)}A${r(w / 2, ry)} 0 0 1 ${r(x + w, y + ry)}V${f(y + h - ry)}A${r(w / 2, ry)} 0 0 1 ${r(x, y + h - ry)}Z`, extra: `M${r(x, y + ry)}A${r(w / 2, ry)} 0 0 0 ${r(x + w, y + ry)}` }; }
    case 'offPage': return { d: `M${r(x, y)}H${f(x + w)}V${f(y + h * 0.62)}L${r(cx, y + h)}L${r(x, y + h * 0.62)}Z` };
    case 'display': { const s = Math.min(w * 0.14, 24); return { d: `M${r(x + s, y)}H${f(x + w - s)}C${r(x + w + s * 0.2, y, x + w + s * 0.2, y + h, x + w - s, y + h)}H${f(x + s)}L${r(x, cy)}Z` }; }
    case 'storage': { const s = Math.min(w * 0.1, 14); return { d: `M${r(x + s, y)}H${f(x + w)}C${r(x + w - s * 1.4, y + h * 0.3, x + w - s * 1.4, y + h * 0.7, x + w, y + h)}H${f(x + s)}C${r(x - s * 0.4, y + h * 0.7, x - s * 0.4, y + h * 0.3, x + s, y)}Z` }; }
    case 'bpmnStart': case 'bpmnEnd': case 'bpmnTimer': case 'bpmnMessage': {
      const rx = w / 2, ry = h / 2, d = `M${r(x, cy)}A${r(rx, ry)} 0 1 1 ${r(x + w, cy)}A${r(rx, ry)} 0 1 1 ${r(x, cy)}Z`;
      if (type === 'bpmnEnd') return { d, strokeWidth: 4 };
      if (type === 'bpmnTimer') return { d, extra: `M${r(x + w * 0.14, cy)}A${r(rx * 0.72, ry * 0.72)} 0 1 1 ${r(x + w * 0.86, cy)}A${r(rx * 0.72, ry * 0.72)} 0 1 1 ${r(x + w * 0.14, cy)}M${r(cx, cy - ry * 0.45)}V${f(cy)}L${r(cx + rx * 0.3, cy + ry * 0.2)}` };
      if (type === 'bpmnMessage') return { d, extra: `M${r(cx - rx * 0.45, cy - ry * 0.3)}H${f(cx + rx * 0.45)}V${f(cy + ry * 0.3)}H${f(cx - rx * 0.45)}ZM${r(cx - rx * 0.45, cy - ry * 0.3)}L${r(cx, cy + ry * 0.05)}L${r(cx + rx * 0.45, cy - ry * 0.3)}` };
      return { d };
    }
    case 'bpmnTask': return { d: `M${r(x, y)}H${f(x + w)}V${f(y + h)}H${f(x)}Z`, rx: Math.min(14, w / 6, h / 4) };
    case 'erTable': { const head = Math.min(32, h * 0.3); return { d: `M${r(x, y)}H${f(x + w)}V${f(y + h)}H${f(x)}Z`, rx: 6, extra: `M${r(x, y + head)}H${f(x + w)}`, head }; }
    case 'erEntity': return { d: `M${r(x, y)}H${f(x + w)}V${f(y + h)}H${f(x)}Z`, rx: 2, extra: `M${r(x + 5, y + 5)}H${f(x + w - 5)}V${f(y + h - 5)}H${f(x + 5)}Z` };
    case 'netPC': { const sh = h * 0.72; return { d: `M${r(x, y)}H${f(x + w)}V${f(y + sh)}H${f(x)}Z`, rx: 5, extra: `M${r(cx, y + sh)}V${f(y + h - 4)}M${r(cx - w * 0.22, y + h - 2)}H${f(cx + w * 0.22)}`, icon: true }; }
    case 'netLaptop': return { d: `M${r(x + w * 0.12, y)}H${f(x + w * 0.88)}V${f(y + h * 0.78)}H${f(x + w * 0.12)}Z`, rx: 4, extra: `M${r(x, y + h * 0.84)}H${f(x + w)}L${r(x + w * 0.93, y + h)}H${f(x + w * 0.07)}Z`, icon: true };
    case 'netServer': return { d: `M${r(x, y)}H${f(x + w)}V${f(y + h)}H${f(x)}Z`, rx: 6, extra: [1, 2, 3].map(i => `M${r(x + 8, y + i * h / 4)}H${f(x + w - 8)}`).join('') + `M${r(x + w - 16, y + h / 8)}h4`, icon: true };
    case 'netSwitch': return { d: `M${r(x, y)}H${f(x + w)}V${f(y + h)}H${f(x)}Z`, rx: 6, extra: Array.from({ length: 6 }, (_, i) => `M${r(x + w * 0.12 + i * w * 0.13, cy - 5)}h${f(w * 0.08)}v10h${f(-w * 0.08)}Z`).join(''), icon: true };
    case 'netRouter': return { d: `M${r(x, cy)}A${r(w / 2, h / 2)} 0 1 1 ${r(x + w, cy)}A${r(w / 2, h / 2)} 0 1 1 ${r(x, cy)}Z`, extra: `M${r(cx - w * 0.2, cy)}H${f(cx + w * 0.2)}M${r(cx, cy - h * 0.25)}V${f(cy + h * 0.25)}M${r(cx + w * 0.12, cy - 6)}L${r(cx + w * 0.2, cy)}L${r(cx + w * 0.12, cy + 6)}`, icon: true };
    case 'netFirewall': return { d: `M${r(x, y)}H${f(x + w)}V${f(y + h)}H${f(x)}Z`, rx: 3, extra: `M${r(x, y + h / 3)}H${f(x + w)}M${r(x, y + 2 * h / 3)}H${f(x + w)}M${r(cx, y)}V${f(y + h / 3)}M${r(x + w / 4, y + h / 3)}V${f(y + 2 * h / 3)}M${r(x + 3 * w / 4, y + h / 3)}V${f(y + 2 * h / 3)}M${r(cx, y + 2 * h / 3)}V${f(y + h)}`, icon: true };
    case 'netPrinter': return { d: `M${r(x, y + h * 0.3)}H${f(x + w)}V${f(y + h * 0.78)}H${f(x)}Z`, rx: 6, extra: `M${r(x + w * 0.22, y + h * 0.3)}V${f(y)}H${f(x + w * 0.78)}V${f(y + h * 0.3)}M${r(x + w * 0.22, y + h * 0.66)}V${f(y + h)}H${f(x + w * 0.78)}V${f(y + h * 0.66)}`, icon: true };
    case 'netCloud': { const p = (fx, fy) => r(x + w * fx, y + h * fy); return { d: `M${p(0.25, 0.85)}C${p(0.05, 0.85)} ${p(0.02, 0.55)} ${p(0.18, 0.48)}C${p(0.12, 0.2)} ${p(0.42, 0.08)} ${p(0.52, 0.25)}C${p(0.62, 0.02)} ${p(0.92, 0.12)} ${p(0.85, 0.42)}C${p(1.02, 0.5)} ${p(0.98, 0.85)} ${p(0.75, 0.85)}Z` }; }
    case 'netAP': return { d: `M${r(x + w * 0.1, y + h * 0.55)}H${f(x + w * 0.9)}V${f(y + h)}H${f(x + w * 0.1)}Z`, rx: 8, extra: `M${r(x + w * 0.3, y + h * 0.36)}A${r(w * 0.24, w * 0.24)} 0 0 1 ${r(x + w * 0.7, y + h * 0.36)}M${r(x + w * 0.18, y + h * 0.2)}A${r(w * 0.38, w * 0.38)} 0 0 1 ${r(x + w * 0.82, y + h * 0.2)}`, icon: true };
    case 'netPhone': return { d: `M${r(x, y)}H${f(x + w)}V${f(y + h)}H${f(x)}Z`, rx: 10, extra: `M${r(cx - 6, y + h - 9)}H${f(cx + 6)}M${r(x + 6, y + 10)}H${f(x + w - 6)}V${f(y + h - 16)}H${f(x + 6)}Z`, icon: true };
    case 'milestone': return { d: `M${r(cx, y)}L${r(x + w, cy)}L${r(cx, y + h)}L${r(x, cy)}Z`, fill: '#FF9500' };
    case 'phase': return { d: `M${r(x, y)}H${f(x + w - h / 2)}L${r(x + w, cy)}L${r(x + w - h / 2, y + h)}H${f(x)}Z`, rx: 0 };
    case 'vsProcess': return { d: `M${r(x, y)}H${f(x + w)}V${f(y + h)}H${f(x)}Z`, rx: 2, extra: `M${r(x, y + Math.min(30, h * 0.32))}H${f(x + w)}`, head: Math.min(30, h * 0.32) };
    case 'vsInventory': return { d: `M${r(cx, y)}L${r(x + w, y + h)}H${f(x)}Z`, fill: '#FFE3B3', extra: `M${r(cx - 3, y + h * 0.5)}V${f(y + h * 0.82)}M${r(cx + 3, y + h * 0.5)}V${f(y + h * 0.82)}M${r(cx - 7, y + h * 0.5)}H${f(cx + 7)}M${r(cx - 7, y + h * 0.82)}H${f(cx + 7)}` };
    case 'vsTruck': return { d: `M${r(x, y + h * 0.1)}H${f(x + w * 0.62)}V${f(y + h * 0.78)}H${f(x)}Z`, extra: `M${r(x + w * 0.62, y + h * 0.35)}H${f(x + w * 0.85)}L${r(x + w, y + h * 0.55)}V${f(y + h * 0.78)}H${f(x + w * 0.62)}M${r(x + w * 0.2 - 8, y + h * 0.9)}a8 8 0 1 0 16 0a8 8 0 1 0 -16 0M${r(x + w * 0.8 - 8, y + h * 0.9)}a8 8 0 1 0 16 0a8 8 0 1 0 -16 0`, icon: true };
    case 'vsSupplier': return { d: `M${r(x, y + h * 0.3)}L${r(x + w / 3, y)}V${f(y + h * 0.3)}L${r(x + 2 * w / 3, y)}V${f(y + h * 0.3)}L${r(x + w, y)}V${f(y + h)}H${f(x)}Z` };
    case 'swimlane': return { d: `M${r(x, y)}H${f(x + w)}V${f(y + h)}H${f(x)}Z`, rx: 6, container: true };
    case 'groupBox': return { d: `M${r(x, y)}H${f(x + w)}V${f(y + h)}H${f(x)}Z`, rx: 10, container: true, dashed: true };
    default: return null;
  }
}

/// Where text sits inside a stencil shape (the usable box).
export function stencilTextBox(type, x, y, w, h) {
  switch (type) {
    case 'decision': return { x: x + w * 0.22, y: y + h * 0.22, w: w * 0.56, h: h * 0.56 };
    case 'data': case 'manualOp': return { x: x + w * 0.14, y: y + 4, w: w * 0.72, h: h - 8 };
    case 'preparation': case 'display': return { x: x + w * 0.15, y: y + 4, w: w * 0.7, h: h - 8 };
    case 'document': case 'multiDoc': return { x: x + 10, y: y + 6, w: w - 20, h: h * 0.72 };
    case 'subprocess': return { x: x + 18, y: y + 4, w: w - 36, h: h - 8 };
    case 'database': return { x: x + 8, y: y + h * 0.22, w: w - 16, h: h * 0.7 };
    case 'offPage': return { x: x + 4, y: y + 4, w: w - 8, h: h * 0.58 };
    case 'manualInput': return { x: x + 8, y: y + h * 0.28, w: w - 16, h: h * 0.68 };
    case 'erTable': case 'vsProcess': return { x: x + 10, y: y + 4, w: w - 20, h: h - 8 };
    case 'bpmnStart': case 'bpmnEnd': case 'bpmnTimer': case 'bpmnMessage': case 'bpmnGateway': case 'bpmnParallel': case 'milestone':
      return { x: x - 40, y: y + h + 4, w: w + 80, h: 40, below: true };
    case 'vsInventory': case 'vsTruck': case 'netPC': case 'netLaptop': case 'netServer': case 'netSwitch': case 'netRouter': case 'netFirewall': case 'netPrinter': case 'netAP': case 'netPhone':
      return { x: x - 40, y: y + h + 4, w: w + 80, h: 40, below: true };
    case 'netCloud': return { x: x + w * 0.2, y: y + h * 0.35, w: w * 0.6, h: h * 0.45 };
    case 'phase': return { x: x + 8, y: y + 2, w: w - h / 2 - 12, h: h - 4 };
    case 'swimlane': case 'groupBox': return { x: x + 10, y: y + 6, w: w - 20, h: 26, title: true };
    default: return { x: x + 10, y: y + 6, w: Math.max(0, w - 20), h: Math.max(0, h - 12) };
  }
}

// ── Swimlanes ────────────────────────────────────────────────────────────────
// A swimlane is one element with lanes: [{ id, name, size }] (sizes along the split axis), horizontal (rows) or vertical
// (columns). The header strip holds the lane names. Shapes belong to the lane their centre sits in.
export const LANE_HEAD = 120;
export function lanesOf(el) {
  const lanes = Array.isArray(el.lanes) && el.lanes.length ? el.lanes : [{ id: 'l1', name: 'Lane 1', size: 1 }];
  const vertical = el.orient === 'vertical';
  const total = lanes.reduce((s, l) => s + (l.size || 1), 0);
  const along = vertical ? el.w : el.h;
  const head = vertical ? Math.min(LANE_HEAD / 2.4, el.h * 0.2) : Math.min(LANE_HEAD, el.w * 0.25);
  let at = 0;
  return lanes.map(l => {
    const len = along * (l.size || 1) / total;
    const box = vertical ? { x: el.x + at, y: el.y, w: len, h: el.h } : { x: el.x, y: el.y + at, w: el.w, h: len };
    const header = vertical ? { x: box.x, y: box.y, w: box.w, h: head } : { x: box.x, y: box.y, w: head, h: box.h };
    at += len;
    return { ...l, box, header, vertical };
  });
}
/// The lane (index) a point falls in, or -1.
export function laneAt(el, px, py) {
  return lanesOf(el).findIndex(l => px >= l.box.x && px <= l.box.x + l.box.w && py >= l.box.y && py <= l.box.y + l.box.h);
}
