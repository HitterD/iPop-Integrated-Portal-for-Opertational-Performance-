// Starter layouts for the whiteboard (grill Q9). Each takes the top-left corner to place it at and returns new elements:
// frames, stickies, shapes and connectors attached to the shapes they join. Nothing is special once it is on the board.
import { create } from './model.js';
import { STENCIL } from './stencils.js';
import { layoutFlow } from './flow.js';

const sticky = (x, y, text = '', color = 'yellow', w = 200, h = 150) => create('sticky', { x, y, w, h, text, color });
const frame = (x, y, w, h, name) => create('frame', { x, y, w, h, name });
const box = (type, x, y, w, h, text = '', more = {}) => create(type, { x, y, w, h, text, ...more });
const link = (a, b, more = {}) => create('connector', { from: { id: a.id }, to: { id: b.id }, route: 'straight', endHead: 'arrow', startHead: 'none', ...more });
const text = (x, y, t, size = 22) => create('text', { x, y, w: Math.max(40, t.length * size * 0.55), h: size * 1.3, text: t, size, font: 'ipop' });
// flowchart helpers: a stencil shape at its default size, and an elbow line between two shapes (sides chosen by the route)
const st = (type, x, y, t = '', more = {}) => create(type, { x, y, w: STENCIL[type].w, h: STENCIL[type].h, text: t, ...more });
const flow = (a, b, label, more = {}) => create('connector', { from: { id: a.id }, to: { id: b.id }, route: 'elbow', endHead: 'arrow', startHead: 'none', label: label || undefined, ...more });
/// Lays a flow out top to bottom at (x, y) so the template is tidy whatever the shape sizes.
const laid = (els, x, y, dir = 'TB') => { const pos = layoutFlow(els, { dir, origin: { x, y } }); return els.map(e => (pos.has(e.id) ? { ...e, ...pos.get(e.id) } : e)); };

/// [key, name, what it is for, category, guided]
export const TEMPLATE_INFO = [
  ['basicflow', 'Basic flowchart', 'Start → step → decision → end', 'Flowchart & process', true],
  ['approval', 'Approval flow', 'Request → manager → Finance → approved or returned', 'Flowchart & process', true],
  ['swimflow', 'Cross-department flow', 'Warehouse, Purchasing, Finance in lanes', 'Flowchart & process', true],
  ['sop', 'Numbered SOP', 'Steps with PIC and duration', 'Flowchart & process', false],
  ['bpmn', 'BPMN process', 'Events, tasks and gateways', 'Flowchart & process', false],
  ['flowchart', 'Flowchart (simple)', 'Steps and decisions', 'Flowchart & process', false],
  ['fishbone', 'Fishbone', 'Find the root cause', 'Flowchart & process', false],
  ['erd', 'ERD database', 'Tables, fields and relations', 'Technical', false],
  ['network', 'Office network', 'Internet, firewall, switch, PCs, printer', 'Technical', false],
  ['valuestream', 'Value stream', 'Production steps, stock and lead time', 'Technical', false],
  ['retro', 'Retro', 'Went well, improve, action', 'Brainstorm', false],
  ['brainstorm', 'Brainstorm', 'Ideas around a topic', 'Brainstorm', false],
  ['mindmap', 'Mind map', 'Branch out from one idea', 'Brainstorm', false],
  ['swot', 'SWOT', 'Strengths to threats', 'Brainstorm', false],
  ['journey', 'User journey', 'Steps a user takes', 'Brainstorm', false],
  ['projecttimeline', 'Project timeline', 'Phases and milestones by month', 'Planning', false],
  ['timeline', 'Timeline', 'Roadmap by month', 'Planning', false],
  ['kanban', 'Kanban', 'To do, doing, done', 'Planning', false],
  ['agenda', 'Meeting agenda', 'Topics, time, owners', 'Planning', false],
  ['orgchart', 'Org chart', 'Who reports to whom', 'Planning', false],
];

export const CANVAS_TEMPLATES = {
  // ── Flowchart & process (round 3) ──
  basicflow({ x, y }) {
    const s = st('terminator', 0, 0, 'Start'), a = st('process', 0, 0, 'Receive request'), d = st('decision', 0, 0, 'Complete?');
    const ok = st('process', 0, 0, 'Process it'), fix = st('process', 0, 0, 'Ask for details'), e = st('terminator', 0, 0, 'End');
    return laid([s, a, d, ok, fix, e, flow(s, a), flow(a, d), flow(d, ok, 'Yes'), flow(d, fix, 'No'), flow(fix, a), flow(ok, e)], x, y);
  },
  approval({ x, y }) {
    const s = st('terminator', 0, 0, 'Start'), pr = st('document', 0, 0, 'Raise PR'), mgr = st('decision', 0, 0, 'Manager approves?');
    const fin = st('decision', 0, 0, 'Within budget?'), po = st('process', 0, 0, 'Make PO'), send = st('process', 0, 0, 'Send PO to vendor');
    const back = st('process', 0, 0, 'Return to requester'), e = st('terminator', 0, 0, 'End');
    return laid([s, pr, mgr, fin, po, send, back, e, flow(s, pr), flow(pr, mgr), flow(mgr, fin, 'Yes'), flow(mgr, back, 'No'), flow(fin, po, 'Yes'), flow(fin, back, 'No'), flow(po, send), flow(send, e), flow(back, e)], x, y);
  },
  swimflow({ x, y }) {
    const lane = create('swimlane', { x, y, w: 1360, h: 540, title: 'Buying spare parts', lanes: [{ id: 'w', name: 'Warehouse', size: 1 }, { id: 'p', name: 'Purchasing', size: 1 }, { id: 'f', name: 'Finance', size: 1 }] });
    const row = i => y + i * 180 + 54;
    const s = st('terminator', x + 160, row(0) + 8, 'Stock low'), pr = st('document', x + 380, row(0), 'Raise PR');
    const po = st('process', x + 620, row(1) + 4, 'Make PO'), ok = st('decision', x + 620, row(2) - 16, 'Budget OK?');
    const send = st('process', x + 880, row(1) + 4, 'Send to vendor'), grn = st('process', x + 1120, row(0) + 4, 'Receive goods (GRN)'), pay = st('process', x + 1120, row(2) + 4, 'Pay invoice');
    return [lane, s, pr, po, ok, send, grn, pay, flow(s, pr), flow(pr, po), flow(po, ok), flow(ok, send, 'Yes'), flow(ok, pr, 'No'), flow(send, grn), flow(grn, pay)];
  },
  sop({ x, y }) {
    const steps = [['Receive the request', 'Admin', '10 min'], ['Check stock in SAP', 'Warehouse', '15 min'], ['Pick the items', 'Warehouse', '30 min'], ['Pack and label', 'Warehouse', '20 min'], ['Print the delivery note', 'Admin', '5 min'], ['Hand over to driver', 'Security', '10 min']];
    const s = st('terminator', 0, 0, 'Start'), e = st('terminator', 0, 0, 'End');
    const boxes = steps.map(([t, pic, dur]) => st('process', 0, 0, t, { w: 240, h: 92, data: [{ name: 'PIC', value: pic }, { name: 'Duration', value: dur }] }));
    const lines = [flow(s, boxes[0]), ...boxes.slice(1).map((b, i) => flow(boxes[i], b)), flow(boxes.at(-1), e)];
    return laid([s, ...boxes, e, ...lines], x, y);
  },
  bpmn({ x, y }) {
    const pool = create('swimlane', { x, y, w: 1300, h: 380, title: 'Leave request', lanes: [{ id: 'e', name: 'Employee', size: 1 }, { id: 'm', name: 'Manager', size: 1 }] });
    const s = st('bpmnStart', x + 160, y + 62, 'Need leave'), req = st('bpmnTask', x + 270, y + 50, 'Submit request');
    const rev = st('bpmnTask', x + 480, y + 240, 'Review request'), gw = st('bpmnGateway', x + 720, y + 245, 'Approved?');
    const ok = st('bpmnTask', x + 880, y + 50, 'Plan handover'), no = st('bpmnTask', x + 880, y + 240, 'Tell the reason'), e = st('bpmnEnd', x + 1140, y + 62, 'Done');
    return [pool, s, req, rev, gw, ok, no, e, flow(s, req), flow(req, rev), flow(rev, gw), flow(gw, ok, 'Yes'), flow(gw, no, 'No'), flow(ok, e), flow(no, req, '', { dashed: true })];
  },
  erd({ x, y }) {
    const cust = st('erTable', x, y, 'Customer\nid  int  PK\nname  text\nphone  text\ncity  text', { h: 150 });
    const order = st('erTable', x + 360, y, 'SalesOrder\nid  int  PK\ncustomer_id  int  FK\ndate  date\ntotal  money', { h: 150 });
    const line = st('erTable', x + 720, y, 'OrderLine\nid  int  PK\norder_id  int  FK\nitem_id  int  FK\nqty  int', { h: 150 });
    const item = st('erTable', x + 720, y + 260, 'Item\nid  int  PK\ncode  text\nname  text\nprice  money', { h: 150 });
    const rel = (a, b) => create('connector', { from: { id: a.id }, to: { id: b.id }, route: 'elbow', startHead: 'one', endHead: 'crow' });
    return [cust, order, line, item, rel(cust, order), rel(order, line), rel(item, line)];
  },
  network({ x, y }) {
    const net = st('netCloud', x + 380, y, 'Internet'), fw = st('netFirewall', x + 405, y + 150, 'Firewall'), rt = st('netRouter', x + 405, y + 290, 'Router');
    const sw = st('netSwitch', x + 385, y + 420, 'Core switch');
    // devices in one row under the switch, each wired from the switch's bottom to its top
    const row = [['netServer', 'NAS'], ['netAP', 'Wi-Fi'], ['netPC', 'PC 1'], ['netPC', 'PC 2'], ['netPC', 'PC 3'], ['netPrinter', 'Printer']];
    const [srv, ap, ...rest] = row.map(([type, name], i) => st(type, x + i * 160, y + 600, name));
    const pcs = rest.slice(0, 3), pr = rest[3];
    const wire = (a, b) => create('connector', { from: { id: a.id, ...(a === sw ? { side: 'b' } : {}) }, to: { id: b.id, side: 't' }, route: 'elbow', endHead: 'none', startHead: 'none' });
    return [net, fw, rt, sw, srv, ap, ...pcs, pr, wire(net, fw), wire(fw, rt), wire(rt, sw), ...[srv, ap, ...pcs, pr].map(n => wire(sw, n))];
  },
  valuestream({ x, y }) {
    const sup = st('vsSupplier', x, y, 'Steel supplier'), cus = st('vsSupplier', x + 1240, y, 'Customer');
    const steps = [['Cutting', '30 s', '1'], ['Welding', '75 s', '2'], ['Painting', '50 s', '1'], ['Packing', '20 s', '1']];
    const out = [sup, cus], boxes = [];
    steps.forEach(([n, ct, ppl], i) => {
      const b = st('vsProcess', x + 120 + i * 280, y + 220, n, { data: [{ name: 'C/T', value: ct }, { name: 'People', value: ppl }] });
      boxes.push(b); out.push(b);
      if (i < steps.length - 1) out.push(st('vsInventory', x + 320 + i * 280, y + 240, `${[300, 120, 80][i]} pcs`));
    });
    out.push(st('vsTruck', x + 220, y + 20, 'Weekly'), st('vsTruck', x + 1080, y + 20, 'Daily'));
    out.push(...boxes.slice(1).map((b, i) => create('connector', { from: { id: boxes[i].id }, to: { id: b.id }, route: 'straight', endHead: 'filled', startHead: 'none', dashed: true })));
    return out;
  },
  projecttimeline({ x, y }) {
    const months = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun'];
    const out = [create('path', { x, y: y + 140, points: [[0, 0, 0.5], [months.length * 220, 0, 0.5]], width: 3, straight: true })];
    months.forEach((m, i) => out.push(text(x + 10 + i * 220, y + 156, m, 18)));
    out.push(st('phase', x, y, 'Plan', { w: 420, fill: '#CFE6FF' }), st('phase', x + 400, y + 50, 'Build', { w: 480, fill: '#C9F2C7' }), st('phase', x + 860, y, 'Test and go live', { w: 440, fill: '#FFDDB8' }));
    out.push(st('milestone', x + 398, y + 118, 'Design OK'), st('milestone', x + 858, y + 118, 'Pilot'), st('milestone', x + 1278, y + 118, 'Go live'));
    return out;
  },
  retro({ x, y }) {
    const cols = [['Went well', 'green'], ['To improve', 'pink'], ['Action items', 'blue']];
    const out = [];
    cols.forEach(([name, color], i) => {
      const fx = x + i * 520;
      out.push(frame(fx, y, 480, 640, name), sticky(fx + 30, y + 40, '', color), sticky(fx + 250, y + 40, '', color), sticky(fx + 30, y + 220, '', color));
    });
    return out;
  },
  flowchart({ x, y }) {
    const start = box('ellipse', x, y + 40, 180, 80, 'Start');
    const step = box('rect', x + 280, y + 40, 200, 80, 'Do the step');
    const ask = box('diamond', x + 580, y + 20, 200, 120, 'OK?');
    const yes = box('rect', x + 880, y + 40, 200, 80, 'Done');
    const no = box('rect', x + 580, y + 240, 200, 80, 'Fix it');
    return [start, step, ask, yes, no, link(start, step), link(step, ask), link(ask, yes, { label: 'Yes' }), link(ask, no, { label: 'No' }), link(no, step, { route: 'elbow' })];
  },
  brainstorm({ x, y }) {
    const topic = box('ellipse', x + 360, y + 220, 260, 120, 'Topic', { fill: '#C9F2C7' });
    const colors = ['yellow', 'blue', 'pink', 'purple', 'orange', 'green'];
    const spots = [[0, 0], [360, -40], [720, 0], [0, 420], [360, 470], [720, 420]];
    return [topic, ...spots.map(([dx, dy], i) => sticky(x + dx, y + dy, 'Idea', colors[i]))];
  },
  mindmap({ x, y }) {
    const root = box('ellipse', x + 360, y + 230, 240, 100, 'Main idea', { fill: '#C9F2C7' });
    const kids = [[0, 40, 'Branch 1'], [0, 420, 'Branch 2'], [760, 40, 'Branch 3'], [760, 420, 'Branch 4']].map(([dx, dy, t]) => box('rect', x + dx, y + dy, 200, 70, t, { fill: '#CFE6FF' }));
    return [root, ...kids, ...kids.map(k => link(root, k, { route: 'curved', endHead: 'none' }))];
  },
  kanban({ x, y }) {
    const out = [];
    ['To do', 'Doing', 'Done'].forEach((name, i) => out.push(frame(x + i * 400, y, 360, 720, name)));
    out.push(sticky(x + 30, y + 40, 'First task', 'yellow', 300, 110), sticky(x + 30, y + 170, 'Second task', 'yellow', 300, 110), sticky(x + 430, y + 40, 'In progress', 'blue', 300, 110));
    return out;
  },
  swot({ x, y }) {
    const q = [['Strengths', 'green'], ['Weaknesses', 'pink'], ['Opportunities', 'blue'], ['Threats', 'orange']];
    return q.flatMap(([name, color], i) => { const fx = x + (i % 2) * 520, fy = y + Math.floor(i / 2) * 440; return [frame(fx, fy, 480, 400, name), sticky(fx + 30, fy + 40, '', color)]; });
  },
  timeline({ x, y }) {
    const months = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun'];
    const line = create('path', { x, y: y + 200, points: [[0, 0, 0.5], [months.length * 260, 0, 0.5]], stroke: null, width: 3, straight: true });
    const out = [line];
    months.forEach((m, i) => {
      out.push(box('ellipse', x + 100 + i * 260, y + 188, 24, 24, '', { fill: '#34A853', stroke: '#34A853' }), text(x + 90 + i * 260, y + 230, m, 20));
      if (i % 2 === 0) out.push(sticky(x + 30 + i * 260, y, 'Milestone', 'yellow', 180, 140));
    });
    return out;
  },
  journey({ x, y }) {
    const steps = ['Notices the need', 'Looks for help', 'Asks IT', 'Gets it fixed', 'Says thanks'];
    const out = [], boxes = [];
    steps.forEach((s, i) => { const b = box('rect', x + i * 280, y, 220, 80, s, { fill: '#E6DAFF' }); boxes.push(b); out.push(b, sticky(x + i * 280, y + 130, 'Feels…', ['green', 'yellow', 'pink', 'blue', 'green'][i], 220, 120)); });
    for (let i = 1; i < boxes.length; i++) out.push(link(boxes[i - 1], boxes[i]));
    return out;
  },
  fishbone({ x, y }) {
    const head = box('rect', x + 1100, y + 240, 220, 100, 'The problem', { fill: '#FFD3E1' });
    const spine = create('path', { x, y: y + 290, points: [[0, 0, 0.5], [1100, 0, 0.5]], width: 3, straight: true });
    const out = [head, spine];
    ['People', 'Process', 'Tools', 'Place'].forEach((c, i) => {
      const bx = x + 120 + (i % 2 ? 0 : 0) + Math.floor(i / 2) * 480, top = i % 2 === 0;
      const label = box('rect', bx, top ? y : y + 500, 180, 60, c, { fill: '#CFE6FF' });
      out.push(label, create('path', { x: bx + 90, y: top ? y + 60 : y + 500, points: top ? [[0, 0, 0.5], [180, 230, 0.5]] : [[0, 0, 0.5], [180, -210, 0.5]], width: 2, straight: true }));
    });
    return out;
  },
  agenda({ x, y }) {
    const rows = [['10 min', 'Welcome and goals', 'Owner'], ['20 min', 'Updates', 'Owner'], ['20 min', 'Decisions', 'Owner'], ['10 min', 'Next steps', 'Owner']];
    const out = [frame(x, y, 900, 120 + rows.length * 110, 'Meeting agenda')];
    rows.forEach(([t, topic, who], i) => {
      const ry = y + 40 + i * 110;
      out.push(box('rect', x + 30, ry, 140, 80, t, { fill: '#F2F2F4' }), box('rect', x + 190, ry, 460, 80, topic), sticky(x + 670, ry, who, 'yellow', 200, 80));
    });
    return out;
  },
  orgchart({ x, y }) {
    const head = box('rect', x + 330, y, 240, 80, 'Manager', { fill: '#C9F2C7' });
    const team = [0, 330, 660].map((dx, i) => box('rect', x + dx, y + 200, 240, 80, `Team ${i + 1}`, { fill: '#CFE6FF' }));
    return [head, ...team, ...team.map(t => link(head, t, { route: 'elbow', endHead: 'none' }))];
  },
};
