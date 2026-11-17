// Round 6 (Template Center): 37 more everyday whiteboard templates. Each sits in a frame named after it, with a grey
// "How to use this template" sticky on the left (the Template Center can leave it out) and a few work examples you can
// delete. What each one is for (name, level, when, steps, search words) is in template-meta.js.
import { create } from './model.js';
import { STENCIL } from './stencils.js';

const sticky = (x, y, text = '', color = 'yellow', w = 180, h = 120) => create('sticky', { x, y, w, h, text, color });
const frame = (x, y, w, h, name) => create('frame', { x, y, w, h, name });
const box = (type, x, y, w, h, text = '', more = {}) => create(type, { x, y, w, h, text, ...more });
const text = (x, y, t, size = 18) => create('text', { x, y, w: Math.max(40, t.length * size * 0.55), h: size * 1.3, text: t, size, font: 'ipop' });
const link = (a, b, more = {}) => create('connector', { from: { id: a.id }, to: { id: b.id }, route: 'straight', endHead: 'arrow', startHead: 'none', ...more });
const flow = (a, b, label, more = {}) => create('connector', { from: { id: a.id }, to: { id: b.id }, route: 'elbow', endHead: 'arrow', startHead: 'none', label: label || undefined, ...more });
const st = (type, x, y, t = '', more = {}) => create(type, { x, y, w: STENCIL[type].w, h: STENCIL[type].h, text: t, ...more });
const zone = (x, y, w, h, title, fill, more = {}) => create('groupBox', { x, y, w, h, title, fill, ...more });

export const SOFT = { blue: '#D8E7FC', green: '#D6F1DE', orange: '#FDE3C6', pink: '#FADDE3', purple: '#E7DDF8', yellow: '#FFF1A8', gray: '#EEEEF0', red: '#FFD9D6' };
const COLORS6 = ['yellow', 'blue', 'pink', 'green', 'orange', 'purple'];
const stickyColour = c => (['yellow', 'green', 'blue', 'pink', 'purple', 'orange'].includes(c) ? c : 'yellow');
export const HOWTO_W = 260;
/// The "How to use" note. Marked howTo so the board can hide all of them at once.
export const howTo = (x, y, steps, h = 300) => create('sticky', { x, y, w: HOWTO_W, h, color: 'gray', howTo: true, size: 14,
  text: `**How to use this template**\n${steps.map((t, i) => `${i + 1}. ${t}`).join('\n')}` });

/// A template laid out as: frame around everything, the how-to note on the left, then the body.
/// body(x, y) returns { els, w, h } in board units. Frames come first in the list (later things draw above them).
export function sheet(name, steps, body) {
  return ({ x, y, howTo: withHow = true, examples = true } = {}) => {
    const pad = 40, top = 60;
    const bx = x + pad + (withHow ? HOWTO_W + 40 : 0), by = y + top;
    const b = body(bx, by);
    let els = b.els;
    if (!examples) els = els.map(e => (e.example ? { ...e, text: '' } : e));
    const w = (bx - x) + b.w + pad, h = top + Math.max(b.h, withHow ? 320 : 0) + pad;
    return [frame(x, y, w, h, name), ...(withHow ? [howTo(x + pad, by, steps)] : []), ...els];
  };
}
const ex = el => ({ ...el, example: true });   // example text: "Include example content" off clears it

/// A 2×2 matrix with optional axis labels.
function matrix2(names, fills, notes, axes) {
  return (x, y) => {
    const cw = 440, ch = 320, g = 20, ox = axes ? 40 : 0, els = [];
    names.forEach((n, i) => {
      const cx = x + ox + (i % 2) * (cw + g), cy = y + Math.floor(i / 2) * (ch + g);
      els.push(zone(cx, cy, cw, ch, n, fills[i]));
      (notes[i] || []).forEach((t, k) => els.push(ex(sticky(cx + 24 + k * 200, cy + 60, t, 'yellow', 180, 110))));
    });
    if (axes) els.push(text(x + ox, y + 2 * ch + g + 14, axes[0], 18), text(x, y - 34, `↑ ${axes[1]}`, 18));
    return { els, w: ox + cw * 2 + g, h: ch * 2 + g + (axes ? 50 : 0) };
  };
}
/// Columns with a heading and stickies: [[name, colour, [notes]]].
function columnsOf(cols, { colW = 280, h = 560 } = {}) {
  return (x, y) => {
    const els = [];
    cols.forEach(([n, color, items = []], i) => {
      const cx = x + i * (colW + 20);
      els.push(zone(cx, y, colW, h, n, SOFT[color] || SOFT.gray));
      items.forEach((t, k) => els.push(ex(sticky(cx + 20, y + 56 + k * 130, t, stickyColour(color), colW - 40, 110))));
    });
    return { els, w: cols.length * (colW + 20) - 20, h };
  };
}
/// A table: header row, first column, cell(i, j) → { text, fill } or null.
function table(heads, rows, { colW = 200, firstW = 200, rowH = 64, cell = () => null } = {}) {
  return (x, y) => {
    const els = [];
    heads.forEach((hd, i) => els.push(box('rect', x + (i ? firstW + (i - 1) * colW : 0), y, i ? colW : firstW, rowH, hd, { fill: i ? SOFT.blue : SOFT.gray, rounded: false, size: 16 })));
    rows.forEach((r, j) => {
      const ry = y + (j + 1) * rowH;
      els.push(ex(box('rect', x, ry, firstW, rowH, r, { rounded: false, size: 15 })));
      for (let i = 1; i < heads.length; i++) {
        const c = cell(i, j) || {};
        const b = box('rect', x + firstW + (i - 1) * colW, ry, colW, rowH, c.text || '', { rounded: false, size: 15, ...(c.fill ? { fill: c.fill } : {}) });
        els.push(c.text ? ex(b) : b);
      }
    });
    return { els, w: firstW + (heads.length - 1) * colW, h: (rows.length + 1) * rowH };
  };
}
/// Lanes (rows) × stages (columns); cell(i, j, x, y, w, h) gives what goes in each cell.
function lanesGrid(laneNames, stages, cell, { colW = 230, rowH = 150, nameW = 150 } = {}) {
  return (x, y) => {
    const els = [];
    stages.forEach((t, i) => els.push(box('rect', x + nameW + 10 + i * colW, y, colW - 10, 50, t, { fill: SOFT.purple, size: 16 })));
    laneNames.forEach((n, j) => {
      const ry = y + 60 + j * rowH;
      els.push(box('rect', x, ry, nameW, rowH - 10, n, { fill: SOFT.gray, size: 15 }));
      stages.forEach((_, i) => { const c = cell(i, j, x + nameW + 10 + i * colW, ry, colW - 10, rowH - 10); if (c) els.push(...[].concat(c)); });
    });
    return { els, w: nameW + 10 + stages.length * colW, h: 60 + laneNames.length * rowH };
  };
}

export const ROUND6 = {
  // ── Strategy & analysis ──
  eisenhower: sheet('Eisenhower Matrix', ['Put each task on a sticky', 'Drag it into one of the 4 boxes', 'Turn “Do first” stickies into tasks'],
    matrix2(['Do first · urgent + important', 'Schedule · important, not urgent', 'Delegate · urgent, not important', "Don't do · neither"], [SOFT.blue, SOFT.green, SOFT.orange, SOFT.pink],
      [['Server down at Karawang', 'PO for line 2 due today'], ['Plan Q1 training'], ['Reply vendor email'], ['Old reports nobody reads']], ['Urgent  →  Not urgent', 'Important'])),
  bcg: sheet('BCG Matrix', ['List products on stickies', 'Place by market growth and share', 'Agree an action per box'],
    matrix2(['Stars · grow', 'Question marks · decide', 'Cash cows · keep', 'Dogs · stop or fix'], [SOFT.yellow, SOFT.purple, SOFT.green, SOFT.gray],
      [['Product A'], ['Product B'], ['Product C'], ['Product D']], ['Market share: high  →  low', 'Market growth'])),
  impact: sheet('Impact / Effort', ['Add ideas', 'Vote on impact and effort', 'Start with Quick wins'],
    matrix2(['Quick wins · high impact, low effort', 'Big bets · high impact, high effort', 'Fill-ins · low impact, low effort', 'Money pit · low impact, high effort'], [SOFT.green, SOFT.blue, SOFT.yellow, SOFT.pink],
      [['Label shelves by colour'], ['New WMS'], ['Tidy the shared drive'], []], ['Effort: low  →  high', 'Impact'])),
  pest: sheet('PEST Analysis', ['One box per force', 'Add facts, not opinions', 'Mark high-impact items red'],
    matrix2(['Political', 'Economic', 'Social', 'Technological'], [SOFT.purple, SOFT.green, SOFT.orange, SOFT.blue],
      [['New import rules'], ['Rupiah exchange rate'], ['Younger workforce'], ['Cheaper automation']])),
  porter: sheet("Porter's Five Forces", ['Fill each force', 'Rate it low / medium / high', 'Summarise in the middle'], (x, y) => {
    const W = 300, H = 170, mid = box('rect', x + 340, y + 220, W, H, 'Rivalry among competitors', { fill: SOFT.purple, size: 18 });
    const f = [[x + 340, y, 'Threat of new entrants', SOFT.blue], [x + 340, y + 440, 'Threat of substitutes', SOFT.green], [x, y + 220, 'Supplier power', SOFT.orange], [x + 680, y + 220, 'Buyer power', SOFT.pink]]
      .map(([bx, by, t, fill]) => box('rect', bx, by, W, H, t, { fill, size: 17 }));
    return { els: [mid, ...f, ...f.map(b => link(b, mid))], w: 980, h: 610 };
  }),
  competitive: sheet('Competitive Analysis', ['Name competitors in the columns', 'Score each row', 'Circle where we win'],
    table(['', 'Us', 'Competitor A', 'Competitor B'], ['Price', 'Quality', 'Delivery time', 'After-sales service', 'Product range', 'Payment terms', 'Overall'],
      { cell: (i, j) => ({ text: ['●●●', '●●', '●'][(i + j) % 3], fill: [SOFT.green, SOFT.yellow, SOFT.red][(i + j) % 3] }) })),
  benchmark: sheet('Benchmark Analysis', ['Write criteria in rows', 'Add options as columns', 'Score 1–5 and add up'],
    table(['Criteria', 'Option 1', 'Option 2', 'Option 3', 'Option 4'], ['Price', 'Lead time', 'Warranty', 'Support', 'Total score'],
      { colW: 170, cell: (i, j) => (j === 4 ? null : { text: String(((i * 3 + j) % 5) + 1) }) })),
  bmc: sheet('Business Model Canvas', ['Start with customers and value', 'Fill channels and revenue', 'Then partners and costs'], (x, y) => {
    const c = 230, els = [];
    const cells = [['Key partners', 0, 0, 2, 'blue'], ['Key activities', 1, 0, 1, 'green'], ['Key resources', 1, 1, 1, 'green'], ['Value proposition', 2, 0, 2, 'pink'], ['Customer relationships', 3, 0, 1, 'orange'], ['Channels', 3, 1, 1, 'orange'], ['Customer segments', 4, 0, 2, 'yellow']];
    for (const [t, ci, ri, rs, col] of cells) els.push(zone(x + ci * c, y + ri * 220, c, 220 * rs, t, 'none'), sticky(x + ci * c + 20, y + ri * 220 + 60, '', col, c - 40, 110));
    els.push(zone(x, y + 440, c * 2.5, 200, 'Cost structure', 'none'), zone(x + c * 2.5, y + 440, c * 2.5, 200, 'Revenue streams', 'none'));
    els.push(sticky(x + 20, y + 500, '', 'pink', 180, 100), sticky(x + c * 2.5 + 20, y + 500, '', 'green', 180, 100));
    return { els, w: c * 5, h: 640 };
  }),
  lean: sheet('Lean Canvas', ['Problem and customers first', 'Then solution and metrics', 'Unfair advantage last'], (x, y) => {
    const c = 230, els = [];
    for (const [t, ci, ri, rs] of [['Problem', 0, 0, 2], ['Solution', 1, 0, 1], ['Key metrics', 1, 1, 1], ['Unique value', 2, 0, 2], ['Unfair advantage', 3, 0, 1], ['Channels', 3, 1, 1], ['Customer segments', 4, 0, 2]])
      els.push(zone(x + ci * c, y + ri * 220, c, 220 * rs, t, rs === 2 ? SOFT.gray : 'none'));
    els.push(zone(x, y + 440, c * 2.5, 200, 'Cost structure', 'none'), zone(x + c * 2.5, y + 440, c * 2.5, 200, 'Revenue streams', 'none'));
    return { els, w: c * 5, h: 640 };
  }),
  // ── Brainstorm ──
  cjm: sheet('Customer Journey Map', ['Fill stages across the top', 'Fill each lane per stage', 'Draw the feelings line', 'Ideas become tasks'],
    lanesGrid(['Actions', 'Touch-points', 'Feelings', 'Pain points', 'Ideas'], ['Aware', 'Consider', 'Order', 'Use', 'Repeat'], (i, j, cx, cy, w, h) => {
      if (j === 2) return create('stamp', { x: cx + w / 2 - 22, y: cy + h / 2 - 22, w: 44, h: 44, emoji: ['🙂', '😐', '😟', '🙂', '😀'][i] });
      return sticky(cx + 12, cy + 12, '', ['blue', 'purple', '', 'pink', 'green'][j], w - 24, h - 24);
    })),
  affinity: sheet('Affinity Map', ['Dump all notes', 'Move similar ones together', 'Name each group'], (x, y) => {
    const els = [];
    [['Price', 'pink'], ['Delivery', 'blue'], ['Quality', 'green']].forEach(([t, c], i) => {
      const gx = x + i * 440;
      els.push(zone(gx, y, 420, 520, t, SOFT[c]));
      for (let k = 0; k < 6; k++) els.push(sticky(gx + 20 + (k % 2) * 200, y + 60 + Math.floor(k / 2) * 150, '', 'yellow', 180, 130));
    });
    return { els, w: 1300, h: 520 };
  }),
  crazy8: sheet('Crazy 8s', ['Start the 8-minute timer', 'One sketch per box', 'Present and vote'], (x, y) => {
    const els = [];
    for (let i = 0; i < 8; i++) els.push(zone(x + (i % 4) * 280, y + Math.floor(i / 4) * 380, 260, 360, String(i + 1), 'none'));
    return { els, w: 1100, h: 740 };
  }),
  '5whys': sheet('5 Whys', ['Write the problem', 'Ask “why?” and answer below', 'Stop at the root cause'], (x, y) => {
    const p = ex(box('rect', x, y, 360, 80, 'Problem: delivery to the customer was late', { fill: SOFT.red, size: 17 }));
    const els = [p]; let prev = p;
    for (let i = 0; i < 5; i++) { const b = box('rect', x, y + 130 + i * 120, 360, 72, `Why? ${i + 1}`, { fill: 'none', size: 16 }); els.push(b, link(prev, b)); prev = b; }
    const r = box('rect', x + 460, y + 610, 320, 80, 'Root cause → action', { fill: SOFT.green, size: 17 });
    els.push(r, link(prev, r));
    return { els, w: 780, h: 700 };
  }),
  sixhats: sheet('Six Thinking Hats', ['Go hat by hat together', 'Add stickies under each', 'Blue hat sums up'],
    columnsOf([['⚪ Facts', 'gray', ['']], ['🔴 Feelings', 'pink', ['']], ['⚫ Risks', 'purple', ['']], ['🟡 Benefits', 'yellow', ['']], ['🟢 Ideas', 'green', ['']], ['🔵 Process', 'blue', ['']]], { colW: 220, h: 460 })),
  starburst: sheet('Starbursting', ['Put the idea in the star', 'Add questions per point', 'Answer the important ones'], (x, y) => {
    const cx = x + 460, cy = y + 330, star = box('star', cx - 110, cy - 105, 220, 210, 'Idea', { fill: SOFT.yellow, size: 20 });
    const els = [star];
    ['Who?', 'What?', 'When?', 'Where?', 'Why?', 'How?'].forEach((t, i) => {
      const a = -Math.PI / 2 + (i / 6) * Math.PI * 2, bx = cx + Math.cos(a) * 340 - 90, by = cy + Math.sin(a) * 260 - 40;
      const b = box('rect', bx, by, 180, 80, t, { fill: SOFT[COLORS6[(i + 1) % 6]], size: 18 }); els.push(b, link(star, b, { endHead: 'none' }));
    });
    return { els, w: 920, h: 660 };
  }),
  empathy: sheet('Empathy Map', ['Name the person in the middle', 'Fill the four quadrants', 'Add pains and gains'], (x, y) => {
    const m = matrix2(['Says', 'Thinks', 'Does', 'Feels'], [SOFT.blue, SOFT.purple, SOFT.green, SOFT.pink], [[''], [''], [''], ['']])(x, y);
    m.els.push(box('ellipse', x + 380, y + 270, 160, 120, 'User', { fill: '#FFFFFF', size: 18 }));
    m.els.push(zone(x, y + 680, 450, 160, 'Pains', SOFT.red), zone(x + 450, y + 680, 450, 160, 'Gains', SOFT.green));
    return { els: m.els, w: m.w, h: 840 };
  }),
  // ── Planning & meeting ──
  sprint: sheet('Sprint Planning', ['Write the sprint goal', 'Pull tasks into This sprint', 'Note risks; make tasks'], (x, y) => {
    const goal = box('rect', x, y, 880, 70, 'Sprint goal: ', { fill: SOFT.green, size: 18 });
    const c = columnsOf([['Backlog', 'gray', ['Barcode for raw material', 'Stock aging report', 'Fix login on TC22']], ['This sprint', 'blue', ['', '']], ['Risks', 'pink', ['']]])(x, y + 90);
    return { els: [goal, ...c.els], w: Math.max(880, c.w), h: 90 + c.h };
  }),
  standup: sheet('Daily Stand-up', ['One row per person', 'Fill three columns', 'Blockers → owner + task'],
    table(['Name', 'Yesterday', 'Today', 'Blockers'], ['Andi', 'Sari', 'Budi', 'Rina', 'Dewi', 'Joko'], { colW: 260, rowH: 70 })),
  weekly: sheet('Weekly Planner', ['Drop tasks on days', 'Colour by priority', 'Review Friday'],
    columnsOf([['Monday', 'blue', ['']], ['Tuesday', 'green', ['']], ['Wednesday', 'orange', ['']], ['Thursday', 'purple', ['']], ['Friday', 'pink', ['', '']]], { colW: 230, h: 520 })),
  raci: sheet('RACI Matrix', ['Tasks in rows, people in columns', 'One A per row', 'Share with the team'],
    table(['Task', 'ICT', 'Finance', 'Purchasing', 'Manager'], ['Request', 'Quotation', 'Approve', 'Make PO', 'Receive (GRN)', 'Pay invoice'],
      { colW: 150, cell: (i, j) => { const L = 'RACI'[(i + j) % 4]; return { text: L, fill: { R: SOFT.blue, A: SOFT.pink, C: SOFT.orange, I: SOFT.gray }[L] }; } })),
  okr: sheet('OKR', ['One objective per row', '3 measurable key results each', 'Update progress monthly'], (x, y) => {
    const els = [];
    for (let o = 0; o < 3; o++) {
      const oy = y + o * 250;
      els.push(box('rect', x, oy, 280, 230, `Objective ${o + 1}`, { fill: SOFT[['blue', 'green', 'purple'][o]], size: 18 }));
      for (let k = 0; k < 3; k++) els.push(box('rect', x + 300, oy + k * 78, 620, 70, `Key result ${o + 1}.${k + 1}: from … to …  (0%)`, { fill: 'none', size: 15, align: 'left' }));
    }
    return { els, w: 920, h: 740 };
  }),
  actionplan: sheet('Action Plan', ['One action per row', 'Name one owner', 'Turn rows into tasks'],
    table(['Action', 'Owner', 'Due', 'Status'], ['Fix the label printer', 'Train warehouse staff', 'Write the new SOP', '5S audit line 2', 'Monthly report'],
      { colW: 180, firstW: 300, cell: (i, j) => (i === 3 ? { text: ['Done', 'Doing', 'To do', 'Doing', 'Done'][j], fill: [SOFT.green, SOFT.orange, SOFT.gray, SOFT.orange, SOFT.green][j] } : null) })),
  meetingnotes: sheet('Meeting Notes', ['Fill attendees and agenda before', 'Take notes during', 'Decisions and actions at the end'], (x, y) => {
    const els = [zone(x, y, 440, 150, 'Attendees', SOFT.blue), zone(x, y + 170, 440, 200, 'Agenda', SOFT.purple), zone(x, y + 390, 440, 300, 'Notes', SOFT.gray),
      zone(x + 460, y, 440, 330, 'Decisions', SOFT.green), zone(x + 460, y + 350, 440, 340, 'Action items', SOFT.orange)];
    for (let i = 0; i < 3; i++) els.push(ex(text(x + 484, y + 406 + i * 44, '☐ … — owner — due date', 16)));
    return { els, w: 900, h: 690 };
  }),
  kickoff: sheet('Project Kickoff', ['Agree the goal in one line', 'Scope in and out', 'Team, dates, risks'], (x, y) => {
    const els = [];
    [['Goal', 'green'], ['In scope', 'blue'], ['Out of scope', 'pink'], ['Team', 'purple'], ['Timeline', 'orange'], ['Risks', 'red']].forEach(([t, c], i) => {
      const bx = x + (i % 3) * 320, by = y + Math.floor(i / 3) * 340;
      els.push(zone(bx, by, 300, 320, t, SOFT[c]), sticky(bx + 20, by + 60, '', 'yellow', 260, 110));
    });
    return { els, w: 940, h: 660 };
  }),
  gantt: sheet('Gantt', ['List tasks down the left', 'Stretch bars across weeks', 'Diamonds for milestones'], (x, y) => {
    const els = [], lw = 220, cw = 110;
    for (let wk = 0; wk < 8; wk++) els.push(box('rect', x + lw + wk * cw, y, cw, 44, `W${wk + 1}`, { fill: SOFT.gray, rounded: false, size: 14 }));
    const tasks = ['Requirements', 'Design', 'Build', 'Testing', 'Training', 'Go live'];
    tasks.forEach((t, i) => {
      const ry = y + 60 + i * 70;
      els.push(ex(text(x, ry + 18, t, 17)));
      els.push(st('phase', x + lw + [0, 1, 2, 4, 6, 7][i] * cw, ry + 10, '', { w: [2, 2, 3, 2, 1, 1][i] * cw - 6, h: 40, fill: SOFT[COLORS6[i % 6]] }));
    });
    els.push(st('milestone', x + lw + 8 * cw - 30, y + 60 + 5 * 70 + 8, 'Go live'));
    return { els, w: lw + 8 * cw, h: 60 + tasks.length * 70 };
  }),
  roadmap: sheet('Product Roadmap', ['One lane per theme', 'Bars per quarter', 'Keep it high level'],
    lanesGrid(['Mobile', 'Notes', 'Mail', 'Infrastructure'], ['Q1', 'Q2', 'Q3', 'Q4'], (i, j, cx, cy, w, h) => ((i + j) % 3 === 2 ? null : st('phase', cx + 6, cy + h / 2 - 22, '', { w: w - 12, h: 44, fill: SOFT[['blue', 'green', 'orange', 'purple'][j]] })), { rowH: 110 })),
  // ── Process improvement ──
  pdca: sheet('PDCA Cycle', ['Plan the change', 'Try it small', 'Check results', 'Act: standardise or retry'], (x, y) => {
    const [P, D, A, C] = [['Plan', 'blue', 0, 0], ['Do', 'green', 1, 0], ['Act', 'pink', 0, 1], ['Check', 'orange', 1, 1]].map(([t, c, i, j]) => box('ellipse', x + i * 360, y + j * 320, 320, 280, t, { fill: SOFT[c], size: 30 }));
    return { els: [P, D, A, C, link(P, D, { route: 'curved' }), link(D, C, { route: 'curved' }), link(C, A, { route: 'curved' }), link(A, P, { route: 'curved' })], w: 680, h: 600 };
  }),
  dmaic: sheet('DMAIC', ['Fill phase by phase', 'Attach data and charts', 'Control plan at the end'],
    columnsOf([['Define', 'blue', ['Problem, goal, scope']], ['Measure', 'purple', ['Baseline data']], ['Analyse', 'orange', ['Root causes']], ['Improve', 'green', ['Solutions tried']], ['Control', 'pink', ['Control plan']]], { colW: 240 })),
  sipoc: sheet('SIPOC', ['Start with the 5–7 process steps', 'Then outputs and customers', 'Then inputs and suppliers'],
    columnsOf([['Suppliers', 'gray', ['Vendor']], ['Inputs', 'blue', ['Raw material', 'PO']], ['Process', 'green', ['Receive', 'Inspect', 'Store']], ['Outputs', 'orange', ['Stock in SAP']], ['Customers', 'purple', ['Production']]], { colW: 240 })),
  a3: sheet('A3 Report', ['Left: background, current state, goal, analysis', 'Right: countermeasures, plan, follow-up', 'Keep it to one page'], (x, y) => {
    const els = []; let ly = y, ry = y;
    for (const [t, h] of [['1  Background', 140], ['2  Current state', 200], ['3  Goal', 110], ['4  Root cause analysis', 220]]) { els.push(zone(x, ly, 520, h, t, 'none')); ly += h + 10; }
    for (const [t, h] of [['5  Countermeasures', 230], ['6  Plan (who, what, when)', 230], ['7  Follow-up', 210]]) { els.push(zone(x + 540, ry, 520, h, t, 'none')); ry += h + 10; }
    return { els, w: 1060, h: Math.max(ly, ry) - y };
  }),
  kaizen: sheet('Kaizen Board', ['Anyone adds a problem', 'Team adds ideas', 'Move to Doing and Result'],
    columnsOf([['Problem', 'pink', ['Forklift waits at dock 2']], ['Idea', 'yellow', ['Book dock time slots']], ['Doing', 'blue', ['']], ['Result', 'green', ['']]])),
  '5s': sheet('5S Checklist', ['One column per S', 'Tick what is done', 'Photos as evidence'], (x, y) => {
    const els = [];
    [['Sort (Ringkas)', SOFT.red], ['Set in order (Rapi)', SOFT.orange], ['Shine (Resik)', SOFT.yellow], ['Standardise (Rawat)', SOFT.green], ['Sustain (Rajin)', SOFT.blue]].forEach(([t, f], i) => {
      const cx = x + i * 250; els.push(zone(cx, y, 230, 460, t, f));
      for (let k = 0; k < 6; k++) els.push(text(cx + 18, y + 60 + k * 62, '☐', 20));
    });
    return { els, w: 1230, h: 460 };
  }),
  blueprint: sheet('Service Blueprint', ['Customer actions on top', 'What staff do (seen / unseen)', 'Systems below'],
    lanesGrid(['Customer', 'Front stage', 'Back stage', 'Support systems'], ['Ask for help', 'Log ticket', 'Fix', 'Close'], (i, j, cx, cy, w, h) => box('rect', cx + 14, cy + 14, w - 28, h - 28, '', { fill: SOFT[['blue', 'green', 'orange', 'purple'][j]] }), { rowH: 140 })),
  // ── Technical ──
  sitemap: sheet('Sitemap', ['Home at the top', 'Sections below', 'Sub-pages per section'], (x, y) => {
    const home = box('rect', x + 420, y, 220, 70, 'Home', { fill: SOFT.purple, size: 18 }), els = [home];
    ['About', 'Products', 'News', 'Contact'].forEach((t, i) => {
      const s = box('rect', x + i * 270, y + 160, 220, 64, t, { fill: SOFT.blue, size: 16 }); els.push(s, link(home, s, { route: 'elbow', endHead: 'none' }));
      for (let k = 0; k < 2; k++) { const c = box('rect', x + i * 270 + 30, y + 270 + k * 90, 190, 60, 'Page', { size: 15 }); els.push(c, link(s, c, { route: 'elbow', endHead: 'none' })); }
    });
    return { els, w: 1030, h: 450 };
  }),
  wireframe: sheet('Wireframe', ['Pick a screen size frame', 'Boxes for header, menu, content', 'Notes for behaviour'], (x, y) => {
    const els = [zone(x, y, 1000, 640, 'Screen', 'none'), box('rect', x + 20, y + 40, 960, 60, 'Header · logo · search · profile', { fill: SOFT.gray }),
      box('rect', x + 20, y + 120, 200, 500, 'Menu', { fill: SOFT.gray })];
    for (let i = 0; i < 6; i++) els.push(box('rect', x + 240 + (i % 3) * 245, y + 120 + Math.floor(i / 3) * 255, 225, 235, 'Card', { fill: 'none' }));
    els.push(ex(sticky(x + 1020, y + 120, 'Click a card → opens the detail', 'yellow', 200, 120)));
    return { els, w: 1220, h: 640 };
  }),
  userflow: sheet('User Flow', ['One box per screen', 'Diamonds for choices', 'Arrows show taps'], (x, y) => {
    const sc = (bx, by, t) => box('rect', bx, by, 170, 220, t, { fill: 'none', size: 16 });
    const login = sc(x, y + 120, 'Login'), ok = st('decision', x + 230, y + 175, 'Password OK?'), home = sc(x + 500, y + 120, 'Home');
    const req = sc(x + 760, y, 'New request'), status = sc(x + 760, y + 260, 'Status'), reset = sc(x + 235, y + 380, 'Reset password');
    return { els: [login, ok, home, req, status, reset, flow(login, ok), flow(ok, home, 'Yes'), flow(ok, reset, 'No'), flow(home, req), flow(home, status), flow(reset, login)], w: 930, h: 600 };
  }),
  architecture: sheet('System Architecture', ['Clients on top', 'Services in the middle', 'Data and storage at the bottom'], (x, y) => {
    const els = [];
    const boxes = [['Clients', SOFT.blue, ['Web', 'Phone app', 'Notifier']], ['Services', SOFT.green, ['API', 'Realtime hub', 'Workers']], ['Data', SOFT.orange, ['Postgres', 'NAS', 'Object storage']]].map(([n, f, items], j) => {
      const ry = y + j * 230; els.push(zone(x, ry, 960, 190, n, 'none'));
      return items.map((t, i) => { const b = box('rect', x + 60 + i * 300, ry + 70, 240, 90, t, { fill: f, size: 17 }); els.push(b); return b; });
    });
    els.push(link(boxes[0][1], boxes[1][0]), link(boxes[1][0], boxes[2][0]));
    return { els, w: 960, h: 650 };
  }),
};
