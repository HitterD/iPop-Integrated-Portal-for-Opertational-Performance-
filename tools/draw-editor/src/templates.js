// Starter layouts for a whiteboard. Each returns Excalidraw "element skeletons" (shapes with text inside and arrows that are
// bound to the shapes they connect), which draw.js turns into real elements with convertToExcalidrawElements. They are only a
// starting point: nothing here is special once it is on the board.

const STICKY = ['#FFE27A', '#B6F0C0', '#FFC2D4', '#BFE0FF', '#E3D2FF', '#FFD3A8'];

const sticky = (id, x, y, text, c = 0) => ({
  type: 'rectangle', id, x, y, width: 170, height: 120, backgroundColor: STICKY[c % STICKY.length], fillStyle: 'solid',
  strokeColor: 'transparent', roughness: 1, roundness: { type: 3 },
  label: { text, fontSize: 18, fontFamily: 5, strokeColor: '#2a2a2a' },
});

const frame = (id, x, y, w, h, name) => ({ type: 'frame', id, x, y, width: w, height: h, name, children: [] });

const box = (id, x, y, text, { w = 190, h = 70, shape = 'rectangle', color = '#ffffff' } = {}) => ({
  type: shape, id, x, y, width: w, height: h, backgroundColor: color, fillStyle: 'solid', roughness: 1,
  roundness: shape === 'rectangle' ? { type: 3 } : undefined,
  label: { text, fontSize: 18, fontFamily: 5 },
});

// An arrow names the two shapes it joins; `link` below turns that into real coordinates between their centres.
const arrow = (from, to, label) => ({ __link: [from, to], label });

// Where the straight line from a shape's centre towards (tx, ty) leaves the shape's outline, pushed out by `gap` px, so an
// arrow starts and ends ON the edge instead of running through the text inside the shape.
export function edgePoint(shape, tx, ty, gap = 8) {
  const w = shape.width ?? 0, h = shape.height ?? 0;
  const cx = shape.x + w / 2, cy = shape.y + h / 2;
  const dx = tx - cx, dy = ty - cy;
  const len = Math.hypot(dx, dy) || 1;
  const hw = w / 2 || 1, hh = h / 2 || 1;
  let t;                                            // how far along (dx, dy) the outline is, as a fraction of the vector
  if (shape.type === 'ellipse') t = 1 / Math.hypot(dx / hw, dy / hh);
  else if (shape.type === 'diamond') t = 1 / (Math.abs(dx) / hw + Math.abs(dy) / hh);
  else t = Math.min(hw / (Math.abs(dx) || 1e-9), hh / (Math.abs(dy) || 1e-9));
  const reach = t * len + gap;                      // distance from the centre to the point just outside the outline
  return { x: cx + (dx / len) * reach, y: cy + (dy / len) * reach };
}

// Turns a list of shapes + { __link } placeholders into skeletons whose arrows run edge to edge between the shapes.
export function link(items) {
  const by = new Map(items.filter(i => i.id).map(i => [i.id, i]));
  const centre = i => ({ x: i.x + (i.width ?? 0) / 2, y: i.y + (i.height ?? 0) / 2 });
  return items.map(i => {
    if (!i.__link) return i;
    const [a, b] = i.__link.map(id => by.get(id));
    if (!a || !b) throw new Error(`template arrow refers to a missing shape: ${i.__link.join(' -> ')}`);
    const ca = centre(a), cb = centre(b);
    const from = edgePoint(a, cb.x, cb.y), to = edgePoint(b, ca.x, ca.y);
    return { type: 'arrow', x: from.x, y: from.y, width: to.x - from.x, height: to.y - from.y, start: { id: a.id }, end: { id: b.id }, roughness: 1,
      ...(i.label ? { label: { text: i.label, fontSize: 14, fontFamily: 5 } } : {}) };
  });
}

// Frames own the shapes that sit inside them, so dragging a frame moves its contents.
function inFrames(items) {
  const frames = items.filter(i => i.type === 'frame');
  for (const f of frames) {
    f.children = items.filter(i => i !== f && i.id && i.type !== 'frame' && !i.__link
      && i.x >= f.x && i.y >= f.y && i.x + (i.width ?? 0) <= f.x + f.width && i.y + (i.height ?? 0) <= f.y + f.height).map(i => i.id);
  }
  return items;
}

export const BOARD_TEMPLATES = {
  retro: () => inFrames(link([
    frame('f1', 0, 0, 300, 420, 'Went well'), frame('f2', 340, 0, 300, 420, 'To improve'), frame('f3', 680, 0, 300, 420, 'Actions'),
    sticky('a', 24, 50, 'Migration finished a week early', 0), sticky('b', 100, 200, 'Users like the new tray app', 1),
    sticky('c', 364, 50, 'Two WIN7 PCs left', 2), sticky('d', 440, 200, 'Need spare SSDs', 3),
    sticky('e', 704, 50, 'Order SSDs (owner: Rina)', 4),
    arrow('d', 'e'),
  ])),
  flow: () => inFrames(link([
    box('n1', 0, 80, 'Ticket opened', { shape: 'rectangle', w: 170 }),
    box('n2', 250, 80, 'Back up sticky notes'),
    box('n3', 520, 60, 'Backup OK?', { shape: 'diamond', w: 190, h: 110 }),
    box('n4', 800, 0, 'Install WIN10'),
    box('n5', 800, 160, 'Retry or call IT'),
    box('n6', 1070, 0, 'Close ticket', { w: 170 }),
    arrow('n1', 'n2'), arrow('n2', 'n3'), arrow('n3', 'n4', 'yes'), arrow('n3', 'n5', 'no'), arrow('n4', 'n6'), arrow('n5', 'n2'),
  ])),
  brainstorm: () => {
    const ideas = ['Dark mode for kiosks', 'QR code on every PC', 'Self-service password reset', 'Photo of each asset', 'Weekly tips email', 'Spare-part shelf'];
    const cx = 420, cy = 260, r = 300;
    return inFrames(link([
      box('m', cx - 95, cy - 40, 'Better IT for everyone', { w: 190, h: 80, shape: 'ellipse', color: '#E3D2FF' }),
      ...ideas.map((t, i) => { const a = (i / ideas.length) * Math.PI * 2 - Math.PI / 2; return sticky(`i${i}`, cx + Math.cos(a) * r - 85, cy + Math.sin(a) * (r * 0.62) - 60, t, i); }),
      ...ideas.map((_, i) => arrow('m', `i${i}`)),
    ]));
  },
};

export { inFrames };
export const TEMPLATE_NAMES = { retro: 'Retro', flow: 'Flowchart', brainstorm: 'Brainstorm' };
