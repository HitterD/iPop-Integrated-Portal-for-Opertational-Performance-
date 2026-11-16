// Starter layouts for the whiteboard (grill Q9). Each takes the top-left corner to place it at and returns new elements:
// frames, stickies, shapes and connectors attached to the shapes they join. Nothing is special once it is on the board.
import { create } from './model.js';

const sticky = (x, y, text = '', color = 'yellow', w = 200, h = 150) => create('sticky', { x, y, w, h, text, color });
const frame = (x, y, w, h, name) => create('frame', { x, y, w, h, name });
const box = (type, x, y, w, h, text = '', more = {}) => create(type, { x, y, w, h, text, ...more });
const link = (a, b, more = {}) => create('connector', { from: { id: a.id }, to: { id: b.id }, route: 'straight', endHead: 'arrow', startHead: 'none', ...more });
const text = (x, y, t, size = 22) => create('text', { x, y, w: Math.max(40, t.length * size * 0.55), h: size * 1.3, text: t, size, font: 'ipop' });

export const TEMPLATE_INFO = [
  ['retro', 'Retro', 'Went well, improve, action'],
  ['flowchart', 'Flowchart', 'Steps and decisions'],
  ['brainstorm', 'Brainstorm', 'Ideas around a topic'],
  ['mindmap', 'Mind map', 'Branch out from one idea'],
  ['kanban', 'Kanban', 'To do, doing, done'],
  ['swot', 'SWOT', 'Strengths to threats'],
  ['timeline', 'Timeline', 'Roadmap by month'],
  ['journey', 'User journey', 'Steps a user takes'],
  ['fishbone', 'Fishbone', 'Find the root cause'],
  ['agenda', 'Meeting agenda', 'Topics, time, owners'],
  ['orgchart', 'Org chart', 'Who reports to whom'],
];

export const CANVAS_TEMPLATES = {
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
