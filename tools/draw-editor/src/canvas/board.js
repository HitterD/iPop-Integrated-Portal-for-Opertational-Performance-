// iPop's own whiteboard editor. One instance per board or /draw block.
//
// Storage: the scene's Y.Map ("wb:<id>") is the single source of truth; every element is one key. The editor draws straight
// from it, so what everyone sees converges with Yjs and needs no reconciliation layer. While a pointer is down, changes go to
// a local draft that is written to the map at most every 60 ms (others see the move live); undo is a Y.UndoManager that only
// tracks this editor's own writes, so Ctrl Z never undoes somebody else's work.
import {
  create, bump, isElement, zBetween, topZ, bottomZ, bounds, unionBounds, hitTest, insideBox, frameOf, moved, resizeBox,
  snap, guides, recognise, centre, SHAPES, STICKY_COLORS,
  cutStroke, strokePieces, alignBoxes, distributeBoxes, tidyRow, expandGroups, recogniseLine, findOnBoard,
  SHAPE_TYPES, isShape, isContainer, connectorPoints,
} from './model.js';
import { STENCIL, STENCILS, STENCIL_CATS, FLOW_TYPES, stencilPath, lanesOf, laneAt, isNetDevice, NET_FIELDS, rackSlots, rackUnits } from './stencils.js';
import { graphicsFor, layoutTree, layoutRadial, layoutOrganic, layoutNetwork, findDevice, rackDrop, rackContents, stackList, reportRows, toCsv, toXlsx, toPptx, pptxNative, diffScenes, diffSummary, orgChart, tasksFromShapes } from './diagram.js';
import { checkFlow, stepNumbers, nextHints, placeNext, layoutFlow, hasFlowchart, facingSide, nearestSide, portPoint, fractionAt } from './flow.js';
import { fromMermaid, drawioModel, fromDrawioModel } from './importers.js';
import { svgEl, defs, drawElement, PALETTE, toPng, toSvg, measure, FONT_STACK, layoutText, pngToPdf } from './render.js';
import { CANVAS_TEMPLATES, TEMPLATE_INFO, TEMPLATE_META, buildTemplate } from './templates.js';
import { toggleMark, toggleBullets, makeLink } from './richtext.js';

const ICONS = {
  select: '<path d="m4 4 7 17 2.5-7.5L21 11z"/>',
  hand: '<path d="M18 11V6a2 2 0 0 0-4 0v5M14 10V4a2 2 0 0 0-4 0v6M10 10.5V6a2 2 0 0 0-4 0v8"/><path d="M18 8a2 2 0 1 1 4 0v6a8 8 0 0 1-8 8h-2c-2.8 0-4.5-.9-6-2.4l-3.6-3.6a2 2 0 0 1 2.8-2.8L7 15"/>',
  pen: '<path d="M12 20h9"/><path d="M16.4 3.6a2 2 0 0 1 2.9 2.9L7 18.8l-4 1 1-4z"/>',
  highlighter: '<path d="m9 11-6 6v3h9l3-3"/><path d="m22 12-4.6 4.6a2 2 0 0 1-2.8 0l-5.2-5.2a2 2 0 0 1 0-2.8L14 4"/>',
  eraser: '<path d="m7 21-4.3-4.3a1 1 0 0 1 0-1.4l10-10a1 1 0 0 1 1.4 0l5.6 5.6a1 1 0 0 1 0 1.4L13 21"/><path d="M22 21H7M5 11l9 9"/>',
  text: '<path d="M4 7V4h16v3M9 20h6M12 4v16"/>',
  rect: '<rect x="3" y="5" width="18" height="14" rx="3"/>',
  ellipse: '<circle cx="12" cy="12" r="9"/>',
  diamond: '<path d="M12 2 22 12 12 22 2 12z"/>',
  triangle: '<path d="M12 3 22 20H2z"/>',
  star: '<path d="m12 2 3.1 6.3 6.9 1-5 4.9 1.2 6.8L12 17.8 5.8 21l1.2-6.8-5-4.9 6.9-1z"/>',
  cloud: '<path d="M17.5 19H9a7 7 0 1 1 6.7-9h1.8a4.5 4.5 0 1 1 0 9z"/>',
  arrowShape: '<path d="M3 9h11V5l7 7-7 7v-4H3z"/>',
  cylinder: '<ellipse cx="12" cy="5" rx="8" ry="3"/><path d="M4 5v14c0 1.7 3.6 3 8 3s8-1.3 8-3V5"/>',
  bubble: '<path d="M21 15a2 2 0 0 1-2 2H7l-4 4V5a2 2 0 0 1 2-2h14a2 2 0 0 1 2 2z"/>',
  connector: '<path d="M5 19 19 5M9 5h10v10"/>',
  frame: '<path d="M22 6H2M22 18H2M6 2v20M18 2v20"/>',
  shapes: '<rect x="3" y="3" width="8" height="8" rx="2"/><circle cx="17" cy="7" r="4"/><path d="m7 14 4 7H3z"/><path d="M14 14h7v7h-7z"/>',
  undo: '<path d="M3 7v6h6"/><path d="M21 17a9 9 0 0 0-15-6.7L3 13"/>',
  redo: '<path d="M21 7v6h-6"/><path d="M3 17a9 9 0 0 1 15-6.7L21 13"/>',
  copy: '<rect x="9" y="9" width="13" height="13" rx="2"/><path d="M5 15H4a2 2 0 0 1-2-2V4a2 2 0 0 1 2-2h9a2 2 0 0 1 2 2v1"/>',
  trash: '<path d="M3 6h18M8 6V4a2 2 0 0 1 2-2h4a2 2 0 0 1 2 2v2M19 6l-1 14a2 2 0 0 1-2 2H8a2 2 0 0 1-2-2L5 6"/>',
  front: '<rect x="8" y="8" width="13" height="13" rx="2"/><path d="M16 8V5a2 2 0 0 0-2-2H5a2 2 0 0 0-2 2v9a2 2 0 0 0 2 2h3"/>',
  back: '<rect x="3" y="3" width="13" height="13" rx="2"/><path d="M8 16v3a2 2 0 0 0 2 2h9a2 2 0 0 0 2-2v-9a2 2 0 0 0-2-2h-3"/>',
  lock: '<rect x="3" y="11" width="18" height="11" rx="2"/><path d="M7 11V7a5 5 0 0 1 10 0v4"/>',
  unlock: '<rect x="3" y="11" width="18" height="11" rx="2"/><path d="M7 11V7a5 5 0 0 1 9.9-1"/>',
  grid: '<rect x="3" y="3" width="7" height="7" rx="1"/><rect x="14" y="3" width="7" height="7" rx="1"/><rect x="3" y="14" width="7" height="7" rx="1"/><rect x="14" y="14" width="7" height="7" rx="1"/>',
  keys: '<rect x="2" y="6" width="20" height="12" rx="2"/><path d="M6 10h.01M10 10h.01M14 10h.01M18 10h.01M7 14h10"/>',
  more: '<circle cx="5" cy="12" r="1"/><circle cx="12" cy="12" r="1"/><circle cx="19" cy="12" r="1"/>',
};
ICONS.org = '<rect x="9" y="2" width="6" height="5" rx="1"/><rect x="3" y="16" width="6" height="5" rx="1"/><rect x="15" y="16" width="6" height="5" rx="1"/><path d="M12 7v4M6 16v-3h12v3"/>';
ICONS.layout = '<circle cx="12" cy="12" r="2.5"/><circle cx="4.5" cy="5" r="1.8"/><circle cx="19.5" cy="5" r="1.8"/><circle cx="4.5" cy="19" r="1.8"/><circle cx="19.5" cy="19" r="1.8"/><path d="m6 6.4 4.2 3.8M18 6.4l-4.2 3.8M6 17.6l4.2-3.8M18 17.6l-4.2-3.8"/>';
ICONS.table = '<rect x="3" y="4" width="18" height="16" rx="2"/><path d="M3 10h18M3 15h18M9 4v16"/>';
ICONS.slides = '<rect x="2" y="4" width="20" height="13" rx="2"/><path d="M12 17v4M8 21h8"/>';
ICONS.history = '<path d="M3 12a9 9 0 1 0 3-6.7L3 8"/><path d="M3 3v5h5M12 7v5l3 2"/>';
ICONS.chart = '<path d="M3 3v18h18"/><rect x="7" y="12" width="3" height="6"/><rect x="12" y="8" width="3" height="10"/><rect x="17" y="5" width="3" height="13"/>';
ICONS.ip = '<rect x="3" y="4" width="18" height="12" rx="2"/><path d="M8 20h8M12 16v4M7 8v4M10 12V8h2.5a1.5 1.5 0 0 1 0 3H10"/>';
ICONS.chat = '<path d="M21 12a8 8 0 0 1-11.6 7.1L3 21l1.9-6.4A8 8 0 1 1 21 12z"/>';
ICONS.book ='<path d="M4 19.5A2.5 2.5 0 0 1 6.5 17H20V3H6.5A2.5 2.5 0 0 0 4 5.5z"/><path d="M4 19.5A2.5 2.5 0 0 0 6.5 22H20v-5"/>';
ICONS.bookplus = '<path d="M4 19.5A2.5 2.5 0 0 1 6.5 17H20V3H6.5A2.5 2.5 0 0 0 4 5.5z"/><path d="M12 7v6M9 10h6"/>';
ICONS.image = '<rect x="3" y="3" width="18" height="18" rx="2"/><circle cx="9" cy="9" r="2"/><path d="m21 15-5-5L5 21"/>';
ICONS.file = '<path d="M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8z"/><path d="M14 2v6h6"/>';
ICONS.laser = '<circle cx="12" cy="12" r="3"/><path d="M12 2v3M12 19v3M2 12h3M19 12h3M4.9 4.9 7 7M17 17l2.1 2.1M4.9 19.1 7 17M17 7l2.1-2.1"/>';
ICONS.play = '<path d="m6 4 14 8-14 8z"/>';
ICONS.timer = '<circle cx="12" cy="13" r="8"/><path d="M12 9v4l2 2M9 2h6"/>';
ICONS.frames = '<rect x="3" y="4" width="7" height="7" rx="1"/><rect x="14" y="4" width="7" height="7" rx="1"/><rect x="3" y="14" width="7" height="7" rx="1"/><path d="M14 17h7"/>';
ICONS.comment = '<path d="M21 15a2 2 0 0 1-2 2H7l-4 4V5a2 2 0 0 1 2-2h14a2 2 0 0 1 2 2z"/>';
ICONS.vote = '<circle cx="12" cy="12" r="9"/><circle cx="12" cy="12" r="3.2" fill="currentColor"/>';
ICONS.task = '<rect x="3" y="3" width="18" height="18" rx="3"/><path d="m8 12 3 3 5-6"/>';
ICONS.note = '<path d="M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8z"/><path d="M14 2v6h6M8 13h8M8 17h5"/>';
ICONS.download = '<path d="M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4"/><path d="m7 10 5 5 5-5M12 15V3"/>';
ICONS.stamp = '<circle cx="12" cy="12" r="9"/><path d="M8 14s1.5 2 4 2 4-2 4-2M9 9h.01M15 9h.01"/>';
ICONS.mind = '<circle cx="12" cy="12" r="3"/><circle cx="4" cy="5" r="2"/><circle cx="20" cy="5" r="2"/><circle cx="4" cy="19" r="2"/><circle cx="20" cy="19" r="2"/><path d="M6 6l4 4M18 6l-4 4M6 18l4-4M18 18l-4-4"/>';
ICONS.card = '<rect x="3" y="5" width="18" height="14" rx="2"/><path d="M7 9h10M7 13h6"/>';
ICONS.marker = '<path d="m9 15-4 5h5l2-2"/><path d="m9 15 7-11 4 3-7 11z"/>';
ICONS.pencil = '<path d="M15 4l5 5L9 20H4v-5z"/><path d="m4 15 5 5"/>';
ICONS.emoji = '<circle cx="12" cy="12" r="9"/><path d="M8.5 14.5a4.5 4.5 0 0 0 7 0M9 9.5h.01M15 9.5h.01"/>';
ICONS.link = '<path d="M10 14a4 4 0 0 0 6 .5l3-3a4 4 0 0 0-5.7-5.7l-1.5 1.5"/><path d="M14 10a4 4 0 0 0-6-.5l-3 3a4 4 0 0 0 5.7 5.7l1.5-1.5"/>';
ICONS.linkcard = '<rect x="3" y="4" width="18" height="16" rx="2"/><path d="M3 11h18"/><path d="M7 15h6"/>';
ICONS.bulb = '<path d="M9 18h6M10 22h4M12 2a7 7 0 0 0-4 12.7V17h8v-2.3A7 7 0 0 0 12 2z"/>';
ICONS.tidy = '<rect x="8" y="2" width="8" height="5" rx="1"/><rect x="8" y="10" width="8" height="5" rx="1"/><rect x="8" y="18" width="8" height="4" rx="1"/><path d="M12 7v3M12 15v3"/>';
ICONS.check = '<circle cx="12" cy="12" r="9"/><path d="m8 12 3 3 5-6"/>';
ICONS.import = '<path d="M12 3v12M7 10l5 5 5-5"/><path d="M5 21h14"/>';
ICONS.search = '<circle cx="11" cy="11" r="7"/><path d="m20 20-3.5-3.5"/>';
ICONS.newtab = '<path d="M14 4h6v6M20 4l-9 9M18 14v5a1 1 0 0 1-1 1H5a1 1 0 0 1-1-1V7a1 1 0 0 1 1-1h5"/>';
ICONS.group = '<rect x="3" y="3" width="8" height="8" rx="1"/><rect x="13" y="13" width="8" height="8" rx="1"/><path d="M3 15v6h6M21 9V3h-6" stroke-dasharray="2 2"/>';
ICONS.ungroup = '<rect x="3" y="3" width="8" height="8" rx="1"/><rect x="13" y="13" width="8" height="8" rx="1"/>';
ICONS.arrange = '<path d="M4 3v18M8 7h12M8 15h7"/>';
ICONS.alignleft = '<path d="M4 3v18M8 7h10M8 15h6"/>';
ICONS.alignhc = '<path d="M12 3v18M6 7h12M8 15h8"/>';
ICONS.alignright = '<path d="M20 3v18M6 7h10M10 15h6"/>';
ICONS.aligntop = '<path d="M3 4h18M7 8v10M15 8v6"/>';
ICONS.alignvc = '<path d="M3 12h18M7 6v12M15 8v8"/>';
ICONS.alignbottom = '<path d="M3 20h18M7 6v10M15 10v6"/>';
ICONS.distx = '<rect x="3" y="8" width="4" height="8"/><rect x="10" y="8" width="4" height="8"/><rect x="17" y="8" width="4" height="8"/>';
ICONS.disty = '<rect x="8" y="3" width="8" height="4"/><rect x="8" y="10" width="8" height="4"/><rect x="8" y="17" width="8" height="4"/>';
ICONS.plusc = '<circle cx="12" cy="12" r="9"/><path d="M12 8v8M8 12h8"/>';
ICONS.plus = '<path d="M12 5v14M5 12h14"/>';
ICONS.star = '<path d="m12 3 2.7 5.6 6.2.9-4.5 4.3 1 6.1L12 17l-5.5 2.9 1-6.1L3 9.5l6.2-.9z"/>';
ICONS.user = '<circle cx="12" cy="8" r="4"/><path d="M4 21a8 8 0 0 1 16 0"/>';
ICONS.users = '<circle cx="9" cy="8" r="3"/><circle cx="17" cy="9" r="2.5"/><path d="M3 20a6 6 0 0 1 12 0M14 20a4 4 0 0 1 7 0"/>';
ICONS.list = '<path d="M9 6h11M9 12h11M9 18h11M4 6h.01M4 12h.01M4 18h.01"/>';
ICONS.eyedrop = '<path d="m2 22 1-1h3l9-9"/><path d="M3 21v-3l9-9"/><path d="m15 6 3.4-3.4a2.1 2.1 0 1 1 3 3L18 9l.4.4a2.1 2.1 0 1 1-3 3l-3.8-3.8a2.1 2.1 0 1 1 3-3l.4.4Z"/>';
ICONS.zoom = '<circle cx="11" cy="11" r="7"/><path d="m20 20-4-4M8 11h6M11 8v6"/>';
ICONS.arrowr = '<path d="M5 12h14M13 6l6 6-6 6"/>';
const HEAD_GLYPH = { none: '', arrow: '➤', open: '›', filled: '▶', diamond: '◇', dot: '●', crow: '⋔', one: '‖' };
const HEADS = [['none', 'None'], ['arrow', 'Arrow'], ['open', 'Open'], ['filled', 'Filled'], ['diamond', 'Diamond'], ['dot', 'Dot'], ['crow', 'Many (crow\'s foot)'], ['one', 'One']];
const icon = (name, cls = 'wb-i') => `<svg class="${cls}" viewBox="0 0 24 24" aria-hidden="true">${ICONS[name] || ''}</svg>`;

const SIZES = [['S', 14], ['M', 18], ['L', 26], ['XL', 40]];
// Colours (grill round 2): 8 bright + 8 soft; "auto" = black on a light board, white on a dark one.
const BRIGHT = ['auto', '#FF3B30', '#FF9500', '#FFCC00', '#34C759', '#30B0C7', '#0A84FF', '#AF52DE'];
const SOFT = ['#8E8E93', '#FFB3AE', '#FFD29A', '#FFE88A', '#A8E6B4', '#A6E3EC', '#A9CFFF', '#DCC1F2'];
const ALL_COLOURS = [...BRIGHT, ...SOFT];
// Pen types: sizes are the S/M/L/XL presets in screen px at 100 %; the slider goes 1–40.
// Round 6: nine pens, each with its own silhouette in the bar (svg(colour) in a 28×44 box), its own stroke and its own sizes.
const WOOD = '#E9C78E';
const PEN_TYPES = {
  pen: { label: 'Pen', sub: 'Pressure, like a ballpoint', sizes: [2, 3, 5, 8], size: 3, color: 'auto',
    svg: c => `<path class="nb" d="M10 44V17l4-11 4 11v27z"/><path d="M10 17h8" class="nl"/><path d="m12.4 10.4 1.6-4.4 1.6 4.4z" fill="${c}"/><rect class="nb2" x="10" y="28" width="8" height="3"/>` },
  fine: { label: 'Fineliner', sub: 'Thin, even line for writing', sizes: [1, 1.5, 2.5, 4], size: 1.5, color: 'auto',
    svg: c => `<path class="nb" d="M11 44V20l3-6 3 6v24z"/><rect x="13.2" y="3" width="1.6" height="11.5" rx=".6" fill="${c}"/><rect x="11" y="32" width="6" height="12" fill="${c}" opacity=".85"/>` },
  marker: { label: 'Marker', sub: 'Bold and round, for headings', sizes: [4, 6, 9, 14], size: 6, color: '#0A84FF',
    svg: c => `<path class="nb" d="M7 44V21q0-3 3-3h8q3 0 3 3v23z"/><path d="M9.5 18v-6q0-5 4.5-5t4.5 5v6z" fill="${c}"/><rect x="7" y="30" width="14" height="5" fill="${c}"/>` },
  highlighter: { label: 'Highlighter', sub: 'See-through, goes under text', sizes: [8, 12, 18, 26], size: 12, color: '#FFD60A',
    svg: c => `<path class="nb" d="M6 44V22q0-2 2-2h12q2 0 2 2v22z"/><path d="M9 20V9l10-5v16z" fill="${c}" opacity=".85"/><path d="M9 20V9l10-5v16z" fill="none" class="nl"/><rect x="6" y="31" width="16" height="4" fill="${c}" opacity=".85"/>` },
  pencil: { label: 'Pencil', sub: 'Soft and grainy, for sketching', sizes: [1.5, 2.5, 4, 6], size: 2.5, color: 'auto',
    svg: c => `<path d="M10 44V18h8v26z" fill="#F4C430" class="nl"/><path d="M12.7 18v26M15.3 18v26" stroke="rgba(0,0,0,.12)" stroke-width=".6"/><path d="M10 18l4-12 4 12z" fill="${WOOD}" class="nl"/><path d="m12.6 10.2 1.4-4.2 1.4 4.2z" fill="${c}"/>` },
  brush: { label: 'Brush', sub: 'Tapered ends, painterly', sizes: [4, 7, 11, 16], size: 7, color: '#FF2D55',
    svg: c => `<rect class="nb2" x="12" y="24" width="4" height="20" rx="1.5"/><rect x="11" y="17" width="6" height="8" rx="1" fill="#B8B8BE" class="nl"/><path d="M14 2c-3.5 5-4 10-3 15h6c1-5 .5-10-3-15z" fill="${c}"/>` },
  callig: { label: 'Calligraphy', sub: 'Thick and thin by direction', sizes: [4, 6, 9, 13], size: 6, color: 'auto',
    svg: c => `<path class="nb" d="M10 44V18h8v26z"/><path d="M10 18 14 3l4 15z" fill="${c}"/><path d="M14 8v8" stroke="var(--wb-nib)" stroke-width=".8"/><circle cx="14" cy="15.5" r="1" fill="var(--wb-nib)"/>` },
  crayon: { label: 'Crayon', sub: 'Waxy texture, fun for workshops', sizes: [4, 7, 10, 14], size: 7, color: '#FF9500',
    svg: c => `<path d="M9 44V18h10v26z" fill="${c}"/><rect class="nb" x="9" y="24" width="10" height="14"/><path d="M11 29h6M11 33h4" stroke="${c}" stroke-width="1"/><path d="M9 18l2-9h6l2 9z" fill="${c}"/><path d="M11 9q3-3 6 0" fill="${c}"/>` },
  neon: { label: 'Neon', sub: 'Glowing line for presenting', sizes: [2, 3, 5, 8], size: 3, color: '#30D5FF',
    svg: c => `<path class="nb" d="M9 44V20q0-2 2-2h6q2 0 2 2v24z"/><circle cx="14" cy="10" r="5" fill="${c}" opacity=".35"/><path d="M11.5 18v-6q0-3 2.5-3t2.5 3v6z" fill="${c}"/><rect x="9" y="30" width="10" height="3" fill="${c}"/>` },
};
const ERASER_SVG = `<rect class="nb" x="8" y="22" width="12" height="22" rx="1"/><rect x="8" y="8" width="12" height="15" rx="3" fill="#FF9EB0" class="nl"/><rect x="8" y="20" width="12" height="4" fill="#5E8DE6"/>`;
const PEN_ORDER = ['pen', 'fine', 'marker', 'highlighter', 'pencil', 'brush', 'callig', 'crayon', 'neon'];
const HL_COLOURS = ['#FFD60A', '#A8F07A', '#FF8FB1', '#7FD3FF', '#FFB35C', '#C7A6FF'];
// the 12 colours shown in the pen bar (the rest are behind ⊕)
const BAR_COLOURS = ['#FF3B30', '#FF2D55', '#FF9500', '#FFCC00', '#34C759', '#A8E6B4', '#0A84FF', '#A9CFFF', '#5E5CE6', '#C3B4F5', '#8E8E93', 'auto'];
const ERASER_SIZES = [8, 16, 28, 44];
const SIZE_NAMES = ['Small', 'Medium', 'Large', 'Extra large'];
const PREFS_KEY = 'ipop.wb.prefs';
/// Per person, per browser: the last pen, sizes, colours. Never shared, so a broken entry only resets the defaults.
function loadPrefs() {
  const base = { ptype: 'pen', size: { eraser: 16 }, color: {}, smooth: 50, erMode: 'whole', snap: true, recent: [], opacity: 100, arrowEnd: false, dashed: false };
  for (const [k, v] of Object.entries(PEN_TYPES)) { base.size[k] = v.size; base.color[k] = v.color; }
  try {
    const s = JSON.parse(localStorage.getItem(PREFS_KEY) || 'null');
    if (s && typeof s === 'object') {
      if (PEN_TYPES[s.ptype]) base.ptype = s.ptype;
      for (const k of Object.keys(base.size)) if (Number.isFinite(s.size?.[k])) base.size[k] = Math.max(1, Math.min(k === 'eraser' ? 80 : 40, s.size[k]));
      for (const k of Object.keys(PEN_TYPES)) if (typeof s.color?.[k] === 'string' && /^(auto|#[0-9a-f]{6})$/i.test(s.color[k])) base.color[k] = s.color[k];
      if (Number.isFinite(s.smooth)) base.smooth = Math.max(0, Math.min(100, s.smooth));
      if (s.erMode === 'part') base.erMode = 'part';
      if (s.snap === false) base.snap = false;
      if (Array.isArray(s.recent)) base.recent = s.recent.filter(c => /^#[0-9a-f]{6}$/i.test(c)).slice(0, 8);
      if (s.arrowEnd === true) base.arrowEnd = true;
      if (s.dashed === true) base.dashed = true;
    }
  } catch { /* private window or bad entry: defaults */ }
  return base;
}
function savePrefs(p) { try { localStorage.setItem(PREFS_KEY, JSON.stringify(p)); } catch { /* blocked */ } }
const isHex = c => /^#[0-9a-f]{6}$/i.test(c || '');
const WHOLE = '__board';   // the slide id of "the whole board" when a board has no frames
const reduceMotion = () => { try { return window.matchMedia?.('(prefers-reduced-motion: reduce)').matches; } catch { return false; } };
const esc = v => String(v ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));

// Letter shortcuts like ClickUp; digits keep the old Excalidraw meaning (grill Q18).
const KEYS = { n: 'card', c: 'comment', i: 'image', v: 'select', l: 'laser', h: 'hand', p: 'pen', m: 'highlighter', e: 'eraser', s: 'sticky', t: 'text', r: 'rect', o: 'ellipse', d: 'diamond', a: 'connector', f: 'frame' };
// (Z is handled on its own: tap = the zoom tool, hold = zoom for a moment)
const DIGITS = { 0: 'eraser', 1: 'select', 2: 'rect', 3: 'diamond', 4: 'ellipse', 5: 'connector', 6: 'connector', 7: 'pen', 8: 'text', 9: 'select' };
const SHAPE_LABEL = { rect: 'Rectangle', ellipse: 'Circle', diamond: 'Diamond', triangle: 'Triangle', star: 'Star', cloud: 'Cloud', arrowShape: 'Block arrow', cylinder: 'Cylinder', bubble: 'Speech bubble', ...Object.fromEntries(STENCILS.map(s => [s[0], s[1]])) };
const BASIC_TIPS = { rect: 'A plain box.', ellipse: 'A circle or oval.', diamond: 'A diamond.', triangle: 'A triangle.', star: 'A star.', cloud: 'A cloud.', arrowShape: 'A big arrow.', cylinder: 'A cylinder.', bubble: 'A speech bubble.' };
const ICON_SHAPES = ['rect', 'ellipse', 'diamond', 'triangle', 'star', 'cloud', 'arrowShape', 'cylinder', 'bubble'];
const MAX_ELEMENTS = 20000;

const el = (tag, cls, html) => { const e = document.createElement(tag); if (cls) e.className = cls; if (html != null) e.innerHTML = html; return e; };
let clipboard = null;   // copy / paste between boards on the same page (the system clipboard also gets a JSON copy)

/// opts: { ymap, doc, readOnly, theme, mode: 'block'|'board', onLinkOpen(url), onChange(count), toast(msg), onFullscreen() }
export function mountCanvas(container, opts) {
  const { ymap, doc, Y } = opts;
  if (!Y?.UndoManager) throw new Error('mountCanvas needs the page\'s Yjs (opts.Y)');
  const mode = opts.mode || 'block';
  let readOnly = !!opts.readOnly, theme = opts.theme || 'light', destroyed = false;
  const ORIGIN = {};
  const undo = new Y.UndoManager(ymap, { trackedOrigins: new Set([ORIGIN]), captureTimeout: 500 });

  // ── DOM ──────────────────────────────────────────────────────────────────
  const root = el('div', `wb wb-${mode}`);
  root.tabIndex = 0;
  root.setAttribute('role', 'application');
  root.setAttribute('aria-label', mode === 'board' ? 'Whiteboard' : 'Drawing');
  const svg = svgEl('svg', { class: 'wb-svg' });
  defs(svg);
  const world = svgEl('g', { class: 'wb-world' }, svg);
  const overlay = svgEl('g', { class: 'wb-overlay' }, svg);
  root.append(svg);
  const toolbar = el('div', 'wb-tools'); toolbar.setAttribute('role', 'toolbar'); toolbar.setAttribute('aria-label', 'Drawing tools');
  const props = el('div', 'wb-props'); props.hidden = true;
  const zoomBar = el('div', 'wb-zoom');
  const shapeMenu = el('div', 'wb-menu wb-shapemenu'); shapeMenu.hidden = true;
  const keysPanel = el('div', 'wb-panel wb-keys'); keysPanel.hidden = true;
  const neat = el('div', 'wb-neat'); neat.hidden = true;
  const toastEl = el('div', 'wb-toast');
  const topLeft = el('div', 'wb-corner wb-tl'), topRight = el('div', 'wb-corner wb-tr');
  const gallery = el('div', 'wb-panel wb-gallery'); gallery.hidden = true;
  const emptyGallery = el('div', 'wb-emptygal'); emptyGallery.hidden = true;
  const libPanel = el('div', 'wb-panel wb-lib'); libPanel.hidden = true; libPanel.setAttribute('role', 'dialog'); libPanel.setAttribute('aria-label', 'Library');
  root.append(toolbar, props, zoomBar, shapeMenu, keysPanel, neat, toastEl, topLeft, topRight, gallery, emptyGallery, libPanel);
  container.append(root);

  // ── State ────────────────────────────────────────────────────────────────
  let view = { x: 40, y: 40, z: 1 };
  let tool = 'select', lastShape = 'rect', stickyColor = 'yellow';
  const prefs = loadPrefs();
  // colour new text, shapes and connectors take: the pen's colour (null = follows the theme, like before)
  const inkNow = () => { const c = prefs.color[prefs.ptype]; return isHex(c) ? c : null; };
  const penKind = () => (tool === 'highlighter' ? 'highlighter' : prefs.ptype);
  let sel = new Set();
  const draft = new Map();       // id -> element being changed under the pointer (not yet written)
  let flushTimer = null;
  let editing = null;            // { id, ta }
  const cache = new Map();       // id -> { key, node }
  // round 4 state, declared early: awareness events can paint the corners before the panels below exist
  let sinceLast = null, r4Kind = null, r4State = {}, versionView = null;
  const photoSeen = new Set();
  // Data graphics rules are stored once per board (Y.Map "wbmeta:<board>"), so everyone sees the same colours.
  const meta = doc?.getMap ? doc.getMap(`wbmeta:${opts.boardKey || 'board'}`) : null;
  const rulesNow = () => { const r = meta?.get('graphics'); return r && typeof r === 'object' ? r : { show: 'off', lateDays: 3 }; };

  const stored = id => { const v = ymap.get(id); return isElement(v) ? v : undefined; };
  const get = id => draft.get(id) ?? stored(id);
  const all = () => { const out = []; ymap.forEach((v, k) => { const d = draft.get(k); if (d) out.push(d); else if (isElement(v)) out.push(v); }); for (const [k, d] of draft) if (!ymap.has(k)) out.push(d); return out; };
  const live = () => all().filter(e => !e.isDeleted);
  const pal = () => PALETTE[theme] || PALETTE.light;
  const toast = msg => { if (opts.toast) { opts.toast(msg); return; } toastEl.textContent = msg; toastEl.classList.add('show'); clearTimeout(toast.t); toast.t = setTimeout(() => toastEl.classList.remove('show'), 2600); };

  const write = (els, { now = false } = {}) => {
    for (const e of els) draft.set(e.id, e);
    if (now) flush(); else if (!flushTimer) flushTimer = setTimeout(flush, 60);
    render();
  };
  function flush() {
    clearTimeout(flushTimer); flushTimer = null;
    if (!draft.size || destroyed) return;
    const batch = [...draft.values()];
    draft.clear();
    if (readOnly) { render(); return; }
    doc.transact(() => { for (const e of batch) ymap.set(e.id, e); }, ORIGIN);
    opts.onChange?.(count());
    try { markSaving(); } catch { /* the pill is created later in start-up */ }
  }
  const commitStop = () => { flush(); undo.stopCapturing(); };
  const count = () => { let n = 0; ymap.forEach(v => { if (isElement(v) && !v.isDeleted) n++; }); return n; };

  const onY = () => { if (!destroyed) { render(); if (r4Kind === 'report' || r4Kind === 'ipfind') { clearTimeout(onY.t); onY.t = setTimeout(() => { if (!destroyed && !r4.hidden && !r4.contains(document.activeElement)) paintPanelR4(); }, 400); } } };
  ymap.observe(onY);

  // ── Coordinates ──────────────────────────────────────────────────────────
  const rect = () => root.getBoundingClientRect();
  const toWorld = (sx, sy) => ({ x: (sx - view.x) / view.z, y: (sy - view.y) / view.z });
  const toScreen = (x, y) => ({ x: x * view.z + view.x, y: y * view.z + view.y });
  const local = e => { const r = rect(); return { sx: e.clientX - r.left, sy: e.clientY - r.top }; };

  // ── Rendering ────────────────────────────────────────────────────────────
  const aw = opts.awareness || null;          // the page's Yjs awareness (cursors, Follow, Present, timer)
  let timerEnds = 0, timerTick = 0, timerBeeped = 0;
  let interaction = null;                     // what the pointer is doing right now
  const pointers = new Map();
  let invitedFor = null, followingPresenter = null;
  // panels and live-collaboration elements: all created up front (several are touched by the first paint)
  const framesPanel = el('div', 'wb-panel wb-frames'); framesPanel.hidden = true;
  const shapePanel = el('div', 'wb-panel wb-shapepanel'); shapePanel.hidden = true; shapePanel.setAttribute('role', 'dialog'); shapePanel.setAttribute('aria-label', 'Shapes');
  const guidePanel = el('div', 'wb-panel wb-guide'); guidePanel.hidden = true; guidePanel.setAttribute('role', 'dialog'); guidePanel.setAttribute('aria-label', 'Make a flowchart');
  const checkPanel = el('div', 'wb-panel wb-check'); checkPanel.hidden = true; checkPanel.setAttribute('role', 'dialog'); checkPanel.setAttribute('aria-label', 'Check this flowchart');
  let flowOn = false;
  const HINTS_KEY = 'ipop.wb.hints';
  const timerPanel = el('div', 'wb-panel wb-timer'); timerPanel.hidden = true;
  const presBar = el('div', 'wb-presbar'); presBar.hidden = true;
  const invite = el('div', 'wb-invite'); invite.hidden = true;
  const people = el('div', 'wb-people');
  const followBar = el('div', 'wb-followbar'); followBar.hidden = true;
  root.append(framesPanel, timerPanel, presBar, invite, followBar);
  // the faces sit at the start of the top-right row, so they never cover a button
  let following = null;                        // awareness client id being followed
  let lastCursor = null, laserTrail = [], laserFade = 0;
  let presenting = null;                       // { index } while I present
  const vmap = opts.voteMap || null;           // shared voting round + votes (a Y.Map next to the scene)
  let afterPaint = () => {};   // set by the live section below (cursors, Follow)
  let raf = 0;
  function render() { if (!raf && !destroyed) raf = requestAnimationFrame(() => { raf = 0; paint(); }); }
  // Flowchart extras worked out once per paint: step numbers, which lines cross (for jumps), shapes a smart line avoids.
  let steps = new Map(), routed = new Map();
  const shapeBoxes = () => live().filter(x => isShape(x) && !isContainer(x)).map(x => ({ id: x.id, ...bounds(x, get) }));
  function paint() {
    world.setAttribute('transform', `translate(${view.x} ${view.y}) scale(${view.z})`);
    if (versionView) diffLayer.setAttribute('transform', `translate(${view.x} ${view.y}) scale(${view.z})`);
    const allEls = live();
    flowOn = hasFlowchart(allEls);
    steps = flowOn && prefs.steps !== false ? stepNumbers(allEls) : new Map();
    const boxes = flowOn || allEls.some(x => x.type === 'connector' && x.route === 'smart') ? shapeBoxes() : [];
    // the dot grid doubles its step when zoomed out, so it never turns into grey noise
    let g = 24 * view.z; if (!(g > 0.01)) g = 24; while (g < 12) g *= 2;
    root.style.backgroundSize = `${g}px ${g}px`;
    root.style.backgroundPosition = `${view.x}px ${view.y}px`;
    // hidden "How to use" notes stay on the board but are not drawn (More → Show them again)
    const els = allEls.filter(x => !x.hide);
    const rulesKey = JSON.stringify(rulesNow());
    const UNDER = x => x.type === 'frame' || isContainer(x);
    const ordered = [...els].sort((p, q) => UNDER(p) !== UNDER(q) ? (UNDER(p) ? -1 : 1) : (p.z < q.z ? -1 : p.z > q.z ? 1 : p.id < q.id ? -1 : 1));
    // a line redraws when any shape moves (its route may now go around it or jump over another line)
    const routeKey = boxes.length ? boxes.map(b => `${Math.round(b.x)},${Math.round(b.y)},${Math.round(b.w)},${Math.round(b.h)}`).join('|').length + ':' + boxes.reduce((s, b) => s + b.x * 3 + b.y * 7 + b.w + b.h, 0).toFixed(0) : '';
    routed = new Map();
    const seen = new Set();
    let prev = null;
    for (const e of ordered) {
      seen.add(e.id);
      if ((e.type === 'image' || e.type === 'file') && e.fileId && e.type === 'image') ensureFile(e.fileId);
      if (e.type === 'link') ensurePreview(e);
      if (e.type === 'sticker') ensureSticker(e.stickerId);
      if (e.type === 'orgCard' && e.photo) ensurePhoto(e.photo);
      // a connector must redraw when either shape it is attached to moves
      const deps = e.type === 'connector' ? `${get(e.from?.id)?.version ?? ''}:${get(e.from?.id)?.x ?? ''},${get(e.from?.id)?.y ?? ''}|${get(e.to?.id)?.version ?? ''}:${get(e.to?.id)?.x ?? ''},${get(e.to?.id)?.y ?? ''},${get(e.to?.id)?.w ?? ''}` : '';
      // a stroke being drawn keeps its version while points are added, so the point count is part of the key
      const key = `${e.version}:${e.versionNonce}:${theme}:${editing?.id === e.id}:${deps}:${rulesKey}:${e.type === 'callout' && e.attach ? JSON.stringify(bounds(get(e.attach) || e, get)) : ''}:${e.x},${e.y},${e.w},${e.h}:${e.points?.length ?? ''}:${steps.get(e.id) ?? ''}:${e.type === 'connector' ? routeKey : ''}`;
      let c = cache.get(e.id);
      if (!c || c.key !== key) {
        const tmp = svgEl('g');
        const node = drawElement(tmp, e, get, { theme, editingId: editing?.id, files: opts.files, cardInfo: opts.cardInfo, steps, getEl: get,
          graphics: x => graphicsFor(x, rulesNow()),
          obstaclesFor: c => boxes.filter(b => b.id !== c.from?.id && b.id !== c.to?.id),
          jumpsFor: c => [...routed.entries()].filter(([id]) => id < c.id).map(([, p]) => p),
          onRouted: (id, pts) => routed.set(id, pts) });
        if (c?.node.classList.contains('wb-lift')) node.classList.add('wb-lift');
        if (c) c.node.replaceWith(node); else (prev ? prev.after(node) : world.prepend(node));
        c = { key, node }; cache.set(e.id, c);
      }
      // keep DOM order = draw order
      if (prev ? prev.nextSibling !== c.node : world.firstChild !== c.node) { if (prev) prev.after(c.node); else world.prepend(c.node); }
      prev = c.node;
    }
    for (const [id, c] of cache) if (!seen.has(id)) { c.node.remove(); cache.delete(id); }
    for (const id of [...sel]) if (!get(id) || get(id).isDeleted) sel.delete(id);
    paintOverlay();
    paintZoom();
    paintEmpty(!els.length);
    afterPaint();
    paintFlowUi();
  }

  function paintOverlay() {
    overlay.innerHTML = '';
    const p = pal();
    // others' changes are drawn by paint(); this layer is selection, guides, marquee, previews
    if (sel.size && !editing) {
      const els = [...sel].map(get).filter(Boolean);
      const b = unionBounds(els, get);
      if (b) {
        const a = toScreen(b.x, b.y), z = toScreen(b.x + b.w, b.y + b.h);
        svgEl('rect', { x: a.x - 4, y: a.y - 4, width: z.x - a.x + 8, height: z.y - a.y + 8, fill: 'none', stroke: p.sel, 'stroke-width': 1.5, rx: 3 }, overlay);
        const one = els.length === 1 ? els[0] : null;
        const canResize = !readOnly && els.every(e => !e.locked) && !(one && (one.type === 'connector'));
        if (canResize) {
          const hs = [['nw', a.x - 4, a.y - 4], ['ne', z.x + 4, a.y - 4], ['sw', a.x - 4, z.y + 4], ['se', z.x + 4, z.y + 4]];
          if (one && one.type !== 'path') hs.push(['n', (a.x + z.x) / 2, a.y - 4], ['s', (a.x + z.x) / 2, z.y + 4], ['w', a.x - 4, (a.y + z.y) / 2], ['e', z.x + 4, (a.y + z.y) / 2]);
          for (const [h, x, y] of hs) svgEl('rect', { x: x - 5, y: y - 5, width: 10, height: 10, rx: 3, fill: '#fff', stroke: p.sel, 'stroke-width': 1.5, 'data-handle': h, class: `wb-h wb-h-${h}` }, overlay);
        }
        if (one?.type === 'connector' && !readOnly) {
          const ends = connectorEndsScreen(one);
          if (ends) for (const [k, pt] of ends) svgEl('circle', { cx: pt.x, cy: pt.y, r: 6, fill: '#fff', stroke: p.sel, 'stroke-width': 1.5, 'data-end': k, class: 'wb-h' }, overlay);
          // bends you can drag (elbow / smart lines): one handle in the middle of each inner segment
          const pts = (one.route === 'elbow' || one.route === 'smart' || one.waypoints?.length) ? connectorPoints(one, get) : null;
          if (pts && pts.length > 2) for (let i = 1; i < pts.length; i++) {
            const a = toScreen(pts[i - 1].x, pts[i - 1].y), b = toScreen(pts[i].x, pts[i].y);
            if (Math.hypot(b.x - a.x, b.y - a.y) < 26) continue;
            svgEl('rect', { x: (a.x + b.x) / 2 - 5, y: (a.y + b.y) / 2 - 5, width: 10, height: 10, rx: 5, fill: '#fff', stroke: p.sel, 'stroke-width': 1.5, 'data-seg': i, class: 'wb-h wb-seg' }, overlay);
          }
          if (one.waypoints?.length) { const c = toScreen((pts?.[0]?.x ?? 0), (pts?.[0]?.y ?? 0)); svgEl('text', { x: c.x + 10, y: c.y - 10, 'font-size': 11, fill: p.ink3, class: 'wb-reroute', 'data-reroute': '1', style: 'cursor:pointer' }, overlay).textContent = 'Reset route'; }
        }
        if (els.some(e => e.locked)) { const t = svgEl('text', { x: a.x, y: a.y - 10, 'font-size': 11, fill: p.ink3 }, overlay); t.textContent = '🔒 Locked'; }
      }
    }
    if (interaction?.marquee) {
      const m = interaction.marquee, a = toScreen(m.x, m.y), z = toScreen(m.x + m.w, m.y + m.h);
      svgEl('rect', { x: a.x, y: a.y, width: z.x - a.x, height: z.y - a.y, fill: 'rgba(10,132,255,.08)', stroke: p.sel, 'stroke-dasharray': '4 3' }, overlay);
    }
    if (interaction?.kind === 'lasso' && interaction.pts.length > 1) {
      svgEl('path', { d: `M${interaction.pts.map(([x, y]) => { const q = toScreen(x, y); return `${q.x.toFixed(1)} ${q.y.toFixed(1)}`; }).join(' L')}Z`, fill: 'rgba(10,132,255,.08)', stroke: p.sel, 'stroke-width': 1.5, 'stroke-dasharray': '4 3' }, overlay);
    }
    for (const d of interaction?.distances || []) {
      const q = toScreen(d.x, d.y), t = `${Math.round(d.gap)} px`;
      svgEl('rect', { x: q.x - (t.length * 3.4 + 6), y: q.y - 9, width: t.length * 6.8 + 12, height: 18, rx: 5, fill: '#FF2D55' }, overlay);
      const tx = svgEl('text', { x: q.x, y: q.y + 4, 'font-size': 10.5, 'font-weight': 600, fill: '#fff', 'text-anchor': 'middle', 'font-family': FONT_STACK.ipop }, overlay); tx.textContent = t;
    }
    if (flashIds.size) {
      const now = performance.now();
      for (const [id, until] of flashIds) {
        const e2 = get(id); if (!e2) continue;
        const b = bounds(e2, get), a = toScreen(b.x, b.y), z = toScreen(b.x + b.w, b.y + b.h), k = Math.max(0, (until - now) / 900);
        svgEl('rect', { x: a.x - 6 - (1 - k) * 8, y: a.y - 6 - (1 - k) * 8, width: z.x - a.x + 12 + (1 - k) * 16, height: z.y - a.y + 12 + (1 - k) * 16, rx: 10, fill: 'none', stroke: '#0A84FF', 'stroke-width': 3, opacity: (k * 0.55).toFixed(2) }, overlay);
      }
    }
    for (const l of interaction?.guideLines || []) {
      const a = toScreen(l.x1, l.y1), z = toScreen(l.x2, l.y2);
      svgEl('line', { x1: a.x, y1: a.y, x2: z.x, y2: z.y, stroke: '#FF2D55', 'stroke-width': 1, 'stroke-dasharray': '3 3' }, overlay);
    }
    if (interaction?.connectHover) {
      const t = get(interaction.connectHover);
      if (t) {
        const b = bounds(t, get), a = toScreen(b.x, b.y), z = toScreen(b.x + b.w, b.y + b.h);
        svgEl('rect', { x: a.x - 3, y: a.y - 3, width: z.x - a.x + 6, height: z.y - a.y + 6, fill: 'none', stroke: p.sel, 'stroke-width': 2, rx: 6, opacity: 0.6 }, overlay);
        // the four ports; the one the line will use is filled
        if (isShape(t)) for (const s of ['t', 'r', 'b', 'l']) { const q = portPoint(b, s), sp = toScreen(q.x, q.y); svgEl('circle', { cx: sp.x, cy: sp.y, r: s === interaction.connectSide ? 6 : 4, fill: s === interaction.connectSide ? p.sel : '#fff', stroke: p.sel, 'stroke-width': 1.5 }, overlay); }
      }
    }
    positionProps();
    afterFind?.();
  }
  function connectorEndsScreen(c) {
    const a = get(c.from?.id), b = get(c.to?.id);
    const pa = a ? centre(a, get) : c.from, pb = b ? centre(b, get) : c.to;
    if (!pa || !pb || !Number.isFinite(pa.x) || !Number.isFinite(pb.x)) return null;
    return [['from', toScreen(pa.x, pa.y)], ['to', toScreen(pb.x, pb.y)]];
  }

  // ── Toolbar ──────────────────────────────────────────────────────────────
  const compact = mode === 'block';
  const TOOLS = [
    ['select', 'Select', 'V'], ['hand', 'Hand', 'H'], '|',
    ['pen', 'Pen (click again for options)', 'P'], ['eraser', 'Eraser (click again for options)', 'E'], '|',
    ['sticky', 'Sticky note', 'S'], ['text', 'Text', 'T'], ['shape', 'Shapes', 'R'], ['connector', 'Connector', 'A'], '|', ['frame', 'Frame', 'F'], ['image', 'Picture or file', 'I'], ['comment', 'Comment', 'C'], ['emoji', 'Emoji and stickers', 'K'], ['linkcard', 'Web link card', 'U'], ['card', 'Add a Notes page or Projects task', 'N'], ['laser', 'Laser pointer', 'L'],
  ];
  // phone = a narrow screen (same breakpoint as the rest of the app: under 768 px)
  // one phone breakpoint for the whole app: ask ipop.mobile (same query as the CSS)
  const isPhone = () => { try { return !!window.ipop?.mobile?.isMobile?.(); } catch { return false; } };
  let moreOpen = false;
  let dockReady = false;   // the dock section below has run (paintToolbar is called before it during start-up)
  let tight = false;   // the board is narrower than the full tool bar
  const moreMenu = el('div', 'wb-menu wb-moremenu'); moreMenu.hidden = true;
  root.append(moreMenu);
  moreMenu.addEventListener('pointerdown', e => e.stopPropagation());
  function paintMore() {
    const items = [['shape', 'Shapes'], ['connector', 'Connector'], ['text', 'Text'], ['eraser', 'Eraser'], ['laser', 'Laser'], ...(compact ? [] : [['frame', 'Frame'], ['emoji', 'Emoji'], ...(opts.linkPreview ? [['linkcard', 'Link card']] : []), ['card', 'Add card']])]
      .filter(([k]) => !(k === 'card' && !opts.pages && !opts.taskSearch));
    moreMenu.innerHTML = items.map(([k, label]) => `<button type="button" data-more="${k}" class="wb-moreitem"><span class="wb-moreic">${k === 'shape' ? icon(lastShape) : icon(k)}</span>${label}</button>`).join('')
      + (opts.voteMap ? '<button type="button" data-more="vote" class="wb-moreitem"><span class="wb-moreic">' + icon('vote') + '</span>Vote</button>' : '');
  }
  moreMenu.addEventListener('click', e => {
    const b = e.target.closest('[data-more]'); if (!b) return;
    moreOpen = false; moreMenu.hidden = true; paintToolbar();
    const k = b.dataset.more;
    if (k === 'vote') { toggleVotePanel(); return; }
    if (k === 'shape') { setTool(lastShape); return; }
    if (k === 'emoji') { toggleEmojiPicker(toolbar); return; }
    if (k === 'linkcard') { askLinkCard(); return; }
    if (k === 'card') { toggleCardPicker(); return; }
    setTool(k);
  });
  function paintToolbar() {
    // the Whiteboard page on a wide screen uses the dock instead (round 6)
    if (dockReady && dockOn()) { toolbar.hidden = true; paintDock(); return; }
    if (dockReady) { dock.hidden = true; ctxBar.hidden = true; root.classList.remove('wb-hasdock'); }
    if (readOnly) {
      // viewers still get the hand and the laser pointer (pointing during a meeting)
      toolbar.hidden = false;
      toolbar.innerHTML = [['select', 'Select', 'V'], ['hand', 'Hand', 'H'], ...(opts.comments?.canComment?.() ? [['comment', 'Comment', 'C']] : []), ['laser', 'Laser pointer', 'L']].map(([k, label, key]) => `<button type="button" data-tool="${k}" class="${tool === k ? 'on' : ''}" title="${label} (${key})" aria-label="${label}" aria-pressed="${tool === k}">${icon(k)}<span class="wb-k">${key}</span></button>`).join('');
      return;
    }
    toolbar.hidden = false;
    // On a phone the bar keeps what a finger needs: select, hand, pen, sticky, picture, comment, and a "More" that opens the rest.
    // A board too narrow for every tool (a /draw block beside the sidebar on a small laptop) does the same, so no tool is cut off.
    const phone = isPhone() || tight;
    const PHONE_KEEP = new Set(['select', 'hand', 'pen', 'sticky', 'image', 'comment']);
    toolbar.innerHTML = TOOLS.filter(t => t === '|' ? !phone : (!phone || PHONE_KEEP.has(t[0])))
      .filter(t => !(compact && (t[0] === 'frame' || t[0] === 'highlighter')) && !(t[0] === 'image' && !opts.upload) && !(t[0] === 'comment' && !opts.comments) && !(compact && (t[0] === 'emoji' || t[0] === 'linkcard')) && !(t[0] === 'linkcard' && !opts.linkPreview) && !(t[0] === 'card' && !opts.pages && !opts.taskSearch)).map(t => {
      if (t === '|') return '<span class="wb-sep"></span>';
      const [k, label, key] = t;
      const on = k === tool || (k === 'shape' && SHAPES.includes(tool)) || (k === 'pen' && tool === 'highlighter');
      const pk = k === 'pen' ? (tool === 'highlighter' ? 'highlighter' : prefs.ptype) : null;
      const ic = k === 'sticky' ? `<span class="wb-stk" style="background:${pal().sticky[stickyColor]}"></span>`
        : k === 'shape' ? icon(lastShape)
        : pk ? `${ICONS[pk] ? icon(pk) : icon('pen')}<span class="wb-pendot" style="background:${colourCss(prefs.color[pk])}"></span>`
        : icon(k);
      return `<button type="button" data-tool="${k}" class="${on ? 'on' : ''}" title="${label} (${key})" aria-label="${label}" aria-pressed="${on}">${ic}<span class="wb-k">${key}</span></button>`;
    }).join('') + (phone ? `<button type="button" data-tool="more" class="${moreOpen ? 'on' : ''}" title="More tools" aria-label="More tools" aria-expanded="${moreOpen}">${icon('more')}</button>` : '') + (compact && opts.onPopOut ? `<span class="wb-sep"></span><button type="button" data-act="popout" title="Open in a new tab" aria-label="Open in a new tab">${icon('newtab')}</button>` : '') + (compact && opts.onFullscreen ? `<button type="button" data-act="fullscreen" title="Open full screen" aria-label="Open full screen"><svg class="wb-i" viewBox="0 0 24 24"><path d="M15 3h6v6M9 21H3v-6M21 3l-7 7M3 21l7-7"/></svg></button>` : '');
  }
  toolbar.addEventListener('pointerdown', e => e.stopPropagation());
  toolbar.addEventListener('click', e => {
    const b = e.target.closest('button'); if (!b) return;
    if (b.dataset.act === 'fullscreen') { opts.onFullscreen?.(); return; }
    if (b.dataset.act === 'popout') { opts.onPopOut?.(); return; }
    const k = b.dataset.tool;
    if (k === 'more') { moreOpen = !moreOpen; if (moreOpen) paintMore(); moreMenu.hidden = !moreOpen; paintToolbar(); return; }
    if (k === 'shape') { toggleShapeMenu(b); return; }
    if (k === 'image') { pickFiles(); return; }
    if (k === 'stamp' || k === 'emoji') { toggleEmojiPicker(b); return; }
    if (k === 'linkcard') { askLinkCard(); return; }
    if (k === 'card') { toggleCardPicker(); return; }
    if (k === 'sticky' && tool === 'sticky') stickyColor = STICKY_COLORS[(STICKY_COLORS.indexOf(stickyColor) + 1) % STICKY_COLORS.length];
    // a second click on the active pen or eraser opens its options (ClickUp-like)
    if ((k === 'pen' && (tool === 'pen' || tool === 'highlighter')) || (k === 'eraser' && tool === 'eraser')) { togglePenPop(k === 'eraser' ? 'eraser' : 'pen', b); return; }
    if (k === 'pen') { setTool(prefs.ptype === 'highlighter' ? 'highlighter' : 'pen'); return; }
    setTool(k);
  });
  // ── Pen and eraser options (popover beside the button; a bottom sheet on a phone) ─────
  const penPop = el('div', 'wb-penpop'); penPop.hidden = true; penPop.setAttribute('role', 'dialog');
  const colourPop = el('div', 'wb-colourpop'); colourPop.hidden = true; colourPop.setAttribute('role', 'dialog'); colourPop.setAttribute('aria-label', 'Colour');
  const eraserRing = el('div', 'wb-eraserring'); eraserRing.hidden = true;
  root.append(penPop, colourPop, eraserRing);
  let penSeen = false;               // a stylus touched this board: resting hands (touch) stop drawing
  let popKind = 'pen';               // pen | eraser
  const colourCss = c => (isHex(c) ? c : pal().ink);
  const swatchBtn = (c, on, extra = '') => `<button type="button" class="wb-swb ${c === 'auto' ? 'wb-auto' : c === 'none' ? 'wb-none' : ''} ${on ? 'on' : ''}" data-c="${c}" ${extra} title="${c === 'auto' ? 'Black or white (follows the theme)' : c === 'none' ? 'No fill' : c}" aria-label="${c === 'auto' ? 'Automatic black or white' : c === 'none' ? 'No fill' : `Colour ${c}`}" aria-pressed="${on}" style="${isHex(c) ? `background:${c}` : ''}"></button>`;
  function setSize(key, v) {
    prefs.size[key] = Math.max(1, Math.min(key === 'eraser' ? 80 : 40, Math.round(v)));
    savePrefs(prefs);
    // with strokes selected, the size applies to them too (ClickUp behaviour)
    if (key !== 'eraser') applyToSelection(x => x.type === 'path' ? { width: +(prefs.size[key] / view.z).toFixed(2) } : null);
    if (!penPop.hidden) paintPenPop();
    paintEraserRing();
  }
  function applyToSelection(fn) {
    if (readOnly || !sel.size) return;
    const els = [...sel].map(get).filter(x => x && !x.locked);
    const out = els.map(x => { const c = fn(x); return c ? bump(x, c) : null; }).filter(Boolean);
    if (out.length) { write(out, { now: true }); commitStop(); }
    const s = out.find(x => FLOW_TYPES.has(x.type)); if (s) lastShapeStyle = Object.fromEntries(['stroke', 'fill', 'textColor', 'strokeWidth', 'dashed', 'shadow', 'rounded', 'font'].filter(k => s[k] !== undefined).map(k => [k, s[k]]));
    const l = out.find(x => x.type === 'connector'); if (l) lastLineStyle = { stroke: l.stroke ?? null };
  }
  function togglePenPop(kind, anchor) {
    if (!penPop.hidden && popKind === kind) { penPop.hidden = true; colourPop.hidden = true; return; }
    popKind = kind; colourPop.hidden = true;
    penPop.hidden = false;
    paintPenPop();
    placePop(penPop, anchor || toolbar.querySelector(`[data-tool="${kind}"]`));
  }
  function placePop(pop, anchor) {
    pop.classList.toggle('wb-sheet', isPhone());
    if (isPhone()) { pop.style.left = ''; pop.style.bottom = ''; return; }
    const r = anchor?.getBoundingClientRect(), rr = rect();
    if (!r) return;
    const w = pop.offsetWidth;
    pop.style.left = `${Math.max(8, Math.min(r.left - rr.left + r.width / 2 - w / 2, rr.width - w - 8))}px`;
    pop.style.bottom = `${Math.max(8, rr.bottom - r.top + 10)}px`;
  }
  function paintPenPop() {
    if (popKind === 'eraser') {
      penPop.setAttribute('aria-label', 'Eraser options');
      penPop.innerHTML = `<div class="wb-pb"><div class="wb-ph5">Eraser</div><div class="wb-modes">
          <button type="button" data-em="whole" class="${prefs.erMode === 'whole' ? 'on' : ''}" aria-pressed="${prefs.erMode === 'whole'}"><span><b>Whole stroke</b><small>Touch a line to remove all of it</small></span></button>
          <button type="button" data-em="part" class="${prefs.erMode === 'part' ? 'on' : ''}" aria-pressed="${prefs.erMode === 'part'}"><span><b>Part of a stroke</b><small>Rub out only where you pass</small></span></button>
        </div></div>
        <div class="wb-pb"><div class="wb-ph5">Size</div><div class="wb-sizes">${ERASER_SIZES.map((v, i) => `<button type="button" data-esz="${v}" class="${prefs.size.eraser === v ? 'on' : ''}" title="${SIZE_NAMES[i]} · ${v} px" aria-label="${SIZE_NAMES[i]} eraser"><i class="wb-ring" style="width:${5 + i * 5}px;height:${5 + i * 5}px"></i></button>`).join('')}</div></div>`;
      return;
    }
    const t = prefs.ptype, def = PEN_TYPES[t], s = prefs.size[t];
    penPop.setAttribute('aria-label', 'Pen options');
    penPop.innerHTML = `<div class="wb-pb"><div class="wb-ph5">Pen</div><div class="wb-ptypes">${Object.entries(PEN_TYPES).map(([k, v]) => `<button type="button" data-pt="${k}" class="${t === k ? 'on' : ''}" aria-pressed="${t === k}" title="${v.sub}"><span class="wb-ptsvg"><svg viewBox="0 0 28 44" aria-hidden="true">${v.svg(colourCss(prefs.color[k]))}</svg></span><span>${v.label}</span></button>`).join('')}</div></div>
      <div class="wb-pb"><div class="wb-ph5">Size</div><div class="wb-sizes">${def.sizes.map((v, i) => `<button type="button" data-sz="${v}" class="${s === v ? 'on' : ''}" title="${SIZE_NAMES[i]} · ${v} px" aria-label="${SIZE_NAMES[i]}"><i class="wb-dotsz" style="width:${Math.min(22, 3 + v * 0.7)}px;height:${Math.min(22, 3 + v * 0.7)}px"></i></button>`).join('')}</div>
        <label class="wb-rl"><span>Custom</span><b>${s} px</b></label><input type="range" min="1" max="40" value="${s}" data-range="size" aria-label="Pen size">
        <canvas class="wb-prev" height="40" aria-hidden="true"></canvas></div>
      <div class="wb-pb"><div class="wb-rowsb"><div class="wb-ph5">Colour</div><button type="button" class="wb-morec" data-act="colours">More colours</button></div>
        <div class="wb-swatches">${BRIGHT.map(c => swatchBtn(c, prefs.color[t] === c)).join('')}</div></div>
      <div class="wb-pb"><label class="wb-rl"><span>Smoothing</span><b>${smoothLabel()}</b></label><input type="range" min="0" max="100" value="${prefs.smooth}" data-range="smooth" aria-label="Smoothing">
        <label class="wb-chk"><input type="checkbox" data-snap ${prefs.snap ? 'checked' : ''}> Hold at the end to make a neat shape</label></div>`;
    requestAnimationFrame(drawPreview);
  }
  const smoothLabel = () => prefs.smooth < 15 ? 'Off' : prefs.smooth < 40 ? 'Light' : prefs.smooth < 70 ? 'Medium' : 'Strong';
  function drawPreview() {
    const c = penPop.querySelector('.wb-prev'); if (!c) return;
    const r = c.getBoundingClientRect(); if (!r.width) return;
    const dpr = window.devicePixelRatio || 1; c.width = r.width * dpr; c.height = 40 * dpr;
    const x = c.getContext('2d'); if (!x) return; x.setTransform(dpr, 0, 0, dpr, 0, 0);
    const t = prefs.ptype, size = prefs.size[t];
    x.strokeStyle = colourCss(prefs.color[t]); x.lineCap = t === 'highlighter' ? 'butt' : 'round'; x.lineJoin = 'round';
    x.globalAlpha = (t === 'highlighter' ? 0.4 : t === 'pencil' ? 0.85 : 1) * prefs.opacity / 100;
    const n = 40;
    for (let i = 1; i < n; i++) {
      const p0 = [12 + (i - 1) * (r.width - 24) / (n - 1), 20 + Math.sin((i - 1) / 6) * 8], p1 = [12 + i * (r.width - 24) / (n - 1), 20 + Math.sin(i / 6) * 8];
      x.lineWidth = t === 'pen' ? size * (0.55 + 0.6 * Math.sin(i / n * Math.PI)) : size;
      x.beginPath(); x.moveTo(p0[0], p0[1]); x.lineTo(p1[0], p1[1]); x.stroke();
    }
  }
  penPop.addEventListener('pointerdown', e => e.stopPropagation());
  penPop.addEventListener('click', e => {
    const b = e.target.closest('button'); if (!b) return;
    if (b.dataset.pt) {
      prefs.ptype = b.dataset.pt; savePrefs(prefs);
      tool = b.dataset.pt === 'highlighter' ? 'highlighter' : 'pen'; root.dataset.tool = tool;
      paintToolbar(); paintPenPop(); root.focus({ preventScroll: true }); return;
    }
    if (b.dataset.sz) { setSize(prefs.ptype, +b.dataset.sz); return; }
    if (b.dataset.esz) { setSize('eraser', +b.dataset.esz); return; }
    if (b.dataset.em) { prefs.erMode = b.dataset.em; savePrefs(prefs); paintPenPop(); return; }
    if (b.dataset.c) { pickColour(b.dataset.c, 'pen'); return; }
    if (b.dataset.act === 'colours') { openColours('pen', b); }
  });
  penPop.addEventListener('input', e => {
    const r = e.target.closest('[data-range]'); if (!r) return;
    if (r.dataset.range === 'size') { prefs.size[prefs.ptype] = +r.value; savePrefs(prefs); penPop.querySelector('.wb-rl b').textContent = `${r.value} px`; drawPreview(); penPop.querySelectorAll('[data-sz]').forEach(x => x.classList.toggle('on', +x.dataset.sz === +r.value)); }
    if (r.dataset.range === 'smooth') { prefs.smooth = +r.value; savePrefs(prefs); r.previousElementSibling.querySelector('b').textContent = smoothLabel(); }
  });
  penPop.addEventListener('change', e => {
    if (e.target.matches('[data-range="size"]')) setSize(prefs.ptype, +e.target.value);
    if (e.target.matches('[data-snap]')) { prefs.snap = e.target.checked; savePrefs(prefs); }
  });

  // ── Colour panel: 16 colours, recent ones, any colour (wheel + hex) and opacity; Line and Fill for shapes ──
  let colourFor = 'pen', colourTab = 'stroke';
  function openColours(forWhat, anchor) {
    colourFor = forWhat;
    const els = [...sel].map(get).filter(Boolean);
    const hasFill = forWhat === 'sel' && els.some(x => isShape(x));
    if (!hasFill) colourTab = 'stroke';
    colourPop.hidden = false;
    paintColours(hasFill);
    if (forWhat === 'pen' && !isPhone()) {
      const pr = penPop.getBoundingClientRect(), rr = rect(), cw = colourPop.offsetWidth;
      colourPop.classList.remove('wb-sheet');
      colourPop.style.top = ''; colourPop.style.bottom = penPop.style.bottom;
      // beside the pen options (right, else left); with no room on either side, it replaces them
      const right = pr.right - rr.left + 10, left = pr.left - rr.left - cw - 10;
      if (right + cw <= rr.width - 8) colourPop.style.left = `${right}px`;
      else if (left >= 8) colourPop.style.left = `${left}px`;
      else { colourPop.style.left = penPop.style.left; penPop.hidden = true; }
    } else if (forWhat === 'sel' && !isPhone()) {
      colourPop.classList.remove('wb-sheet');
      const r = anchor.getBoundingClientRect(), rr = rect();
      colourPop.style.bottom = '';
      const top = r.bottom - rr.top + 8;
      colourPop.style.top = `${top + colourPop.offsetHeight > rr.height - 8 ? Math.max(8, r.top - rr.top - colourPop.offsetHeight - 8) : top}px`;
      colourPop.style.left = `${Math.max(8, Math.min(r.left - rr.left - 20, rr.width - colourPop.offsetWidth - 8))}px`;
    } else colourPop.classList.add('wb-sheet');
  }
  function currentColour() {
    if (colourFor === 'pen') return prefs.color[prefs.ptype];
    const els = [...sel].map(get).filter(Boolean);
    const s = colourTab === 'fill' ? els.find(x => isShape(x)) : els[0];
    if (!s) return 'auto';
    if (colourTab === 'fill') return s.fill === 'none' ? 'none' : (s.fill || 'auto');
    return s.stroke || 'auto';
  }
  function currentOpacity() {
    if (colourFor === 'pen') return prefs.opacity;
    const s = [...sel].map(get).find(Boolean);
    return Math.round((s?.opacity ?? 1) * 100);
  }
  function paintColours(hasFill = colourPop.querySelector('[data-ct]') != null) {
    const cur = currentColour();
    const list = colourTab === 'fill' ? ['none', ...ALL_COLOURS.slice(1)] : ALL_COLOURS;
    const hex = isHex(cur) ? cur.toUpperCase() : '#000000';
    colourPop.innerHTML = `${hasFill ? `<div class="wb-seg wb-ctabs" role="tablist"><button type="button" role="tab" data-ct="stroke" class="${colourTab === 'stroke' ? 'on' : ''}" aria-selected="${colourTab === 'stroke'}">Line</button><button type="button" role="tab" data-ct="fill" class="${colourTab === 'fill' ? 'on' : ''}" aria-selected="${colourTab === 'fill'}">Fill</button></div>` : ''}
      <div class="wb-ph5">Colours</div><div class="wb-swatches">${list.map(c => swatchBtn(c, cur === c)).join('')}</div>
      <div class="wb-ph5">Recent</div><div class="wb-recent">${prefs.recent.length ? prefs.recent.map(c => swatchBtn(c, cur === c)).join('') : '<span class="wb-note">Colours you pick show up here</span>'}</div>
      <div class="wb-ph5">Custom</div><div class="wb-custom"><input type="color" value="${hex}" data-picker aria-label="Pick any colour"><input type="text" value="${hex}" maxlength="7" spellcheck="false" data-hex aria-label="Hex code"></div>
      <label class="wb-rl"><span>Opacity</span><b>${currentOpacity()}%</b></label><input type="range" min="10" max="100" value="${currentOpacity()}" data-range="opacity" aria-label="Opacity">`;
  }
  function pickColour(c, forWhat = colourFor) {
    if (isHex(c)) { prefs.recent = [c.toUpperCase(), ...prefs.recent.filter(x => x.toUpperCase() !== c.toUpperCase())].slice(0, 8); }
    if (forWhat === 'pen') {
      prefs.color[prefs.ptype] = c === 'none' ? 'auto' : c;
      savePrefs(prefs);
      applyToSelection(x => x.type === 'path' ? { stroke: isHex(c) ? c : null } : null);
      paintToolbar(); if (!penPop.hidden) paintPenPop();
    } else {
      savePrefs(prefs);
      if (colourTab === 'fill') applyToSelection(x => isShape(x) ? { fill: c === 'auto' ? null : c } : null);
      else applyToSelection(x => x.type === 'sticky' || x.type === 'frame' || x.type === 'image' || x.type === 'sticker' ? null : { stroke: isHex(c) ? c : null, ...(isShape(x) ? { textColor: isHex(c) ? c : null } : {}) });
      paintProps();
    }
    if (!colourPop.hidden) paintColours();
  }
  colourPop.addEventListener('pointerdown', e => e.stopPropagation());
  colourPop.addEventListener('click', e => {
    const t = e.target.closest('[data-ct]'); if (t) { colourTab = t.dataset.ct; paintColours(true); return; }
    const b = e.target.closest('[data-c]'); if (b) pickColour(b.dataset.c);
  });
  colourPop.addEventListener('change', e => {
    if (e.target.matches('[data-picker]')) pickColour(e.target.value.toUpperCase());
    if (e.target.matches('[data-range="opacity"]')) {
      const v = +e.target.value;
      if (colourFor === 'pen') { prefs.opacity = v; savePrefs(prefs); }
      else applyToSelection(() => ({ opacity: v >= 100 ? undefined : v / 100 }));
    }
  });
  colourPop.addEventListener('input', e => { if (e.target.matches('[data-range="opacity"]')) e.target.previousElementSibling.querySelector('b').textContent = `${e.target.value}%`; });
  colourPop.addEventListener('keydown', e => {
    e.stopPropagation();
    if (e.key === 'Escape') { colourPop.hidden = true; root.focus({ preventScroll: true }); return; }
    if (e.key === 'Enter' && e.target.matches('[data-hex]')) { let v = e.target.value.trim(); if (!v.startsWith('#')) v = `#${v}`; if (isHex(v)) pickColour(v.toUpperCase()); else e.target.select(); }
  });
  // Esc closes the options wherever focus is (a button inside them, the page, the board)
  const popKeys = e => {
    // Esc closes the open pop-up wherever focus is: after clicking a chip the chip has focus, not the board
    const mine = t => t === document.body || root.contains(t);
    if (e.key === 'Escape' && dockReady && !tcOverlay.hidden) { e.stopPropagation(); e.preventDefault(); const bp = tc.querySelector('.wb-bp'); if (bp && !bp.hidden) { tcs.bp = null; bp.hidden = true; } else closeCenter(); return; }
    if (e.key === 'Escape' && dockReady && (!ctxPop.hidden || !picker.hidden || !morePanel.hidden) && mine(e.target) && !e.target.closest?.('textarea, [contenteditable="true"], .wb-edit')) { e.stopPropagation(); ctxPop.hidden = true; picker.hidden = true; morePanel.hidden = true; paintDock(); root.focus({ preventScroll: true }); return; }
    if (e.key === 'Escape' && penPop.hidden && colourPop.hidden && mine(e.target) && !e.target.closest?.('textarea, [contenteditable="true"], .wb-edit')) {
      if (!propsMore.hidden) { e.stopPropagation(); propsMore.hidden = true; root.focus({ preventScroll: true }); return; }
      if (!gallery.hidden) { e.stopPropagation(); gallery.hidden = true; root.focus({ preventScroll: true }); return; }
      if (!shapePanel.hidden && !shapePanel.contains(e.target)) { e.stopPropagation(); shapePanel.hidden = true; shapeTip.hidden = true; paintCorners(); root.focus({ preventScroll: true }); return; }
    }
    // a round-4 side panel closes on Esc wherever focus is (also from inside its own select boxes)
    if (e.key === 'Escape' && penPop.hidden && colourPop.hidden && !r4.hidden && (r4.contains(e.target) || e.target === document.body || root.contains(e.target))) { e.stopPropagation(); r4.hidden = true; clearVersionView(); root.focus({ preventScroll: true }); return; }
    if (e.key !== 'Escape' || (penPop.hidden && colourPop.hidden)) return;
    if (e.target?.closest?.('.wb-colourpop') && !colourPop.hidden && !penPop.hidden) { e.stopPropagation(); colourPop.hidden = true; return; }
    e.stopPropagation(); e.preventDefault(); penPop.hidden = true; colourPop.hidden = true; root.focus({ preventScroll: true });
  };
  document.addEventListener('keydown', popKeys, true);

  // the eraser shows its size under the pointer, so you know what it will remove
  function paintEraserRing(sx, sy) {
    if (tool !== 'eraser') { eraserRing.hidden = true; return; }
    const d = prefs.size.eraser;
    eraserRing.style.width = eraserRing.style.height = `${d}px`;
    if (sx != null) { eraserRing.style.left = `${sx - d / 2}px`; eraserRing.style.top = `${sy - d / 2}px`; eraserRing.hidden = false; }
  }
  root.addEventListener('pointermove', e => { if (tool === 'eraser' && e.pointerType !== 'touch') { const { sx, sy } = local(e); paintEraserRing(sx, sy); } });
  root.addEventListener('pointerleave', () => { eraserRing.hidden = true; });

  function toggleShapeMenu(anchor) {
    if (!shapeMenu.hidden) { shapeMenu.hidden = true; return; }
    shapeMenu.innerHTML = SHAPES.map(s => `<button type="button" data-shape="${s}" class="${s === lastShape ? 'on' : ''}" title="${SHAPE_LABEL[s]}" aria-label="${SHAPE_LABEL[s]}">${icon(s)}</button>`).join('');
    shapeMenu.hidden = false;
    const r = anchor.getBoundingClientRect(), rr = rect();
    shapeMenu.style.left = `${Math.max(8, Math.min(r.left - rr.left + r.width / 2 - shapeMenu.offsetWidth / 2, rr.width - shapeMenu.offsetWidth - 8))}px`;
    shapeMenu.style.bottom = `${rr.bottom - r.top + 8}px`;
  }
  shapeMenu.addEventListener('pointerdown', e => e.stopPropagation());
  shapeMenu.addEventListener('click', e => { const b = e.target.closest('[data-shape]'); if (!b) return; lastShape = b.dataset.shape; shapeMenu.hidden = true; setTool(lastShape); });

  function setTool(t) {
    if (t === 'pen' && prefs.ptype === 'highlighter') t = 'highlighter';
    if (t === 'highlighter') { prefs.ptype = 'highlighter'; savePrefs(prefs); }
    else if (t === 'pen' && prefs.ptype === 'highlighter') { prefs.ptype = 'pen'; savePrefs(prefs); }
    tool = t;
    if (t !== 'select') { sel.clear(); }
    root.dataset.tool = SHAPES.includes(t) ? 'shape' : t;
    if (typeof brushRing !== 'undefined' && t !== 'pen' && t !== 'highlighter') brushRing.hidden = true;
    shapeMenu.hidden = true;
    if (t !== 'pen' && t !== 'highlighter' && t !== 'eraser') penPop.hidden = true;
    eraserRing.hidden = t !== 'eraser';
    if (t === 'pen' || t === 'highlighter' || t === 'eraser') dockKind = 'pen';
    else if (SHAPES.includes(t) && dockKind !== 'flow') dockKind = 'shape';
    else if (!['select', 'hand', 'laser', 'comment'].includes(t) && dockKind !== 'flow') dockKind = t;
    if (t === 'select' && dockKind === 'pen') dockKind = null;
    paintToolbar(); render();
    if (typeof paintDock === 'function') paintDock();
    paintEmpty(count() === 0);
  }

  // ══ Round 6: the dock, one contextual bar, Task / Pages picker, the More panel and the Template Center ══════════
  // The dock (big picture tiles like ClickUp) is used on the Whiteboard page, full screen and the pop-out tab; a /draw block
  // and a phone keep the small icon bar. The bar above the dock follows the active tool or what is selected; it replaces the
  // floating selection bar and the pen popover on the dock (they stay for the small bar).
  // the dock needs about 940 px; a narrower board keeps the small bar (which itself goes to "More" when tight)
  // narrower than ~1060 px the tiles shrink (a laptop with both sidebars open is about 880 px); under 720 px the small bar is back
  const dockOn = () => mode === 'board' && !readOnly && !isPhone() && !tight && root.clientWidth >= 720;
  const dock = el('div', 'wb-dock'); dock.setAttribute('role', 'toolbar'); dock.setAttribute('aria-label', 'Tools'); dock.hidden = true;
  const ctxBar = el('div', 'wb-ctx'); ctxBar.setAttribute('role', 'toolbar'); ctxBar.setAttribute('aria-label', 'Options'); ctxBar.hidden = true;
  const ctxPop = el('div', 'wb-ctxpop'); ctxPop.hidden = true; ctxPop.setAttribute('role', 'dialog');
  const picker = el('div', 'wb-picker'); picker.hidden = true; picker.setAttribute('role', 'dialog'); picker.setAttribute('aria-label', 'Add a task or a page');
  const morePanel = el('div', 'wb-morep'); morePanel.hidden = true; morePanel.setAttribute('role', 'dialog'); morePanel.setAttribute('aria-label', 'More tools');
  const dockWrap = el('div', 'wb-dockwrap');
  dockWrap.append(ctxPop, picker, morePanel, ctxBar, dock);
  root.append(dockWrap);
  for (const n of [dockWrap]) n.addEventListener('pointerdown', e => e.stopPropagation());
  const tcOverlay = el('div', 'wb-tcov'); tcOverlay.hidden = true;
  const tc = el('div', 'wb-tc'); tc.setAttribute('role', 'dialog'); tc.setAttribute('aria-label', 'Template Center'); tc.setAttribute('aria-modal', 'true');
  tcOverlay.append(tc);
  root.append(tcOverlay);
  tcOverlay.addEventListener('pointerdown', e => { e.stopPropagation(); if (e.target === tcOverlay) closeCenter(); });

  // tile pictures (ClickUp-like): drawn here so they follow the theme through CSS variables
  const ART = {
    card: `<svg width="56" height="46" viewBox="0 0 56 46"><rect x="4" y="8" width="48" height="44" rx="6" class="wb-a-card"/><circle cx="12" cy="17" r="3.2" fill="none" stroke="#86868B" stroke-width="1.4"/><path d="m10.6 17 1 1 2-2" stroke="#86868B" stroke-width="1.2" fill="none"/><text x="18" y="20" font-size="8" font-weight="600" class="wb-a-ink">Task</text><rect x="9" y="27" width="16" height="4" rx="2" fill="#C9C9CF"/><rect x="29" y="26" width="18" height="6" rx="3" fill="#0A84FF"/><rect x="9" y="36" width="12" height="4" rx="2" fill="#C9C9CF"/></svg>`,
    shape: () => `<svg width="54" height="46" viewBox="0 0 54 46"><g transform="translate(5 10)" class="wb-a-shape">${tileShape(lastShape)}</g></svg>`,
    connector: `<svg width="40" height="50" viewBox="0 0 40 50"><path d="M20 50V10M10 20l10-10 10 10" class="wb-a-line" stroke-width="2.4" fill="none" stroke-linecap="round" stroke-linejoin="round"/></svg>`,
    sticky: () => `<svg width="56" height="48" viewBox="0 0 56 48"><path d="M8 12h40v28l-8 10H8z" fill="${pal().sticky[stickyColor] || '#FFE27A'}"/><path d="M48 40h-8v10z" fill="rgba(0,0,0,.12)"/></svg>`,
    text: `<svg width="48" height="46" viewBox="0 0 48 46"><rect x="6" y="10" width="36" height="40" rx="7" fill="#2C2C2E"/><path d="M16 20h16M24 20v18" stroke="#fff" stroke-width="3.2" stroke-linecap="round"/></svg>`,
    frame: `<svg width="54" height="46" viewBox="0 0 54 46"><rect x="16" y="8" width="18" height="7" rx="2" fill="#636366"/><rect x="5" y="14" width="44" height="36" rx="5" fill="#3A3A3C"/></svg>`,
    image: `<svg width="54" height="46" viewBox="0 0 54 46"><rect x="5" y="10" width="44" height="40" rx="5" fill="#A9D0FA" stroke="#D9C38A" stroke-width="2"/><circle cx="38" cy="20" r="4" fill="#FFD24A"/><path d="M5 44 20 28l10 10 6-6 13 12v6H5z" fill="#F2B544"/></svg>`,
    flow: `<svg width="58" height="48" viewBox="0 0 58 48"><rect x="4" y="14" width="50" height="38" rx="6" class="wb-a-card"/><circle cx="12" cy="30" r="3.5" fill="#AF52DE"/><path d="M15 30h6M31 30h4" stroke="#0A84FF" stroke-width="1.5"/><rect x="21" y="26" width="10" height="8" rx="2" fill="#0A84FF"/><path d="m40 24 5 6-5 6-5-6z" fill="#34C759"/><rect x="40" y="18" width="10" height="4" rx="2" fill="#FF2D55"/><rect x="40" y="40" width="10" height="4" rx="2" fill="#34C759"/></svg>`,
    mind: `<svg width="58" height="48" viewBox="0 0 58 48"><rect x="4" y="14" width="50" height="38" rx="6" class="wb-a-card"/><rect x="22" y="28" width="14" height="7" rx="3.5" fill="#FF9500"/><path d="M22 31c-4 0-4-8-8-8M22 31c-4 0-4 8-8 8M36 31c4 0 4-8 8-8M36 31c4 0 4 8 8 8" stroke="#86868B" fill="none"/><rect x="6" y="20" width="9" height="5" rx="2.5" fill="#0A84FF"/><rect x="6" y="37" width="9" height="5" rx="2.5" fill="#34C759"/><rect x="43" y="20" width="9" height="5" rx="2.5" fill="#AF52DE"/><rect x="43" y="37" width="9" height="5" rx="2.5" fill="#FF2D55"/></svg>`,
    tpl: `<svg width="58" height="48" viewBox="0 0 58 48"><rect x="10" y="12" width="40" height="34" rx="5" fill="#E5DBF7" transform="rotate(-8 30 30)"/><rect x="8" y="14" width="42" height="36" rx="5" class="wb-a-card"/><rect x="12" y="19" width="16" height="12" rx="2" fill="#FADDE3"/><rect x="30" y="19" width="16" height="12" rx="2" fill="#D6F1DE"/><rect x="12" y="33" width="16" height="12" rx="2" fill="#D8E7FC"/><rect x="30" y="33" width="16" height="12" rx="2" fill="#FDE3C6"/></svg>`,
    more: `<svg width="56" height="48" viewBox="0 0 56 48"><defs><linearGradient id="wb-mg" x1="0" y1="0" x2="1" y2="1"><stop offset="0" stop-color="#FF9500"/><stop offset=".5" stop-color="#FF2D55"/><stop offset="1" stop-color="#AF52DE"/></linearGradient></defs><rect x="8" y="12" width="40" height="36" rx="9" fill="url(#wb-mg)"/><circle cx="20" cy="24" r="4" fill="#fff"/><circle cx="36" cy="24" r="4" fill="#fff" opacity=".85"/><circle cx="20" cy="38" r="4" fill="#fff" opacity=".85"/><circle cx="36" cy="38" r="4" fill="#fff" opacity=".7"/></svg>`,
  };
  const tileShape = t => t === 'ellipse' ? '<ellipse cx="22" cy="20" rx="20" ry="18"/>' : t === 'diamond' ? '<path d="M22 1 43 20 22 39 1 20z"/>' : t === 'triangle' ? '<path d="M22 2 43 38H1z"/>' : '<rect x="0" y="0" width="44" height="40" rx="5"/>';
  const penSvg = (k, c) => `<svg viewBox="0 0 28 44" aria-hidden="true">${k === 'eraser' ? ERASER_SVG : PEN_TYPES[k].svg(c)}</svg>`;
  const penColourCss = k => { const c = prefs.color[k]; return isHex(c) ? c : pal().ink; };
  // the dock tiles: [key, label, shortcut]
  const TILES = () => [
    ...(opts.pages || opts.taskSearch ? [['card', 'Task or page', '⇧T']] : []),
    ['pen', 'Pen', 'P'], ['shape', 'Shape', 'R'], ['connector', 'Connector', 'A'], ['sticky', 'Sticky note', 'S'], ['text', 'Text', 'T'], ['frame', 'Frame', 'F'],
    ...(opts.upload ? [['image', 'Picture or file', 'I']] : []), ['flow', 'Flowchart', ''], ['mind', 'Mind map', ''], ['tpl', 'Templates', '⇧G'], ['more', 'More', ''],
  ];
  let dockKind = null;   // pen | shape | connector | sticky | text | frame | image | flow | mind (what the bar shows when nothing is selected)
  function dockActive(k) {
    if (k === 'card') return !picker.hidden;
    if (k === 'more') return !morePanel.hidden;
    if (k === 'pen') return tool === 'pen' || tool === 'highlighter' || tool === 'eraser';
    if (k === 'shape') return SHAPES.includes(tool);
    if (k === 'flow' || k === 'mind' || k === 'image') return dockKind === k;
    return tool === k;
  }
  function paintDock() {
    if (!dockOn()) { dock.hidden = true; ctxBar.hidden = true; ctxPop.hidden = true; root.classList.remove('wb-hasdock'); return; }
    dock.hidden = false; root.classList.add('wb-hasdock'); root.classList.toggle('wb-dock-s', root.clientWidth < 1060);
    requestAnimationFrame(layoutBars);
    const small = [['select', 'Select', 'V'], ['hand', 'Hand', 'H'], ['zoom', 'Zoom (hold Z)', 'Z'], ...(opts.comments ? [['comment', 'Comment', 'C']] : []), ['laser', 'Laser pointer', 'L']];
    const ptype = tool === 'eraser' ? 'eraser' : penKind();
    dock.innerHTML = `<div class="wb-dsmall">${small.map(([k, l, key]) => `<button type="button" data-dtool="${k}" class="${tool === k ? 'on' : ''}" title="${l} (${key})" aria-label="${l}" aria-pressed="${tool === k}">${icon(k)}<span class="wb-k">${key}</span></button>`).join('')}</div>
      <div class="wb-tiles">${TILES().map(([k, l, key]) => {
        const art = k === 'pen' ? `<span class="wb-penart">${penSvg(ptype, penColourCss(ptype === 'eraser' ? 'pen' : ptype))}</span>` : typeof ART[k] === 'function' ? ART[k]() : ART[k];
        const on = dockActive(k);
        return `<button type="button" class="wb-tile ${on ? 'on' : ''}" data-tile="${k}" title="${l}${key ? ` (${key})` : ''}" aria-label="${l}" aria-pressed="${on}"><span class="wb-tk">${key}</span><span class="wb-art">${art}</span><span class="wb-tl2">${l}</span></button>`;
      }).join('')}</div>
      <div class="wb-dur"><button type="button" data-dact="undo" title="Undo (Ctrl Z)" aria-label="Undo" ${undo.undoStack.length ? '' : 'disabled'}>${icon('undo')}</button><button type="button" data-dact="redo" title="Redo (Ctrl Y)" aria-label="Redo" ${undo.redoStack.length ? '' : 'disabled'}>${icon('redo')}</button></div>`;
    paintCtx();
  }
  // ── the contextual bar ──
  const dd = '<span class="wb-dd" aria-hidden="true">▾</span>';
  const sep = '<span class="wb-sep"></span>';
  const cb = (attrs, inner, title = '', cls = '') => `<button type="button" class="wb-cb ${cls}" ${attrs} ${title ? `title="${esc(title)}" aria-label="${esc(title)}"` : ''}>${inner}</button>`;
  const dot = c => `<span class="wb-dot" style="background:${c === 'none' ? 'transparent' : isHex(c) ? c : pal().ink}"></span>`;
  // defaults the bar sets for the next shape / connector / text (only used when nothing is selected)
  const next = { fill: null, stroke: null, font: 'ipop', size: 18, align: 'center', route: 'straight', endHead: 'arrow', startHead: 'none', dashed: false, width: 2, frameSize: '16:9', mindLayout: 'both' };
  function paintCtx() {
    if (!dockOn()) { ctxBar.hidden = true; return; }
    const h = ctxHtml();
    ctxBar.hidden = !h;
    if (ctxBar._html !== h) { ctxBar._html = h; ctxBar.innerHTML = h; fitCtx(); }
    if (!h) ctxPop.hidden = true;
  }
  /// The bar is never wider than the dock: what does not fit (from the right) goes into a ⌄ pop-up, in the same order.
  let ctxOverflow = [];
  function fitCtx() {
    const max = dock.offsetWidth; if (!max) return;
    root.style.setProperty('--wb-dockw', `${max}px`);
    ctxOverflow = [];
    const kids = [...ctxBar.children];
    if (ctxBar.scrollWidth <= max + 1) return;
    const more = el('button', 'wb-cb wb-more-ov', '⌄'); more.type = 'button'; more.dataset.cp = 'ctxov'; more.title = 'More options'; more.setAttribute('aria-label', 'More options');
    ctxBar.append(more);
    // take items off the end (never the pens or the first group) until it fits
    for (let i = kids.length - 1; i > 2 && ctxBar.scrollWidth > max + 1; i--) {
      const n = kids[i];
      if (n.classList.contains('wb-nibs')) break;
      ctxOverflow.unshift(n.outerHTML); n.remove();
    }
    // a trailing separator looks odd
    while (more.previousElementSibling?.classList.contains('wb-sep')) more.previousElementSibling.remove();
  }
  const selected = () => [...sel].map(get).filter(Boolean);
  function ctxHtml() {
    if (interaction || editing) return ctxBar._html || '';
    const els = selected();
    if (els.length) return selCtx(els);
    if (tool === 'pen' || tool === 'highlighter' || tool === 'eraser') return penCtx();
    if (SHAPES.includes(tool) && dockKind !== 'flow') return shapeCtx(null);
    if (dockKind === 'flow') return flowCtx();
    if (tool === 'connector') return connCtx(null);
    if (tool === 'sticky') return stickyCtx(null);
    if (tool === 'text') return textCtx(null);
    if (tool === 'frame') return frameCtx();
    if (dockKind === 'image') return imageCtx();
    if (dockKind === 'mind') return mindCtx();
    return '';
  }
  function penCtx() {
    const k = tool === 'eraser' ? 'eraser' : penKind();
    const pens = '<span class="wb-nibs">' + [...Object.keys(PEN_TYPES), 'eraser'].map(p => `<button type="button" class="wb-nib ${p === k ? 'on' : ''}" data-pen="${p}" title="${p === 'eraser' ? 'Eraser — whole strokes or part of a stroke' : `${PEN_TYPES[p].label} — ${PEN_TYPES[p].sub}`}" aria-label="${p === 'eraser' ? 'Eraser' : PEN_TYPES[p].label}" aria-pressed="${p === k}">${penSvg(p, penColourCss(p === 'eraser' ? 'pen' : p))}</button>`).join('') + '</span>';
    if (k === 'eraser') return pens + sep + ['whole', 'part'].map(m => cb(`data-erm="${m}"`, m === 'whole' ? 'Whole stroke' : 'Part of a stroke', '', `wb-txt ${prefs.erMode === m ? 'on' : ''}`)).join('') + sep
      + ERASER_SIZES.map((v, i) => cb(`data-esz="${v}"`, `<i class="wb-szd" style="width:${4 + i * 3.4}px;height:${4 + i * 3.4}px"></i>`, `${SIZE_NAMES[i]} · ${v} px`, prefs.size.eraser === v ? 'on' : '')).join('');
    const def = PEN_TYPES[k], cur = prefs.color[k];
    // a narrow board keeps 6 colours in the bar; the rest are one click away behind ⊕
    const cols = (k === 'highlighter' ? HL_COLOURS : BAR_COLOURS).map(c => cb(`data-pcol="${c}"`, dot(c), c === 'auto' ? 'Black or white (follows the theme)' : c, `wb-sw ${cur === c ? 'on' : ''}`)).join('') + cb('data-cp="pencolours"', icon('plusc'), 'More colours, recent, any colour');
    const sizes = def.sizes.map((v, i) => cb(`data-psz="${v}"`, `<i class="wb-szd" style="width:${4 + i * 3.4}px;height:${4 + i * 3.4}px"></i>`, `${SIZE_NAMES[i]} · ${v} px`, prefs.size[k] === v ? 'on' : '')).join('');
    return pens + sep + cols + sep + sizes + sep + (opts.prefs ? cb('data-cp="mypens"', `${icon('star')}<span>My pens</span>${dd}`, 'Your saved pens', 'wb-txt') : '') + cb('data-cp="penadv"', '⌄', 'More pen options');
  }
  const fontBtn = f => cb('data-cp="font"', `<span style="font-family:${(FONT_STACK[f || 'ipop']).replace(/"/g, "'")}">Aa</span>${dd}`, 'Font', 'wb-txt');
  const sizeName = s => (SIZES.find(([, v]) => v === s)?.[0] ? { S: 'Small', M: 'Medium', L: 'Large', XL: 'Extra large' }[SIZES.find(([, v]) => v === s)[0]] : 'Medium');
  const sizeBtn = s => cb('data-cp="size"', `${sizeName(s)}${dd}`, 'Text size', 'wb-txt');
  const alignBtn = cb('data-cp="align"', `${icon('alignhc')}${dd}`, 'Text align');
  function shapeCtx(s) {
    const row = ICON_SHAPES.map(t => cb(`data-shape="${t}"`, icon(t), SHAPE_LABEL[t], (s ? s.type === t : tool === t) ? 'on' : '')).join('') + cb('data-cp="moreshapes"', 'More shapes', 'All shapes and stencils', 'wb-txt');
    const fill = s ? (s.fill || 'auto') : (next.fill || 'auto'), line = s ? (s.stroke || 'auto') : (next.stroke || 'auto');
    return row + sep + cb('data-cp="fill"', dot(fill === 'auto' ? pal().shapeFill : fill) + dd, 'Fill') + cb('data-cp="line"', `<span class="wb-ringd" style="--c:${isHex(line) ? line : pal().ink}"></span>${dd}`, 'Line: colour, width, dashed') + fontBtn(s?.font || next.font) + sizeBtn(s?.size || next.size) + alignBtn;
  }
  function connCtx(c) {
    const route = c ? (c.route || 'straight') : next.route, head = c ? (c.endHead ?? 'arrow') : next.endHead, start = c ? (c.startHead || 'none') : next.startHead;
    const routes = [['straight', 'Straight', '<path d="M4 20 20 4"/>'], ['elbow', 'Elbow', '<path d="M4 20V10h16V4"/>'], ['curved', 'Curved', '<path d="M4 20C4 8 20 16 20 4"/>'], ['smart', 'Goes around shapes', '<path d="M4 20h6V8h10"/><rect x="11" y="12" width="5" height="5"/>']];
    return routes.map(([r, t, d]) => cb(`data-route="${r}"`, `<svg class="wb-i" viewBox="0 0 24 24">${d}</svg>`, t, route === r ? 'on' : '')).join('') + sep
      + cb('data-cp="heads"', `<span class="wb-heads">${HEAD_GLYPH[start]}─${HEAD_GLYPH[head]}</span>${dd}`, 'Arrow ends', 'wb-txt')
      + cb('data-cdash', '┄', 'Dashed', (c ? c.dashed : next.dashed) ? 'on' : '')
      + cb('data-cp="line"', `<span class="wb-ringd" style="--c:${isHex(c?.stroke ?? next.stroke) ? (c?.stroke ?? next.stroke) : pal().ink}"></span>${dd}`, 'Colour and width')
      + (c ? cb('data-act2="label"', `${icon('text')}<span>Label</span>`, 'Write on the line (double-click it too)', 'wb-txt') : '');
  }
  function stickyCtx(s) {
    const cur = s ? s.color : stickyColor;
    return STICKY_COLORS.map(c => cb(`data-stcol="${c}"`, `<span class="wb-dot wb-sq" style="background:${pal().sticky[c]}"></span>`, c[0].toUpperCase() + c.slice(1), `wb-sw ${cur === c ? 'on' : ''}`)).join('') + sep + fontBtn(s?.font) + sizeBtn(s?.size || 16)
      + (!s ? '<span class="wb-cnote">Click to place</span>' : '');
  }
  function textCtx(t) {
    return fontBtn(t?.font || 'ipop') + sizeBtn(t?.size || 20) + sep + cb('data-fmt="bold"', '<b>B</b>', 'Bold (Ctrl B)') + cb('data-fmt="italic"', '<i>I</i>', 'Italic (Ctrl I)') + cb('data-fmt="strike"', '<s>S</s>', 'Strike') + cb('data-fmt="bullets"', icon('list'), 'List') + alignBtn
      + cb('data-cp="line"', `${dot(t?.stroke || 'auto')}${dd}`, 'Text colour');
  }
  const FRAME_SIZES = [['free', 'Free', null], ['16:9', '16:9 slide', [960, 540]], ['a4', 'A4', [595, 842]], ['phone', 'Phone', [390, 844]], ['square', 'Square', [600, 600]]];
  function frameCtx() {
    return FRAME_SIZES.map(([k, l]) => cb(`data-fsz="${k}"`, l, '', `wb-txt ${next.frameSize === k ? 'on' : ''}`)).join('') + sep + cb('data-run="frames"', `${icon('frames')}<span>Frame list</span>`, '', 'wb-txt') + cb('data-run="present"', `${icon('play')}<span>Present</span>`, '', 'wb-txt wb-pri');
  }
  function imageCtx() {
    return cb('data-run="upload"', `${icon('image')}<span>Upload picture or file</span>`, '', 'wb-txt') + (opts.linkPreview ? cb('data-run="linkcard"', `${icon('linkcard')}<span>Web link card</span>`, '', 'wb-txt') : '') + cb('data-run="emoji"', `${icon('emoji')}<span>Emoji &amp; stickers</span>`, '', 'wb-txt') + '<span class="wb-cnote">or drop / paste onto the board</span>';
  }
  const FLOW_ROW = ['terminator', 'process', 'decision', 'data', 'document', 'database'];
  function flowCtx() {
    return cb('data-run="guide"', `${icon('bulb')}<span>Guide me</span>`, 'Make a flowchart step by step', 'wb-txt wb-pri') + sep
      + FLOW_ROW.map(t => cb(`data-stencil="${t}"`, shapeIcon(t), SHAPE_LABEL[t], tool === t ? 'on' : '')).join('') + cb('data-run="shapes"', 'All stencils', 'Every shape: flowchart, BPMN, ERD, network, UML…', 'wb-txt') + sep
      + cb('data-stencil="swimlane"', 'Swimlane', 'Rows for departments', 'wb-txt') + cb('data-run="import"', 'Import', 'draw.io file or Mermaid text', 'wb-txt')
      + (flowOn ? cb('data-run="check"', `${icon('check')}<span>Check</span>`, 'Missing Start, End or branches', 'wb-txt') + cb('data-run="tidy"', `${icon('tidy')}<span>Tidy</span>`, 'Line the steps up', 'wb-txt') : '') + cb('data-cp="flowadv"', '⌄', 'Flowchart options');
  }
  function mindCtx() {
    return '<span class="wb-cnote"><kbd>Tab</kbd> child · <kbd>Enter</kbd> sibling</span>' + sep + cb('data-run="mindnew"', `${icon('mind')}<span>New mind map</span>`, 'Put a main idea in the middle of the screen', 'wb-txt wb-pri')
      + [['tree', 'Tree'], ['radial', 'Radial'], ['organic', 'Organic']].map(([k, l]) => cb(`data-relayout="${k}"`, l, `Lay the mind map out as a ${l.toLowerCase()}`, 'wb-txt')).join('');
  }
  /// The bar for what is selected: the same groups as for the tool, plus the things you do with a selection.
  function selCtx(els) {
    const one = els.length === 1 ? els[0] : null;
    const types = new Set(els.map(e => e.type));
    const only = t => types.size === 1 && types.has(t);
    const locked = els.every(e => e.locked);
    let main = '';
    if (only('sticky')) main = stickyCtx(one || els[0]);
    else if (only('connector')) main = connCtx(one || els[0]);
    else if (only('text')) main = textCtx(one || els[0]);
    else if (only('path')) {
      const p = els[0], k = PEN_TYPES[p.tool] ? p.tool : 'pen';
      main = (p.tool === 'highlighter' ? HL_COLOURS : BAR_COLOURS).map(c => cb(`data-scol="${c}"`, dot(c), c, `wb-sw ${(p.stroke || 'auto') === c ? 'on' : ''}`)).join('') + cb('data-cp="selcolours"', icon('plusc'), 'More colours')
        + sep + PEN_TYPES[k].sizes.map((v, i) => cb(`data-ssz="${v}"`, `<i class="wb-szd" style="width:${4 + i * 3.4}px;height:${4 + i * 3.4}px"></i>`, `${SIZE_NAMES[i]} · ${v} px`)).join('');
    } else if (els.every(e => isShape(e))) {
      main = FLOW_TYPES.has(els[0].type) && one ? cb('data-cp="fill"', dot(one.fill || pal().shapeFill) + dd, 'Fill') + cb('data-cp="line"', `<span class="wb-ringd" style="--c:${isHex(one.stroke) ? one.stroke : pal().ink}"></span>${dd}`, 'Line') + fontBtn(one.font) + sizeBtn(one.size || 18) + alignBtn : shapeCtx(els[0]);
    } else if (els.some(e => isShape(e) || e.type === 'sticky' || e.type === 'text')) main = cb('data-cp="line"', `${dot('auto')}${dd}`, 'Colour') + fontBtn() + sizeBtn(18);
    const acts = [];
    // what a flowchart shape can do next (these were hidden in the ⋯ menu before)
    if (one && FLOW_TYPES.has(one.type)) {
      const hs = nextHints(one, live()).filter(h => h.kind !== 'info');
      if (hs.length) acts.push(cb('data-act2="next"', `${icon('arrowr')}<span>Next step</span>`, hs.map(h => h.label).join(' · '), 'wb-txt'));
      acts.push(cb('data-run="check"', `${icon('check')}<span>Check</span>`, 'Missing Start, End or branches', 'wb-txt'), cb('data-run="tidy"', `${icon('tidy')}<span>Tidy</span>`, 'Line the steps up', 'wb-txt'));
      if (!isContainer(one)) acts.push(cb('data-act2="data"', `${icon('table')}<span>Data</span>`, 'Shape data: PIC, duration, status', `wb-txt ${one.data?.length ? 'on' : ''}`));
      acts.push(cb('data-act2="theme"', '🎨', 'Diagram theme: colours and font for the whole flowchart'));
    }
    if (one?.type === 'swimlane') acts.push(cb('data-act2="laneadd"', '+ Lane', '', 'wb-txt'), cb('data-act2="laneflip"', one.orient === 'vertical' ? '⇅ Rows' : '⇄ Columns', 'Turn the lanes', 'wb-txt'));
    if (one?.type === 'listBox') acts.push(cb('data-act2="restack"', '≡ Tidy list', '', 'wb-txt'));
    if (opts.tasks?.create && els.every(x => x.type === 'sticky')) acts.push(cb('data-act2="task"', `${icon('task')}<span>Make ${els.length > 1 ? `${els.length} tasks` : 'a task'}</span>`, 'Turn into Projects tasks', 'wb-txt'));
    if (vmap && votingOn() && one && one.type !== 'connector') acts.push(cb('data-act2="vote"', '👍 Vote', 'Vote on this', 'wb-txt'));
    if (one && (isShape(one) || one.type === 'sticky') && !FLOW_TYPES.has(one.type)) acts.push(cb('data-act2="branch"', icon('mind'), 'Add a branch (Tab)'));
    if (els.length > 1) { const grouped = els.every(x => x.groupId && x.groupId === els[0].groupId); acts.push(cb(`data-act2="${grouped ? 'ungroup' : 'group'}"`, `${icon(grouped ? 'ungroup' : 'group')}<span>${grouped ? 'Ungroup' : 'Group'}</span>`, grouped ? 'Ungroup (Ctrl Shift G)' : 'Group (Ctrl G)', 'wb-txt'), cb('data-act2="arrange"', `${icon('arrange')}<span>Arrange</span>`, 'Align and space evenly', 'wb-txt')); }
    const tail = [];
    if (one && one.type !== 'connector') tail.push(cb('data-act2="link"', icon('link'), one.link ? 'Change or remove the link' : 'Link to a web page or a Notes page (Ctrl K)', one.link ? 'on' : ''));
    if (one && opts.comments?.canComment?.()) tail.push(cb('data-act2="comment"', icon('comment'), 'Comment on this'));
    tail.push(cb('data-act2="lock"', icon(locked ? 'unlock' : 'lock'), locked ? 'Unlock' : 'Lock', locked ? 'on' : ''), cb('data-act2="dup"', icon('copy'), 'Duplicate (Ctrl D)'), cb('data-cp="selmore"', icon('more'), 'More: copy style, arrange, library…'), cb('data-act2="del"', icon('trash'), 'Delete (Del)', 'wb-danger'));
    return [main, acts.join(''), tail.join('')].filter(Boolean).join(sep);
  }
  // ── small pop-ups above the bar ──
  function openCtxPop(kind, anchor) {
    if (!ctxPop.hidden && ctxPop.dataset.kind === kind) { ctxPop.hidden = true; return; }
    const html = ctxPopHtml(kind);
    if (html == null) return;
    ctxPop.dataset.kind = kind; ctxPop.innerHTML = html; ctxPop.hidden = false;
    const r = anchor.getBoundingClientRect(), wr = dockWrap.getBoundingClientRect();
    ctxPop.style.left = `${Math.max(0, Math.min(r.left - wr.left - 16, wr.width - ctxPop.offsetWidth))}px`;
    if (kind === 'pencolours' || kind === 'selcolours') requestAnimationFrame(() => ctxPop.querySelector('[data-hex]')?.select?.());
  }
  const swatchGrid = (list, cur, attr) => `<div class="wb-swgrid">${list.map(c => `<button type="button" ${attr}="${c}" class="wb-swb ${c === 'auto' ? 'wb-auto' : c === 'none' ? 'wb-none' : ''} ${cur === c ? 'on' : ''}" style="${isHex(c) ? `background:${c}` : ''}" title="${c === 'auto' ? 'Black or white' : c === 'none' ? 'No fill' : c}" aria-label="${c}"></button>`).join('')}</div>`;
  function ctxPopHtml(kind) {
    const els = selected(), one = els[0];
    switch (kind) {
      case 'ctxov': return `<div class="wb-ovrow">${ctxOverflow.filter(h => !/class="wb-sep"/.test(h)).join('')}</div>`;
      case 'pencolours': case 'selcolours': case 'fill': case 'line': {
        const forPen = kind === 'pencolours', fill = kind === 'fill';
        const cur = forPen ? prefs.color[penKind()] : fill ? (one ? (one.fill || 'auto') : (next.fill || 'auto')) : (one ? (one.stroke || 'auto') : (next.stroke || 'auto'));
        const attr = forPen ? 'data-pcol' : fill ? 'data-fillc' : 'data-linec';
        const list = fill ? ['none', ...ALL_COLOURS.slice(1)] : ALL_COLOURS;
        const hex = isHex(cur) ? cur.toUpperCase() : '#000000';
        const widths = kind === 'line' ? `<div class="wb-ph5">Width</div><div class="wb-row">${[1, 2, 4, 6, 8].map(v => `<button type="button" class="wb-cb ${(one?.strokeWidth ?? next.width) === v ? 'on' : ''}" data-lw="${v}" title="${v} px"><span class="wb-wline" style="height:${Math.max(1, v)}px"></span></button>`).join('')}</div><div class="wb-row">${['solid', 'dashed'].map(s => `<button type="button" class="wb-cb wb-txt ${((one ? one.dashed : next.dashed) ? 'dashed' : 'solid') === s ? 'on' : ''}" data-ldash="${s}">${s === 'solid' ? 'Solid' : 'Dashed'}</button>`).join('')}</div>` : '';
        return `<div class="wb-ph5">${fill ? 'Fill' : forPen ? 'Pen colour' : 'Colour'}</div>${swatchGrid(list, cur, attr)}
          <div class="wb-ph5">Recent</div>${prefs.recent.length ? swatchGrid(prefs.recent, cur, attr) : '<p class="wb-note">Colours you pick show up here</p>'}
          <div class="wb-ph5">Any colour</div><div class="wb-custom"><input type="color" value="${hex}" data-anyc="${attr}" aria-label="Pick any colour"><input type="text" value="${hex}" maxlength="7" spellcheck="false" data-hex="${attr}" aria-label="Hex code"><button type="button" class="wb-cb" data-eyedrop="${forPen ? 'pen' : fill ? 'fill' : 'line'}" title="Take a colour from the board" aria-label="Take a colour from the board">${icon('eyedrop')}</button></div>${widths}`;
      }
      case 'penadv': {
        const k = penKind(), s = prefs.size[k];
        return `<div class="wb-ph5">${esc(PEN_TYPES[k].label)} options</div>
          <label class="wb-rl"><span>Exact size</span><b>${s} px</b></label><input type="range" min="1" max="40" value="${s}" data-rng="size" aria-label="Pen size">
          <label class="wb-rl"><span>Smoothing</span><b>${smoothLabel()}</b></label><input type="range" min="0" max="100" value="${prefs.smooth}" data-rng="smooth" aria-label="Smoothing">
          <label class="wb-rl"><span>Opacity</span><b>${prefs.opacity}%</b></label><input type="range" min="10" max="100" value="${prefs.opacity}" data-rng="opacity" aria-label="Opacity">
          <label class="wb-chk"><input type="checkbox" data-pchk="snap" ${prefs.snap ? 'checked' : ''}> Hold at the end to make a neat shape</label>
          <label class="wb-chk"><input type="checkbox" data-pchk="arrowEnd" ${prefs.arrowEnd ? 'checked' : ''}> Arrow at the end of the stroke</label>
          <label class="wb-chk"><input type="checkbox" data-pchk="dashed" ${prefs.dashed ? 'checked' : ''}> Dashed stroke</label>`;
      }
      case 'mypens': {
        const list = myPens || [];
        return `<div class="wb-ph5">My pens</div><div class="wb-mypens">${list.length ? list.map((m, i) => `<div class="wb-mypen"><button type="button" data-mypen="${i}">${penSvg(PEN_TYPES[m.type] ? m.type : 'pen', isHex(m.color) ? m.color : pal().ink)}<span>${esc(m.name)}</span><small>${esc(PEN_TYPES[m.type]?.label || '')} · ${['S', 'M', 'L', 'XL'][m.size] || 'M'}</small></button><button type="button" class="wb-x" data-mypendel="${i}" aria-label="Remove ${esc(m.name)}" title="Remove">✕</button></div>`).join('') : '<p class="wb-note">Save the pen you use most, with its colour and size.</p>'}</div>
          <div class="wb-row"><input class="wb-search" data-mypenname placeholder="Name, e.g. Red check" maxlength="40" aria-label="Name for this pen"><button type="button" class="wb-cb wb-txt wb-pri" data-mypensave>Save</button></div><p class="wb-note">Saved for you, on every device.</p>`;
      }
      case 'font': return `<div class="wb-ph5">Font</div>${['ipop', 'hand', 'mono'].map(f => `<button type="button" class="wb-mrow2" data-font2="${f}" style="font-family:${FONT_STACK[f].replace(/"/g, "'")}">${f === 'ipop' ? 'iPop' : f === 'hand' ? 'Handwriting' : 'Monospace'}</button>`).join('')}`;
      case 'size': return `<div class="wb-ph5">Text size</div>${SIZES.map(([n, v]) => `<button type="button" class="wb-mrow2" data-size2="${v}">${{ S: 'Small', M: 'Medium', L: 'Large', XL: 'Extra large' }[n]}</button>`).join('')}`;
      case 'align': return `<div class="wb-ph5">Align</div><div class="wb-row">${[['left', 'alignleft'], ['center', 'alignhc'], ['right', 'alignright']].map(([a, ic]) => `<button type="button" class="wb-cb" data-align2="${a}" title="${a}">${icon(ic)}</button>`).join('')}</div>`;
      case 'heads': { const c = one?.type === 'connector' ? one : null; return `<div class="wb-ph5">Start</div><div class="wb-row wb-wrap">${HEADS.map(([k, l]) => `<button type="button" class="wb-cb wb-txt ${(c ? (c.startHead || 'none') : next.startHead) === k ? 'on' : ''}" data-head2="s:${k}" title="${l}">${HEAD_GLYPH[k] || '—'}</button>`).join('')}</div><div class="wb-ph5">End</div><div class="wb-row wb-wrap">${HEADS.map(([k, l]) => `<button type="button" class="wb-cb wb-txt ${(c ? (c.endHead ?? 'arrow') : next.endHead) === k ? 'on' : ''}" data-head2="e:${k}" title="${l}">${HEAD_GLYPH[k] || '—'}</button>`).join('')}</div>`; }
      case 'moreshapes': toggleShapePanel(); paintCorners(); return null;
      case 'flowadv': return `<div class="wb-ph5">Flowchart</div><label class="wb-chk"><input type="checkbox" data-hints ${hintsOn() ? 'checked' : ''}> Next-step hints</label><button type="button" class="wb-mrow2" data-run="tidylr">Tidy left to right</button><button type="button" class="wb-mrow2" data-act2="theme">Diagram theme…</button>`;
      case 'selmore': return `<div class="wb-ph5">Selection</div>${[['copystyle', 'Copy style (Ctrl Shift C)'], ...(styleClip ? [['pastestyle', 'Paste style (Ctrl Shift V)']] : []), ['front', 'Bring to front (Ctrl ])'], ['back', 'Send to back (Ctrl [)'], ...(opts.library ? [['lib', 'Add to my library']] : []), ...(els.length === 1 && isShape(one) && !isContainer(one) ? [['callout', 'Add a callout that follows it']] : []), ...(els.length > 1 ? [['arrange', 'Align and space evenly']] : [])].map(([k, l]) => `<button type="button" class="wb-mrow2" data-act2="${k}">${l}</button>`).join('')}`;
      default: return '';
    }
  }
  // ── bar actions ──
  const applySel = (fn, { repaint = true } = {}) => { applyToSelection(fn); if (repaint) { ctxBar._html = null; paintCtx(); } };
  function setPen(k) {
    if (k === 'eraser') { setTool('eraser'); return; }
    prefs.ptype = k; savePrefs(prefs);
    setTool(k === 'highlighter' ? 'highlighter' : 'pen');
  }
  function pickPenColour(c) {
    const k = penKind();
    if (isHex(c)) prefs.recent = [c.toUpperCase(), ...prefs.recent.filter(x => x.toUpperCase() !== c.toUpperCase())].slice(0, 8);
    prefs.color[k] = c; savePrefs(prefs);
    paintToolbar();
  }
  const runAct = (act, anchor) => {
    // the selection bar's own handler already knows how to do these: reuse it
    const map = { dup: 'dup', del: 'del', lock: 'lock', group: 'group', ungroup: 'ungroup', arrange: 'arrange', link: 'link', data: 'data', theme: 'theme', task: 'task', branch: 'branch', laneadd: 'laneadd', laneflip: 'laneflip', restack: 'restack', copystyle: 'copystyle', pastestyle: 'pastestyle', front: 'front', back: 'back', lib: 'lib', callout: 'callout' };
    if (map[act]) { const b = document.createElement('button'); b.dataset.act = map[act]; b.getBoundingClientRect = () => anchor.getBoundingClientRect(); props.dispatchEvent(new CustomEvent('wb-act', { detail: b })); ctxBar._html = null; paintCtx(); return; }
    const one = selected()[0];
    if (act === 'next' && one) { const h = nextHints(one, live()).find(x => x.kind !== 'info'); if (h) addAfter(one, h.dir || 'b', h.type, { text: h.text, label: h.branch }); return; }
    if (act === 'label' && one) { editText(one); return; }
    if (act === 'comment' && one) { const b = bounds(one, get); startComment({ x: b.x + b.w, y: b.y }, one); return; }
    if (act === 'vote' && one) { castVote(one, false); return; }
  };
  const runTool = k => {
    ctxPop.hidden = true;
    switch (k) {
      case 'upload': pickFiles(); break;
      case 'linkcard': askLinkCard(); break;
      case 'emoji': toggleEmojiPicker(dock); break;
      case 'guide': runCorner(Object.assign(document.createElement('button'), { dataset: { c: 'guide' } }), {}); break;
      case 'shapes': toggleShapePanel(); paintCorners(); break;
      case 'import': toggleImport(); break;
      case 'check': runCorner(Object.assign(document.createElement('button'), { dataset: { c: 'check' } }), {}); break;
      case 'tidy': tidyFlow('TB'); break;
      case 'tidylr': tidyFlow('LR'); break;
      case 'frames': toggleFrames(); break;
      case 'present': startPresent(); break;
      case 'mindnew': { const r = rect(), c = toWorld(r.width / 2, r.height / 2); const n = create('rect', { x: c.x - 110, y: c.y - 40, w: 220, h: 80, text: 'Main idea', fill: '#FFDDB8', size: 20 }); const [p] = add([n]) || []; if (p) { setTool('select'); sel = new Set([p.id]); editSoon2(p); toast('Press Tab to add a branch, Enter for the next one.'); } break; }
      default: break;
    }
  };
  dock.addEventListener('click', e => {
    const s = e.target.closest('[data-dtool]'); if (s) { dockKind = null; setTool(s.dataset.dtool); return; }
    const a = e.target.closest('[data-dact]'); if (a) { if (a.dataset.dact === 'undo') doUndo(); else doRedo(); paintDock(); return; }
    const t = e.target.closest('[data-tile]'); if (!t) return;
    const k = t.dataset.tile;
    picker.hidden = k !== 'card' || !picker.hidden ? true : picker.hidden; morePanel.hidden = k !== 'more' ? true : morePanel.hidden; ctxPop.hidden = true;
    if (k === 'card') { if (picker.hidden) openPicker(); else picker.hidden = true; paintDock(); return; }
    if (k === 'more') { if (morePanel.hidden) openMore(); else morePanel.hidden = true; paintDock(); return; }
    if (k === 'tpl') { openCenter(); return; }
    if (k === 'pen') { dockKind = 'pen'; if (tool === 'pen' || tool === 'highlighter' || tool === 'eraser') { setTool('select'); return; } setPen(prefs.ptype); return; }
    if (k === 'shape') { dockKind = 'shape'; setTool(SHAPES.includes(lastShape) ? lastShape : 'rect'); return; }
    if (k === 'flow') { dockKind = dockKind === 'flow' ? null : 'flow'; sel.clear(); paintDock(); return; }
    if (k === 'mind' || k === 'image') { dockKind = dockKind === k ? null : k; sel.clear(); if (tool !== 'select') setTool('select'); else paintDock(); return; }
    dockKind = k; setTool(k);
  });
  ctxBar.addEventListener('click', e => ctxBarClick(e));
  function ctxBarClick(e) {
    const b = e.target.closest('button'); if (!b) return;
    const d = b.dataset;
    if (d.pen) { setPen(d.pen); return; }
    if (d.pcol) { pickPenColour(d.pcol); paintDock(); return; }
    if (d.psz) { setSize(penKind(), +d.psz); ctxBar._html = null; paintCtx(); return; }
    if (d.esz) { setSize('eraser', +d.esz); ctxBar._html = null; paintCtx(); return; }
    if (d.erm) { prefs.erMode = d.erm; savePrefs(prefs); ctxBar._html = null; paintCtx(); return; }
    if (d.cp) { openCtxPop(d.cp, ctxBar.contains(b) ? b : (ctxBar.querySelector('.wb-more-ov') || b)); return; }
    if (d.shape) { if (sel.size) applySel(x => isShape(x) && !isContainer(x) ? { type: d.shape } : null); else { lastShape = d.shape; setTool(d.shape); } return; }
    // a flowchart shape goes in the middle of the screen (like the Shapes panel); then the blue arrows add the next one
    if (d.stencil) { const r = rect(); placeShape(d.stencil, toWorld(r.width / 2, r.height / 2)); return; }
    if (d.route) { if (sel.size) applySel(x => x.type === 'connector' ? { route: d.route } : null); else { next.route = d.route; ctxBar._html = null; paintCtx(); } return; }
    if ('cdash' in d) { if (sel.size) applySel(x => x.type === 'connector' ? { dashed: !x.dashed || undefined } : null); else { next.dashed = !next.dashed; ctxBar._html = null; paintCtx(); } return; }
    if (d.stcol) { if (sel.size) applySel(x => x.type === 'sticky' ? { color: d.stcol } : null); else { stickyColor = d.stcol; paintDock(); } return; }
    if (d.scol) { applySel(x => x.type === 'path' ? { stroke: isHex(d.scol) ? d.scol : null } : null); return; }
    if (d.ssz) { applySel(x => x.type === 'path' ? { width: +(+d.ssz / view.z).toFixed(2) } : null); return; }
    if (d.fmt) { const els = selected().filter(x => x.type === 'text' || x.type === 'sticky' || isShape(x)); if (els.length === 1) { editText(els[0], { selectAll: true }); setTimeout(() => fmt(d.fmt), 0); } else toast('Double-click the text, select words, then press B, I or S'); return; }
    if (d.fsz) { next.frameSize = d.fsz; ctxBar._html = null; paintCtx(); return; }
    if (d.run) { runTool(d.run); return; }
    if (d.relayout) { relayout(d.relayout); return; }
    if (d.act2) { runAct(d.act2, b); }
  }
  ctxPop.addEventListener('pointerdown', e => e.stopPropagation());
  ctxPop.addEventListener('click', e => {
    const b = e.target.closest('button'); if (!b) return;
    // a button that lives in the bar but did not fit: run it like the bar would
    if (ctxPop.dataset.kind === 'ctxov') { ctxPop.hidden = true; ctxBarClick({ target: b }); return; }
    const d = b.dataset;
    if (d.eyedrop) { ctxPop.hidden = true; startEyedrop(d.eyedrop); return; }
    if (d.pcol) { pickPenColour(d.pcol); paintDock(); ctxPop.innerHTML = ctxPopHtml('pencolours'); return; }
    if (d.fillc) { const c = d.fillc; if (isHex(c)) prefs.recent = [c.toUpperCase(), ...prefs.recent.filter(x => x !== c.toUpperCase())].slice(0, 8); savePrefs(prefs); if (sel.size) applySel(x => isShape(x) ? { fill: c === 'auto' ? null : c } : null); else next.fill = c === 'auto' ? null : c; ctxPop.innerHTML = ctxPopHtml('fill'); ctxBar._html = null; paintCtx(); return; }
    if (d.linec) { const c = d.linec; if (isHex(c)) prefs.recent = [c.toUpperCase(), ...prefs.recent.filter(x => x !== c.toUpperCase())].slice(0, 8); savePrefs(prefs); if (sel.size) applySel(x => x.type === 'sticky' || x.type === 'frame' || x.type === 'image' ? null : { stroke: isHex(c) ? c : null, ...(isShape(x) ? { textColor: isHex(c) ? c : null } : {}) }); else next.stroke = isHex(c) ? c : null; ctxPop.innerHTML = ctxPopHtml(ctxPop.dataset.kind); ctxBar._html = null; paintCtx(); return; }
    if (d.lw) { if (sel.size) applySel(x => (x.type === 'connector' || isShape(x)) ? { strokeWidth: +d.lw } : null); else next.width = +d.lw; ctxPop.innerHTML = ctxPopHtml('line'); return; }
    if (d.ldash) { const on = d.ldash === 'dashed'; if (sel.size) applySel(x => (x.type === 'connector' || isShape(x)) ? { dashed: on || undefined } : null); else next.dashed = on; ctxPop.innerHTML = ctxPopHtml('line'); return; }
    if (d.font2) { if (sel.size) applySel(x => (x.type === 'text' || x.type === 'sticky' || isShape(x)) ? { font: d.font2 } : null); else next.font = d.font2; ctxPop.hidden = true; ctxBar._html = null; paintCtx(); return; }
    if (d.size2) { const s = +d.size2; if (sel.size) applySel(x => { if (x.type === 'text') { const lines = String(x.text).split('\n'); return { size: s, w: Math.max(...lines.map(l => measure(l, s, x.font || 'ipop'))) + 4, h: lines.length * s * 1.3 }; } return (x.type === 'sticky' || isShape(x)) ? { size: s } : null; }); else next.size = s; ctxPop.hidden = true; ctxBar._html = null; paintCtx(); return; }
    if (d.align2) { if (sel.size) applySel(x => (x.type === 'text' || x.type === 'sticky' || isShape(x)) ? { align: d.align2 } : null); else next.align = d.align2; ctxPop.hidden = true; return; }
    if (d.head2) { const [w, k] = d.head2.split(':'); const key = w === 's' ? 'startHead' : 'endHead'; if (sel.size) applySel(x => x.type === 'connector' ? { [key]: k } : null); else next[key] = k; ctxPop.innerHTML = ctxPopHtml('heads'); ctxBar._html = null; paintCtx(); return; }
    if (d.mypen) { const m = myPens[+d.mypen]; if (!m) return; const k = PEN_TYPES[m.type] ? m.type : 'pen'; prefs.color[k] = m.color; prefs.size[k] = PEN_TYPES[k].sizes[m.size] ?? PEN_TYPES[k].size; savePrefs(prefs); ctxPop.hidden = true; setPen(k); return; }
    if (d.mypendel) { myPens.splice(+d.mypendel, 1); saveMyPens(); ctxPop.innerHTML = ctxPopHtml('mypens'); return; }
    if ('mypensave' in d) {
      const k = penKind(), name = (ctxPop.querySelector('[data-mypenname]')?.value || '').trim() || `${PEN_TYPES[k].label} ${myPens.length + 1}`;
      if (myPens.length >= 12) { toast('You can keep 12 pens. Remove one first.'); return; }
      const sizeIdx = Math.max(0, PEN_TYPES[k].sizes.indexOf(prefs.size[k]));
      myPens.push({ name: name.slice(0, 40), type: k, color: isHex(prefs.color[k]) ? prefs.color[k] : 'auto', size: sizeIdx < 0 ? 1 : sizeIdx });
      saveMyPens(); ctxPop.innerHTML = ctxPopHtml('mypens'); toast(`Saved to My pens as “${name}”`); return;
    }
    if (d.run) { runTool(d.run); return; }
    if (d.act2) { ctxPop.hidden = true; runAct(d.act2, b); }
  });
  ctxPop.addEventListener('input', e => {
    const r = e.target.closest('[data-rng]'); if (!r) return;
    const v = +r.value, k = penKind(), lab = r.previousElementSibling?.querySelector('b');
    if (r.dataset.rng === 'size') { prefs.size[k] = v; if (lab) lab.textContent = `${v} px`; }
    if (r.dataset.rng === 'smooth') { prefs.smooth = v; if (lab) lab.textContent = smoothLabel(); }
    if (r.dataset.rng === 'opacity') { prefs.opacity = v; if (lab) lab.textContent = `${v}%`; }
    savePrefs(prefs);
  });
  ctxPop.addEventListener('change', e => {
    const t = e.target;
    if (t.matches('[data-rng="size"]')) { setSize(penKind(), +t.value); ctxBar._html = null; paintCtx(); }
    if (t.dataset.pchk) { prefs[t.dataset.pchk] = t.checked; savePrefs(prefs); }
    if (t.matches('[data-hints]')) { try { if (t.checked) localStorage.removeItem(HINTS_KEY); else localStorage.setItem(HINTS_KEY, 'off'); } catch { /* blocked */ } paintHint(); }
    if (t.dataset.anyc) pickFromPop(t.dataset.anyc, t.value.toUpperCase());
  });
  ctxPop.addEventListener('keydown', e => {
    e.stopPropagation();
    if (e.key === 'Escape') { ctxPop.hidden = true; root.focus({ preventScroll: true }); return; }
    if (e.key === 'Enter' && e.target.dataset.hex) { let v = e.target.value.trim(); if (!v.startsWith('#')) v = `#${v}`; if (isHex(v)) pickFromPop(e.target.dataset.hex, v.toUpperCase()); else e.target.select(); }
    if (e.key === 'Enter' && 'mypenname' in e.target.dataset) ctxPop.querySelector('[data-mypensave]')?.click();
  });
  /// "Any colour" (wheel or hex): the same as clicking a swatch with that colour.
  function pickFromPop(attr, c) { const b = document.createElement('button'); b.type = 'button'; b.hidden = true; b.setAttribute(attr, c); ctxPop.append(b); b.click(); b.remove(); }

  // ── My pens (per person, on the server) ──
  let myPens = [];
  const saveMyPens = () => { opts.prefs?.set?.('wb-pens', myPens).catch?.(() => {}); };
  if (opts.prefs && mode === 'board') opts.prefs.get('wb-pens').then(v => { if (Array.isArray(v)) myPens = v.filter(p => p && PEN_TYPES[p.type]).slice(0, 12); }).catch(() => {});

  // ── Task / Pages picker ──
  let pickTab = 'tasks', pickQ = '', pickSeq = 0, pickKb = 0, pickItems = [];
  function openPicker() {
    if (readOnly) return;
    if (!opts.taskSearch) pickTab = 'pages';
    picker.hidden = false; pickQ = ''; pickKb = 0;
    picker.innerHTML = `<div class="wb-ptabs" role="tablist">${opts.taskSearch ? `<button type="button" role="tab" data-ptab="tasks">${icon('check')}Tasks</button>` : ''}${opts.pages ? `<button type="button" role="tab" data-ptab="pages">${icon('note')}Pages</button>` : ''}</div>
      <div class="wb-pq">${icon('search')}<input type="search" data-pq placeholder="" aria-label="Search"></div>
      <div class="wb-plh"><span>Recent</span><button type="button" class="wb-plink" data-pbrowse></button></div>
      <ul class="wb-plist" role="listbox"></ul>
      <div class="wb-pft"><button type="button" data-pnew>${icon('plus')}<span></span></button><span class="wb-pwhere"></span></div>`;
    paintPickTabs();
    const input = picker.querySelector('[data-pq]');
    input.addEventListener('input', () => { pickQ = input.value; pickKb = 0; clearTimeout(openPicker.t); openPicker.t = setTimeout(fillPicker, 180); });
    input.addEventListener('keydown', e => {
      e.stopPropagation();
      if (e.key === 'Escape') { picker.hidden = true; paintDock(); root.focus({ preventScroll: true }); return; }
      if (e.key === 'Tab' && opts.taskSearch && opts.pages) { e.preventDefault(); pickTab = pickTab === 'tasks' ? 'pages' : 'tasks'; paintPickTabs(); return; }
      if (e.key === 'ArrowDown' || e.key === 'ArrowUp') { e.preventDefault(); pickKb = Math.max(0, Math.min(pickItems.length - 1, pickKb + (e.key === 'ArrowDown' ? 1 : -1))); markPick(); return; }
      if (e.key === 'Enter') { e.preventDefault(); placePicked(pickItems[pickKb]); }
    });
    input.focus({ preventScroll: true });
  }
  function paintPickTabs() {
    picker.querySelectorAll('[data-ptab]').forEach(b => { b.classList.toggle('on', b.dataset.ptab === pickTab); b.setAttribute('aria-selected', String(b.dataset.ptab === pickTab)); });
    picker.querySelector('[data-pq]').placeholder = pickTab === 'tasks' ? 'Search for task name, ID, or URL' : 'Search Notes pages';
    picker.querySelector('[data-pbrowse]').textContent = pickTab === 'tasks' ? 'Browse tasks' : 'All pages';
    picker.querySelector('[data-pnew] span').textContent = pickTab === 'tasks' ? 'Create new task' : 'New page card';
    picker.querySelector('[data-pnew]').hidden = pickTab === 'tasks' ? !opts.taskCreate : true;
    const where = picker.querySelector('.wb-pwhere'); where.textContent = '';
    if (pickTab === 'tasks' && opts.lastProject) opts.lastProject().then(n => { if (n && !picker.hidden) where.textContent = `in ${n} (last used)`; }).catch(() => {});
    fillPicker();
  }
  async function fillPicker() {
    const seq = ++pickSeq, list = picker.querySelector('.wb-plist'); if (!list) return;
    const q = pickQ.trim();
    // a pasted task link opens that task: /projects/<p>/tasks/<id> or …?task=<id>
    const idFromUrl = /(?:tasks\/|task=)([0-9a-f-]{36})/i.exec(q)?.[1];
    if (pickTab === 'pages') {
      pickItems = (opts.pages?.all?.() || []).filter(p => !q || String(p.title || '').toLowerCase().includes(q.toLowerCase())).slice(0, 40).map(p => ({ kind: 'page', ...p }));
    } else {
      list.innerHTML = '<li class="wb-pnote">Loading…</li>';
      const items = await opts.taskSearch(idFromUrl ? '' : q).catch(() => []);
      if (seq !== pickSeq) return;
      pickItems = (idFromUrl ? items.filter(t => String(t.id).toLowerCase() === idFromUrl.toLowerCase()) : items).map(t => ({ kind: 'task', ...t }));
    }
    pickKb = Math.min(pickKb, Math.max(0, pickItems.length - 1));
    const STATUS = { todo: '#8E8E93', 'in-progress': '#0A84FF', inprogress: '#0A84FF', doing: '#0A84FF', review: '#FF9500', done: '#34C759', blocked: '#FF3B30' };
    const AV = ['#AF52DE', '#5E5CE6', '#34A853', '#FF9500', '#0A84FF', '#FF2D55'];
    const initials = n => String(n || '').split(/\s+/).filter(Boolean).slice(0, 2).map(w => w[0]).join('').toUpperCase();
    list.innerHTML = pickItems.length ? pickItems.map((it, i) => it.kind === 'page'
      ? `<li role="option" data-pi="${i}" class="${i === pickKb ? 'kb' : ''}"><span class="wb-ppr">Notes</span><span class="wb-pst">${esc(it.emoji || '📄')}</span><span class="wb-pnm">${esc(it.title || 'Untitled')}</span><span></span></li>`
      : `<li role="option" data-pi="${i}" class="${i === pickKb ? 'kb' : ''}"><span class="wb-ppr">${esc(it.project || '')}</span><span class="wb-pst"><i style="border-color:${STATUS[String(it.status || '').toLowerCase()] || '#8E8E93'}"></i></span><span class="wb-pnm">${esc(it.title)}</span><span class="wb-pav">${(it.people || []).slice(0, 3).map((n, k) => `<b style="background:${AV[(String(n).charCodeAt(0) + k) % AV.length]}" title="${esc(n)}">${esc(initials(n))}</b>`).join('')}</span></li>`).join('')
      : `<li class="wb-pnote">${pickTab === 'tasks' ? (q ? 'No task found. Tasks you can’t open are never listed.' : 'No tasks yet in projects you can add to.') : 'No page found.'}</li>`;
  }
  const markPick = () => picker.querySelectorAll('[data-pi]').forEach(li => li.classList.toggle('kb', +li.dataset.pi === pickKb));
  function placePicked(it) {
    if (!it) return;
    const r = rect(), c = toWorld(r.width / 2, r.height / 2);
    if (it.kind === 'page') addPageCard(it, c); else addTaskCards([it], { x: c.x - 130, y: c.y - 52 });
    picker.hidden = true; paintDock();
  }
  picker.addEventListener('pointerdown', e => e.stopPropagation());
  picker.addEventListener('click', async e => {
    const tab = e.target.closest('[data-ptab]'); if (tab) { pickTab = tab.dataset.ptab; pickKb = 0; paintPickTabs(); picker.querySelector('[data-pq]').focus(); return; }
    const li = e.target.closest('[data-pi]'); if (li) { placePicked(pickItems[+li.dataset.pi]); return; }
    if (e.target.closest('[data-pbrowse]')) { if (pickTab === 'tasks') window.open('/projects', '_blank', 'noopener'); else { pickQ = ''; picker.querySelector('[data-pq]').value = ''; fillPicker(); } return; }
    if (e.target.closest('[data-pnew]') && opts.taskCreate) {
      const title = pickQ.trim() || 'New task';
      const t = await opts.taskCreate(title).catch(() => null);
      if (!t) { toast('Couldn’t create the task. You need a project you can add tasks to.'); return; }
      placePicked({ kind: 'task', ...t });
      toast(`Task created in ${t.project}`);
    }
  });

  // ── More panel: every other tool, as coloured tiles ──
  const MORE_GROUPS = () => [
    ['Create', [['tpl', 'Templates', 'grid', '#AF52DE'], ['emoji', 'Emoji & stickers', 'emoji', '#FF9500'], ...(opts.linkPreview ? [['linkcard', 'Web link card', 'linkcard', '#0A84FF']] : []), ...(opts.library ? [['lib', 'Library', 'book', '#5E5CE6']] : []), ['import', 'Import Mermaid / draw.io', 'import', '#8E8E93']]],
    ['Diagram', [['guide', 'Flowchart guide', 'bulb', '#34C759'], ...(flowOn ? [['check', 'Check diagram', 'check', '#34C759'], ['tidy', 'Tidy / auto layout', 'tidy', '#00C7BE']] : []), ['relayout', 'Re-layout', 'layout', '#00C7BE'], ...(opts.people ? [['org', 'Org chart from Directory', 'org', '#0A84FF']] : []), ['graphics', 'Data graphics', 'chart', '#FF9500'], ...(opts.tasks?.createMany ? [['tasks', 'Tasks from shapes', 'task', '#AF52DE']] : []), ['ipfind', 'Find device by IP', 'ip', '#8E8E93'], ['report', 'Report to Excel', 'table', '#34A853']]],
    ['Meeting', [['present', 'Present', 'play', '#FF3B30'], ...(aw ? [['timer', 'Timer', 'timer', '#FF9500']] : []), ...(vmap ? [['vote', 'Vote', 'vote', '#5E5CE6']] : []), ['frames', 'Frames', 'frames', '#0A84FF'], ['find', 'Find on board', 'search', '#8E8E93'], ['laser', 'Laser pointer', 'laser', '#FF2D55']]],
    ['Board', [['savetpl', 'Save as template', 'bookplus', '#AF52DE'], ['howto', live().some(x => x.howTo && x.hide) ? 'Show “How to use” notes' : 'Hide “How to use” notes', 'note', '#8E8E93'], ['export', 'Export', 'download', '#34A853'], ['pptx', 'PowerPoint', 'slides', '#FF9500'], ...(opts.history ? [['history', 'Version history', 'history', '#8E8E93']] : []), ['keys', 'Keyboard shortcuts', 'keys', '#8E8E93']]],
  ];
  function openMore(q = '') {
    morePanel.hidden = false;
    const ql = q.trim().toLowerCase();
    const groups = MORE_GROUPS().map(([g, items]) => [g, items.filter(([, l]) => !ql || l.toLowerCase().includes(ql) || g.toLowerCase().includes(ql))]).filter(([, items]) => items.length);
    const body = groups.length ? groups.map(([g, items]) => `<div class="wb-mh">${g}</div><div class="wb-mgrid">${items.map(([k, l, ic, c]) => `<button type="button" class="wb-mt" data-more2="${k}"><span class="wb-mtic" style="background:${c}">${icon(ic)}</span><span>${esc(l)}</span></button>`).join('')}</div>`).join('') : '<p class="wb-note">Nothing found.</p>';
    if (!morePanel.querySelector('.wb-search')) { morePanel.innerHTML = '<input class="wb-search" type="search" placeholder="Search tools…" aria-label="Search tools"><div class="wb-mbody"></div>'; const s = morePanel.querySelector('.wb-search'); s.addEventListener('input', () => openMore(s.value)); s.addEventListener('keydown', e => { e.stopPropagation(); if (e.key === 'Escape') { morePanel.hidden = true; paintDock(); } if (e.key === 'Enter') morePanel.querySelector('[data-more2]')?.click(); }); }
    morePanel.querySelector('.wb-mbody').innerHTML = body;
    if (!q) requestAnimationFrame(() => morePanel.querySelector('.wb-search')?.focus({ preventScroll: true }));
  }
  morePanel.addEventListener('pointerdown', e => e.stopPropagation());
  morePanel.addEventListener('click', e => {
    const b = e.target.closest('[data-more2]'); if (!b) return;
    const k = b.dataset.more2;
    morePanel.hidden = true; const s = morePanel.querySelector('.wb-search'); if (s) s.value = '';
    paintDock();
    if (k === 'tpl') { openCenter(); return; }
    if (k === 'emoji') { toggleEmojiPicker(dock); return; }
    if (k === 'linkcard') { askLinkCard(); return; }
    if (k === 'laser') { setTool('laser'); return; }
    if (k === 'keys') { toggleKeys(); return; }
    if (k === 'savetpl') { openSaveTemplate(); return; }
    if (k === 'howto') { toggleHowTo(); return; }
    runCorner(Object.assign(document.createElement('button'), { dataset: { c: k } }), {});
  });
  /// Hide or show every "How to use this template" note on the board (they stay on the board, just hidden).
  function toggleHowTo() {
    const notes = live().filter(x => x.howTo);
    if (!notes.length) { toast('There are no “How to use” notes on this board.'); return; }
    const hide = notes.some(x => !x.hide);
    write(notes.map(x => bump(x, { hide: hide || undefined })), { now: true }); commitStop();
    toast(hide ? `${notes.length === 1 ? 'The note is' : `${notes.length} notes are`} hidden. Show them again from More.` : 'The “How to use” notes are back.');
  }

  // ══ Template Center ══
  const LEVELS = ['Beginner', 'Intermediate', 'Advanced'];
  const TPL_CATS6 = ['Brainstorm', 'Strategy & analysis', 'Planning & meeting', 'Flowchart & process', 'Process improvement', 'Technical'];
  const META = new Map(TEMPLATE_META.map(t => [t.key, t]));
  const tcs = { view: 'featured', q: '', cats: new Set(), levels: new Set(), open: new Set(), bp: null, zoom: 1, mine: null, recent: [], where: 'board', withHow: true, withEx: true };
  const pvCache = new Map();
  /// The preview of a template: its own elements drawn with this renderer, so it always matches and follows the theme.
  function tplElements(t) {
    if (t.json) { try { return JSON.parse(t.json).filter(isElement); } catch { return []; } }
    return buildTemplate(t.key, { x: 0, y: 0 });
  }
  function previewSvg(t, big = false) {
    const key = `${t.key || t.id}:${theme}:${big ? 1 : 0}`;
    if (pvCache.has(key)) return pvCache.get(key);
    let html = '';
    try {
      const o = toSvg(tplElements(t), { theme, background: true, padding: big ? 40 : 24, cardInfo: { task: () => null, page: () => null } });
      if (o) { o.svg.setAttribute('width', '100%'); o.svg.setAttribute('height', '100%'); o.svg.setAttribute('preserveAspectRatio', 'xMidYMid meet'); o.svg.querySelectorAll('[id]').forEach(n => { if (n.closest('defs')) return; }); html = new XMLSerializer().serializeToString(o.svg); }
    } catch { html = ''; }
    pvCache.set(key, html);
    return html;
  }
  const userTpl = t => ({ key: `u:${t.id}`, id: t.id, name: t.name, desc: t.description || (t.mine ? (t.shared ? 'Shared with the team' : 'Only you') : `By ${t.ownerName}`), cat: t.mine ? 'My templates' : 'Team templates', level: 'Beginner', when: t.description || 'A board someone in the company saved to reuse.', steps: ['Use it like any template', 'Change anything on it'], json: t.json, mine: t.mine, shared: t.shared, owner: t.ownerName });
  async function loadMine(force = false) {
    if (!opts.templates) return [];
    if (tcs.mine && !force) return tcs.mine;
    tcs.mine = (await opts.templates.list().catch(() => [])).map(userTpl);
    return tcs.mine;
  }
  const allTpl = () => [...TEMPLATE_META, ...(tcs.mine || [])];
  const hay = t => `${t.name} ${t.desc} ${t.cat} ${t.aliases || ''}`.toLowerCase();
  const tcCard = t => `<button type="button" class="wb-tcard" data-tk="${esc(t.key)}"><span class="wb-tpv">${previewSvg(t)}</span>${t.isNew ? '<span class="wb-ttag">New</span>' : t.guided ? '<span class="wb-ttag">Guided</span>' : ''}
      ${t.id && (t.mine || (t.shared && opts.templates?.isAdmin?.())) ? `<span class="wb-tdel" data-tdel="${t.id}" title="${t.mine ? 'Remove' : 'Remove from Team templates (admin)'}" role="button" aria-label="Remove ${esc(t.name)}">${icon('trash')}</span>` : ''}
      <span class="wb-thov"><span class="wb-thb" data-tprev="${esc(t.key)}">Preview</span><span class="wb-thb wb-pri" data-tuse="${esc(t.key)}">Use</span></span>
      <span class="wb-tmeta"><b>${esc(t.name)}</b><span class="wb-lv wb-lv-${t.level}">${t.level}</span></span></button>`;
  function centerBody() {
    const filt = list => list.filter(t => (!tcs.q || tcs.q.split(/\s+/).every(w => hay(t).includes(w))) && (!tcs.levels.size || tcs.levels.has(t.level)) && (!tcs.cats.size || tcs.cats.has(t.cat)));
    const grid = list => `<div class="wb-tcgrid">${list.map(tcCard).join('')}</div>`;
    const sec = (title, list, more = '', id = '') => `<section class="wb-tcsec" ${id ? `data-sec="${esc(id)}"` : ''}><div class="wb-tcsh"><h3>${esc(title)} <span class="wb-tccount">${list.length}</span></h3>${more}</div>${grid(list)}</section>`;
    const mine = (tcs.mine || []).filter(t => t.mine), team = (tcs.mine || []).filter(t => t.shared);
    if (tcs.q || tcs.cats.size || tcs.levels.size) {
      const r = filt(tcs.view === 'mine' ? mine : tcs.view === 'team' ? team : allTpl());
      return r.length ? sec(`${r.length} result${r.length === 1 ? '' : 's'}`, r) : '<p class="wb-tcempty">No template matches. Try fewer filters or other words.</p>';
    }
    if (tcs.view === 'mine') return mine.length ? sec('My templates', mine) : `<p class="wb-tcempty">Save any board with <b>More → Save as template</b>. Its preview is made from the board automatically.</p>`;
    if (tcs.view === 'team') return team.length ? sec('Team templates', team) : `<p class="wb-tcempty">Nothing shared yet. Save a board as a template and tick “Share with everyone”.</p>`;
    let out = '';
    if (tcs.view === 'featured') {
      out += `<div class="wb-tcban">${icon('bookplus')}<span>Make your own: save any board as a template for yourself or the whole company.</span><button type="button" data-tsave>Save this board</button></div>`;
      const rec = tcs.recent.map(k => allTpl().find(t => t.key === k)).filter(Boolean);
      if (rec.length) out += sec('Recently used', rec);
      out += sec('Featured', TEMPLATE_META.filter(t => t.featured));
      out += '<h2 class="wb-tch2">iPop templates</h2>';
    }
    // every template of every category is shown; the headers stick while scrolling, the sidebar jumps between them
    for (const c of TPL_CATS6) {
      out += sec(c, TEMPLATE_META.filter(t => t.cat === c), '', c);
    }
    return out;
  }
  function paintCenter({ keepScroll = true } = {}) {
    const body = tc.querySelector('.wb-tcbody'), sc = keepScroll && body ? body.scrollTop : 0;
    const mine = (tcs.mine || []).filter(t => t.mine).length, team = (tcs.mine || []).filter(t => t.shared).length;
    const nv = (k, l, n, ic) => `<button type="button" class="wb-nv ${tcs.view === k ? 'on' : ''}" data-tview="${k}">${icon(ic)}<span>${l}</span><span class="wb-n">${n}</span></button>`;
    tc.innerHTML = `<aside class="wb-tcside"><div class="wb-tcbrand"><i></i>Template Center</div>
        ${nv('featured', 'Featured', TEMPLATE_META.filter(t => t.featured).length, 'star')}${opts.templates ? nv('mine', 'My templates', mine, 'user') + nv('team', 'Team templates', team, 'users') : ''}${nv('all', 'iPop templates', TEMPLATE_META.length, 'grid')}
        ${tcs.view === 'featured' || tcs.view === 'all' ? `<div class="wb-tcgh">Jump to</div>${TPL_CATS6.map(c => `<button type="button" class="wb-nv wb-jump" data-tjump="${esc(c)}"><span>${esc(c)}</span><span class="wb-n">${TEMPLATE_META.filter(t => t.cat === c).length}</span></button>`).join('')}` : ''}
        <div class="wb-tcgh">Filter by category</div>${TPL_CATS6.map(c => `<label class="wb-ck"><input type="checkbox" data-tcat="${esc(c)}" ${tcs.cats.has(c) ? 'checked' : ''}><span>${esc(c)}</span><span class="wb-n">${TEMPLATE_META.filter(t => t.cat === c).length}</span></label>`).join('')}
        <div class="wb-tcgh">Level</div>${LEVELS.map(l => `<label class="wb-ck"><input type="checkbox" data-tlv="${l}" ${tcs.levels.has(l) ? 'checked' : ''}><span>${l}</span></label>`).join('')}</aside>
      <div class="wb-tcmain"><div class="wb-tcbar"><div class="wb-tcq">${icon('search')}<input type="search" data-tq value="${esc(tcs.q)}" placeholder="Search templates… (e.g. approval, notulen rapat, 5S, akar masalah)" aria-label="Search templates"></div>
        <select data-tcsel aria-label="Category"><option value="">All categories</option>${TPL_CATS6.map(c => `<option ${tcs.cats.size === 1 && tcs.cats.has(c) ? 'selected' : ''}>${esc(c)}</option>`).join('')}</select>
        <select data-tlsel aria-label="Level"><option value="">Any level</option>${LEVELS.map(l => `<option ${tcs.levels.size === 1 && tcs.levels.has(l) ? 'selected' : ''}>${l}</option>`).join('')}</select>
        <button type="button" class="wb-tcx" data-tclose title="Close (Esc)" aria-label="Close">✕</button></div>
        <div class="wb-tcbody">${centerBody()}</div></div>
      <div class="wb-bp" hidden></div>`;
    const b2 = tc.querySelector('.wb-tcbody'); if (b2) { b2.scrollTop = sc; b2.addEventListener('scroll', spyCategories, { passive: true }); spyCategories(); }
    if (tcs.bp) showPreview(tcs.bp);
  }
  /// the sidebar marks the category being read
  function spyCategories() {
    const body = tc.querySelector('.wb-tcbody'); if (!body) return;
    let cur = null;
    for (const s2 of body.querySelectorAll('[data-sec]')) if (s2.offsetTop - body.scrollTop <= 60) cur = s2.dataset.sec;
    tc.querySelectorAll('[data-tjump]').forEach(b => b.classList.toggle('on', b.dataset.tjump === cur));
  }
  function showPreview(key) {
    const t = allTpl().find(x => x.key === key); if (!t) return;
    tcs.bp = key;
    const bp = tc.querySelector('.wb-bp');
    bp.innerHTML = `<div class="wb-bpc"><button type="button" class="wb-bpback" data-tback>← All templates</button><div class="wb-bpz" style="transform:scale(${tcs.zoom})">${previewSvg(t, true)}</div>
        <div class="wb-bpzoom"><button type="button" data-tz="-1" aria-label="Zoom out">−</button><span>${Math.round(tcs.zoom * 100)}%</span><button type="button" data-tz="1" aria-label="Zoom in">+</button></div>
        <button type="button" class="wb-bpnav" data-tstep="-1" style="left:14px" title="Previous (←)" aria-label="Previous template">‹</button><button type="button" class="wb-bpnav" data-tstep="1" style="right:14px" title="Next (→)" aria-label="Next template">›</button></div>
      <div class="wb-bpi"><h3>${esc(t.name)}</h3><div class="wb-bpchips"><span>${esc(t.cat)}</span><span class="wb-lv wb-lv-${t.level}">${t.level}</span>${t.guided ? '<span>Guided</span>' : ''}${t.isNew ? '<span>New</span>' : ''}</div>
        <h4>What it is</h4><p>${esc(t.desc)}</p><h4>When to use it</h4><p>${esc(t.when)}</p><h4>How</h4><ol>${t.steps.map(s => `<li>${esc(s)}</li>`).join('')}</ol>
        <h4>Add it</h4><div class="wb-useopts"><label><input type="radio" name="wb-uw" data-uw="board" ${tcs.where === 'board' ? 'checked' : ''}> Into this board, beside my work</label>${opts.templates?.newBoardPage ? `<label><input type="radio" name="wb-uw" data-uw="page" ${tcs.where === 'page' ? 'checked' : ''}> As a new board page</label>` : ''}
        ${t.json ? '' : `<label><input type="checkbox" data-uex ${tcs.withEx ? 'checked' : ''}> Include example content</label><label><input type="checkbox" data-uhow ${tcs.withHow ? 'checked' : ''}> Include “How to use” note</label>`}</div>
        <button type="button" class="wb-use" data-tuse="${esc(t.key)}">Use template</button></div>`;
    bp.hidden = false;
  }
  function stepPreview(d) { const list = allTpl(), i = list.findIndex(x => x.key === tcs.bp); tcs.zoom = 1; showPreview(list[(i + d + list.length) % list.length].key); }
  async function openCenter(view) {
    if (readOnly) return;
    ctxPop.hidden = true; picker.hidden = true; morePanel.hidden = true; gallery.hidden = true; emptyGallery.hidden = true;
    if (view) tcs.view = view;
    tcs.bp = null; tcs.zoom = 1;
    tcOverlay.hidden = false;
    paintCenter({ keepScroll: false });
    tc.querySelector('[data-tq]')?.focus({ preventScroll: true });
    if (opts.prefs && !tcs.recentLoaded) { tcs.recentLoaded = true; opts.prefs.get('wb-tpl-recent').then(v => { if (Array.isArray(v)) { tcs.recent = v.slice(0, 6); if (!tcOverlay.hidden && tcs.view === 'featured') paintCenter(); } }).catch(() => {}); }
    if (opts.templates) { await loadMine(); if (!tcOverlay.hidden) paintCenter(); }
  }
  function closeCenter() { tcOverlay.hidden = true; tcs.bp = null; paintEmpty(count() === 0); root.focus({ preventScroll: true }); }
  async function useTemplate(key) {
    const t = allTpl().find(x => x.key === key); if (!t) return;
    tcs.recent = [key, ...tcs.recent.filter(k => k !== key)].slice(0, 6);
    opts.prefs?.set?.('wb-tpl-recent', tcs.recent.filter(k => !k.startsWith('u:') || true).slice(0, 6));
    closeCenter();
    if (tcs.where === 'page' && opts.templates?.newBoardPage && !t.json) { await opts.templates.newBoardPage(t.name, t.key, { howTo: tcs.withHow, examples: tcs.withEx }); return; }
    const n = t.json ? placeElements(tplElements(t)) : api.applyTemplate(t.key, { howTo: tcs.withHow, examples: tcs.withEx });
    if (n) toast(t.guided ? `“${t.name}” added. Click a shape for hints on what comes next.` : `“${t.name}” added beside your work. Undo with Ctrl Z.`);
  }
  /// A saved template (elements starting at 0,0) placed to the right of what is on the board, with fresh ids.
  function placeElements(els) {
    if (readOnly || !els.length) return 0;
    const b = unionBounds(live(), get), at = { x: b ? b.x + b.w + 120 : 0, y: b ? b.y : 0 };
    const map = new Map(els.map(x => [x.id, create(x.type).id]));
    const out = els.map(x => {
      const copy = { ...structuredClone(x), id: map.get(x.id), version: 1 };
      if (x.type === 'connector') { const end = y => (y?.id && map.has(y.id) ? { id: map.get(y.id) } : { x: (y?.x ?? 0) + at.x, y: (y?.y ?? 0) + at.y }); return { ...copy, from: end(x.from), to: end(x.to) }; }
      return { ...copy, x: x.x + at.x, y: x.y + at.y };
    });
    const placed = add(out) || [];
    fit(placed, { max: 1 });
    return placed.length;
  }
  tc.addEventListener('click', async e => {
    const d = e.target.closest('[data-tdel]');
    if (d) {
      e.stopPropagation();
      const t = (tcs.mine || []).find(x => x.id === d.dataset.tdel); if (!t) return;
      if (!confirm(`Remove the template “${t.name}”${!t.mine ? ' from Team templates for everyone' : ''}?`)) return;
      try { await opts.templates.remove(t.id); await loadMine(true); pvCache.clear(); paintCenter(); toast('Template removed'); } catch { toast('Couldn’t remove it'); }
      return;
    }
    const v = e.target.closest('[data-tview]'); if (v) { tcs.view = v.dataset.tview; tcs.q = ''; tcs.cats.clear(); tcs.levels.clear(); paintCenter({ keepScroll: false }); return; }
    const jp = e.target.closest('[data-tjump]');
    if (jp) { const body = tc.querySelector('.wb-tcbody'), target = body.querySelector(`[data-sec="${CSS.escape(jp.dataset.tjump)}"]`); if (target) body.scrollTo({ top: target.offsetTop - 8, behavior: reduceMotion() ? 'auto' : 'smooth' }); return; }
    if (e.target.closest('[data-tclose]')) { closeCenter(); return; }
    if (e.target.closest('[data-tsave]')) { closeCenter(); openSaveTemplate(); return; }
    if (e.target.closest('[data-tback]')) { tcs.bp = null; tc.querySelector('.wb-bp').hidden = true; return; }
    const z = e.target.closest('[data-tz]'); if (z) { tcs.zoom = Math.max(0.5, Math.min(3, tcs.zoom + (+z.dataset.tz) * 0.25)); tc.querySelector('.wb-bpz').style.transform = `scale(${tcs.zoom})`; tc.querySelector('.wb-bpzoom span').textContent = `${Math.round(tcs.zoom * 100)}%`; return; }
    const st2 = e.target.closest('[data-tstep]'); if (st2) { stepPreview(+st2.dataset.tstep); return; }
    const u = e.target.closest('[data-tuse]'); if (u) { e.stopPropagation(); useTemplate(u.dataset.tuse); return; }
    const p = e.target.closest('[data-tprev]'); if (p) { e.stopPropagation(); tcs.zoom = 1; showPreview(p.dataset.tprev); return; }
    const card = e.target.closest('[data-tk]');
    if (card) {
      // one click = big preview; a double click uses it straight away
      if (e.detail >= 2) { clearTimeout(tc._t); useTemplate(card.dataset.tk); return; }
      clearTimeout(tc._t); tc._t = setTimeout(() => { tcs.zoom = 1; showPreview(card.dataset.tk); }, 220);
    }
  });
  tc.addEventListener('change', e => {
    const t = e.target;
    if (t.dataset.tcat) { if (t.checked) tcs.cats.add(t.dataset.tcat); else tcs.cats.delete(t.dataset.tcat); paintCenter({ keepScroll: false }); }
    if (t.dataset.tlv) { if (t.checked) tcs.levels.add(t.dataset.tlv); else tcs.levels.delete(t.dataset.tlv); paintCenter({ keepScroll: false }); }
    if ('tcsel' in t.dataset) { tcs.cats.clear(); if (t.value) tcs.cats.add(t.value); paintCenter({ keepScroll: false }); }
    if ('tlsel' in t.dataset) { tcs.levels.clear(); if (t.value) tcs.levels.add(t.value); paintCenter({ keepScroll: false }); }
    if (t.dataset.uw) tcs.where = t.dataset.uw;
    if ('uex' in t.dataset) tcs.withEx = t.checked;
    if ('uhow' in t.dataset) tcs.withHow = t.checked;
  });
  tc.addEventListener('input', e => {
    if (!('tq' in e.target.dataset)) return;
    tcs.q = e.target.value.trim().toLowerCase();
    const pos = e.target.selectionStart;
    paintCenter({ keepScroll: false });
    const n = tc.querySelector('[data-tq]'); n.focus(); n.setSelectionRange(pos, pos);
  });
  tc.addEventListener('keydown', e => {
    e.stopPropagation();
    const bp = tc.querySelector('.wb-bp');
    if (e.key === 'Escape') { e.preventDefault(); if (bp && !bp.hidden) { tcs.bp = null; bp.hidden = true; } else closeCenter(); return; }
    if (bp && !bp.hidden && (e.key === 'ArrowLeft' || e.key === 'ArrowRight') && !e.target.matches('input[type=search], input[type=text]')) { e.preventDefault(); stepPreview(e.key === 'ArrowLeft' ? -1 : 1); }
  });
  // ── Save this board as a template ──
  const savePanel = el('div', 'wb-panel wb-savetpl'); savePanel.hidden = true; savePanel.setAttribute('role', 'dialog'); savePanel.setAttribute('aria-label', 'Save as template');
  root.append(savePanel);
  savePanel.addEventListener('pointerdown', e => e.stopPropagation());
  function openSaveTemplate() {
    if (readOnly || !opts.templates) { if (!opts.templates) toast('Templates can be saved from a Notes whiteboard'); return; }
    if (!count()) { toast('The board is empty: add something first.'); return; }
    const els = sel.size ? selected() : live();
    savePanel.innerHTML = `<div class="wb-ph"><b>Save as template</b><button type="button" data-close aria-label="Close">✕</button></div>
      <div class="wb-stpv">${previewSvg({ key: `save:${Date.now()}`, json: JSON.stringify(packForLibrary(els)) })}</div>
      <label class="wb-fl">Name<input class="wb-search" data-sname maxlength="100" value="${esc((opts.title?.() || 'My template').slice(0, 100))}"></label>
      <label class="wb-fl">What it is for (optional)<input class="wb-search" data-sdesc maxlength="300" placeholder="e.g. Monthly 5S audit at Karawang"></label>
      <label class="wb-chk"><input type="checkbox" data-sshare> Share with everyone (Team templates)</label>
      <p class="wb-note">${sel.size ? `Only the ${sel.size} selected thing${sel.size === 1 ? '' : 's'} will be saved.` : 'The whole board is saved. Select some things first to save only those.'} Pictures are left out.</p>
      <div class="wb-row"><button type="button" class="wb-cb wb-txt wb-pri" data-ssave>Save template</button></div>`;
    savePanel.hidden = false;
    savePanel.querySelector('[data-sname]').select();
  }
  savePanel.addEventListener('keydown', e => { e.stopPropagation(); if (e.key === 'Escape') savePanel.hidden = true; if (e.key === 'Enter' && e.target.matches('input.wb-search')) savePanel.querySelector('[data-ssave]')?.click(); });
  savePanel.addEventListener('click', async e => {
    if (e.target.closest('[data-close]')) { savePanel.hidden = true; return; }
    const b = e.target.closest('[data-ssave]'); if (!b || b.disabled) return;
    const els = packForLibrary(sel.size ? selected() : live());
    if (!els.length) { toast('Nothing to save (pictures are left out)'); return; }
    b.disabled = true;
    try {
      await opts.templates.save({ name: savePanel.querySelector('[data-sname]').value.trim(), description: savePanel.querySelector('[data-sdesc]').value.trim() || null, json: JSON.stringify(els), shared: savePanel.querySelector('[data-sshare]').checked });
      savePanel.hidden = true; tcs.mine = null; pvCache.clear();
      toast(savePanel.querySelector('[data-sshare]').checked ? 'Saved. Everyone finds it under Team templates.' : 'Saved under My templates.');
    } catch (err) { toast(err?.message === 'templates_full' ? 'You have 100 templates. Remove some first.' : err?.message === 'template_too_big' ? 'This board is too big for a template. Save part of it (select first).' : 'Couldn’t save the template'); }
    finally { b.disabled = false; }
  });

  // ══ Round 7: motion, Z zoom, context menu, command palette, quality of life ═══════════════════════════════════
  // ── Smooth camera ──
  // Every jump of the view (zoom buttons, Fit, Shift 1/2, a frame, a template, Follow) glides instead of snapping.
  let camAnim = 0;
  function glideTo(target, ms = 260) {
    cancelAnimationFrame(camAnim);
    if (reduceMotion() || destroyed) { view = { ...target }; render(); paintZoom(); return; }
    const from = { ...view }, t0 = performance.now();
    const ease = t => 1 - Math.pow(1 - t, 3);
    const step = now => {
      const t = Math.min(1, (now - t0) / ms), k = ease(t);
      // zoom is interpolated in log space so it feels even
      const z = Math.exp(Math.log(from.z) + (Math.log(target.z) - Math.log(from.z)) * k);
      // keep the centre moving on a straight line in world terms
      const r = rect(), cx = r.width / 2, cy = r.height / 2;
      const wa = { x: (cx - from.x) / from.z, y: (cy - from.y) / from.z }, wb = { x: (cx - target.x) / target.z, y: (cy - target.y) / target.z };
      const wx = wa.x + (wb.x - wa.x) * k, wy = wa.y + (wb.y - wa.y) * k;
      view = { z, x: cx - wx * z, y: cy - wy * z };
      render();
      if (t < 1) camAnim = requestAnimationFrame(step); else { view = { ...target }; render(); paintZoom(); }
    };
    camAnim = requestAnimationFrame(step);
  }
  /// The view that fit() would make, without moving there.
  function fitView(els, { pad = 60, max = 1.4 } = {}) {
    const r = rect(); const b = unionBounds(els, get); if (!b || r.width < 50) return null;
    const top = 30, bottom = readOnly ? 20 : 70;
    const z = Math.max(0.1, Math.min(max, (r.width - pad * 2) / Math.max(b.w, 1), (r.height - top - bottom - pad) / Math.max(b.h + 26, 1)));
    return { z, x: (r.width - b.w * z) / 2 - b.x * z, y: top + (r.height - top - bottom - b.h * z) / 2 - b.y * z + 13 * z };
  }
  const glideFit = (els = live(), o = {}) => { const v = fitView(els, o); if (v) glideTo(v); };
  const glideZoom = (f, sx, sy) => { const r = rect(); sx ??= r.width / 2; sy ??= r.height / 2; const w = toWorld(sx, sy), z = Math.min(4, Math.max(0.1, view.z * f)); glideTo({ z, x: sx - w.x * z, y: sy - w.y * z }, 180); showZoomHud(sx, sy, z); };

  // ── Inertial pan: a flick keeps gliding and slows down ──
  let panVel = null, panTrack = [], inertiaRaf = 0;
  function trackPan(sx, sy) { const t = performance.now(); panTrack.push({ sx, sy, t }); while (panTrack.length > 5) panTrack.shift(); }
  function startInertia() {
    cancelAnimationFrame(inertiaRaf);
    if (reduceMotion() || panTrack.length < 2) { panTrack = []; return; }
    const a = panTrack[0], b = panTrack.at(-1), dt = Math.max(1, b.t - a.t);
    panTrack = [];
    if (performance.now() - b.t > 60) return;           // the finger stopped before lifting: no glide
    panVel = { x: (b.sx - a.sx) / dt * 16, y: (b.sy - a.sy) / dt * 16 };
    if (Math.hypot(panVel.x, panVel.y) < 2) return;
    const step = () => {
      panVel.x *= 0.92; panVel.y *= 0.92;
      view.x += panVel.x; view.y += panVel.y; render();
      if (Math.hypot(panVel.x, panVel.y) > 0.4 && !destroyed) inertiaRaf = requestAnimationFrame(step);
    };
    inertiaRaf = requestAnimationFrame(step);
  }
  const stopInertia = () => { cancelAnimationFrame(inertiaRaf); cancelAnimationFrame(camAnim); };

  // ── Zoom HUD (percentage near the cursor) ──
  const zoomHud = el('div', 'wb-zhud'); zoomHud.setAttribute('aria-hidden', 'true');
  const zoomBox = el('div', 'wb-zbox');
  root.append(zoomHud, zoomBox);
  function showZoomHud(sx, sy, z = view.z) {
    zoomHud.textContent = `${Math.round(z * 100)}%`;
    zoomHud.style.left = `${sx}px`; zoomHud.style.top = `${sy}px`;
    zoomHud.classList.add('on');
    clearTimeout(showZoomHud.t); showZoomHud.t = setTimeout(() => zoomHud.classList.remove('on'), 900);
  }

  // ── Z: the zoom tool, and hold Z for a moment of zooming (like Space for moving) ──
  let zHeld = false, zDownAt = 0, zUsed = false;
  const zooming = () => tool === 'zoom' || zHeld;
  function zoomDown(e, sx, sy) {
    zUsed = true;
    interaction = { kind: 'zoom', x: sx, y: sy, sy0: e.clientY, z0: view.z, moved: false, box: false, alt: e.altKey };
  }
  function zoomMove(it, e, sx, sy) {
    const dx = sx - it.x, dy = sy - it.y;
    if (!it.moved && Math.hypot(dx, dy) > 4) it.moved = true;
    if (!it.moved) return;
    // mostly sideways = a box to zoom into; up/down = a smooth zoom around where the press started
    if (it.box || Math.abs(dx) > Math.abs(dy) * 1.2) {
      it.box = true;
      zoomBox.style.display = 'block';
      zoomBox.style.left = `${Math.min(sx, it.x)}px`; zoomBox.style.top = `${Math.min(sy, it.y)}px`;
      zoomBox.style.width = `${Math.abs(dx)}px`; zoomBox.style.height = `${Math.abs(dy)}px`;
      return;
    }
    const target = Math.max(0.1, Math.min(4, it.z0 * Math.exp(-(e.clientY - it.sy0) * 0.008)));
    const w = toWorld(it.x, it.y);
    view = { z: target, x: it.x - w.x * target, y: it.y - w.y * target };
    render(); showZoomHud(sx, sy, target);
  }
  function zoomUp(it, e, sx, sy) {
    zoomBox.style.display = 'none';
    if (!it.moved) { glideZoom(e.altKey || it.alt ? 1 / 1.5 : 1.5, sx, sy); return; }
    if (it.box) {
      const bw = Math.abs(sx - it.x), bh = Math.abs(sy - it.y); if (bw < 20 || bh < 20) return;
      const a = toWorld(Math.min(sx, it.x), Math.min(sy, it.y)), b = toWorld(Math.max(sx, it.x), Math.max(sy, it.y));
      const v = fitView([{ id: '__z', type: 'rect', x: a.x, y: a.y, w: b.x - a.x, h: b.y - a.y, version: 1 }], { pad: 20, max: 4 });
      if (v) { glideTo(v); const r = rect(); showZoomHud(r.width / 2, r.height / 2, v.z); }
    }
    paintZoom();
  }

  // ── Stylus: the side button erases while held, and a one-time tips card ──
  const TIPS_KEY = 'ipop.wb.stylusTips';
  const tipsCard = el('div', 'wb-tips'); tipsCard.hidden = true; tipsCard.setAttribute('role', 'status');
  tipsCard.innerHTML = `<span class="wb-tipsic" aria-hidden="true">✍️</span><div><b>Tips for your stylus</b><ul><li><kbd>Z</kbd> hold and drag up or down to zoom · tap to zoom in</li><li><kbd>Space</kbd> hold to move the board</li><li>Stylus side button = eraser while you hold it</li></ul></div><div class="wb-tipsx"><button type="button" class="wb-cb wb-txt wb-pri" data-tips="ok">Got it</button><button type="button" class="wb-cb wb-txt" data-tips="never">Don’t show again</button></div>`;
  root.append(tipsCard);
  tipsCard.addEventListener('pointerdown', e => e.stopPropagation());
  tipsCard.addEventListener('click', e => {
    const b = e.target.closest('[data-tips]'); if (!b) return;
    closeFloat(tipsCard);
    try { localStorage.setItem(TIPS_KEY, b.dataset.tips === 'never' ? 'never' : String(Date.now())); } catch { /* blocked */ }
  });
  let tipsShown = false;
  function maybeStylusTips() {
    if (tipsShown || readOnly || mode !== 'board') return;
    tipsShown = true;
    let seen = null; try { seen = localStorage.getItem(TIPS_KEY); } catch { /* blocked */ }
    if (seen) return;
    openFloat(tipsCard);
    setTimeout(() => { if (!tipsCard.hidden) closeFloat(tipsCard); }, 14000);
  }

  // ── Opening and closing floating things with a spring (and fast close) ──
  function openFloat(n) { n.hidden = false; if (reduceMotion()) return; n.classList.remove('wb-out'); n.classList.remove('wb-in'); void n.offsetWidth; n.classList.add('wb-in'); }
  function closeFloat(n) {
    if (n.hidden) return;
    if (reduceMotion()) { n.hidden = true; return; }
    n.classList.remove('wb-in'); n.classList.add('wb-out');
    setTimeout(() => { n.hidden = true; n.classList.remove('wb-out'); }, 110);
  }
  // every panel and pop-up gets the spring when it appears (they are shown in many places; watch the attribute)
  const springWatch = new MutationObserver(list => {
    if (reduceMotion()) return;
    for (const m of list) {
      const n = m.target;
      if (m.attributeName !== 'hidden' || n.hidden || !(n instanceof HTMLElement)) continue;
      if (!n.matches('.wb-panel, .wb-menu, .wb-picker, .wb-morep, .wb-ctxpop, .wb-penpop, .wb-colourpop, .wb-boardmenu, .wb-tc, .wb-tcov, .wb-hint, .wb-cmenu, .wb-palov, .wb-savetpl')) continue;
      n.classList.remove('wb-in'); void n.offsetWidth; n.classList.add('wb-in');
    }
  });
  springWatch.observe(root, { attributes: true, attributeFilter: ['hidden'], subtree: true });

  // ── New elements pop in; the camera glides to a template; undo flashes what changed ──
  const flashIds = new Map();   // id -> until (ms); drawn in paintOverlay as a soft blue ring
  function flash(ids, ms = 900) { if (reduceMotion()) return; const until = performance.now() + ms; for (const id of ids) flashIds.set(id, until); tickFlash(); }
  function tickFlash() {
    cancelAnimationFrame(tickFlash.r);
    const now = performance.now();
    for (const [id, u] of flashIds) if (u < now) flashIds.delete(id);
    render();
    if (flashIds.size) tickFlash.r = requestAnimationFrame(tickFlash);
  }
  function popIn(ids) {
    if (reduceMotion()) return;
    requestAnimationFrame(() => { for (const id of ids) { const n = cache.get(id)?.node; if (!n) continue; n.classList.remove('wb-popin'); void n.getBBox?.(); n.classList.add('wb-popin'); setTimeout(() => n.classList.remove('wb-popin'), 260); } });
  }
  /// Undo / redo: what changed is highlighted and a toast names it (with the opposite action one click away).
  function undoStep(redo) {
    const before = new Map(live().map(e => [e.id, e.version]));
    flush(); (redo ? undo.redo : undo.undo).call(undo); sel.clear();
    const after = live();
    const changed = after.filter(e => before.get(e.id) !== e.version).map(e => e.id);
    const added = after.filter(e => !before.has(e.id)).length, removed = [...before.keys()].filter(id => !after.some(e => e.id === id)).length;
    render(); paintZoom(); paintDock();
    flash(changed);
    const what = added && !removed ? `brought back ${added} thing${added === 1 ? '' : 's'}` : removed && !added ? `removed ${removed} thing${removed === 1 ? '' : 's'}` : changed.length ? `changed ${changed.length} thing${changed.length === 1 ? '' : 's'}` : 'nothing to change';
    actionToast(`${redo ? 'Redid' : 'Undid'}: ${what}`, redo ? 'Undo' : 'Redo', () => undoStep(!redo));
  }
  const actToast = el('div', 'wb-acttoast'); actToast.hidden = true; root.append(actToast);
  actToast.addEventListener('pointerdown', e => e.stopPropagation());
  function actionToast(text, label, fn) {
    actToast.innerHTML = `<span>${esc(text)}</span>${label ? `<button type="button">${esc(label)}</button>` : ''}`;
    actToast.querySelector('button')?.addEventListener('click', () => { closeFloat(actToast); fn?.(); });
    openFloat(actToast);
    clearTimeout(actionToast.t); actionToast.t = setTimeout(() => closeFloat(actToast), 2600);
  }

  // ── Saved / Saving… ──
  const savedPill = el('div', 'wb-saved'); savedPill.innerHTML = '<i></i><span>Saved</span>'; savedPill.hidden = mode !== 'board' || readOnly;
  root.append(savedPill);
  function markSaving() {
    if (savedPill.hidden) return;
    savedPill.classList.add('saving'); savedPill.querySelector('span').textContent = 'Saving…';
    clearTimeout(markSaving.t); markSaving.t = setTimeout(() => { savedPill.classList.remove('saving'); savedPill.querySelector('span').textContent = 'Saved'; }, 800);
  }

  // ── Remember the view per person per board (this browser) ──
  const VIEW_KEY = opts.seenKey ? `ipop.wb.view:${opts.seenKey}` : null;
  function saveView() { if (!VIEW_KEY) return; try { localStorage.setItem(VIEW_KEY, JSON.stringify({ x: Math.round(view.x), y: Math.round(view.y), z: +view.z.toFixed(3), w: Math.round(rect().width), h: Math.round(rect().height) })); } catch { /* blocked */ } }
  function restoreView() {
    if (!VIEW_KEY) return false;
    try {
      const v = JSON.parse(localStorage.getItem(VIEW_KEY) || 'null');
      if (!v || !Number.isFinite(v.z) || !count()) return false;
      // keep the same centre if the window size changed
      const r = rect(), cxW = ((v.w || r.width) / 2 - v.x) / v.z, cyW = ((v.h || r.height) / 2 - v.y) / v.z;
      view = { z: v.z, x: r.width / 2 - cxW * v.z, y: r.height / 2 - cyW * v.z };
      render(); return true;
    } catch { return false; }
  }
  const viewSaver = setInterval(saveView, 3000);

  // ── Brush ring under the pointer for every pen (the eraser has its own) ──
  const brushRing = el('div', 'wb-brushring'); brushRing.hidden = true; root.append(brushRing);
  root.addEventListener('pointermove', e => {
    if ((tool === 'pen' || tool === 'highlighter') && e.pointerType !== 'touch' && !e.target.closest(OVER_UI)) {
      const d = Math.max(4, prefs.size[penKind()] || 3);
      brushRing.style.width = brushRing.style.height = `${d}px`;
      brushRing.style.left = `${local(e).sx - d / 2}px`; brushRing.style.top = `${local(e).sy - d / 2}px`;
      brushRing.style.borderColor = penColourCss(penKind());
      brushRing.hidden = false;
    } else brushRing.hidden = true;
  });
  root.addEventListener('pointerleave', () => { brushRing.hidden = true; });

  // ── Lasso (Alt + draw on empty board with the select tool) ──
  function lassoSelect(pts) {
    const inside = (px, py) => { let c = false; for (let i = 0, j = pts.length - 1; i < pts.length; j = i++) { const [xi, yi] = pts[i], [xj, yj] = pts[j]; if ((yi > py) !== (yj > py) && px < (xj - xi) * (py - yi) / (yj - yi) + xi) c = !c; } return c; };
    const hit = live().filter(x => x.type !== 'frame' && x.type !== 'connector' && (() => { const b = bounds(x, get); return inside(b.x + b.w / 2, b.y + b.h / 2); })());
    sel = expandGroups(new Set(hit.map(x => x.id)), live());
  }

  // ── Distance labels while dragging (gap to the nearest neighbour on the same row or column) ──
  function distanceLabels(box, others) {
    const out = [];
    let bestX = null, bestY = null;
    for (const o of others) {
      const overlapY = o.y < box.y + box.h && o.y + o.h > box.y, overlapX = o.x < box.x + box.w && o.x + o.w > box.x;
      if (overlapY) { const gap = o.x >= box.x + box.w ? o.x - (box.x + box.w) : box.x >= o.x + o.w ? box.x - (o.x + o.w) : null; if (gap != null && gap < 600 && (!bestX || gap < bestX.gap)) bestX = { gap, x: o.x >= box.x + box.w ? box.x + box.w + gap / 2 : o.x + o.w + gap / 2, y: Math.max(box.y, o.y) + Math.min(box.h, o.h) / 2 }; }
      if (overlapX) { const gap = o.y >= box.y + box.h ? o.y - (box.y + box.h) : box.y >= o.y + o.h ? box.y - (o.y + o.h) : null; if (gap != null && gap < 600 && (!bestY || gap < bestY.gap)) bestY = { gap, y: o.y >= box.y + box.h ? box.y + box.h + gap / 2 : o.y + o.h + gap / 2, x: Math.max(box.x, o.x) + Math.min(box.w, o.w) / 2 }; }
    }
    if (bestX) out.push(bestX); if (bestY) out.push(bestY);
    return out;
  }

  // ── Eyedropper: the next click on the board takes that element's colour ──
  let eyedropFor = null;
  function startEyedrop(forWhat) { eyedropFor = forWhat; root.classList.add('wb-eyedrop'); toast('Click something on the board to take its colour. Esc to cancel.'); }
  function takeColour(hit) {
    const c = hit.type === 'sticky' ? pal().sticky[hit.color] : isHex(hit.fill) ? hit.fill : isHex(hit.stroke) ? hit.stroke : null;
    const f = eyedropFor; eyedropFor = null; root.classList.remove('wb-eyedrop');
    if (!c || !isHex(c)) { toast('That one has the automatic colour. Pick something coloured.'); return; }
    const hex = c.toUpperCase();
    prefs.recent = [hex, ...prefs.recent.filter(x => x !== hex)].slice(0, 8); savePrefs(prefs);
    if (f === 'pen') { prefs.color[penKind()] = hex; savePrefs(prefs); paintToolbar(); }
    else if (f === 'fill') applySel(x => isShape(x) ? { fill: hex } : null);
    else applySel(x => x.type === 'sticky' || x.type === 'frame' || x.type === 'image' ? null : { stroke: hex, ...(isShape(x) ? { textColor: hex } : {}) });
    toast(`Took ${hex}`);
  }

  // ── Context menu (right-click on the board or on something) ──
  const cmenu = el('div', 'wb-cmenu'); cmenu.hidden = true; cmenu.setAttribute('role', 'menu');
  root.append(cmenu);
  cmenu.addEventListener('pointerdown', e => e.stopPropagation());
  let cmenuAt = null;
  const kb = k => (k ? `<kbd>${k}</kbd>` : '');
  function openContextMenu(e) {
    const { sx, sy } = local(e), w = toWorld(sx, sy);
    const hit = hitTest(live(), w.x, w.y, get, tolerance());
    cmenuAt = w;
    if (hit && !sel.has(hit.id)) { sel = pickInside ? new Set([hit.id]) : expandGroups(new Set([hit.id]), live()); paintProps(); render(); }
    if (!hit && sel.size) { sel.clear(); paintProps(); render(); }
    const els = selected(), one = els.length === 1 ? els[0] : null, locked = els.length && els.every(x => x.locked);
    const grouped = els.length > 1 && els.every(x => x.groupId && x.groupId === els[0].groupId);
    const items = readOnly
      ? [['fit', 'Fit everything', 'Shift 1'], ...(opts.comments?.canComment?.() ? [['comment', 'Add a comment here', 'C']] : []), ['copyimg', 'Copy as picture']]
      : els.length
        ? [['copy', 'Copy', 'Ctrl C'], ['cut', 'Cut', 'Ctrl X'], ['dup', 'Duplicate', 'Ctrl D'], ['copystyle', 'Copy style', 'Ctrl Shift C'], ...(styleClip ? [['pastestyle', 'Paste style', 'Ctrl Shift V']] : []), '-',
          ['front', 'Bring to front', 'Ctrl ]'], ['back', 'Send to back', 'Ctrl ['], ...(els.length > 1 ? [[grouped ? 'ungroup' : 'group', grouped ? 'Ungroup' : 'Group', grouped ? 'Ctrl Shift G' : 'Ctrl G'], ['arrange', 'Align and space evenly', '']] : []), ['lock', locked ? 'Unlock' : 'Lock', ''], '-',
          ...(one && opts.comments?.canComment?.() ? [['comment', 'Add a comment', 'C']] : []), ...(one && one.type !== 'connector' ? [['link', one.link ? 'Change link…' : 'Link…', 'Ctrl K']] : []),
          ...(opts.tasks?.create && els.every(x => x.type === 'sticky') ? [['task', els.length > 1 ? `Make ${els.length} tasks` : 'Make a task', '']] : []), ...(opts.library ? [['lib', 'Add to my library', '']] : []), ['zoomsel', 'Zoom to this', 'Shift 2'], '-',
          ['del', 'Delete', 'Del', 'red']]
        : [['paste', 'Paste here', 'Ctrl V', clipboard?.length ? '' : 'dis'], ['selall', 'Select all', 'Ctrl A'], '-', ['addsticky', 'Add a sticky here', 'S'], ['addtext', 'Add text here', 'T'], ['addshape', 'Add a shape here', 'R'], '-',
          ['fit', 'Fit everything', 'Shift 1'], ['tpl', 'Templates…', 'Shift G'], ['palette', 'Command palette', 'Ctrl /'], ['keys', 'Keyboard shortcuts', '?']];
    cmenu.innerHTML = items.map(i => i === '-' ? '<div class="wb-cmsep" role="separator"></div>' : `<button type="button" role="menuitem" class="wb-cmi ${i[3] || ''}" data-cm="${i[0]}" ${i[3] === 'dis' ? 'disabled' : ''}><span>${esc(i[1])}</span>${kb(i[2])}</button>`).join('');
    cmenu.hidden = false;
    const r = rect();
    cmenu.style.left = `${Math.max(8, Math.min(sx, r.width - cmenu.offsetWidth - 8))}px`;
    cmenu.style.top = `${sy + cmenu.offsetHeight + 8 <= r.height ? sy : Math.max(8, sy - cmenu.offsetHeight)}px`;
    cmenu.querySelector('.wb-cmi:not([disabled])')?.focus({ preventScroll: true });
  }
  cmenu.addEventListener('keydown', e => {
    e.stopPropagation();
    const items = [...cmenu.querySelectorAll('.wb-cmi:not([disabled])')], i = items.indexOf(document.activeElement);
    if (e.key === 'ArrowDown' || e.key === 'ArrowUp') { e.preventDefault(); items[(i + (e.key === 'ArrowDown' ? 1 : -1) + items.length) % items.length]?.focus(); }
    if (e.key === 'Escape') { cmenu.hidden = true; root.focus({ preventScroll: true }); }
  });
  cmenu.addEventListener('click', e => {
    const b = e.target.closest('[data-cm]'); if (!b || b.disabled) return;
    cmenu.hidden = true; root.focus({ preventScroll: true });
    runCommand(b.dataset.cm, b);
  });
  root.addEventListener('contextmenu', e => {
    if (e.target.closest(OVER_UI) || e.target.closest('.wb-edit')) return;
    e.preventDefault(); openContextMenu(e);
  });

  /// One place that runs a named command (context menu, command palette, keyboard).
  function runCommand(c, anchor) {
    const els = selected(), w = cmenuAt || toWorld(rect().width / 2, rect().height / 2);
    const passToBar = act => { const b = document.createElement('button'); b.dataset.act = act; b.getBoundingClientRect = () => (anchor || root).getBoundingClientRect(); props.dispatchEvent(new CustomEvent('wb-act', { detail: b })); };
    switch (c) {
      case 'copy': copySel(els); break;
      case 'cut': copySel(els); remove(els.filter(x => !x.locked)); break;
      case 'paste': pasteClip(w); break;
      case 'selall': sel = new Set(live().filter(x => x.type !== 'frame').map(x => x.id)); paintProps(); render(); break;
      case 'dup': case 'front': case 'back': case 'group': case 'ungroup': case 'arrange': case 'lock': case 'link': case 'task': case 'lib': case 'copystyle': case 'pastestyle': case 'del': passToBar(c); break;
      case 'comment': { const one = els[0]; const b = one ? bounds(one, get) : null; startComment(b ? { x: b.x + b.w, y: b.y } : w, one || null); break; }
      case 'zoomsel': glideFit(els, { max: 2 }); break;
      case 'addsticky': { const s = create('sticky', { x: w.x - 100, y: w.y - 80, w: 200, h: 160, color: stickyColor, text: '' }); const [p] = add([s]) || []; if (p) editSoon2(p); break; }
      case 'addtext': { const t = create('text', { x: w.x, y: w.y - 13, w: 40, h: 26, text: '', size: 20, font: 'ipop', stroke: inkNow() }); const [p] = add([t]) || []; if (p) editSoon2(p); break; }
      case 'addshape': { const s = makeShape(lastShape || 'rect', { x: w.x - 80, y: w.y - 50 }, ''); const [p] = add([{ ...s, w: s.w || 160, h: s.h || 100 }]) || []; if (p) editSoon2(p); break; }
      case 'fit': glideFit(); break;
      case 'tpl': openCenter(); break;
      case 'palette': openPalette(); break;
      case 'keys': toggleKeys(); break;
      case 'copyimg': runCorner(Object.assign(document.createElement('button'), { dataset: { c: 'export' } }), {}); break;
      default: break;
    }
    cmenuAt = null;
  }

  // ── Command palette (Ctrl /) ──
  const palOv = el('div', 'wb-palov'); palOv.hidden = true;
  palOv.innerHTML = `<div class="wb-pal" role="dialog" aria-label="Command palette"><div class="wb-palq">${icon('search')}<input type="search" placeholder="Type a command, tool or template…  (e.g. notulen, timer, export)" aria-label="Command"></div><ul class="wb-pall" role="listbox"></ul><div class="wb-palft"><span><kbd>↑</kbd><kbd>↓</kbd> move</span><span><kbd>Enter</kbd> run</span><span><kbd>Esc</kbd> close</span></div></div>`;
  root.append(palOv);
  palOv.addEventListener('pointerdown', e => { e.stopPropagation(); if (e.target === palOv) closePalette(); });
  const palInput = palOv.querySelector('input'), palList = palOv.querySelector('.wb-pall');
  let palItems = [], palKb = 0;
  function paletteCommands() {
    const toolCmd = (k, label, key, words = '') => ({ group: 'Tool', label, key, words, ic: ICONS[k] ? k : 'pen', run: () => (k === 'pen' ? setPen(prefs.ptype) : setTool(k)) });
    const out = [
      toolCmd('select', 'Select', 'V', 'pilih'), toolCmd('hand', 'Hand', 'H', 'geser'), toolCmd('zoom', 'Zoom', 'Z', 'perbesar'), toolCmd('pen', 'Pen', 'P', 'pena gambar'),
      toolCmd('eraser', 'Eraser', 'E', 'hapus penghapus'), toolCmd('sticky', 'Sticky note', 'S', 'catatan tempel'), toolCmd('text', 'Text', 'T', 'teks tulisan'),
      toolCmd('rect', 'Shape', 'R', 'bentuk kotak'), toolCmd('connector', 'Connector', 'A', 'panah garis'), toolCmd('frame', 'Frame', 'F', 'bingkai slide'), toolCmd('laser', 'Laser pointer', 'L', 'presentasi'),
      ...(opts.comments ? [toolCmd('comment', 'Comment', 'C', 'komentar')] : []),
      ...PEN_ORDER.map(k => ({ group: 'Pen', label: PEN_TYPES[k].label, key: '', words: `pen ${PEN_TYPES[k].sub}`, ic: 'pen', run: () => setPen(k) })),
      ...TEMPLATE_META.map(t => ({ group: 'Template', label: t.name, key: '', words: `${t.desc} ${t.cat} ${t.aliases || ''}`, ic: 'grid', run: () => { tcs.where = 'board'; useTemplate(t.key); } })),
      ...boardTools().map(x => ({ group: 'Action', label: x.label, key: x.hint || '', words: `${x.desc} ${x.group}`, ic: x.ic, run: () => runCorner(Object.assign(document.createElement('button'), { dataset: { c: x.c } }), {}) })),
      { group: 'Action', label: 'Templates', key: 'Shift G', words: 'template galeri', ic: 'grid', run: () => openCenter() },
      { group: 'Action', label: 'Save board as template', key: '', words: 'simpan template', ic: 'bookplus', run: () => openSaveTemplate() },
      { group: 'Action', label: live().some(x => x.howTo && x.hide) ? 'Show “How to use” notes' : 'Hide “How to use” notes', key: '', words: 'sembunyikan petunjuk', ic: 'note', run: () => toggleHowTo() },
      { group: 'View', label: 'Fit everything', key: 'Shift 1', words: 'semua lihat', ic: 'layout', run: () => glideFit() },
      { group: 'View', label: 'Zoom to 100%', key: 'Ctrl 0', words: 'ukuran asli', ic: 'search', run: () => glideZoom(1 / view.z) },
      { group: 'View', label: 'Keyboard shortcuts', key: '?', words: 'pintasan tombol', ic: 'keys', run: () => toggleKeys() },
      ...(opts.onFullscreen && mode === 'board' ? [{ group: 'View', label: fullscreen ? 'Exit full screen' : 'Full screen', key: 'Shift F', words: 'layar penuh', ic: 'newtab', run: () => toggleFullscreen() }] : []),
    ];
    return out;
  }
  function paintPalette() {
    const q = palInput.value.trim().toLowerCase();
    const words = q.split(/\s+/).filter(Boolean);
    palItems = paletteCommands().filter(c => !words.length || words.every(w => `${c.label} ${c.group} ${c.words}`.toLowerCase().includes(w)))
      // labels that start with what was typed come first
      .sort((a, b) => (b.label.toLowerCase().startsWith(q) ? 1 : 0) - (a.label.toLowerCase().startsWith(q) ? 1 : 0)).slice(0, 60);
    palKb = Math.min(palKb, Math.max(0, palItems.length - 1));
    palList.innerHTML = palItems.length ? palItems.map((c, i) => `<li role="option" data-pi="${i}" class="${i === palKb ? 'kb' : ''}" aria-selected="${i === palKb}"><span class="wb-palic">${icon(c.ic)}</span><span class="wb-pallb">${esc(c.label)}</span>${c.key ? `<kbd>${esc(c.key)}</kbd>` : ''}<span class="wb-palg">${c.group}</span></li>`).join('') : '<li class="wb-palnone">Nothing found. Try other words.</li>';
  }
  function openPalette() { if (destroyed) return; palInput.value = ''; palKb = 0; paintPalette(); palOv.hidden = false; palInput.focus({ preventScroll: true }); }
  function closePalette() { palOv.hidden = true; root.focus({ preventScroll: true }); }
  function runPalette(i) { const c = palItems[i]; if (!c) return; closePalette(); try { c.run(); } catch { /* the command explains itself */ } }
  palInput.addEventListener('input', () => { palKb = 0; paintPalette(); });
  palInput.addEventListener('keydown', e => {
    e.stopPropagation();
    if (e.key === 'Escape') { e.preventDefault(); closePalette(); return; }
    if (e.key === 'ArrowDown' || e.key === 'ArrowUp') { e.preventDefault(); palKb = Math.max(0, Math.min(palItems.length - 1, palKb + (e.key === 'ArrowDown' ? 1 : -1))); paintPalette(); palList.children[palKb]?.scrollIntoView({ block: 'nearest' }); return; }
    if (e.key === 'Enter') { e.preventDefault(); runPalette(palKb); }
  });
  palList.addEventListener('click', e => { const li = e.target.closest('[data-pi]'); if (li) runPalette(+li.dataset.pi); });
  palList.addEventListener('pointermove', e => { const li = e.target.closest('[data-pi]'); if (li && +li.dataset.pi !== palKb) { palKb = +li.dataset.pi; palList.querySelectorAll('[data-pi]').forEach(x => x.classList.toggle('kb', x === li)); } });

  // ── Keyboard shortcuts panel: grouped and searchable ──
  const KEY_GROUPS = [
    ['Tools', [['V', 'Select'], ['H', 'Hand (or hold Space)'], ['Z', 'Zoom (or hold Z)'], ['P', 'Pen'], ['M', 'Highlighter'], ['E', 'Eraser'], ['S', 'Sticky note (again: next colour)'], ['T', 'Text'], ['R', 'Shape (again: next shape)'], ['A', 'Connector'], ['F', 'Frame'], ['C', 'Comment'], ['L', 'Laser pointer'], ['I', 'Picture or file'], ['Shift T', 'Task or page card']]],
    ['Edit', [['Ctrl Z / Ctrl Y', 'Undo / redo'], ['Ctrl C / X / V', 'Copy / cut / paste'], ['Ctrl D', 'Duplicate'], ['Alt + drag', 'Duplicate while moving'], ['Ctrl G', 'Group (Shift: ungroup)'], ['Ctrl Shift C / V', 'Copy / paste style'], ['Ctrl ] / [', 'Front / back'], ['Arrows', 'Move 1 px (Shift: 10 px)'], ['Del', 'Delete'], ['Ctrl K', 'Link'], ['Enter', 'Edit text'], ['Ctrl Enter', 'Next sticky (while typing)'], ['Tab / Shift Enter', 'Mind map branch / sibling'], ['Alt + draw', 'Lasso select']]],
    ['View', [['Ctrl /', 'Command palette'], ['Shift 1', 'Fit everything'], ['Shift 2', 'Zoom to selection'], ['Ctrl + / −', 'Zoom in / out'], ['Ctrl 0', 'Zoom to 100%'], ['Shift F', 'Full screen'], ['Ctrl F', 'Find on board'], ['Shift G', 'Templates'], ['[ / ]', 'Pen size'], ['?', 'This list']]],
  ];
  function paintKeys(q = '') {
    const ql = q.trim().toLowerCase();
    const groups = KEY_GROUPS.map(([g, rows]) => [g, rows.filter(([k, d]) => !ql || `${k} ${d}`.toLowerCase().includes(ql))]).filter(([, r]) => r.length);
    keysPanel.querySelector('.wb-kbody').innerHTML = groups.length ? `<div class="wb-kgrid3">${groups.map(([g, rows]) => `<div><h5>${g}</h5>${rows.map(([k, d]) => `<div class="wb-krow"><span>${esc(d)}</span><kbd>${esc(k)}</kbd></div>`).join('')}</div>`).join('')}</div>` : '<p class="wb-note">No shortcut matches.</p>';
  }

  dockReady = true;
  // ── Corners: templates and full screen ──────────────────────────────────
  const TPL_EMOJI = { retro: '🔁', flowchart: '🔀', brainstorm: '💡', mindmap: '🧠', kanban: '🗂️', swot: '🧭', timeline: '🗓️', journey: '🚶', fishbone: '🐟', agenda: '📋', orgchart: '🏢' };
  Object.assign(TPL_EMOJI, { basicflow: '🟢', approval: '✅', swimflow: '🏊', sop: '🔢', bpmn: '⚙️', erd: '🗄️', network: '🖧', valuestream: '🏭', projecttimeline: '📆' });
  let galleryCat = 'All';
  const TPL_CATS = ['All', 'Brainstorm', 'Flowchart & process', 'Technical', 'Planning'];
  const galleryHtml = (cat = galleryCat) => TEMPLATE_INFO.filter(t => cat === 'All' || t[3] === cat).map(([k, n, d, , guided]) => `<button type="button" class="wb-tpl" data-tpl="${k}"><span class="wb-tpl-e" aria-hidden="true">${TPL_EMOJI[k] || '▦'}</span><span><b>${n}</b><small>${d}</small>${guided ? '<em class="wb-tag">Guided</em>' : ''}</span></button>`).join('');
  const galleryTabs = () => `<div class="wb-seg wb-tcats" role="tablist">${TPL_CATS.map(c => `<button type="button" role="tab" data-tcat="${c}" class="${c === galleryCat ? 'on' : ''}" aria-selected="${c === galleryCat}">${c}</button>`).join('')}</div>`;
  function openGallery(cat) {
    if (cat) galleryCat = cat;
    shapePanel.hidden = true; shapeTip.hidden = true; if (!r4.hidden) { r4.hidden = true; clearVersionView(); }
    gallery.innerHTML = `<div class="wb-ph"><b>Start from a template</b><button type="button" data-close aria-label="Close">✕</button></div>${galleryTabs()}<p class="wb-note">Added beside what is already on the board, never over it.</p><div class="wb-tgrid">${galleryHtml()}</div>`;
    gallery.hidden = false;
  }
  let fullscreen = false;
  // The corner buttons are rebuilt only when what they show changes. Rebuilding them on every cursor move or timer tick
  // (others' awareness fires many times a second) replaced a button between press and release, so clicks were lost and
  // people had to click several times.
  let cornerHtml = { l: null, r: null };
  const setHtml = (node, key, html) => { if (cornerHtml[key] === html) return false; cornerHtml[key] = html; node.innerHTML = html; return true; };
  // the timer chip's label ticks every half second: update its text in place
  function paintTimerChip() {
    const b = topRight.querySelector('[data-c="timer"] span');
    if (b) b.textContent = timerEnds ? timerLabel() : 'Timer';
    topRight.querySelector('[data-c="timer"]')?.classList.toggle('on', !!timerEnds);
  }
  function paintCorners() {
    const canTemplates = !readOnly && mode === 'board';
    setHtml(topLeft, 'l', canTemplates ? `<button type="button" class="wb-chip ${!shapePanel.hidden ? 'on' : ''}" data-c="shapes" aria-haspopup="dialog">${icon('shapes')}<span>Shapes</span></button><button type="button" class="wb-chip" data-c="tpl" aria-haspopup="dialog">${icon('grid')}<span>Templates</span></button>` : '');
    topLeft.hidden = !canTemplates;
    const fs = mode === 'board' && opts.onFullscreen;
    const lockChip = opts.boardLock?.canChange?.() ? `<button type="button" class="wb-chip ${opts.boardLock.isLocked() ? 'on' : ''}" data-c="lockboard" title="${opts.boardLock.isLocked() ? 'Unlock the board so people can edit it' : 'Lock the board: everyone can still view, comment and vote'}" aria-pressed="${opts.boardLock.isLocked()}">${icon(opts.boardLock.isLocked() ? 'lock' : 'unlock')}<span>${opts.boardLock.isLocked() ? 'Locked' : 'Lock'}</span></button>` : '';
    const flowChips = mode === 'board' && !readOnly ? `<button type="button" class="wb-chip ${!guidePanel.hidden ? 'on' : ''}" data-c="guide" title="Step-by-step help to make a flowchart">${icon('bulb')}<span>Guide</span></button>`
      + (flowOn ? `<button type="button" class="wb-chip" data-c="tidy" title="Tidy up flow (top to bottom; Shift: left to right)">${icon('tidy')}<span>Tidy up</span></button><button type="button" class="wb-chip ${!checkPanel.hidden ? 'on' : ''}" data-c="check" title="Check this flowchart">${icon('check')}<span>Check</span><span class="wb-badge" hidden>0</span></button>` : '')
      + `<button type="button" class="wb-chip" data-c="import" title="Import a draw.io file or Mermaid text">${icon('import')}</button>` : '';
    void flowChips; void lockChip;
    // Chips that stay in view: what people use all the time, or what shows a live state (a running timer, a vote, a lock).
    const live = [];
    if (mode === 'board' && !readOnly && flowOn) live.push(`<button type="button" class="wb-chip ${!checkPanel.hidden ? 'on' : ''}" data-c="check" title="Check this flowchart">${icon('check')}<span>Check</span><span class="wb-badge" hidden>0</span></button>`);
    // the countdown text is filled in afterwards (paintTimerChip), so the ticking label never rebuilds the buttons
    if (mode === 'board' && timerEnds && aw) live.push(`<button type="button" class="wb-chip on" data-c="timer" title="Timer everyone sees">${icon('timer')}<span></span></button>`);
    if (mode === 'board' && vmap && votingOn()) live.push(`<button type="button" class="wb-chip on" data-c="vote" title="Dot voting">${icon('vote')}<span>Voting · ${Math.max(0, (round()?.per || 0) - myVotes().length)} left</span></button>`);
    if (mode === 'board' && opts.boardLock?.isLocked?.()) live.push(`<button type="button" class="wb-chip on" data-c="lockboard" title="Unlock the board so people can edit it">${icon('lock')}<span>Locked</span></button>`);
    if (mode === 'board' && sinceLast) live.push(`<button type="button" class="wb-chip on wb-chg" data-c="changes" title="Since you last opened: ${esc(sinceLast.text)}">${icon('history')}<span>${(n => `${n} change${n === 1 ? '' : 's'}`)(sinceLast.added.length + sinceLast.changed.length + sinceLast.removed.length)}</span></button>`);
    if (mode === 'board') live.push(`<button type="button" class="wb-chip" data-c="present" title="Present frame by frame">${icon('play')}<span>Present</span></button>`);
    if (fs) live.push(`<button type="button" class="wb-chip" data-c="fs" title="${fullscreen ? 'Exit full screen (Esc)' : 'Full screen (Shift F)'}" aria-label="${fullscreen ? 'Exit full screen' : 'Full screen'}" aria-pressed="${fullscreen}"><svg class="wb-i" viewBox="0 0 24 24" aria-hidden="true">${fullscreen ? '<path d="M4 14h6v6M20 10h-6V4M14 10l7-7M3 21l7-7"/>' : '<path d="M15 3h6v6M9 21H3v-6M21 3l-7 7M3 21l7-7"/>'}</svg></button>`);
    if (mode === 'board') live.push(`<button type="button" class="wb-chip ${!boardMenu.hidden ? 'on' : ''}" data-c="menu" title="More: guide, tidy up, find, frames, timer, vote, export…" aria-label="More board tools" aria-haspopup="menu">${icon('more')}</button>`);
    const changed = setHtml(topRight, 'r', live.join(''));
    if (changed || !people.isConnected) topRight.prepend(people);
    paintTimerChip();
    topRight.hidden = !live.length && people.hidden;
    if (!boardMenu.hidden) paintBoardMenu();
  }

  // ── The board menu (⋯): every board tool, searchable, grouped ───────────
  const boardMenu = el('div', 'wb-panel wb-boardmenu'); boardMenu.hidden = true; boardMenu.setAttribute('role', 'menu'); boardMenu.setAttribute('aria-label', 'Board tools');
  root.append(boardMenu);
  boardMenu.addEventListener('pointerdown', e => e.stopPropagation());
  let boardMenuQ = '';
  // Every board tool: a short label and one grey line saying what it does (no cut-off "…" labels). Left column:
  // Diagram, Network, Meeting; right column: Board, Share (mockup whiteboard-menu-polish).
  const MENU_LEFT = ['Diagram', 'Network', 'Meeting'];
  function boardTools() {
    const t = [];
    const add2 = (group, c, label, ic, desc, hint = '', on = false) => t.push({ group, c, label, ic, desc, hint, on });
    if (!readOnly) {
      add2('Diagram', 'guide', 'Guide', 'bulb', 'Make a flowchart step by step', '', !guidePanel.hidden);
      if (flowOn) add2('Diagram', 'tidy', 'Tidy up flow', 'tidy', 'Line the steps up neatly', 'Alt Shift T');
      if (flowOn) add2('Diagram', 'check', 'Check flowchart', 'check', 'Missing Start, End or branches', '', !checkPanel.hidden);
      add2('Diagram', 'import', 'Import', 'import', 'draw.io file or Mermaid text');
      if (opts.library) add2('Diagram', 'lib', 'Library', 'book', 'Your saved shapes and the team’s');
      add2('Diagram', 'relayout', 'Re-layout', 'layout', 'Hierarchy, radial or organic');
      add2('Diagram', 'graphics', 'Data graphics', 'chart', 'Bars, icons and colours from shape data', '', rulesNow().show && rulesNow().show !== 'off');
      if (opts.people) add2('Diagram', 'org', 'Org chart', 'org', 'A team from Directory, with photos');
      if (opts.tasks?.createMany) add2('Diagram', 'tasks', 'Make tasks', 'task', 'Turn steps into Projects tasks');
      add2('Network', 'ipfind', 'Find a device', 'ip', 'By IP, hostname or name');
    }
    if (aw) add2('Meeting', 'timer', timerEnds ? `Timer · ${timerLabel()}` : 'Timer', 'timer', 'A countdown everyone sees', '', !!timerEnds);
    if (vmap) add2('Meeting', 'vote', 'Dot voting', 'vote', votingOn() ? 'Voting is running' : 'Everyone picks their favourites', '', votingOn());
    add2('Meeting', 'present', 'Present', 'play', framesInOrder().length ? 'Frame by frame, full screen' : 'The whole board, full screen');
    add2('Board', 'find', 'Find on board', 'search', 'Words on shapes and stickies', 'Ctrl F');
    add2('Board', 'frames', 'Frames', 'frames', 'Jump to a frame');
    if (opts.onPopOut) add2('Board', 'popout', 'Open in a new tab', 'newtab', 'The board on its own');
    if (opts.boardLock?.canChange?.()) add2('Board', 'lockboard', opts.boardLock.isLocked() ? 'Unlock board' : 'Lock board', opts.boardLock.isLocked() ? 'unlock' : 'lock', opts.boardLock.isLocked() ? 'Let people edit again' : 'People can still view, comment and vote', '', opts.boardLock.isLocked());
    add2('Board', 'comments', 'Comments', 'comment', 'Everything said on this board');
    if (opts.history) add2('Board', 'history', 'Version history', 'history', 'See and restore earlier versions');
    add2('Share', 'export', 'Export', 'download', 'Picture, SVG or PDF');
    add2('Share', 'report', 'Report', 'table', 'Shapes and their data to Excel');
    add2('Share', 'pptx', 'PowerPoint', 'slides', 'Each frame becomes a slide');
    if (opts.shareToChat) add2('Share', 'chat', 'Send to Chat', 'chat', 'A live card in a chat');
    return t;
  }
  // The menu is painted by many things (others' cursors, the timer, a lock change): rebuilding it each time replaced the row
  // under the pointer, so the hover flickered, the list jumped back to the top and clicks were lost. Now the search box is
  // built once, the list is rebuilt only when the items themselves change, and "on" states and labels are updated in place.
  const menuSearch = el('input', 'wb-search');
  menuSearch.type = 'search'; menuSearch.placeholder = 'Search board tools'; menuSearch.setAttribute('aria-label', 'Search board tools');
  const menuList = el('div', 'wb-mlist');
  const menuFoot = el('div', 'wb-mfoot', '<span>↑↓ to move · Enter to open · Esc to close</span><span data-n></span>');
  boardMenu.append(menuSearch, menuList, menuFoot);
  let menuShape = '', menuKb = -1;
  const menuRows = () => [...menuList.querySelectorAll('.wb-mitem')];
  const markKb = () => menuRows().forEach((b, i) => b.classList.toggle('kb', i === menuKb));
  menuSearch.addEventListener('input', () => { boardMenuQ = menuSearch.value; menuKb = -1; paintBoardMenu(); });
  menuSearch.addEventListener('keydown', e => {
    e.stopPropagation();
    if (e.key === 'Escape') { boardMenu.hidden = true; paintCorners(); topRight.querySelector('[data-c="menu"]')?.focus({ preventScroll: true }); return; }
    if (e.key === 'ArrowDown' || e.key === 'ArrowUp') {
      e.preventDefault();
      const rows = menuRows(); if (!rows.length) return;
      menuKb = Math.max(0, Math.min(rows.length - 1, menuKb + (e.key === 'ArrowDown' ? 1 : -1)));
      markKb(); rows[menuKb].scrollIntoView({ block: 'nearest' });
      return;
    }
    if (e.key === 'Enter') { e.preventDefault(); (menuRows()[Math.max(0, menuKb)])?.click(); }
  });
  function paintBoardMenu() {
    const q = boardMenuQ.trim().toLowerCase();
    const items = boardTools().filter(x => !q || `${x.label} ${x.desc} ${x.group}`.toLowerCase().includes(q));
    if (menuSearch.value !== boardMenuQ) menuSearch.value = boardMenuQ;
    const shape = items.map(x => `${x.group}/${x.c}`).join('|');
    if (shape !== menuShape) {
      menuShape = shape;
      const row = x => `<button type="button" role="menuitem" data-c="${x.c}" class="wb-mitem"><span class="wb-mic">${icon(x.ic)}</span><span class="wb-mtext"></span>${x.hint ? `<kbd>${x.hint}</kbd>` : '<span></span>'}<span class="wb-mdesc"></span></button>`;
      const col = names => names.map(g => { const list = items.filter(x => x.group === g); return list.length ? `<div class="wb-mh">${g}</div>${list.map(row).join('')}` : ''; }).join('');
      // Diagram always starts the left column and Board the right one; Network, Meeting and Share each go to whichever
      // column is shorter at that point, so the two stay about the same height (a flowchart adds Tidy up and Check)
      const size = g => { const n = items.filter(x => x.group === g).length; return n ? n + 0.7 : 0; };
      const leftG = ['Diagram'], rightG = ['Board'];
      let lh = size('Diagram'), rh = size('Board');
      for (const g of ['Network', 'Meeting', 'Share']) { if (!size(g)) continue; if (lh <= rh) { leftG.push(g); lh += size(g); } else { rightG.push(g); rh += size(g); } }
      // a phone shows one list in a bottom sheet, in the plain order
      const left = isPhone() ? col(['Diagram', 'Network', 'Meeting', 'Board', 'Share']) : col(leftG), right = isPhone() ? '' : col(rightG);
      menuList.innerHTML = left || right ? `<div class="wb-mcol">${left}</div><div class="wb-mcol">${right}</div>` : '<p class="wb-note">Nothing found. Try “export”, “timer” or “rack”.</p>';
      menuFoot.querySelector('[data-n]').textContent = `${items.length} tool${items.length === 1 ? '' : 's'}`;
      menuKb = Math.min(menuKb, items.length - 1); markKb();
    }
    // labels, descriptions and on/off can change without the list changing (a running timer, a lock): update in place
    for (const x of items) {
      const b = menuList.querySelector(`[data-c="${x.c}"]`); if (!b) continue;
      b.classList.toggle('on', !!x.on);
      const t = b.querySelector('.wb-mtext'); if (t.textContent !== x.label) t.textContent = x.label;
      const d = b.querySelector('.wb-mdesc'); if (d.textContent !== x.desc) { d.textContent = x.desc; b.title = x.desc; }
    }
    // a short board shows the labels only (the grey lines become tooltips), so the menu never needs scrolling
    // on a short laptop the grey lines go (into tooltips) so the menu needs no scrolling; a phone sheet scrolls and keeps them
    boardMenu.classList.remove('wb-mshort');
    if (!boardMenu.hidden && !isPhone() && menuList.scrollHeight > menuList.clientHeight + 2) boardMenu.classList.add('wb-mshort');
  }
  function toggleBoardMenu() {
    if (!boardMenu.hidden) { boardMenu.hidden = true; paintCorners(); return; }
    boardMenuQ = '';
    if (!r4.hidden) { r4.hidden = true; clearVersionView(); }
    boardMenu.hidden = false;
    paintBoardMenu();
    paintCorners();
    menuList.scrollTop = 0; menuKb = -1; markKb();
    menuSearch.focus({ preventScroll: true });
  }
  // a pick in the menu runs the same action as the chip of the same name
  boardMenu.addEventListener('click', e => {
    const b = e.target.closest('[data-c]'); if (!b) return;
    boardMenu.hidden = true;
    const fake = document.createElement('button'); fake.dataset.c = b.dataset.c;
    runCorner(fake, e);
    paintCorners();
  });
  // While full screen, Esc must work wherever focus is (moving the board element to <body> drops its focus).
  const fsKeys = e => { if (e.key === 'Escape' && presenting) { e.preventDefault(); e.stopPropagation(); stopPresent(); return; }
    if (e.key === 'Escape' && fullscreen && !editing && keysPanel.hidden && gallery.hidden) { e.preventDefault(); e.stopPropagation(); toggleFullscreen(); } };
  async function toggleFullscreen() {
    if (!opts.onFullscreen) return;
    const r = await opts.onFullscreen(!fullscreen);
    fullscreen = typeof r === 'boolean' ? r : !fullscreen;
    if (fullscreen) document.addEventListener('keydown', fsKeys, true); else document.removeEventListener('keydown', fsKeys, true);
    paintCorners();
    requestAnimationFrame(() => { fit(); root.focus({ preventScroll: true }); });
  }
  for (const c of [topLeft, topRight, gallery, emptyGallery, libPanel]) c.addEventListener('pointerdown', e => e.stopPropagation());
  topLeft.addEventListener('click', e => {
    if (e.target.closest('[data-c="lib"]')) { toggleLibrary(); return; }
    if (e.target.closest('[data-c="shapes"]')) { toggleShapePanel(); paintCorners(); return; }
    if (!e.target.closest('[data-c="tpl"]')) return;
    openCenter();
  });
  topRight.addEventListener('click', e => { const b = e.target.closest('[data-c]'); if (b) runCorner(b, e); });
  function runCorner(b, e) {
    const c = b.dataset.c;
    const is = k => c === k;
    const target = { closest: sel2 => (sel2 === `[data-c="${c}"]` ? b : null) };
    if (is('menu')) { toggleBoardMenu(); return; }
    if (is('lib')) { toggleLibrary(); return; }
    e = { target, shiftKey: e?.shiftKey };
    if (e.target.closest('[data-c="fs"]')) toggleFullscreen();
    else if (e.target.closest('[data-c="frames"]')) toggleFrames();
    else if (e.target.closest('[data-c="present"]')) startPresent();
    else if (e.target.closest('[data-c="timer"]')) toggleTimerPanel();
    else if (e.target.closest('[data-c="vote"]')) toggleVotePanel();
    else if (e.target.closest('[data-c="export"]')) toggleExport();
    else if (e.target.closest('[data-c="find"]')) openFind();
    else if (e.target.closest('[data-c="guide"]')) { const show = guidePanel.hidden; for (const p of [checkPanel, framesPanel, timerPanel, votePanel, exportPanel]) p.hidden = true; guidePanel.hidden = !show; if (show) { try { localStorage.removeItem(HINTS_KEY); } catch { /* blocked */ } } paintGuide(); paintCorners(); }
    else if (e.target.closest('[data-c="check"]')) { const show = checkPanel.hidden; for (const p of [guidePanel, framesPanel, timerPanel, votePanel, exportPanel]) p.hidden = true; checkPanel.hidden = !show; paintCheck(); paintCorners(); }
    else if (e.target.closest('[data-c="tidy"]')) tidyFlow(e.shiftKey ? 'LR' : 'TB');
    else if (e.target.closest('[data-c="import"]')) toggleImport();
    else if (e.target.closest('[data-c="popout"]')) opts.onPopOut?.();
    else if (e.target.closest('[data-c="lockboard"]')) opts.boardLock?.toggle?.();
    else if (is('relayout')) togglePanelR4('relayout');
    else if (is('graphics')) togglePanelR4('graphics');
    else if (is('org')) togglePanelR4('org');
    else if (is('tasks')) togglePanelR4('tasks');
    else if (is('ipfind')) togglePanelR4('ipfind');
    else if (is('comments')) togglePanelR4('comments');
    else if (is('history')) togglePanelR4('history');
    else if (is('report')) togglePanelR4('report');
    else if (is('pptx')) exportPptx();
    else if (is('chat')) opts.shareToChat?.();
    else if (is('changes')) togglePanelR4('changes');
  }
  const pickTemplate = e => {
    if (e.target.closest('[data-close]')) { gallery.hidden = true; return; }
    if (e.target.closest('[data-tbrowse]')) { openCenter('featured'); return; }
    if (e.target.closest('[data-tguide]')) { runCorner(Object.assign(document.createElement('button'), { dataset: { c: 'guide' } }), {}); return; }
    if (e.target.closest('[data-timport]')) { toggleImport(); return; }
    const tu = e.target.closest('[data-tuse]'); if (tu) { useTemplate(tu.dataset.tuse); return; }
    const tp = e.target.closest('[data-tprev], [data-tk]'); if (tp && emptyGallery.contains(tp)) { openCenter('featured').then(() => { tcs.zoom = 1; showPreview(tp.dataset.tprev || tp.dataset.tk); }); return; }
    const tc = e.target.closest('[data-tcat]');
    if (tc) { galleryCat = tc.dataset.tcat; if (gallery.contains(tc)) openGallery(); else emptyGallery.innerHTML = emptyHtml(); return; }
    const b = e.target.closest('[data-tpl]'); if (!b) return;
    gallery.hidden = true;
    const n = api.applyTemplate(b.dataset.tpl);
    if (n) toast(TEMPLATE_INFO.find(t => t[0] === b.dataset.tpl)?.[4] ? 'Template added. Click a shape for hints on what comes next; double-click to change its text.' : 'Template added. Change anything on it.');
  };
  gallery.addEventListener('click', pickTemplate);
  emptyGallery.addEventListener('click', pickTemplate);
  const emptyHtml = () => `<b>Start with a blank board, or pick a template</b><p class="wb-note">Each template comes in a frame with a “How to use” note you can hide in one click.</p><div class="wb-tcgrid wb-feat6">${TEMPLATE_META.filter(t => t.featured).map(tcCard).join('')}</div><div class="wb-row wb-startrow"><button type="button" class="wb-cb wb-txt wb-pri" data-tbrowse>Browse all templates</button><button type="button" class="wb-cb wb-txt" data-tguide>Make a flowchart (Guide)</button><button type="button" class="wb-cb wb-txt" data-timport>Import Mermaid / draw.io</button></div><p class="wb-note" hidden>Making a flowchart? Press <b>Guide</b> at the top for step-by-step help. Or pick a tool below and start drawing.</p>`;
  function paintEmpty(isEmpty) {
    // the start card steps aside as soon as a tool is picked (or the person starts drawing)
    const show = isEmpty && !readOnly && mode === 'board' && !interaction && tool === 'select' && !dockKind;
    if (show && emptyGallery.hidden) emptyGallery.innerHTML = emptyHtml();
    emptyGallery.hidden = !show;
    root.classList.toggle('wb-empty', isEmpty && !show);
  }

  // ── Working together: cursors, what others hold, Follow, laser ──────────
  // Shared through the page's Yjs awareness (field "wb"): { board, x, y, sel, laser:[[x,y,t]...], view:{x,y,z,w,h} }.
  // Nothing of this is stored; it disappears when someone leaves.
  const boardKey = opts.boardKey || 'board';
  function now() { return Date.now(); }
  function setMine(patch) {
    if (!aw) return;
    const cur = aw.getLocalState()?.wb || {};
    aw.setLocalStateField('wb', { ...cur, board: boardKey, ...patch });
  }
  let cursorTimer = 0;
  root.addEventListener('pointermove', e => {
    if (!aw) return;
    const { sx, sy } = local(e);
    lastCursor = toWorld(sx, sy);
    if (!cursorTimer) cursorTimer = setTimeout(() => { cursorTimer = 0; setMine({ x: Math.round(lastCursor.x), y: Math.round(lastCursor.y) }); }, 50);
  });
  root.addEventListener('pointerleave', () => { if (aw) setMine({ x: null, y: null }); });
  let viewTimer = 0;
  function shareView() {
    if (!aw || viewTimer) return;
    viewTimer = setTimeout(() => { viewTimer = 0; const r = rect(); setMine({ view: { x: Math.round(view.x), y: Math.round(view.y), z: +view.z.toFixed(3), w: Math.round(r.width), h: Math.round(r.height) } }); }, 120);
  }
  function laserAt(w) {
    laserTrail.push([Math.round(w.x), Math.round(w.y), now()]);
    laserTrail = laserTrail.filter(p => now() - p[2] < 1200).slice(-40);
    setMine({ laser: laserTrail });
    paintLive();
    clearTimeout(laserFade); laserFade = setTimeout(function fade() { laserTrail = laserTrail.filter(p => now() - p[2] < 1200); setMine({ laser: laserTrail }); paintLive(); if (laserTrail.length) laserFade = setTimeout(fade, 120); }, 120);
  }
  // One face per person: the same person in two tabs (or a tab that just reloaded) is shown once, using the newest state.
  const others = () => {
    const byPerson = new Map();
    aw?.getStates().forEach((st, id) => {
      if (id === aw.clientID || st?.wb?.board !== boardKey || !st.user) return;
      const key = st.user.nik || st.user.name || String(id);
      if (key === (aw.getLocalState()?.user?.nik ?? null)) return;   // my own other tab
      const prev = byPerson.get(key);
      if (!prev || id > prev.id) byPerson.set(key, { id, user: st.user, wb: st.wb });
    });
    return [...byPerson.values()];
  };
  const TYPING_IDLE = 30000;
  /// The other person typing in this element right now (their name), or null.
  function typingBy(id) {
    for (const o of others()) { const t = o.wb.typing; if (t?.id === id && now() - (t.at || 0) < TYPING_IDLE) return o; }
    return null;
  }
  const liveLayer = svgEl('g', { class: 'wb-live' }, svg);
  function paintLive() {
    liveLayer.innerHTML = '';
    const list = others();
    for (const o of list) {
      const color = o.user.color || '#5856D6';
      // what they hold
      for (const id of o.wb.sel || []) {
        const e = get(id); if (!e || e.isDeleted) continue;
        const b = bounds(e, get), a = toScreen(b.x, b.y), z = toScreen(b.x + b.w, b.y + b.h);
        svgEl('rect', { x: a.x - 5, y: a.y - 5, width: z.x - a.x + 10, height: z.y - a.y + 10, rx: 5, fill: 'none', stroke: color, 'stroke-width': 2, 'stroke-dasharray': '5 3' }, liveLayer);
      }
      // typing in a shape: a purple "<Name> is typing…" pill over it and a dashed ring (the shape is locked for others)
      const ty = o.wb.typing;
      if (ty?.id && now() - (ty.at || 0) < TYPING_IDLE) {
        const e2 = get(ty.id);
        if (e2 && !e2.isDeleted) {
          const b = bounds(e2, get), a = toScreen(b.x, b.y), z = toScreen(b.x + b.w, b.y + b.h);
          svgEl('rect', { x: a.x - 6, y: a.y - 6, width: z.x - a.x + 12, height: z.y - a.y + 12, rx: 8, fill: 'none', stroke: color, 'stroke-width': 2, 'stroke-dasharray': '6 4' }, liveLayer);
          const name = String(o.user.name || 'Someone').split(' ')[0];
          const label = `${name} is typing…`, w = measure(label, 11.5, 'ipop', 600) + 18;
          const g = svgEl('g', { class: 'wb-typing', transform: `translate(${a.x - 6} ${Math.max(4, a.y - 32)})` }, liveLayer);
          svgEl('rect', { width: w, height: 22, rx: 11, fill: color }, g);
          const t = svgEl('text', { x: 9, y: 15, 'font-size': 11.5, 'font-weight': 600, fill: '#fff', 'font-family': FONT_STACK.ipop }, g);
          t.textContent = label;
        }
      }
      // laser trail
      const trail = (o.wb.laser || []).filter(p => now() - p[2] < 1500);
      if (trail.length > 1) {
        let d = '';
        trail.forEach((p, i) => { const q = toScreen(p[0], p[1]); d += `${i ? 'L' : 'M'}${q.x.toFixed(1)} ${q.y.toFixed(1)}`; });
        svgEl('path', { d, fill: 'none', stroke: '#FF3B30', 'stroke-width': 5, 'stroke-linecap': 'round', 'stroke-linejoin': 'round', opacity: 0.55 }, liveLayer);
        const l = toScreen(trail.at(-1)[0], trail.at(-1)[1]);
        svgEl('circle', { cx: l.x, cy: l.y, r: 6, fill: '#FF3B30' }, liveLayer);
      }
      // cursor + name
      if (Number.isFinite(o.wb.x) && Number.isFinite(o.wb.y)) {
        const c = toScreen(o.wb.x, o.wb.y);
        const g = svgEl('g', { transform: `translate(${c.x.toFixed(1)} ${c.y.toFixed(1)})`, class: 'wb-cursor' }, liveLayer);
        svgEl('path', { d: 'M0 0L5.5 15 7.7 8.6 14.5 6.6Z', fill: color, stroke: '#fff', 'stroke-width': 1.3, 'stroke-linejoin': 'round' }, g);
        const name = String(o.user.name || 'Someone').split(' ')[0];
        const w = measure(name, 11, 'ipop', 600) + 14;
        svgEl('rect', { x: 12, y: 14, width: w, height: 18, rx: 6, fill: color }, g);
        const t = svgEl('text', { x: 19, y: 26.5, 'font-size': 11, 'font-weight': 600, fill: '#fff', 'font-family': FONT_STACK.ipop }, g);
        t.textContent = name;
      }
    }
    // my own laser
    if (laserTrail.length > 1) {
      let d = '';
      laserTrail.forEach((p, i) => { const q = toScreen(p[0], p[1]); d += `${i ? 'L' : 'M'}${q.x.toFixed(1)} ${q.y.toFixed(1)}`; });
      svgEl('path', { d, fill: 'none', stroke: '#FF3B30', 'stroke-width': 5, 'stroke-linecap': 'round', opacity: 0.55 }, liveLayer);
    }
    paintPeople(list);
    checkPresenters(list);
    syncTimer(list);
    if (followingPresenter != null && following !== followingPresenter) following = followingPresenter;
    if (following != null) followStep(list);
  }
  function paintPeople(list) {
    if (!aw) { people.hidden = true; return; }
    people.hidden = !list.length;
    // rebuilt only when the faces change (this runs on every cursor move of everyone on the board)
    const html = list.slice(0, 6).map(o => {
      const name = String(o.user.name || 'Someone');
      const ini = name.split(/\s+/).map(w => w[0]).join('').slice(0, 2).toUpperCase();
      return `<button type="button" class="wb-face ${following === o.id ? 'on' : ''}" data-follow="${o.id}" style="background:${o.user.color || '#5856D6'}" title="${following === o.id ? 'Stop following' : 'Follow'} ${name.replace(/"/g, '')}" aria-label="${following === o.id ? 'Stop following' : 'Follow'} ${name.replace(/"/g, '')}">${ini}</button>`;
    }).join('') + (list.length > 6 ? `<span class="wb-face more">+${list.length - 6}</span>` : '');
    if (people._html !== html) { people._html = html; people.innerHTML = html; }
  }
  function followStep(list) {
    const o = list.find(x => x.id === following);
    if (!o) { stopFollow(); return; }
    const v = o.wb.view; if (!v) return;
    const r = rect();
    // show the same middle of the board at the same zoom (the other screen may be a different size)
    const cx = (v.w / 2 - v.x) / v.z, cy = (v.h / 2 - v.y) / v.z;
    view = { z: v.z, x: r.width / 2 - cx * v.z, y: r.height / 2 - cy * v.z };
    followBar.style.setProperty('--fc', o.user.color || '#5856D6');
    followBar.innerHTML = `<span>Following ${String(o.user.name || 'someone').split(' ')[0]}</span><button type="button" data-unfollow>Stop</button>`;
    followBar.hidden = false;
    root.style.setProperty('--wb-follow', o.user.color || '#5856D6');
    root.classList.add('wb-following');
    world.setAttribute('transform', `translate(${view.x} ${view.y}) scale(${view.z})`);
    { let g2 = 24 * view.z; if (!(g2 > 0.01)) g2 = 24; while (g2 < 12) g2 *= 2; root.style.backgroundSize = `${g2}px ${g2}px`; }
    root.style.backgroundPosition = `${view.x}px ${view.y}px`;
    paintOverlay(); paintZoom();
  }
  function stopFollow() {
    if (following == null) return;
    following = null; followingPresenter = null; followBar.hidden = true; root.classList.remove('wb-following');
    paintPeople(others());
  }
  people.addEventListener('pointerdown', e => e.stopPropagation());
  followBar.addEventListener('pointerdown', e => e.stopPropagation());
  people.addEventListener('click', e => {
    const b = e.target.closest('[data-follow]'); if (!b) return;
    const id = +b.dataset.follow;
    if (following === id) stopFollow(); else { following = id; paintLive(); toast('Following. Move the board yourself or press Esc to stop.'); }
  });
  followBar.addEventListener('click', e => { if (e.target.closest('[data-unfollow]')) stopFollow(); });
  const onAw = () => { if (!destroyed) paintLive(); };
  aw?.on('change', onAw);
  if (aw) setMine({ x: null, y: null, sel: [], laser: [] });
  // the laser trail fades on everyone's screen even without new messages
  afterPaint = () => {
    paintPins(); paintVotes(); paintMini();
    paintLive();
    shareView();
    if (aw) { const s2 = [...sel].sort().join(','); if (s2 !== afterPaint.lastSel) { afterPaint.lastSel = s2; setMine({ sel: [...sel].slice(0, 50) }); } }
  };
  const liveTick = setInterval(() => { if (!destroyed && others().some(o => o.wb.laser?.length)) paintLive(); }, 150);

  // ── Frames list, Present and the timer ───────────────────────────────────
  for (const c of [framesPanel, timerPanel, presBar, invite]) c.addEventListener('pointerdown', e => e.stopPropagation());
  function framesInOrder() { return live().filter(e => e.type === 'frame').sort((a, b) => (a.order ?? 1e9) - (b.order ?? 1e9) || a.y - b.y || a.x - b.x); }
  function showFrame(f, { animate = true } = {}) {
    if (!f) return;
    const r = rect();
    const z = Math.min(4, (r.width - 80) / Math.max(f.w, 1), (r.height - 120) / Math.max(f.h + 30, 1));
    const v = { z, x: (r.width - f.w * z) / 2 - f.x * z, y: (r.height - f.h * z) / 2 - f.y * z + 10 };
    if (animate && dockReady) glideTo(v, 320); else { view = v; render(); }
  }
  function toggleFrames() {
    if (!framesPanel.hidden) { framesPanel.hidden = true; return; }
    gallery.hidden = true; libPanel.hidden = true; timerPanel.hidden = true;
    paintFrames(); framesPanel.hidden = false;
  }
  function paintFrames() {
    const fs = framesInOrder();
    framesPanel.innerHTML = `<div class="wb-ph"><b>Frames</b><button type="button" data-close aria-label="Close">✕</button></div>`
      + (fs.length ? `<p class="wb-note">Click to go there. ${readOnly ? '' : 'Use the arrows to change the order of the slides.'}</p><ol class="wb-flist">${fs.map((f, i) => `<li><button type="button" class="wb-fgo" data-go="${f.id}"><span class="wb-fnum">${i + 1}</span>${esc(f.name || 'Frame')}</button>${readOnly ? '' : `<span class="wb-fmv"><button type="button" data-up="${f.id}" aria-label="Earlier" ${i ? '' : 'disabled'}>↑</button><button type="button" data-down="${f.id}" aria-label="Later" ${i < fs.length - 1 ? '' : 'disabled'}>↓</button></span>`}</li>`).join('')}</ol>`
        : `<p class="wb-note">No frames yet. Press <b>F</b> and drag over an area to make one. Each frame is a slide when you present.</p>`);
  }
  framesPanel.addEventListener('click', e => {
    if (e.target.closest('[data-close]')) { framesPanel.hidden = true; return; }
    const go = e.target.closest('[data-go]'); if (go) { showFrame(get(go.dataset.go)); return; }
    const up = e.target.closest('[data-up]'), down = e.target.closest('[data-down]');
    if ((up || down) && !readOnly) {
      const fs = framesInOrder(), id = (up || down).dataset.up || (up || down).dataset.down, i = fs.findIndex(f => f.id === id), j = up ? i - 1 : i + 1;
      if (i < 0 || j < 0 || j >= fs.length) return;
      [fs[i], fs[j]] = [fs[j], fs[i]];
      write(fs.map((f, k) => (f.order === k ? null : bump(f, { order: k }))).filter(Boolean), { now: true }); commitStop();
      paintFrames();
    }
  });
  // Present: one frame at a time, full screen; others are invited (never pulled along).
  // Without frames, Present shows the whole board as one slide (it used to only say "add a frame first").
  function slides() {
    const fs = framesInOrder();
    if (fs.length) return fs;
    const b = unionBounds(live().filter(x => !x.isDeleted), get);
    return b ? [{ id: WHOLE, name: 'Whole board', x: b.x - 20, y: b.y - 20, w: b.w + 40, h: b.h + 40, whole: true }] : [];
  }
  function startPresent() {
    const fs = slides();
    if (!fs.length) { toast('The board is empty. Add something to present.'); return; }
    framesPanel.hidden = true;
    presenting = { index: 0 };
    if (!fullscreen && opts.onFullscreen) toggleFullscreen();
    root.classList.add('wb-presenting');
    goSlide(0);
  }
  function goSlide(i) {
    const fs = slides();
    if (!presenting || !fs.length) return;
    presenting.index = Math.max(0, Math.min(fs.length - 1, i));
    const f = fs[presenting.index];
    requestAnimationFrame(() => showFrame(f));
    setMine({ present: { frame: f.id, at: now() } });
    presBar.innerHTML = `<button type="button" data-p="prev" aria-label="Previous slide" ${presenting.index ? '' : 'disabled'}>‹</button><span>${presenting.index + 1} / ${fs.length} · ${esc(f.name || 'Frame')}</span><button type="button" data-p="next" aria-label="Next slide" ${presenting.index < fs.length - 1 ? '' : 'disabled'}>›</button><button type="button" data-p="stop" class="wb-stop">Stop</button>`;
    presBar.hidden = false;
    // no frames yet: one quiet tip at the top, with a way to make the first one
    presHint.hidden = !f.whole || readOnly;
  }
  function stopPresent() {
    if (!presenting) return;
    presenting = null; presBar.hidden = true; presHint.hidden = true; root.classList.remove('wb-presenting');
    setMine({ present: null });
    if (fullscreen) toggleFullscreen();
  }
  const presHint = el('div', 'wb-preshint', '<span>Showing the whole board. Add frames to present step by step.</span><button type="button" data-addframe>Add a frame</button>');
  presHint.hidden = true;
  root.append(presHint);
  presHint.addEventListener('pointerdown', e => e.stopPropagation());
  presHint.addEventListener('click', e => { if (!e.target.closest('[data-addframe]')) return; stopPresent(); setTool('frame'); toast('Drag on the board to draw a frame. Each frame is one slide.'); });
  presBar.addEventListener('click', e => {
    const b = e.target.closest('[data-p]'); if (!b) return;
    if (b.dataset.p === 'prev') goSlide(presenting.index - 1);
    else if (b.dataset.p === 'next') goSlide(presenting.index + 1);
    else stopPresent();
  });
  // someone else presenting: one quiet invitation; following them = Follow + their slide
  function checkPresenters(list) {
    const p = list.find(o => o.wb.present?.frame);
    if (!p) { invite.hidden = true; invitedFor = null; if (followingPresenter != null) { followingPresenter = null; stopFollow(); } return; }
    if (followingPresenter === p.id) { const f = p.wb.present.frame === WHOLE ? slides().find(s => s.whole) || null : get(p.wb.present.frame); if (f && p.wb.present.frame !== checkPresenters.last) { checkPresenters.last = p.wb.present.frame; showFrame(f); } return; }
    if (invitedFor === p.id || presenting) return;
    invitedFor = p.id;
    invite.innerHTML = `<span><b>${esc(String(p.user.name || 'Someone').split(' ')[0])}</b> is presenting.</span><button type="button" data-i="follow" class="wb-pri">Follow</button><button type="button" data-i="no">Not now</button>`;
    invite.hidden = false;
  }
  invite.addEventListener('click', e => {
    const b = e.target.closest('[data-i]'); if (!b) return;
    invite.hidden = true;
    if (b.dataset.i === 'follow') { followingPresenter = invitedFor; checkPresenters.last = null; paintLive(); }
  });
  // Timer: a countdown everyone on the board sees (it lives in the starter's awareness; ends for all at the same moment).
  function timerLabel() { const left = Math.max(0, Math.ceil((timerEnds - now()) / 1000)); return `${String(Math.floor(left / 60)).padStart(2, '0')}:${String(left % 60).padStart(2, '0')}`; }
  function toggleTimerPanel() {
    if (!timerPanel.hidden) { timerPanel.hidden = true; return; }
    framesPanel.hidden = true; gallery.hidden = true; libPanel.hidden = true;
    timerPanel.innerHTML = `<div class="wb-ph"><b>Timer</b><button type="button" data-close aria-label="Close">✕</button></div><p class="wb-note">Everyone on this board sees it count down.</p><div class="wb-tbtns">${[1, 3, 5, 10, 15].map(m => `<button type="button" data-min="${m}">${m} min</button>`).join('')}</div>${timerEnds ? '<button type="button" data-min="0" class="wb-tstop">Stop the timer</button>' : ''}`;
    timerPanel.hidden = false;
  }
  timerPanel.addEventListener('click', e => {
    if (e.target.closest('[data-close]')) { timerPanel.hidden = true; return; }
    const b = e.target.closest('[data-min]'); if (!b) return;
    const m = +b.dataset.min;
    setMine({ timer: m ? { ends: now() + m * 60000 } : null });
    timerPanel.hidden = true;
    syncTimer(others());
  });
  function syncTimer(list) {
    // the latest running timer wins (mine or someone else's)
    const all3 = [...list.map(o => o.wb.timer?.ends || 0), aw?.getLocalState()?.wb?.timer?.ends || 0].filter(t => t > now() - 3000);
    const ends = all3.length ? Math.max(...all3) : 0;
    if (ends !== timerEnds) { timerEnds = ends; timerBeeped = 0; }
    if (timerEnds && !timerTick) timerTick = setInterval(() => {
      if (destroyed) return;
      if (timerEnds && now() >= timerEnds && !timerBeeped) { timerBeeped = 1; toast('Time is up'); }
      if (timerEnds && now() > timerEnds + 3000) { timerEnds = 0; clearInterval(timerTick); timerTick = 0; paintCorners(); return; }
      paintTimerChip();
      if (!boardMenu.hidden) paintBoardMenu();
    }, 500);
    paintCorners();
  }

  // ── Comments pinned to the board ─────────────────────────────────────────
  // Uses the page's own comment threads (mentions, reactions, resolve). The anchor is {"wb":board,"x","y","el"?}: a pin at a
  // point, or on an element (then it follows the element).
  const pinLayer = el('div', 'wb-pins');
  root.append(pinLayer);
  let threads = [];
  const anchorOf = t => { try { const a = JSON.parse(t.anchor || ''); return a && a.wb === boardKey ? a : null; } catch { return null; } };
  function pinPos(a) {
    if (a.el) { const e = get(a.el); if (e && !e.isDeleted) { const b = bounds(e, get); return { x: b.x + b.w, y: b.y }; } }
    return { x: a.x, y: a.y };
  }
  function paintPins() {
    if (!opts.comments) { pinLayer.innerHTML = ''; return; }
    const list = threads.filter(t => !t.resolvedAt).map(t => ({ t, a: anchorOf(t) })).filter(x => x.a);
    pinLayer.innerHTML = list.map(({ t, a }, i) => {
      const p = toScreen(pinPos(a).x, pinPos(a).y);
      return `<button type="button" class="wb-pin" data-thread="${t.id}" style="left:${p.x.toFixed(0)}px;top:${p.y.toFixed(0)}px" title="Open comment" aria-label="Comment ${i + 1}">${t.count > 1 ? t.count : ''}</button>`;
    }).join('');
  }
  pinLayer.addEventListener('pointerdown', e => e.stopPropagation());
  pinLayer.addEventListener('click', e => { const b = e.target.closest('[data-thread]'); if (b) opts.comments?.open(b.dataset.thread); });
  function startComment(w, onEl) {
    if (!opts.comments) return;
    const anchor = JSON.stringify(onEl ? { wb: boardKey, el: onEl.id, x: Math.round(w.x), y: Math.round(w.y) } : { wb: boardKey, x: Math.round(w.x), y: Math.round(w.y) });
    const quote = onEl ? String(onEl.text || onEl.name || onEl.title || (SHAPE_LABEL[onEl.type] || onEl.type)).slice(0, 120) : 'On the board';
    opts.comments.start(quote, anchor);
  }

  // ── Voting ───────────────────────────────────────────────────────────────
  // The round lives in the shared document ("wbvote:<board>" key on the scene map is avoided: a separate Y.Map), so it survives
  // reloads and everyone sees the same round. Votes are per person; they stay hidden until the round ends.
  function myKey() { return String(opts.me?.id || aw?.getLocalState()?.user?.nik || 'me'); }
  function round() { const r = vmap?.get('round'); return r && typeof r === 'object' ? r : null; }
  function votingOn() { const r = round(); return !!r && !r.closedAt && (!r.endsAt || r.endsAt > now()); }
  function myVotes() { const v = vmap?.get(`v:${round()?.id}:${myKey()}`); return Array.isArray(v) ? v : []; }
  function tally() {
    const r = round(); if (!r) return new Map();
    const t = new Map();
    vmap.forEach((v, k) => { if (k.startsWith(`v:${r.id}:`) && Array.isArray(v)) for (const id of v) t.set(id, (t.get(id) || 0) + 1); });
    return t;
  }
  function castVote(target, takeBack) {
    const r = round(); if (!r) return;
    const mine = myVotes().slice();
    const i = mine.lastIndexOf(target.id);
    if (takeBack) { if (i < 0) return; mine.splice(i, 1); }
    else if (mine.length < r.per) mine.push(target.id);
    else { toast(`You used all ${r.per} votes. Alt-click a sticky to take one back.`); return; }
    doc.transact(() => vmap.set(`v:${r.id}:${myKey()}`, mine), ORIGIN);
    paintVotes(); paintVotePanel();
  }
  const votePanel = el('div', 'wb-panel wb-votep'); votePanel.hidden = true;
  const voteLayer = el('div', 'wb-votes');
  root.append(votePanel, voteLayer);
  votePanel.addEventListener('pointerdown', e => e.stopPropagation());
  function paintVotes() {
    const r = round();
    if (!r || !vmap) { voteLayer.innerHTML = ''; return; }
    const showAll = !!r.closedAt || (r.endsAt && r.endsAt <= now()) || !r.hidden;
    const counts = showAll ? tally() : new Map(myVotes().reduce((m, id) => m.set(id, (m.get(id) || 0) + 1), new Map()));
    voteLayer.innerHTML = [...counts].filter(([, n]) => n > 0).map(([id, n]) => {
      const e = get(id); if (!e || e.isDeleted) return '';
      const b = bounds(e, get), p = toScreen(b.x + b.w, b.y + b.h);
      return `<span class="wb-vote ${showAll ? 'all' : 'mine'}" style="left:${p.x.toFixed(0)}px;top:${p.y.toFixed(0)}px">${n}</span>`;
    }).join('');
  }
  function paintVotePanel() {
    if (votePanel.hidden) return;
    const r = round(), on = votingOn();
    if (!r || (!on && !r.closedAt && !(r.endsAt && r.endsAt <= now()))) {
      votePanel.innerHTML = `<div class="wb-ph"><b>Voting</b><button type="button" data-close aria-label="Close">✕</button></div>
        <label class="wb-row">Votes for each person <select data-per>${[1, 2, 3, 5, 10].map(n => `<option ${n === 3 ? 'selected' : ''}>${n}</option>`).join('')}</select></label>
        <label class="wb-row">Time limit <select data-mins><option value="0">None</option>${[2, 5, 10, 15].map(n => `<option value="${n}">${n} min</option>`).join('')}</select></label>
        <label class="wb-row"><input type="checkbox" data-hide checked> Hide votes until it ends</label>
        ${readOnly ? '<p class="wb-note">Only people who can edit can start a vote.</p>' : '<button type="button" class="wb-wide wb-pri" data-start>Start voting</button>'}`;
    } else if (on) {
      const left = r.per - myVotes().length;
      votePanel.innerHTML = `<div class="wb-ph"><b>Voting is on</b><button type="button" data-close aria-label="Close">✕</button></div>
        <p class="wb-note">Click stickies or cards to vote. You have <b>${left}</b> of ${r.per} left. Alt-click takes one back.${r.endsAt ? ` Ends in ${Math.max(0, Math.ceil((r.endsAt - now()) / 60000))} min.` : ''}</p>
        ${readOnly && r.by !== myKey() ? '' : '<button type="button" class="wb-wide" data-end>End and show results</button>'}`;
    } else {
      const t = [...tally()].sort((a, b) => b[1] - a[1]).slice(0, 10);
      votePanel.innerHTML = `<div class="wb-ph"><b>Results</b><button type="button" data-close aria-label="Close">✕</button></div>`
        + (t.length ? `<ol class="wb-results">${t.map(([id, n]) => { const e = get(id); return `<li><button type="button" data-goto="${id}"><b>${n}</b> ${esc(String(e?.text || e?.title || 'Item').split('\n')[0].slice(0, 40))}</button></li>`; }).join('')}</ol>` : '<p class="wb-note">No votes were cast.</p>')
        + (readOnly ? '' : '<div class="wb-row2"><button type="button" class="wb-wide" data-sort>Line up by votes</button><button type="button" class="wb-wide" data-clear>New vote</button></div>');
    }
  }
  votePanel.addEventListener('click', e => {
    if (e.target.closest('[data-close]')) { votePanel.hidden = true; return; }
    if (e.target.closest('[data-start]') && vmap) {
      const per = +votePanel.querySelector('[data-per]').value, mins = +votePanel.querySelector('[data-mins]').value, hidden = votePanel.querySelector('[data-hide]').checked;
      doc.transact(() => {
        vmap.forEach((_, k) => { if (k.startsWith('v:')) vmap.delete(k); });
        vmap.set('round', { id: Math.random().toString(36).slice(2, 9), per, hidden, endsAt: mins ? now() + mins * 60000 : null, by: myKey(), startedAt: now() });
      }, ORIGIN);
      toast('Voting started for everyone on the board');
    }
    if (e.target.closest('[data-end]') && vmap) { const r = round(); if (r) doc.transact(() => vmap.set('round', { ...r, closedAt: now() }), ORIGIN); }
    if (e.target.closest('[data-clear]') && vmap) doc.transact(() => { vmap.forEach((_, k) => vmap.delete(k)); }, ORIGIN);
    const go = e.target.closest('[data-goto]'); if (go) { const t = get(go.dataset.goto); if (t) { sel = new Set([t.id]); fit([t], { max: 1.2 }); } }
    if (e.target.closest('[data-sort]')) sortByVotes();
    paintVotes(); paintVotePanel();
  });
  function toggleVotePanel() {
    if (!vmap) return;
    if (!votePanel.hidden) { votePanel.hidden = true; return; }
    framesPanel.hidden = true; timerPanel.hidden = true; libPanel.hidden = true; gallery.hidden = true;
    votePanel.hidden = false; paintVotePanel();
  }
  /// Results as a row: the voted items move into one line, most votes first (one undo step).
  function sortByVotes() {
    const t = [...tally()].sort((a, b) => b[1] - a[1]).map(([id]) => get(id)).filter(e => e && !e.locked);
    if (!t.length) return;
    const start = unionBounds(t, get);
    let x = start.x;
    write(t.map(e => { const m = moved(e, x - e.x, start.y - e.y); x += (e.w || 100) + 24; return m; }), { now: true });
    commitStop(); fit(t.map(e => get(e.id)), { max: 1 });
  }
  const onVote = () => { if (!destroyed) { paintVotes(); paintVotePanel(); paintCorners(); } };
  vmap?.observe(onVote);
  const unThreads = opts.comments?.subscribe?.(list => { threads = (list || []).map(t => ({ id: t.id, anchor: t.anchor, resolvedAt: t.resolvedAt, count: t.count || 1, text: t.text || '', author: t.author || '' })); paintPins(); if (r4Kind === 'comments') paintPanelR4(); });

  // ── Emoji and stickers ───────────────────────────────────────────────────
  // One picker: the full emoji list (search + skin tones, same data as Chat) and the team's stickers. Picking one puts it in the
  // middle of the screen, ready to move; the picker stays open so several can be added in a row. Stamps stay as they were.
  let stampEmoji = '👍';
  const RECENT_EMOJI = 'ipop.wb.emoji';
  const emojiPop = el('div', 'wb-panel wb-emojipop'); emojiPop.hidden = true; emojiPop.setAttribute('role', 'dialog'); emojiPop.setAttribute('aria-label', 'Emoji and stickers');
  root.append(emojiPop);
  emojiPop.addEventListener('pointerdown', e => e.stopPropagation());
  let emojiTab = 'emoji', emojiCat = 0, emojiQ = '', emojiTone = 0;
  const CATS = [['😀', 'Smileys and people'], ['🐻', 'Animals and nature'], ['🍔', 'Food and drink'], ['⚽', 'Activities'], ['✈️', 'Travel and places'], ['💡', 'Objects'], ['❤️', 'Symbols'], ['🏳️', 'Flags']];
  const recentEmoji = () => { try { return JSON.parse(localStorage.getItem(RECENT_EMOJI) || '[]').slice(0, 24); } catch { return []; } };
  async function toggleEmojiPicker(anchor) {
    if (!emojiPop.hidden) { emojiPop.hidden = true; return; }
    if (readOnly) return;
    for (const p of [framesPanel, timerPanel, libPanel, gallery, votePanel, exportPanel, cardPicker]) p.hidden = true;
    emojiPop.hidden = false;
    paintEmoji();
    if (!isPhone() && anchor) {
      const r = anchor.getBoundingClientRect(), rr = rect();
      emojiPop.style.transform = 'none'; emojiPop.style.top = '';
      emojiPop.style.left = `${Math.max(8, Math.min(r.left - rr.left + r.width / 2 - emojiPop.offsetWidth / 2, rr.width - emojiPop.offsetWidth - 8))}px`;
      emojiPop.style.bottom = `${rr.bottom - r.top + 10}px`;
    } else { emojiPop.style.transform = ''; emojiPop.style.left = ''; emojiPop.style.bottom = ''; }
  }
  async function paintEmoji() {
    const head = `<div class="wb-seg" role="tablist"><button type="button" data-et="emoji" class="${emojiTab === 'emoji' ? 'on' : ''}">Emoji</button>${opts.stickers ? `<button type="button" data-et="stickers" class="${emojiTab === 'stickers' ? 'on' : ''}">Stickers</button>` : ''}</div>`;
    if (emojiTab === 'stickers') {
      emojiPop.innerHTML = `${head}<div class="wb-note">Loading stickers…</div>`;
      const list = await opts.stickers.list().catch(() => []);
      if (emojiPop.hidden || emojiTab !== 'stickers') return;
      emojiPop.innerHTML = `${head}<div class="wb-stgrid">${list.map(s => `<button type="button" data-st="${esc(s.key)}" title="${esc(s.name || 'Sticker')}" aria-label="${esc(s.name || 'Sticker')}"><img src="${esc(s.url)}" alt="" loading="lazy"></button>`).join('') || '<p class="wb-note">No stickers yet. Make some in Chat.</p>'}</div>`;
      return;
    }
    const data = await (opts.emojiData?.() || Promise.resolve(null)).catch(() => null);
    if (emojiPop.hidden || emojiTab !== 'emoji') return;
    const all2 = data?.emoji || [];
    const q = emojiQ.trim().toLowerCase();
    const tone = e2 => (emojiTone && e2.t ? e2.t[emojiTone - 1] || e2.e : e2.e);
    const shown = q ? all2.filter(x => x.n.includes(q) || (x.k || '').includes(q)).slice(0, 160) : all2.filter(x => x.c === emojiCat);
    const recent = recentEmoji();
    emojiPop.innerHTML = `${head}<div class="wb-emhead"><input class="wb-search" type="search" placeholder="Search emoji" value="${esc(emojiQ)}" aria-label="Search emoji"><select class="wb-tone" aria-label="Skin tone">${['✋', '✋🏻', '✋🏼', '✋🏽', '✋🏾', '✋🏿'].map((t, i) => `<option value="${i}" ${i === emojiTone ? 'selected' : ''}>${t}</option>`).join('')}</select></div>
      ${!q ? `<div class="wb-emcats" role="tablist">${CATS.map(([c, n], i) => `<button type="button" data-cat="${i}" class="${i === emojiCat ? 'on' : ''}" title="${n}" aria-label="${n}">${c}</button>`).join('')}</div>` : ''}
      ${!q && recent.length ? `<div class="wb-ph5">Recent</div><div class="wb-emgrid">${recent.map(x => `<button type="button" data-em2="${esc(x)}" aria-label="${esc(x)}">${esc(x)}</button>`).join('')}</div><div class="wb-ph5">${CATS[emojiCat][1]}</div>` : ''}
      <div class="wb-emgrid">${shown.map(x => `<button type="button" data-em2="${esc(tone(x))}" title="${esc(x.n)}" aria-label="${esc(x.n)}">${esc(tone(x))}</button>`).join('') || '<p class="wb-note">No emoji found.</p>'}</div>`;
    if (!all2.length) emojiPop.querySelector('.wb-emgrid:last-child').innerHTML = ['👍', '❤️', '⭐', '✅', '❓', '🔥', '🎉', '👀', '💡', '🚀', '⚠️', '📌'].map(x => `<button type="button" data-em2="${x}">${x}</button>`).join('');
    const input = emojiPop.querySelector('.wb-search');
    input.addEventListener('input', () => { emojiQ = input.value; clearTimeout(paintEmoji.t); paintEmoji.t = setTimeout(() => { paintEmoji().then(() => { const i2 = emojiPop.querySelector('.wb-search'); if (i2) { i2.focus(); i2.setSelectionRange(i2.value.length, i2.value.length); } }); }, 150); });
    input.addEventListener('keydown', e => { e.stopPropagation(); if (e.key === 'Escape') emojiPop.hidden = true; });
    emojiPop.querySelector('.wb-tone').addEventListener('change', e => { emojiTone = +e.target.value; paintEmoji(); });
  }
  emojiPop.addEventListener('click', e => {
    const t = e.target.closest('[data-et]'); if (t) { emojiTab = t.dataset.et; paintEmoji(); return; }
    const c = e.target.closest('[data-cat]'); if (c) { emojiCat = +c.dataset.cat; paintEmoji(); return; }
    const r = rect(), w = toWorld(r.width / 2, r.height / 2);
    // each new one lands a little further along, so a row of them does not pile up on one spot
    const nudge = (emojiPop._n = ((emojiPop._n || 0) + 1) % 8) * 28;
    const em = e.target.closest('[data-em2]');
    if (em) {
      const x = em.dataset.em2, s = 72;   // board units, like a sticky (200): the same size at any zoom
      add([create('stamp', { x: w.x - s / 2 + nudge * 2, y: w.y - s / 2, w: s, h: s, emoji: x })]);
      try { localStorage.setItem(RECENT_EMOJI, JSON.stringify([x, ...recentEmoji().filter(y => y !== x)].slice(0, 24))); } catch { /* blocked */ }
      return;
    }
    const st = e.target.closest('[data-st]');
    if (st) {
      const s = 140;
      add([create('sticker', { x: w.x - s / 2 + nudge * 2, y: w.y - s / 2, w: s, h: s, stickerId: st.dataset.st })]);
    }
  });

  // ── Web link cards ───────────────────────────────────────────────────────
  // The board keeps the address, title and site. The picture is fetched again when the board opens (the preview service signs
  // its picture links for a limited time, so a stored picture link would stop working).
  const linkPreviews = new Map();   // url -> { title, site, image } | 'loading'
  function ensurePreview(el2) {
    if (!el2.url || !opts.linkPreview || linkPreviews.has(el2.url)) return;
    linkPreviews.set(el2.url, 'loading');
    opts.linkPreview(el2.url).then(async p => {
      linkPreviews.set(el2.url, p || {});
      if (p?.image && opts.loadPreviewImage) {
        try { files.set(`lp:${el2.url}`, await opts.loadPreviewImage(p.image)); } catch { /* card without picture */ }
      }
      // fill in a title the card did not have yet (made before the preview came back)
      for (const c of live().filter(x => x.type === 'link' && x.url === el2.url && !x.title && (p?.title || p?.site))) {
        if (!readOnly) write([bump(c, { title: p.title || undefined, site: p.site || undefined, image: p.image ? c.url : undefined })], { now: true });
      }
      cache.clear(); world.innerHTML = ''; render();
    }, () => linkPreviews.set(el2.url, {}));
  }
  function addLinkCard(url, at) {
    if (readOnly) return;
    const r = rect(), c = at || toWorld(r.width / 2, r.height / 2);
    const cw = 300;
    const card = create('link', { x: c.x - cw / 2, y: c.y - 115, w: cw, h: 230, url, image: url });
    add([card]);
    ensurePreview(card);
  }
  function askLinkCard() {
    if (readOnly || !opts.linkPreview) return;
    for (const p of [framesPanel, timerPanel, libPanel, gallery, votePanel, exportPanel, cardPicker, emojiPop]) p.hidden = true;
    linkEd.innerHTML = `<div class="wb-ph"><b>Add a web link</b><button type="button" data-close aria-label="Close">✕</button></div>
      <input class="wb-search" data-lc placeholder="Paste a web address (https://…)" aria-label="Web address">
      <p class="wb-note">The card shows the page's title and picture. Click it to open the page.</p>
      <div class="wb-row2"><button type="button" class="wb-wide wb-pri" data-lcadd>Add to board</button></div>`;
    linkEd.style.transform = ''; linkEd.style.left = ''; linkEd.style.top = '';
    linkEd.hidden = false;
    const i = linkEd.querySelector('[data-lc]'); i.focus();
    const go = () => { const v = i.value.trim().replace(/^www\./i, 'https://www.'); if (!/^https?:\/\/\S+\.\S+/i.test(v)) { toast('Paste a full web address, starting with https://'); return; } linkEd.hidden = true; addLinkCard(v); };
    i.addEventListener('keydown', ev => { ev.stopPropagation(); if (ev.key === 'Enter') go(); if (ev.key === 'Escape') linkEd.hidden = true; });
    linkEd.querySelector('[data-lcadd]').addEventListener('click', go);
  }

  // ── Mind map: Tab = branch, Shift+Enter = sibling ────────────────────────
  function mindAdd(from, kind) {
    if (readOnly || from.locked) return;
    const links = live().filter(c => c.type === 'connector');
    const parentLink = links.find(c => c.to?.id === from.id);
    const parent = kind === 'sibling' ? (parentLink ? get(parentLink.from.id) : null) : from;
    if (kind === 'sibling' && !parent) { mindAdd(from, 'child'); return; }
    const kids = links.filter(c => c.from?.id === parent.id).map(c => get(c.to?.id)).filter(Boolean);
    const right = !parentLink || centre(from, get).x >= centre(get(parentLink.from.id) || from, get).x;
    const dir = kind === 'child' ? (right ? 1 : -1) : (centre(from, get).x >= centre(parent, get).x ? 1 : -1);
    const w = Math.max(160, Math.min(from.w || 160, 240)), h = Math.max(56, Math.min(from.h || 60, 90));
    const pb = bounds(parent, get);
    const x = dir > 0 ? pb.x + pb.w + 120 : pb.x - 120 - w;
    const sameSide = kids.filter(k => (centre(k, get).x - centre(parent, get).x) * dir > 0);
    const y = sameSide.length ? Math.max(...sameSide.map(k => k.y + k.h)) + 24 : pb.y + pb.h / 2 - h / 2;
    const node = create(from.type === 'sticky' ? 'sticky' : 'rect', from.type === 'sticky' ? { x, y, w, h, text: '', color: from.color } : { x, y, w, h, text: '', fill: from.fill ?? null, stroke: from.stroke ?? null });
    const link = create('connector', { from: { id: parent.id }, to: { id: node.id }, route: 'curved', endHead: 'none', startHead: 'none' });
    add([node, link], { select: false });
    sel = new Set([node.id]);
    editSoon2(node);
  }
  function editSoon2(node) { setTimeout(() => { const n = get(node.id); if (n && !destroyed) editText(n, { selectAll: true }); }, 0); }

  // ── Notes page and Projects task cards ───────────────────────────────────
  const taskCache = new Map();   // taskId -> { title, status, due, people, project, private } | 'loading'
  function ensureTask(id) {
    if (!id || taskCache.has(id) || !opts.tasks?.get) return;
    taskCache.set(id, 'loading');
    opts.tasks.get(id).then(t => { taskCache.set(id, t || { private: true }); cache.clear(); world.innerHTML = ''; render(); }, () => taskCache.set(id, { private: true }));
  }
  opts.cardInfo = { task: id => { ensureTask(id); const t = taskCache.get(id); return t === 'loading' ? null : t; }, page: id => opts.pages?.info?.(id) };
  function addTaskCards(tasks, at) {
    let y = at.y;
    const cards = tasks.map(t => { const c = create('taskCard', { x: at.x, y, w: 260, h: 104, taskId: t.id, title: t.title }); y += 120; taskCache.set(t.id, t); return c; });
    add(cards);
  }
  async function makeTasks(stickies) {
    if (!opts.tasks?.create) return;
    const r = await opts.tasks.create(stickies.map(s => ({ id: s.id, text: s.text || '' })));
    if (!r?.length) return;
    // each sticky becomes the task card in its place
    const byId = new Map(r.map(x => [x.stickyId, x.task]));
    const out = [];
    for (const s of stickies) {
      const t = byId.get(s.id); if (!t) continue;
      taskCache.set(t.id, t);
      out.push(bump(s, { isDeleted: true }), { ...create('taskCard', { x: s.x, y: s.y, w: Math.max(240, s.w), h: 104, taskId: t.id, title: t.title }), z: s.z });
    }
    write(out, { now: true }); commitStop(); sel.clear(); paintProps();
    toast(r.length === 1 ? 'Task created in Projects' : `${r.length} tasks created in Projects`);
  }
  // card picker: Notes pages (from the sidebar list) and tasks (projects I can add to)
  const cardPicker = el('div', 'wb-panel wb-cardpick'); cardPicker.hidden = true;
  root.append(cardPicker);
  cardPicker.addEventListener('pointerdown', e => e.stopPropagation());
  let cardTab = 'pages', cardQ = '', cardSeq = 0;
  function toggleCardPicker() {
    if (readOnly) return;
    if (!cardPicker.hidden) { cardPicker.hidden = true; return; }
    for (const p of [framesPanel, timerPanel, libPanel, gallery, votePanel, exportPanel]) p.hidden = true;
    cardPicker.hidden = false; paintCardPicker();
  }
  async function paintCardPicker() {
    const seq = ++cardSeq;
    cardPicker.innerHTML = `<div class="wb-ph"><b>Add a card</b><button type="button" data-close aria-label="Close">✕</button></div>
      <div class="wb-seg" role="tablist">${opts.pages ? `<button type="button" data-tab="pages" class="${cardTab === 'pages' ? 'on' : ''}">Notes page</button>` : ''}${opts.taskSearch ? `<button type="button" data-tab="tasks" class="${cardTab === 'tasks' ? 'on' : ''}">Projects task</button>` : ''}</div>
      <input class="wb-search" type="search" placeholder="${cardTab === 'pages' ? 'Find a page' : 'Find a task'}" value="${esc(cardQ)}" aria-label="Search">
      <div class="wb-cardlist"><p class="wb-note">${cardTab === 'tasks' ? 'Loading tasks…' : ''}</p></div>`;
    const input = cardPicker.querySelector('.wb-search');
    input.focus(); input.setSelectionRange(input.value.length, input.value.length);
    input.addEventListener('input', () => { cardQ = input.value; clearTimeout(paintCardPicker.t); paintCardPicker.t = setTimeout(fillCards, 200); });
    input.addEventListener('keydown', e => { e.stopPropagation(); if (e.key === 'Escape') cardPicker.hidden = true; });
    fillCards(seq);
  }
  async function fillCards(seq = cardSeq) {
    const list = cardPicker.querySelector('.wb-cardlist'); if (!list) return;
    const q = cardQ.trim().toLowerCase();
    if (cardTab === 'pages') {
      const items = (opts.pages?.all?.() || []).filter(p => !q || String(p.title || '').toLowerCase().includes(q)).slice(0, 40);
      list.innerHTML = items.length ? items.map(p => `<button type="button" data-page="${p.id}">${esc(p.emoji || '📄')} ${esc(p.title || 'Untitled')}</button>`).join('') : '<p class="wb-note">No page found.</p>';
    } else {
      const items = await opts.taskSearch(q).catch(() => []);
      if (seq !== cardSeq && cardTab !== 'tasks') return;
      list.innerHTML = items.length ? items.map(t => `<button type="button" data-task="${t.id}"><b>${esc(t.title)}</b><small>${esc(t.project || '')}</small></button>`).join('') : '<p class="wb-note">No task found in your projects.</p>';
      cardPicker._tasks = items;
    }
  }
  cardPicker.addEventListener('click', e => {
    if (e.target.closest('[data-close]')) { cardPicker.hidden = true; return; }
    const tab = e.target.closest('[data-tab]'); if (tab) { cardTab = tab.dataset.tab; paintCardPicker(); return; }
    const r = rect(), c = toWorld(r.width / 2, r.height / 2);
    const pg = e.target.closest('[data-page]');
    if (pg) { const p = (opts.pages?.all?.() || []).find(x => x.id === pg.dataset.page); if (p) addPageCard(p, c); cardPicker.hidden = true; return; }
    const tk = e.target.closest('[data-task]');
    if (tk) { const t = (cardPicker._tasks || []).find(x => x.id === tk.dataset.task); if (t) addTaskCards([t], { x: c.x - 130, y: c.y - 52 }); cardPicker.hidden = true; }
  });
  const unFocus = opts.comments?.onFocus?.(id => {
    const t = threads.find(x => x.id === id); const a = t && anchorOf(t); if (!a) return;
    const p = pinPos(a), r = rect(); view.x = r.width / 2 - p.x * view.z; view.y = r.height / 2 - p.y * view.z; render();
    requestAnimationFrame(() => pinLayer.querySelector(`[data-thread="${id}"]`)?.classList.add('wb-pin-focus'));
  });
  function addPageCard(page, at) { add([create('noteCard', { x: at.x - 130, y: at.y - 40, w: 260, h: 80, pageId: page.id, title: page.title, emoji: page.emoji })]); }

  // ── Export ───────────────────────────────────────────────────────────────
  const exportPanel = el('div', 'wb-panel wb-exportp'); exportPanel.hidden = true;
  root.append(exportPanel);
  exportPanel.addEventListener('pointerdown', e => e.stopPropagation());
  function toggleExport() {
    if (!exportPanel.hidden) { exportPanel.hidden = true; return; }
    const frames = framesInOrder();
    exportPanel.innerHTML = `<div class="wb-ph"><b>Export</b><button type="button" data-close aria-label="Close">✕</button></div>
      <label class="wb-row">What <select data-what><option value="all">Whole board</option>${sel.size ? '<option value="sel" selected>Selection</option>' : ''}${frames.map(f => `<option value="f:${f.id}">Frame: ${esc(f.name || 'Frame')}</option>`).join('')}</select></label>
      <label class="wb-row">Format <select data-fmt><option value="png">PNG picture</option><option value="svg">SVG (sharp at any size)</option><option value="pdf">PDF</option></select></label>
      <label class="wb-row">Background <select data-bg><option value="light">White</option><option value="dark">Dark</option><option value="none">None (PNG / SVG)</option></select></label>
      <div class="wb-row2"><button type="button" class="wb-wide" data-copy>Copy as image</button><button type="button" class="wb-wide wb-pri" data-dl>Download</button></div>`;
    framesPanel.hidden = true; votePanel.hidden = true; timerPanel.hidden = true; libPanel.hidden = true; gallery.hidden = true;
    exportPanel.hidden = false;
  }
  function exportSet(what) {
    if (what === 'sel') return [...sel].map(get).filter(Boolean);
    if (what?.startsWith('f:')) { const f = get(what.slice(2)); if (!f) return []; return [f, ...live().filter(x => x.id !== f.id && frameOf(x, live(), get)?.id === f.id), ...live().filter(c => c.type === 'connector')].filter((x, i, a) => a.indexOf(x) === i && (x.type !== 'connector' || frameOf(get(x.from?.id) || x, live(), get)?.id === f.id)); }
    return live();
  }
  async function renderExport(els, fmt, bg) {
    const files = await dataUrls();
    const theme = bg === 'dark' ? 'dark' : 'light', background = bg !== 'none' || fmt === 'pdf';
    if (fmt === 'svg') { const o = toSvg(els, { theme, files, background, cardInfo: opts.cardInfo }); return o ? new Blob([new XMLSerializer().serializeToString(o.svg)], { type: 'image/svg+xml' }) : null; }
    const png = await toPng(els, { scale: 2, theme, files, background, cardInfo: opts.cardInfo });
    if (fmt !== 'pdf' || !png) return png;
    return pngToPdf(png);
  }
  exportPanel.addEventListener('click', async e => {
    if (e.target.closest('[data-close]')) { exportPanel.hidden = true; return; }
    const what = exportPanel.querySelector('[data-what]')?.value, fmt = exportPanel.querySelector('[data-fmt]')?.value, bg = exportPanel.querySelector('[data-bg]')?.value;
    const els = exportSet(what);
    if (!els.length) { toast('Nothing to export'); return; }
    if (e.target.closest('[data-copy]')) {
      try {
        const png = await toPng(els, { scale: 2, theme: bg === 'dark' ? 'dark' : 'light', files: await dataUrls(), background: bg !== 'none', cardInfo: opts.cardInfo });
        await navigator.clipboard.write([new ClipboardItem({ 'image/png': png })]);
        toast('Copied. Paste it in Chat or Mail.');
      } catch { toast('This browser cannot copy pictures. Use Download instead.'); }
      return;
    }
    if (e.target.closest('[data-dl]')) {
      const blob = await renderExport(els, fmt, bg).catch(() => null);
      if (!blob) { toast('Couldn’t make the file'); return; }
      const name = `${(opts.title?.() || 'whiteboard').replace(/[\\/:*?"<>|]+/g, ' ').trim() || 'whiteboard'}.${fmt}`;
      const a = document.createElement('a'); a.href = URL.createObjectURL(blob); a.download = name; a.click();
      setTimeout(() => URL.revokeObjectURL(a.href), 4000);
      exportPanel.hidden = true;
    }
  });

  // ── Minimap ──────────────────────────────────────────────────────────────
  const mini = el('div', 'wb-mini'); mini.setAttribute('aria-label', 'Map of the board');
  const miniSvg = svgEl('svg', { class: 'wb-minisvg' }, mini);
  root.append(mini);
  mini.addEventListener('pointerdown', e => {
    e.stopPropagation();
    const b = unionBounds(live(), get); if (!b) return;
    const r = mini.getBoundingClientRect(), vb = miniSvg.viewBox.baseVal;
    const wx = vb.x + (e.clientX - r.left) / r.width * vb.width, wy = vb.y + (e.clientY - r.top) / r.height * vb.height;
    const rr = rect(); view.x = rr.width / 2 - wx * view.z; view.y = rr.height / 2 - wy * view.z; render();
  });
  function paintMini() {
    if (mode !== 'board') { mini.hidden = true; return; }
    const els = live();
    const b = unionBounds(els, get);
    const r = rect();
    // only worth showing once the board is bigger than the screen
    if (!b || (b.w * view.z < r.width * 0.9 && b.h * view.z < r.height * 0.9)) { mini.hidden = true; return; }
    mini.hidden = false;
    const pad = Math.max(b.w, b.h) * 0.06;
    miniSvg.setAttribute('viewBox', `${b.x - pad} ${b.y - pad} ${b.w + pad * 2} ${b.h + pad * 2}`);
    const pal = PALETTE[theme] || PALETTE.light, stroke = Math.max(b.w, b.h) / 120;
    let html = '';
    for (const e of els) {
      if (e.type === 'connector' || e.type === 'path' || e.type === 'stamp') continue;
      const eb = bounds(e, get);
      const fill = e.type === 'sticky' ? pal.sticky[e.color] || pal.sticky.yellow : e.type === 'frame' ? 'none' : 'rgba(118,118,128,.35)';
      html += `<rect x="${eb.x}" y="${eb.y}" width="${Math.max(eb.w, stroke * 2)}" height="${Math.max(eb.h, stroke * 2)}" rx="${stroke * 2}" fill="${fill}" stroke="${e.type === 'frame' ? 'rgba(118,118,128,.6)' : 'none'}" stroke-width="${stroke}"/>`;
    }
    const a = toWorld(0, 0), z = toWorld(r.width, r.height);
    html += `<rect x="${a.x}" y="${a.y}" width="${z.x - a.x}" height="${z.y - a.y}" fill="rgba(10,132,255,.08)" stroke="${pal.sel}" stroke-width="${stroke * 1.5}" rx="${stroke * 3}"/>`;
    miniSvg.innerHTML = html;
  }

  // ── Pictures and files ───────────────────────────────────────────────────
  // A picture or file is uploaded through the page (stored on the NAS with the page's access); the board keeps only its id.
  const files = new Map();     // fileId -> object URL, for drawing
  opts.files = files;
  const loading = new Set();
  function ensureFile(id) {
    if (!id || files.has(id) || loading.has(id) || !opts.loadFile) return;
    loading.add(id);
    opts.loadFile(id).then(url => { files.set(id, url); cache.clear(); world.innerHTML = ''; render(); }, () => {}).finally(() => loading.delete(id));
  }
  function ensureSticker(key) {
    const k = `st:${key}`;
    if (!key || files.has(k) || loading.has(k) || !opts.stickers?.url) return;
    loading.add(k);
    opts.stickers.url(key).then(u => { if (u) { files.set(k, u); cache.clear(); world.innerHTML = ''; render(); } }, () => {}).finally(() => loading.delete(k));
  }
  const IMAGE_TYPES = /^image\/(png|jpe?g|webp|gif)$/i;
  const MAX_UPLOAD = 10 * 1024 * 1024;
  function pickFiles() {
    if (readOnly || !opts.upload) return;
    const input = document.createElement('input');
    input.type = 'file'; input.multiple = true;
    input.onchange = () => { const r = rect(); dropFiles([...input.files], toWorld(r.width / 2, r.height / 2)); };
    input.click();
  }
  async function dropFiles(list, at) {
    if (readOnly || !opts.upload || !list.length) return;
    let x = at.x, placed = 0;
    for (const f of list) {
      if (f.size > MAX_UPLOAD && IMAGE_TYPES.test(f.type)) { toast(`${f.name} is larger than 10 MB`); continue; }
      const imgCount = live().filter(e => e.type === 'image').length;
      if (IMAGE_TYPES.test(f.type) && imgCount >= 200) { toast('This board already has 200 pictures'); break; }
      try {
        toast(`Uploading ${f.name}…`);
        const up = await opts.upload(f);
        if (IMAGE_TYPES.test(f.type)) {
          const size = await imageSize(f).catch(() => ({ w: 400, h: 300 }));
          const scale = Math.min(1, 480 / size.w, 360 / size.h);
          const w = Math.max(40, size.w * scale), h = Math.max(40, size.h * scale);
          files.set(up.id, URL.createObjectURL(f));
          add([create('image', { x: x - w / 2, y: at.y - h / 2, w, h, fileId: up.id, name: f.name })], { select: list.length === 1 });
          x += w + 24;
        } else {
          add([create('file', { x: x - 120, y: at.y - 32, w: 240, h: 64, fileId: up.id, name: f.name, size: f.size })], { select: list.length === 1 });
          x += 264;
        }
        placed++;
      } catch (err) { toast(err?.message || `Couldn’t upload ${f.name}`); }
    }
    if (placed) toast(placed === 1 ? 'Added to the board' : `Added ${placed} files`);
  }
  function imageSize(file) {
    return new Promise((ok, fail) => { const img = new Image(), u = URL.createObjectURL(file); img.onload = () => { ok({ w: img.naturalWidth, h: img.naturalHeight }); URL.revokeObjectURL(u); }; img.onerror = () => { URL.revokeObjectURL(u); fail(new Error('not an image')); }; img.src = u; });
  }
  const dropZone = el('div', 'wb-dropzone', '<span>Drop to add pictures and files to the board</span>'); root.append(dropZone);
  root.addEventListener('dragover', e => { if (!readOnly && opts.upload && [...(e.dataTransfer?.types || [])].includes('Files')) { e.preventDefault(); e.dataTransfer.dropEffect = 'copy'; root.classList.add('wb-dropping'); } });
  root.addEventListener('dragleave', e => { if (e.target === root) root.classList.remove('wb-dropping'); });
  root.addEventListener('drop', e => {
    root.classList.remove('wb-dropping');
    if (readOnly || !opts.upload || !e.dataTransfer?.files?.length) return;
    e.preventDefault(); e.stopPropagation();
    const { sx, sy } = local(e);
    dropFiles([...e.dataTransfer.files], toWorld(sx, sy));
  });
  root.addEventListener('paste', e => {
    if (readOnly || editing) return;
    const fl = [...(e.clipboardData?.files || [])];
    if (fl.length && opts.upload) { e.preventDefault(); const r = rect(); dropFiles(fl, toWorld(r.width / 2, r.height / 2)); return; }
    const text = e.clipboardData?.getData('text/plain');
    if (text && /^\{"ipopBoard":1/.test(text)) { try { clipboard = JSON.parse(text).elements; e.preventDefault(); pasteClip(); } catch { /* not ours */ } return; }
    if (text && opts.linkPreview && mode === 'board' && /^https?:\/\/\S+$/i.test(text.trim())) { e.preventDefault(); addLinkCard(text.trim()); }
  });

  // ── Library (My library + team library, saved on the server) ────────────
  let libItems = null, libTab = 'mine', libBusy = false;
  /// Elements moved so the group starts at 0,0; connectors keep only links to shapes that come along.
  function packForLibrary(els) {
    const ids = new Set(els.map(e => e.id));
    const extra = live().filter(c => c.type === 'connector' && !ids.has(c.id) && ids.has(c.from?.id) && ids.has(c.to?.id));
    const all2 = [...els, ...extra].filter(e => e.type !== 'image');
    const b = unionBounds(all2, get) || { x: 0, y: 0 };
    return all2.map(e => {
      const c = structuredClone(e);
      delete c.locked; delete c.frameId;
      if (c.type === 'connector') {
        const end = x => (x?.id && ids.has(x.id) ? { id: x.id } : { x: (x?.x ?? 0) - b.x, y: (x?.y ?? 0) - b.y });
        return { ...c, from: end(c.from), to: end(c.to) };
      }
      return { ...c, x: c.x - b.x, y: c.y - b.y };
    });
  }
  async function saveToLibrary(els) {
    if (!opts.library || !els.length) return;
    const packed = packForLibrary(els);
    if (!packed.length) { toast('Pictures cannot go in the library'); return; }
    const first = packed.find(e => e.text || e.name);
    const name = (first?.text || first?.name || '').split('\n')[0].slice(0, 60) || (packed.length === 1 ? (SHAPE_LABEL[packed[0].type] || packed[0].type) : `${packed.length} items`);
    try {
      await opts.library.save({ name, json: JSON.stringify(packed) });
      libItems = null;
      toast(`Added to your library as "${name}"`);
      if (!libPanel.hidden) paintLibrary();
    } catch (err) { toast(err?.message === 'library_full' ? 'Your library is full (200 items). Remove some first.' : 'Couldn’t save to the library'); }
  }
  async function loadLibrary(force = false) {
    if (!opts.library) return [];
    if (libItems && !force) return libItems;
    libItems = await opts.library.list().catch(() => null);
    return libItems || [];
  }
  function libThumb(item) {
    let els; try { els = JSON.parse(item.json); } catch { return ''; }
    const o = toSvg(els.filter(isElement), { theme, background: false, padding: 8 });
    if (!o) return '';
    o.svg.setAttribute('width', '100%'); o.svg.setAttribute('height', '100%'); o.svg.setAttribute('preserveAspectRatio', 'xMidYMid meet');
    return new XMLSerializer().serializeToString(o.svg);
  }
  async function paintLibrary() {
    libPanel.innerHTML = `<div class="wb-ph"><b>Library</b><button type="button" data-close aria-label="Close">✕</button></div><div class="wb-note">Loading…</div>`;
    const items = await loadLibrary();
    if (libPanel.hidden) return;
    const shown = items.filter(i => (libTab === 'mine' ? i.mine : i.shared && !i.mine));
    libPanel.innerHTML = `<div class="wb-ph"><b>Library</b><button type="button" data-close aria-label="Close">✕</button></div>
      <div class="wb-seg" role="tablist"><button type="button" role="tab" data-tab="mine" aria-selected="${libTab === 'mine'}" class="${libTab === 'mine' ? 'on' : ''}">My library</button><button type="button" role="tab" data-tab="team" aria-selected="${libTab === 'team'}" class="${libTab === 'team' ? 'on' : ''}">Team library</button></div>
      ${shown.length ? `<div class="wb-libgrid">${shown.map(i => `<div class="wb-libitem" data-id="${i.id}">
          <button type="button" class="wb-libuse" data-use="${i.id}" title="Put it on the board"><span class="wb-libthumb">${libThumb(i)}</span><span class="wb-libname">${esc(i.name)}</span><small>${i.mine ? (i.shared ? 'Shared with the team' : 'Only you') : esc(i.ownerName)}</small></button>
          ${i.mine ? `<span class="wb-libacts"><button type="button" data-share="${i.id}" title="${i.shared ? 'Stop sharing' : 'Share with the team'}">${i.shared ? 'Unshare' : 'Share'}</button><button type="button" data-del="${i.id}" title="Remove" aria-label="Remove ${esc(i.name)}">✕</button></span>` : ''}
        </div>`).join('')}</div>`
        : `<p class="wb-note">${libTab === 'mine' ? 'Select things on the board and press <b>Add to my library</b> in their menu to keep them for later.' : 'Nothing shared yet. Share an item from your library and everyone can use it.'}</p>`}`;
  }
  function toggleLibrary() {
    if (!libPanel.hidden) { libPanel.hidden = true; return; }
    gallery.hidden = true;
    libPanel.hidden = false;
    paintLibrary();
  }
  libPanel.addEventListener('click', async e => {
    if (e.target.closest('[data-close]')) { libPanel.hidden = true; return; }
    const tab = e.target.closest('[data-tab]');
    if (tab) { libTab = tab.dataset.tab; paintLibrary(); return; }
    const items = libItems || [];
    const use = e.target.closest('[data-use]');
    if (use) {
      const item = items.find(i => i.id === use.dataset.use); if (!item || readOnly) return;
      let els; try { els = JSON.parse(item.json).filter(isElement); } catch { return; }
      const r = rect(), c = toWorld(r.width / 2, r.height / 2);
      const b = unionBounds(els, id => els.find(x => x.id === id)) || { w: 0, h: 0 };
      const map = new Map(els.map(x => [x.id, create(x.type).id]));
      const out = els.map(x => {
        const copy = { ...structuredClone(x), id: map.get(x.id), version: 1 };
        if (x.type === 'connector') { const end = y => (y?.id && map.has(y.id) ? { id: map.get(y.id) } : { x: (y?.x ?? 0) + c.x - b.w / 2, y: (y?.y ?? 0) + c.y - b.h / 2 }); return { ...copy, from: end(x.from), to: end(x.to) }; }
        return { ...copy, x: x.x + c.x - b.w / 2, y: x.y + c.y - b.h / 2 };
      });
      add(out);
      return;
    }
    const share = e.target.closest('[data-share]');
    if (share && !libBusy) {
      const item = items.find(i => i.id === share.dataset.share); if (!item) return;
      libBusy = true;
      try { await opts.library.update(item.id, { shared: !item.shared }); libItems = null; toast(item.shared ? 'Only you can see it now' : 'Shared with the team library'); paintLibrary(); }
      catch { toast('Couldn’t change it'); } finally { libBusy = false; }
      return;
    }
    const del = e.target.closest('[data-del]');
    if (del && !libBusy) {
      libBusy = true;
      try { await opts.library.remove(del.dataset.del); libItems = null; paintLibrary(); }
      catch { toast('Couldn’t remove it'); } finally { libBusy = false; }
    }
  });

  // ── Zoom ─────────────────────────────────────────────────────────────────
  // The tool bar is centred and the zoom bar sits bottom right: when the board is too narrow for both on one line, the zoom bar
  // moves up above the tool bar instead of covering its last buttons.
  function layoutBars() {
    const w = root.clientWidth;
    const bar = dockReady && !dock.hidden ? dock : toolbar;
    root.classList.toggle('wb-narrow', w > 0 && (w - bar.offsetWidth) / 2 < zoomBar.offsetWidth + 24);
  }
  function paintZoom() {
    zoomBar.innerHTML = `<button type="button" data-z="out" aria-label="Zoom out" title="Zoom out (Ctrl −)">−</button><button type="button" data-z="pct" class="wb-pct" title="Zoom to 100%">${Math.round(view.z * 100)}%</button><button type="button" data-z="in" aria-label="Zoom in" title="Zoom in (Ctrl +)">+</button><button type="button" data-z="fit" title="Fit everything (Shift 1)">Fit</button>`
      + (readOnly || (dockReady && dockOn()) ? '' : `<span class="wb-sep"></span><button type="button" data-z="undo" aria-label="Undo" title="Undo (Ctrl Z)" ${undo.undoStack.length ? '' : 'disabled'}>${icon('undo')}</button><button type="button" data-z="redo" aria-label="Redo" title="Redo (Ctrl Y)" ${undo.redoStack.length ? '' : 'disabled'}>${icon('redo')}</button>`)
      + (compact ? '' : `<span class="wb-sep"></span><button type="button" data-z="keys" aria-label="Keyboard shortcuts" title="Keyboard shortcuts (?)">?</button>`);
    layoutBars();
  }
  zoomBar.addEventListener('pointerdown', e => e.stopPropagation());
  zoomBar.addEventListener('click', e => {
    const b = e.target.closest('[data-z]'); if (!b) return;
    const z = b.dataset.z;
    if (z === 'in') glideZoom(1.25); else if (z === 'out') glideZoom(1 / 1.25); else if (z === 'pct') glideZoom(1 / view.z); else if (z === 'fit') glideFit();
    else if (z === 'undo') doUndo(); else if (z === 'redo') doRedo(); else if (z === 'keys') toggleKeys();
  });
  function zoomAt(f, sx, sy) {
    const r = rect(); sx ??= r.width / 2; sy ??= r.height / 2;
    const w = toWorld(sx, sy);
    view.z = Math.min(4, Math.max(0.1, view.z * f));
    view.x = sx - w.x * view.z; view.y = sy - w.y * view.z;
    render();
  }
  function fit(els = live(), { pad = 60, max = 1.4 } = {}) {
    const r = rect();
    if (r.width < 50 || r.height < 50) return;
    const b = unionBounds(els, get);
    if (!b) { view = { x: r.width / 2 - 200, y: r.height / 2 - 150, z: 1 }; render(); return; }
    const top = 30, bottom = readOnly ? 20 : 70;
    const z = Math.max(0.1, Math.min(max, (r.width - pad * 2) / Math.max(b.w, 1), (r.height - top - bottom - pad) / Math.max(b.h + 26, 1)));
    view = { z, x: (r.width - b.w * z) / 2 - b.x * z, y: top + (r.height - top - bottom - b.h * z) / 2 - b.y * z + 13 * z };
    render();
  }

  // ── Selection quick menu ─────────────────────────────────────────────────
  function paintProps() {
    const els = [...sel].map(get).filter(Boolean);
    widthMenu.hidden = true; arrangeMenu.hidden = true; headsMenu.hidden = true; themeMenu.hidden = true; propsMore.hidden = true;
    if (!els.length || readOnly || interaction || editing) { props.hidden = true; if (colourFor === 'sel') colourPop.hidden = true; if (dockReady) { ctxBar._html = null; paintCtx(); } return; }
    const types = new Set(els.map(e => e.type));
    const only = t => types.size === 1 && types.has(t);
    const anyShape = els.some(e => isShape(e));
    const parts = [];
    if (only('sticky')) parts.push(STICKY_COLORS.map(c => `<button type="button" data-sticky="${c}" title="${c[0].toUpperCase() + c.slice(1)}" aria-label="${c} sticky"><span class="wb-sw" style="background:${pal().sticky[c]}"></span></button>`).join(''));
    else if (els.some(e => !['frame', 'image', 'sticker', 'file', 'taskCard', 'noteCard', 'link'].includes(e.type))) {
      const first = els.find(e => !['frame', 'image', 'sticker', 'sticky'].includes(e.type));
      parts.push(`<button type="button" data-colour="stroke" title="${anyShape ? 'Line colour' : 'Colour'}" aria-label="${anyShape ? 'Line colour' : 'Colour'}"><span class="wb-sw wb-ring2" style="--c:${colourCss(first?.stroke)}"></span></button>`);
      if (anyShape) { const f = els.find(e => isShape(e)).fill; parts.push(`<button type="button" data-colour="fill" title="Fill colour" aria-label="Fill colour"><span class="wb-sw ${f === 'none' ? 'wb-fillsw' : ''}" style="background:${f === 'none' ? 'transparent' : (f || pal().shapeFill)}"></span></button>`); }
    }
    if (els.some(e => e.type === 'connector')) {
      const c0 = els.find(e => e.type === 'connector');
      parts.push([['straight', 'Straight', '／'], ['elbow', 'Elbow', '⌐'], ['smart', 'Elbow that goes around shapes', '⌗'], ['curved', 'Curved', '∿']].map(([r, t, g]) => `<button type="button" data-route="${r}" title="${t}" class="${(c0.route || 'straight') === r ? 'on' : ''}">${g}</button>`).join('')
        + `<button type="button" data-heads title="Arrow ends" class="wb-txtbtn"><span>${HEAD_GLYPH[c0.startHead || 'none']}─${HEAD_GLYPH[c0.endHead ?? 'arrow']}</span></button><button type="button" data-dash title="Dashed" class="${c0.dashed ? 'on' : ''}">┄</button>`);
    }
    const more = [];   // less used: go into ⋯ More
    if (anyShape && els.some(x => x.type !== 'connector')) {
      const s0 = els.find(x => isShape(x));
      more.push(`<button type="button" data-style="dash" class="${s0?.dashed ? 'on' : ''}">┅<span>Dashed outline</span></button><button type="button" data-style="shadow" class="${s0?.shadow ? 'on' : ''}">▣<span>Shadow</span></button><button type="button" data-style="round">${s0?.rounded === false ? '▢' : '◻'}<span>${s0?.rounded === false ? 'Rounded corners' : 'Sharp corners'}</span></button>`);
    }
    if (els.length === 1 && isShape(els[0]) && !isContainer(els[0])) parts.push(`<button type="button" data-act="data" title="Shape data: PIC, duration, status" class="${els[0].data?.length ? 'on' : ''}">ⓘ</button>`);
    if (els.length === 1 && els[0].type === 'rack') { const rc = rackContents(els[0], live()); parts.push(`<button type="button" data-act="data" class="wb-txtbtn" title="Rack size and location">${rc.units} U · ${rc.free} free</button>`); }
    if (els.length === 1 && els[0].type === 'listBox') parts.push(`<button type="button" data-act="restack" class="wb-txtbtn" title="Stack the items again">≡ Tidy list</button>`);
    if (els.length === 1 && (isShape(els[0]) && !isContainer(els[0]) && els[0].type !== 'callout')) more.push(`<button type="button" data-act="callout">💬<span>Add a callout that follows it</span></button>`);
    if (els.length === 1 && els[0].type === 'swimlane') parts.push(`<button type="button" data-act="laneadd" class="wb-txtbtn" title="Add a lane">+ Lane</button><button type="button" data-act="laneflip" class="wb-txtbtn" title="Turn the lanes">${els[0].orient === 'vertical' ? '⇅ Rows' : '⇄ Columns'}</button>`);
    if (els.some(e => e.type === 'path' || e.type === 'connector' || anyShape)) {
      const p = els.find(e => e.type === 'path'), cur = p ? Math.round((p.width || 3) * view.z) : (els.find(e => e.strokeWidth)?.strokeWidth || 2);
      parts.push(`<button type="button" data-width title="Line weight · ${cur} px" aria-label="Line weight"><span class="wb-wline" style="height:${Math.max(1.5, Math.min(8, cur / (p ? 2 : 1)))}px"></span></button>`);
    }
    const textLike = els.some(e => e.type === 'text' || e.type === 'sticky' || anyShape);
    const fontRow = ['ipop', 'hand', 'mono'].map(f => `<button type="button" data-font="${f}" title="${f === 'ipop' ? 'iPop font' : f === 'hand' ? 'Handwriting' : 'Monospace'}" style="font-family:${FONT_STACK[f].replace(/"/g, "'")}">Aa</button>`).join('') + SIZES.map(([n, s]) => `<button type="button" data-size="${s}" title="Text size ${n}">${n}</button>`).join('');
    // plain text and stickies keep font and size in the bar; diagram shapes have them under More
    const diagram = els.some(x => isShape(x));
    if (textLike && !diagram) parts.push(fontRow);
    else if (textLike) more.unshift(`<div class="wb-mrow">${fontRow}</div>`);
    const locked = els.every(e => e.locked);
    if (opts.tasks?.create && els.every(x => x.type === 'sticky')) parts.push(`<button type="button" data-act="task" class="wb-taskbtn" title="Turn into Projects tasks">${icon('task')}<span>Make ${els.length > 1 ? els.length + ' tasks' : 'task'}</span></button>`);
    if (els.length === 1 && (isShape(els[0]) || els[0].type === 'sticky')) (diagram ? more : parts).push(diagram ? `<button type="button" data-act="branch">${icon('mind')}<span>Add a branch (Tab)</span></button>` : `<button type="button" data-act="branch" title="Add a branch (Tab)">${icon('mind')}</button>`);
    const grouped = els.length > 1 && els.every(x => x.groupId && x.groupId === els[0].groupId);
    if (els.some(x => FLOW_TYPES.has(x.type))) parts.push(`<button type="button" data-act="theme" title="Diagram theme: colours and font for the whole flowchart" class="wb-txtbtn">🎨<span>Theme</span></button>`);
    if (els.length > 1) parts.push(`<button type="button" data-act="${grouped ? 'ungroup' : 'group'}" title="${grouped ? 'Ungroup (Ctrl Shift G)' : 'Group (Ctrl G)'}" class="wb-txtbtn">${icon(grouped ? 'ungroup' : 'group')}<span>${grouped ? 'Ungroup' : 'Group'}</span></button><button type="button" data-act="arrange" title="Align and space evenly" class="wb-txtbtn">${icon('arrange')}<span>Arrange</span></button>`);
    if (els.length === 1 && els[0].type !== 'connector') parts.push(`<button type="button" data-act="link" title="${els[0].link ? 'Change or remove the link' : 'Link to a web page or a Notes page (Ctrl K)'}" class="${els[0].link ? 'on' : ''}">${icon('link')}</button>`);
    if (els.length === 1) more.push(`<button type="button" data-act="copystyle">🖌<span>Copy style (Ctrl Shift C)</span></button>${styleClip ? '<button type="button" data-act="pastestyle">📋<span>Paste style (Ctrl Shift V)</span></button>' : ''}`);
    if (opts.library) more.push(`<button type="button" data-act="lib">${icon('bookplus')}<span>Add to my library</span></button>`);
    more.push(`<button type="button" data-act="front">${icon('front')}<span>Bring to front (Ctrl ])</span></button><button type="button" data-act="back">${icon('back')}<span>Send to back (Ctrl [)</span></button><button type="button" data-act="lock">${icon(locked ? 'unlock' : 'lock')}<span>${locked ? 'Unlock' : 'Lock'}</span></button>`);
    parts.push(`<button type="button" data-act="dup" title="Duplicate (Ctrl D)">${icon('copy')}</button><button type="button" data-act="propsmore" title="More" aria-label="More" aria-haspopup="menu">${icon('more')}</button><button type="button" data-act="del" title="Delete (Del)" class="wb-danger">${icon('trash')}</button>`);
    propsMore.innerHTML = more.join('');
    propsMore.hidden = true;
    props.innerHTML = parts.join('<span class="wb-sep"></span>');
    props.hidden = dockOn();
    if (dockOn()) { ctxBar._html = null; paintCtx(); } else positionProps();
  }
  // the selection bar's ⋯ More menu: the same buttons, handled by the bar's own click handler
  const propsMore = el('div', 'wb-menu wb-propsmore'); propsMore.hidden = true; propsMore.setAttribute('role', 'menu');
  root.append(propsMore);
  propsMore.addEventListener('pointerdown', e => e.stopPropagation());
  propsMore.addEventListener('click', e => { const b = e.target.closest('button'); if (!b) return; propsMore.hidden = true; props.dispatchEvent(new CustomEvent('wb-act', { detail: b })); });
  const widthMenu = el('div', 'wb-menu wb-widthmenu'); widthMenu.hidden = true;
  root.append(widthMenu);
  widthMenu.addEventListener('pointerdown', e => e.stopPropagation());
  function toggleWidthMenu(anchor, els) {
    if (!widthMenu.hidden) { widthMenu.hidden = true; return; }
    const strokes = els.some(x => x.type === 'path');
    const opts2 = strokes ? [2, 4, 8, 14, 24] : [1, 2, 4, 6, 8];
    widthMenu.innerHTML = opts2.map(v => `<button type="button" data-w="${v}" title="${v} px" aria-label="${v} px"><span class="wb-wline" style="height:${Math.max(1, Math.min(10, v / (strokes ? 2.2 : 1)))}px"></span></button>`).join('');
    widthMenu.dataset.kind = strokes ? 'path' : 'shape';
    widthMenu.hidden = false;
    const r = anchor.getBoundingClientRect(), rr = rect();
    widthMenu.style.left = `${Math.max(8, Math.min(r.left - rr.left - 8, rr.width - widthMenu.offsetWidth - 8))}px`;
    widthMenu.style.top = `${r.bottom - rr.top + 6}px`;
  }
  widthMenu.addEventListener('click', e => {
    const b = e.target.closest('[data-w]'); if (!b) return;
    const v = +b.dataset.w; widthMenu.hidden = true;
    applyToSelection(x => x.type === 'path' ? { width: +(v / view.z).toFixed(2) } : (x.type === 'connector' || isShape(x)) ? { strokeWidth: v } : null);
    paintProps();
  });
  // ── Group, align, distribute ─────────────────────────────────────────────
  function groupSel(on) {
    const els = [...sel].map(get).filter(x => x && !x.locked);
    if (on && els.length < 2) return;
    const gid = on ? create('group').id : undefined;
    write(els.map(x => bump(x, { groupId: gid })), { now: true }); commitStop();
    toast(on ? `Grouped ${els.length} things. Double-click to pick one inside.` : 'Ungrouped');
    paintProps(); render();
  }
  const arrangeMenu = el('div', 'wb-menu wb-arrange'); arrangeMenu.hidden = true;
  root.append(arrangeMenu);
  arrangeMenu.addEventListener('pointerdown', e => e.stopPropagation());
  function toggleArrange(anchor) {
    if (!arrangeMenu.hidden) { arrangeMenu.hidden = true; return; }
    const n = [...sel].length;
    const btn = (k, label) => `<button type="button" data-al="${k}" title="${label}" aria-label="${label}">${icon(k.replace(/^(left|hcenter|right|top|vcenter|bottom)$/, m => ({ left: 'alignleft', hcenter: 'alignhc', right: 'alignright', top: 'aligntop', vcenter: 'alignvc', bottom: 'alignbottom' }[m])))}</button>`;
    arrangeMenu.innerHTML = `<div class="wb-mh">Align</div><div class="wb-ig">${btn('left', 'Align left')}${btn('hcenter', 'Align centre')}${btn('right', 'Align right')}${btn('top', 'Align top')}${btn('vcenter', 'Align middle')}${btn('bottom', 'Align bottom')}</div>
      <div class="wb-mh">Space evenly</div><div class="wb-ig"><button type="button" data-al="distx" title="Across" aria-label="Space evenly across" ${n < 3 ? 'disabled' : ''}>${icon('distx')}</button><button type="button" data-al="disty" title="Down" aria-label="Space evenly down" ${n < 3 ? 'disabled' : ''}>${icon('disty')}</button></div>
      <button type="button" class="wb-mi" data-al="row">Tidy up into a row</button>`;
    arrangeMenu.hidden = false;
    const r = anchor.getBoundingClientRect(), rr = rect();
    arrangeMenu.style.left = `${Math.max(8, Math.min(r.left - rr.left - 20, rr.width - arrangeMenu.offsetWidth - 8))}px`;
    const top = r.bottom - rr.top + 6;
    arrangeMenu.style.top = `${top + arrangeMenu.offsetHeight > rr.height - 8 ? Math.max(8, r.top - rr.top - arrangeMenu.offsetHeight - 6) : top}px`;
  }
  arrangeMenu.addEventListener('click', e => {
    const b = e.target.closest('[data-al]'); if (!b || b.disabled) return;
    // a group moves as one box
    const els = [...sel].map(get).filter(x => x && !x.locked && x.type !== 'connector');
    const units = new Map();
    for (const x of els) { const k = x.groupId || x.id; if (!units.has(k)) units.set(k, []); units.get(k).push(x); }
    const items = [...units.entries()].map(([id, list]) => ({ id, b: unionBounds(list, get), list }));
    const how = b.dataset.al;
    const moves = how === 'distx' ? distributeBoxes(items, 'x') : how === 'disty' ? distributeBoxes(items, 'y') : how === 'row' ? tidyRow(items) : alignBoxes(items, how);
    const out = [];
    for (const m of moves) for (const x of units.get(m.id)) out.push(moved(x, m.dx, m.dy));
    if (out.length) { write(out, { now: true }); commitStop(); }
    arrangeMenu.hidden = true;
    render(); paintProps();
  });

  // ── Link on any element: a web address or a Notes page ───────────────────
  const linkEd = el('div', 'wb-panel wb-linked'); linkEd.hidden = true; linkEd.setAttribute('role', 'dialog'); linkEd.setAttribute('aria-label', 'Link');
  root.append(linkEd);
  linkEd.addEventListener('pointerdown', e => e.stopPropagation());
  let linkFor = null;
  const notePages = q => (opts.pages?.all?.() || []).filter(p => !q || String(p.title || '').toLowerCase().includes(q.toLowerCase())).slice(0, 8);
  function openLinkEditor(target, anchor) {
    linkFor = target.id;
    const cur = target.link || '';
    const curPage = cur.startsWith('note:') ? opts.pages?.info?.(cur.slice(5)) : null;
    linkEd.innerHTML = `<div class="wb-ph"><b>Link</b><button type="button" data-close aria-label="Close">✕</button></div>
      <input class="wb-search" data-q value="${esc(curPage ? curPage.title : cur)}" placeholder="Paste a web address or find a Notes page" aria-label="Link to">
      <div class="wb-cardlist" data-res></div>
      <div class="wb-row2">${cur ? '<button type="button" class="wb-wide" data-unlink>Remove link</button>' : ''}<button type="button" class="wb-wide wb-pri" data-save>Save</button></div>`;
    linkEd.hidden = false;
    const input = linkEd.querySelector('[data-q]');
    const fill = () => {
      const v = input.value.trim();
      const res = linkEd.querySelector('[data-res]');
      const web = /^(https?:\/\/|www\.)\S+$/i.test(v);
      res.innerHTML = web ? `<button type="button" data-url="${esc(v)}">↗ ${esc(v.slice(0, 80))}</button>` : notePages(v).map(p => `<button type="button" data-page="${p.id}">${esc(p.emoji || '📄')} ${esc(p.title || 'Untitled')}</button>`).join('') || '<p class="wb-note">No Notes page found. Paste a web address instead.</p>';
    };
    input.addEventListener('input', fill);
    input.addEventListener('keydown', ev => { ev.stopPropagation(); if (ev.key === 'Escape') linkEd.hidden = true; if (ev.key === 'Enter') linkEd.querySelector('[data-res] button, [data-save]')?.click(); });
    fill(); input.focus(); input.select();
    if (anchor && !isPhone()) {
      const r = anchor.getBoundingClientRect(), rr = rect();
      linkEd.style.transform = 'none';
      linkEd.style.left = `${Math.max(8, Math.min(r.left - rr.left - 40, rr.width - linkEd.offsetWidth - 8))}px`;
      const top = r.bottom - rr.top + 8;
      linkEd.style.top = `${top + linkEd.offsetHeight > rr.height - 8 ? Math.max(8, r.top - rr.top - linkEd.offsetHeight - 8) : top}px`;
    } else { linkEd.style.transform = ''; linkEd.style.left = ''; linkEd.style.top = ''; }
  }
  function setLink(value) {
    const t = get(linkFor); linkEd.hidden = true;
    if (!t || t.locked || readOnly) return;
    write([bump(t, { link: value || undefined })], { now: true }); commitStop(); paintProps();
    toast(value ? 'Link added. Click the blue badge to open it.' : 'Link removed');
  }
  linkEd.addEventListener('click', e => {
    if (e.target.closest('[data-close]')) { linkEd.hidden = true; return; }
    if (e.target.closest('[data-unlink]')) { setLink(null); return; }
    const pg = e.target.closest('[data-page]'); if (pg) { setLink(`note:${pg.dataset.page}`); return; }
    const u = e.target.closest('[data-url]'); if (u) { setLink(normUrl(u.dataset.url)); return; }
    if (e.target.closest('[data-save]')) {
      const v = linkEd.querySelector('[data-q]').value.trim();
      if (!v) { setLink(null); return; }
      if (/^(https?:\/\/|www\.)\S+$/i.test(v)) { setLink(normUrl(v)); return; }
      const p = notePages(v)[0]; if (p) setLink(`note:${p.id}`); else toast('Paste a web address (https://…) or pick a Notes page');
    }
  });
  const normUrl = v => (/^www\./i.test(v) ? `https://${v}` : v);
  /// Opening a link: Notes pages inside iPop, web addresses in a new tab.
  function openLink(link) {
    if (!link) return;
    if (link.startsWith('note:')) { opts.pages?.open?.(link.slice(5)); return; }
    if (/^https?:\/\//i.test(link)) { if (opts.onLinkOpen) opts.onLinkOpen(link); else window.open(link, '_blank', 'noopener,noreferrer'); }
  }

  // ── Arrow ends menu ──────────────────────────────────────────────────────
  const headsMenu = el('div', 'wb-menu wb-headsmenu'); headsMenu.hidden = true;
  root.append(headsMenu);
  headsMenu.addEventListener('pointerdown', e => e.stopPropagation());
  function toggleHeads(anchor) {
    if (!headsMenu.hidden) { headsMenu.hidden = true; return; }
    const c = [...sel].map(get).find(x => x?.type === 'connector'); if (!c) return;
    const row = (end, cur) => `<div class="wb-mh">${end === 'startHead' ? 'Start' : 'End'}</div><div class="wb-hrow">${HEADS.map(([k, t]) => `<button type="button" data-hk="${end}" data-hv="${k}" title="${t}" class="${cur === k ? 'on' : ''}">${HEAD_GLYPH[k] || '—'}</button>`).join('')}</div>`;
    headsMenu.innerHTML = row('startHead', c.startHead || 'none') + row('endHead', c.endHead ?? 'arrow');
    headsMenu.hidden = false;
    const r = anchor.getBoundingClientRect(), rr = rect();
    headsMenu.style.left = `${Math.max(8, Math.min(r.left - rr.left - 20, rr.width - headsMenu.offsetWidth - 8))}px`;
    headsMenu.style.top = `${r.bottom - rr.top + 6}px`;
  }
  headsMenu.addEventListener('click', e => {
    const b = e.target.closest('[data-hk]'); if (!b) return;
    applyToSelection(x => x.type === 'connector' ? { [b.dataset.hk]: b.dataset.hv } : null);
    headsMenu.hidden = true; paintProps();
  });

  // ── Diagram themes (Visio): one click restyles the flowchart ─────────────
  const DIAGRAM_THEMES = [
    ['classic', 'Classic', { fill: undefined, stroke: undefined, textColor: undefined, font: undefined, decision: undefined, end: undefined }],
    ['blue', 'Blue', { fill: '#DCEBFF', stroke: '#0A84FF', decision: '#FFE9C7', end: '#D6F5DD' }],
    ['green', 'iPop green', { fill: '#DDF3E4', stroke: '#1E8E3E', decision: '#FFF3C4', end: '#E8F0FF' }],
    ['warm', 'Warm', { fill: '#FFE9D6', stroke: '#C2410C', decision: '#FFF3C4', end: '#FDE2E2' }],
    ['sketch', 'Sketch', { fill: '#FFF8DC', stroke: '#5C4A1E', font: 'hand', decision: '#FFE9C7', end: '#E3F2E6' }],
    ['mono', 'Mono', { fill: 'none', stroke: '#1D1D1F', font: 'mono', decision: 'none', end: 'none' }],
  ];
  const themeMenu = el('div', 'wb-menu wb-thememenu'); themeMenu.hidden = true;
  root.append(themeMenu);
  themeMenu.addEventListener('pointerdown', e => e.stopPropagation());
  function toggleThemes(anchor) {
    if (!themeMenu.hidden) { themeMenu.hidden = true; return; }
    themeMenu.innerHTML = `<div class="wb-mh">Theme for the whole flowchart</div><div class="wb-themes">${DIAGRAM_THEMES.map(([k, n, t]) => `<button type="button" data-theme="${k}"><i><span style="background:${t.fill && t.fill !== 'none' ? t.fill : 'var(--wb-surface)'};border-color:${t.stroke || 'var(--wb-ink)'}"></span><span style="background:${t.decision && t.decision !== 'none' ? t.decision : 'var(--wb-surface)'};border-color:${t.stroke || 'var(--wb-ink)'}"></span></i>${n}</button>`).join('')}</div>`;
    themeMenu.hidden = false;
    const r = anchor.getBoundingClientRect(), rr = rect();
    themeMenu.style.left = `${Math.max(8, Math.min(r.left - rr.left - 40, rr.width - themeMenu.offsetWidth - 8))}px`;
    const top = r.bottom - rr.top + 6;
    themeMenu.style.top = `${top + themeMenu.offsetHeight > rr.height - 8 ? Math.max(8, r.top - rr.top - themeMenu.offsetHeight - 6) : top}px`;
  }
  themeMenu.addEventListener('click', e => {
    const b = e.target.closest('[data-theme]'); if (!b || readOnly) return;
    const t = DIAGRAM_THEMES.find(x => x[0] === b.dataset.theme)[2];
    // the flowchart the selection belongs to: everything connected to it
    const start = [...sel].map(get).filter(Boolean);
    const ids = new Set(start.map(x => x.id)), q = [...ids];
    const lines = live().filter(x => x.type === 'connector');
    while (q.length) { const id = q.shift(); for (const c of lines) { const o = c.from?.id === id ? c.to?.id : c.to?.id === id ? c.from?.id : null; if (o && !ids.has(o)) { ids.add(o); q.push(o); } } }
    const out = [];
    for (const x of live()) {
      if (!ids.has(x.id) && !(x.type === 'connector' && ids.has(x.from?.id))) continue;
      if (x.locked) continue;
      if (x.type === 'connector') { out.push(bump(x, { stroke: t.stroke })); continue; }
      if (!isShape(x) || isContainer(x)) continue;
      const fill = x.type === 'decision' || x.type === 'bpmnGateway' ? t.decision : x.type === 'terminator' ? t.end : t.fill;
      out.push(bump(x, { fill, stroke: t.stroke, font: t.font, textColor: undefined }));
    }
    if (out.length) { write(out, { now: true }); commitStop(); }
    themeMenu.hidden = true; paintProps();
    toast('Theme applied. Ctrl Z undoes it.');
  });

  // ── Copy / paste style ───────────────────────────────────────────────────
  let styleClip = null;
  const STYLE_KEYS = ['stroke', 'fill', 'textColor', 'strokeWidth', 'dashed', 'shadow', 'rounded', 'font', 'size', 'align', 'opacity', 'route', 'startHead', 'endHead'];
  function copyStyle(x) { if (!x) return; styleClip = Object.fromEntries(STYLE_KEYS.filter(k => k in x).map(k => [k, x[k]])); styleClip._line = x.type === 'connector'; toast('Style copied. Select other shapes and press Ctrl Shift V.'); paintProps(); }
  function pasteStyle() {
    if (!styleClip) return;
    const lineKeys = new Set(['stroke', 'strokeWidth', 'dashed', 'route', 'startHead', 'endHead', 'opacity']);
    applyToSelection(x => {
      const isLine = x.type === 'connector';
      const keys = STYLE_KEYS.filter(k => k in styleClip && (isLine ? lineKeys.has(k) : !['route', 'startHead', 'endHead'].includes(k)));
      return keys.length ? Object.fromEntries(keys.map(k => [k, styleClip[k]])) : null;
    });
    paintProps();
  }
  // the last shape style is used for the next shape (draw.io behaviour)
  let lastShapeStyle = null;

  // ── Shape data (Visio): PIC, duration, status, or any field ─────────────
  const dataEd = el('div', 'wb-panel wb-dataed'); dataEd.hidden = true; dataEd.setAttribute('role', 'dialog'); dataEd.setAttribute('aria-label', 'Shape data');
  root.append(dataEd);
  dataEd.addEventListener('pointerdown', e => e.stopPropagation());
  let dataFor = null;
  function openDataEditor(target, anchor) {
    dataFor = target.id;
    const rows = (target.data?.length ? target.data : isNetDevice(target) ? [...NET_FIELDS, 'VLAN'].map(name => ({ name, value: '' })) : target.type === 'rack' ? [{ name: 'U', value: '24' }, { name: 'Location', value: '' }] : [{ name: 'PIC', value: '' }, { name: 'Duration', value: '' }, { name: 'Status', value: '' }]);
    dataEd.innerHTML = `<div class="wb-ph"><b>Shape data</b><button type="button" data-close aria-label="Close">✕</button></div>
      <p class="wb-note">Shown under the title. A status of Done, In progress or Late colours the left edge.</p>
      <div class="wb-drows">${rows.map((r, i) => `<div class="wb-drow"><input data-dn="${i}" value="${esc(r.name)}" placeholder="Field" aria-label="Field name"><input data-dv="${i}" value="${esc(r.value)}" placeholder="Value" aria-label="Value"></div>`).join('')}</div>
      <div class="wb-row2"><button type="button" class="wb-wide" data-addrow>+ Field</button><button type="button" class="wb-wide wb-pri" data-savedata>Save</button></div>`;
    dataEd.hidden = false;
    if (anchor && !isPhone()) { const r = anchor.getBoundingClientRect(), rr = rect(); dataEd.style.transform = 'none'; dataEd.style.left = `${Math.max(8, Math.min(r.left - rr.left - 60, rr.width - dataEd.offsetWidth - 8))}px`; const top = r.bottom - rr.top + 8; dataEd.style.top = `${top + dataEd.offsetHeight > rr.height - 8 ? Math.max(8, r.top - rr.top - dataEd.offsetHeight - 8) : top}px`; }
    else { dataEd.style.transform = ''; dataEd.style.left = ''; dataEd.style.top = ''; }
    dataEd.querySelector('input')?.focus();
  }
  dataEd.addEventListener('keydown', e => { e.stopPropagation(); if (e.key === 'Escape') dataEd.hidden = true; if (e.key === 'Enter') dataEd.querySelector('[data-savedata]').click(); });
  dataEd.addEventListener('click', e => {
    if (e.target.closest('[data-close]')) { dataEd.hidden = true; return; }
    if (e.target.closest('[data-addrow]')) { const box = dataEd.querySelector('.wb-drows'); const i = box.children.length; box.insertAdjacentHTML('beforeend', `<div class="wb-drow"><input data-dn="${i}" placeholder="Field" aria-label="Field name"><input data-dv="${i}" placeholder="Value" aria-label="Value"></div>`); box.lastElementChild.querySelector('input').focus(); return; }
    if (e.target.closest('[data-savedata]')) {
      const rows = [...dataEd.querySelectorAll('.wb-drow')].map(r => ({ name: r.children[0].value.trim().slice(0, 30), value: r.children[1].value.trim().slice(0, 80) })).filter(r => r.value);
      const t = get(dataFor); dataEd.hidden = true;
      if (t && !t.locked && !readOnly) { write([bump(t, { data: rows.length ? rows : undefined })], { now: true }); commitStop(); paintProps(); }
    }
  });

  // ── Shapes panel: every stencil, searchable, with what each shape means ──
  const shapeTip = el('div', 'wb-shapetip'); shapeTip.hidden = true;
  root.append(shapePanel, shapeTip);
  shapePanel.addEventListener('pointerdown', e => e.stopPropagation());
  const PANEL_CATS = [['Basic', ICON_SHAPES.map(t => [t, SHAPE_LABEL[t], BASIC_TIPS[t]])], ...STENCIL_CATS.map(c => [c, STENCILS.filter(s => s[2] === c).map(s => [s[0], s[1], s[5]])])];
  const ORDER = ['Flowchart', 'Basic', 'Containers', 'BPMN', 'UML', 'ERD', 'Network', 'Org chart', 'Floor plan', 'Timeline', 'Value stream'];
  function shapeIcon(type) {
    const st = stencilPath(type, 6, 10, 88, 60);
    if (st) return `<svg viewBox="0 0 100 80" aria-hidden="true">${(st.behind || []).map(b => `<path d="${b}" class="wb-sic"/>`).join('')}${st.rx && /^M[\d.-]+ [\d.-]+H/.test(st.d) && !st.extra?.includes('A') ? `<rect x="6" y="10" width="88" height="60" rx="${Math.min(st.rx, 10)}" class="wb-sic"/>` : `<path d="${st.d}" class="wb-sic" ${st.fill ? `style="fill:${st.fill}"` : ''}/>`}${st.extra ? `<path d="${st.extra}" class="wb-sicx"/>` : ''}${st.dot ? `<path d="${st.dot}" style="fill:currentColor;stroke:none"/>` : ''}${st.solid ? `<path d="${st.d}" style="fill:currentColor"/>` : ''}</svg>`;
    return `<svg viewBox="0 0 24 24" class="wb-i" aria-hidden="true">${ICONS[type] || ''}</svg>`;
  }
  function toggleShapePanel() {
    if (!shapePanel.hidden) { shapePanel.hidden = true; shapeTip.hidden = true; return; }
    for (const p of [framesPanel, timerPanel, libPanel, gallery, votePanel, exportPanel, cardPicker, emojiPop, guidePanel, checkPanel, importPanel]) p.hidden = true;
    shapePanel.hidden = false;
    paintShapePanel('');
  }
  function paintShapePanel(q) {
    const s = q.trim().toLowerCase();
    const cats = ORDER.map(name => PANEL_CATS.find(c => c[0] === name)).filter(Boolean);
    const body = cats.map(([cat, list]) => {
      const shown = list.filter(([t, label, tip]) => !s || label.toLowerCase().includes(s) || (tip || '').toLowerCase().includes(s) || cat.toLowerCase().includes(s));
      return shown.length ? `<div class="wb-scat"><div class="wb-mh">${cat}<span>${shown.length}</span></div><div class="wb-sgrid">${shown.map(([t, label]) => `<button type="button" data-st="${t}" aria-label="${esc(label)}" draggable="false">${shapeIcon(t)}</button>`).join('')}</div></div>` : '';
    }).join('');
    shapePanel.innerHTML = `<div class="wb-ph"><b>Shapes</b><button type="button" data-close aria-label="Close">✕</button></div>
      <input class="wb-search" type="search" placeholder="Search shapes (decision, database, router…)" value="${esc(q)}" aria-label="Search shapes">
      <div class="wb-sbody">${body || '<p class="wb-note">No shape found.</p>'}</div>
      <p class="wb-note">Click to add. With a shape selected, the new one is added after it and connected.</p>`;
    const input = shapePanel.querySelector('.wb-search');
    input.addEventListener('input', () => { const v = input.value; paintShapePanel(v); const i2 = shapePanel.querySelector('.wb-search'); i2.focus(); i2.setSelectionRange(v.length, v.length); });
    input.addEventListener('keydown', e => { e.stopPropagation(); if (e.key === 'Escape') shapePanel.hidden = true; if (e.key === 'Enter') shapePanel.querySelector('[data-st]')?.click(); });
  }
  shapePanel.addEventListener('pointerover', e => {
    const b = e.target.closest('[data-st]');
    if (!b) { shapeTip.hidden = true; return; }
    const t = b.dataset.st, label = SHAPE_LABEL[t], tip = STENCIL[t]?.tip || BASIC_TIPS[t] || '';
    shapeTip.innerHTML = `<b>${esc(label)}</b>${esc(tip)}`; shapeTip.hidden = false;
    const r = b.getBoundingClientRect(), rr = rect(), pr = shapePanel.getBoundingClientRect();
    const right = pr.right - rr.left + 8;
    shapeTip.style.left = `${right + 220 > rr.width ? Math.max(8, pr.left - rr.left - 228) : right}px`; shapeTip.style.top = `${r.top - rr.top}px`;
  });
  shapePanel.addEventListener('pointerleave', () => { shapeTip.hidden = true; });
  shapePanel.addEventListener('click', e => {
    if (e.target.closest('[data-close]')) { shapePanel.hidden = true; shapeTip.hidden = true; return; }
    const b = e.target.closest('[data-st]'); if (!b || readOnly) return;
    const t = b.dataset.st, from = sel.size === 1 ? get([...sel][0]) : null;
    // a callout points at the selected shape and follows it
    if (t === 'callout') {
      const r0 = rect(), c0 = from ? { x: from.x + from.w + 110, y: from.y - 40 } : toWorld(r0.width / 2, r0.height / 2);
      const s = makeShape('callout', { x: c0.x - 80, y: c0.y - 30 }, '');
      const [placed] = add([from && from.type !== 'connector' ? { ...s, attach: from.id } : s]) || [];
      if (placed) editSoon2(placed);
      if (!from) toast('Tip: select a shape first and the callout points at it.');
      return;
    }
    // a device clicked while a rack is selected goes into the rack's lowest free slot
    if (from?.type === 'rack' && isNetDevice({ type: t })) { addToRack(from, t); return; }
    if (from && isShape(from) && !isContainer(from) && !isContainer({ type: t })) { addAfter(from, 'b', t); return; }
    const r = rect(); let c = toWorld(r.width / 2, r.height / 2);
    if (isContainer({ type: t })) {
      const all2 = live().filter(x => !x.isDeleted), ub = unionBounds(all2, get), d = STENCIL[t];
      if (ub) c = { x: ub.x + ub.w + 120 + d.w / 2, y: ub.y + d.h / 2 };
      const placed = placeShape(t, c);
      if (placed) fit([placed], { max: 1 });
      return;
    }
    placeShape(t, c);
  });
  /// A new shape of type t centred at c (with its default size and the last style used).
  function makeShape(t, box, text) {
    const d = STENCIL[t] || { w: 180, h: 80 };
    const base = { x: box.x, y: box.y, w: box.w ?? d.w, h: box.h ?? d.h, text: text ?? '', stroke: inkNow() };
    if (t === 'swimlane') return create(t, { ...base, text: '', lanes: [{ id: 'l1', name: 'Lane 1', size: 1 }, { id: 'l2', name: 'Lane 2', size: 1 }, { id: 'l3', name: 'Lane 3', size: 1 }] });
    if (t === 'groupBox') return create(t, { ...base, title: 'Group', fill: 'none' });
    if (t === 'erTable') return create(t, { ...base, text: text ?? 'Customer\nid  int  PK\nname  text\nphone  text' });
    if (t === 'vsProcess') return create(t, { ...base, text: text ?? 'Assembly', data: [{ name: 'C/T', value: '45 s' }, { name: 'People', value: '2' }] });
    if (t === 'rack') return create(t, { ...base, title: 'Rack A', fill: 'none', data: [{ name: 'U', value: '24' }, { name: 'Location', value: '' }] });
    if (t === 'listBox' || t === 'fpRoom' || t === 'umlPackage') return create(t, { ...base, title: { listBox: 'Checklist', fpRoom: 'Room', umlPackage: 'Package' }[t], fill: 'none' });
    if (t === 'umlClass') return create(t, { ...base, text: text ?? 'Order\n- id: int\n- date: Date\n--\n+ approve()' });
    if (t === 'umlInterface') return create(t, { ...base, text: text ?? 'Approvable\n+ approve()\n+ reject()' });
    if (t === 'umlLifeline') return create(t, { ...base, text: text ?? 'Purchasing' });
    if (t === 'orgCard') return create(t, { ...base, text: text ?? 'Name\nDepartment' });
    if (isNetDevice({ type: t })) return create(t, { ...base, data: NET_FIELDS.map(name => ({ name, value: '' })) });
    return create(t, { ...base, ...(lastShapeStyle && FLOW_TYPES.has(t) ? lastShapeStyle : {}) });
  }
  function placeShape(t, c) {
    const d = STENCIL[t] || { w: 180, h: 80 };
    const s = makeShape(t, { x: c.x - d.w / 2, y: c.y - d.h / 2 });
    const [placed] = add([s]) || [];
    if (placed && !isContainer(placed) && !/^net|milestone|vsInventory|vsTruck/.test(t)) editSoon2(placed);
    return placed;
  }
  /// Add a shape after `from` in direction dir, connected (the blue arrows, hints and guide all use this).
  function addAfter(from, dir, t, { text, label, select = true, edit = true } = {}) {
    if (readOnly || !from) return null;
    const d = STENCIL[t] || { w: from.w, h: from.h };
    const box = placeNext(bounds(from, get), dir, { w: d.w, h: d.h }, live());
    const node = makeShape(t, box, text);
    // sides are left to the route (it faces the other shape), so lines stay clean when shapes move or get tidied
    const line = create('connector', { from: { id: from.id }, to: { id: node.id }, route: 'elbow', endHead: 'arrow', startHead: 'none', stroke: lastLineStyle?.stroke ?? null, label: label || undefined });
    add([node, line], { select: false });
    if (select) { sel = new Set([node.id]); paintProps(); }
    if (edit && !text && !isContainer(node)) editSoon2(node);
    render();
    return node;
  }
  let lastLineStyle = null;

  // ── Blue arrows (draw.io): hover a shape, click an arrow to add the next one, hold to pick a shape ──
  const arrowsLayer = el('div', 'wb-arrows');
  const miniPick = el('div', 'wb-menu wb-minipick'); miniPick.hidden = true;
  root.append(arrowsLayer, miniPick);
  miniPick.addEventListener('pointerdown', e => e.stopPropagation());
  let arrowFor = null, arrowHold = 0, arrowDrag = null;
  const MINI = ['process', 'decision', 'terminator', 'data', 'document', 'subprocess', 'database', 'delay'];
  function paintArrows() {
    if (arrowDrag) return;            // never swap the buttons under a pressed pointer
    arrowsLayer.innerHTML = '';
    const t = arrowFor && get(arrowFor);
    if (!t || readOnly || tool !== 'select' || interaction || editing || !isShape(t) || isContainer(t) || t.locked) return;
    const b = bounds(t, get), a = toScreen(b.x, b.y), z = toScreen(b.x + b.w, b.y + b.h), off = 20;
    const spots = [['t', (a.x + z.x) / 2, a.y - off, -90], ['r', z.x + off, (a.y + z.y) / 2, 0], ['b', (a.x + z.x) / 2, z.y + off, 90], ['l', a.x - off, (a.y + z.y) / 2, 180]];
    arrowsLayer.innerHTML = spots.map(([d, x, y, rot]) => `<button type="button" class="wb-arrow" data-dir="${d}" style="left:${x}px;top:${y}px" title="Click: add the next shape here. Drag: draw a line. Hold: pick a shape." aria-label="Add a shape ${({ t: 'above', r: 'to the right', b: 'below', l: 'to the left' })[d]}"><svg viewBox="0 0 24 24" style="transform:rotate(${rot}deg)"><path d="M4 12h12M12 6l6 6-6 6"/></svg></button>`).join('');
  }
  root.addEventListener('pointermove', e => {
    if (interaction || e.pointerType === 'touch') return;
    if (e.target.closest('.wb-arrows,.wb-minipick')) return;
    const { sx, sy } = local(e), w = toWorld(sx, sy);
    const hit = hitTest(live().filter(x => isShape(x) && !isContainer(x)), w.x, w.y, get, 4 / view.z);
    if (hit) { if (hit.id !== arrowFor) { arrowFor = hit.id; paintArrows(); } return; }
    // keep the arrows while the pointer is near the shape (on its way to an arrow)
    const t = arrowFor && get(arrowFor);
    if (t) { const b = bounds(t, get); const near = 40 / view.z; if (w.x < b.x - near || w.x > b.x + b.w + near || w.y < b.y - near || w.y > b.y + b.h + near) { arrowFor = null; paintArrows(); } }
  });
  root.addEventListener('pointerleave', () => { arrowFor = null; paintArrows(); });
  arrowsLayer.addEventListener('pointerdown', e => {
    const b = e.target.closest('.wb-arrow'); if (!b) return;
    e.stopPropagation(); e.preventDefault();
    const from = get(arrowFor); if (!from) return;
    arrowDrag = { dir: b.dataset.dir, from, x: e.clientX, y: e.clientY, moved: false, id: e.pointerId, btn: b, t: Date.now() };
    clearTimeout(arrowHold);
    arrowHold = setTimeout(() => { if (arrowDrag && !arrowDrag.moved) { openMini(b, arrowDrag.dir, from); arrowDrag = null; } }, 450);
    try { b.setPointerCapture(e.pointerId); } catch { /* fine */ }
  });
  arrowsLayer.addEventListener('pointermove', e => {
    if (!arrowDrag) return;
    if (Math.hypot(e.clientX - arrowDrag.x, e.clientY - arrowDrag.y) > 6 && !arrowDrag.moved) {
      // drag from an arrow = draw a line from that side
      arrowDrag.moved = true; clearTimeout(arrowHold);
      const from = arrowDrag.from, dir = arrowDrag.dir;
      const c = create('connector', { from: { id: from.id, side: dir }, to: toWorld(local(e).sx, local(e).sy), route: 'elbow', endHead: 'arrow', startHead: 'none', stroke: inkNow() });
      c.z = zBetween(topZ(all()) || null, null);
      interaction = { kind: 'connect', el: c };
      draft.set(c.id, c); arrowsLayer.innerHTML = '';
      try { root.setPointerCapture(e.pointerId); } catch { /* fine */ }
      pointers.set(e.pointerId, local(e));
    }
  });
  const dropArrow = () => { arrowDrag = null; clearTimeout(arrowHold); };
  arrowsLayer.addEventListener('pointercancel', dropArrow);
  arrowsLayer.addEventListener('lostpointercapture', e => { if (arrowDrag && !arrowDrag.moved && e.pointerId === arrowDrag.id) setTimeout(() => { if (arrowDrag && Date.now() - arrowDrag.t > 50) dropArrow(); }, 0); });
  arrowsLayer.addEventListener('pointerup', e => {
    const d = arrowDrag; arrowDrag = null; clearTimeout(arrowHold);
    // a click counts only on the same arrow, the same pointer, and quickly (a hold opens the shape picker instead)
    if (!d || d.moved || e.pointerId !== d.id || e.target.closest('.wb-arrow') !== d.btn || Date.now() - d.t > 600) return;
    e.stopPropagation();
    const from = d.from;
    const t = from.type === 'terminator' ? 'process' : (STENCIL[from.type] || SHAPE_TYPES.has(from.type) ? from.type : 'process');
    const next = addAfter(from, d.dir, t, { edit: true });
    if (next) { arrowFor = next.id; requestAnimationFrame(paintArrows); }
  });
  function openMini(anchor, dir, from) {
    miniPick.innerHTML = MINI.map(t => `<button type="button" data-mt="${t}" title="${SHAPE_LABEL[t]}" aria-label="${SHAPE_LABEL[t]}">${shapeIcon(t)}</button>`).join('');
    miniPick.dataset.dir = dir; miniPick.dataset.from = from.id;
    miniPick.hidden = false;
    const r = anchor.getBoundingClientRect(), rr = rect();
    miniPick.style.left = `${Math.max(8, Math.min(r.right - rr.left + 6, rr.width - miniPick.offsetWidth - 8))}px`;
    miniPick.style.top = `${Math.max(8, Math.min(r.top - rr.top - 20, rr.height - miniPick.offsetHeight - 8))}px`;
  }
  miniPick.addEventListener('click', e => {
    const b = e.target.closest('[data-mt]'); if (!b) return;
    miniPick.hidden = true;
    const from = get(miniPick.dataset.from); if (!from) return;
    addAfter(from, miniPick.dataset.dir, b.dataset.mt, { text: b.dataset.mt === 'terminator' ? 'End' : undefined });
  });

  // ── Flowchart guide, in-place hints, Check ───────────────────────────────
  const hintBar = el('div', 'wb-hint'); hintBar.hidden = true;
  root.append(guidePanel, checkPanel, hintBar);
  for (const p of [guidePanel, checkPanel, hintBar]) p.addEventListener('pointerdown', e => e.stopPropagation());
  const GUIDE = [
    ['start', 'Start', 'An oval where the flow begins'],
    ['step', 'First step', 'A box for something someone does'],
    ['choice', 'A choice?', 'A diamond asks a yes / no question'],
    ['end', 'End', 'An oval where the flow stops'],
  ];
  function guideState() {
    const els = live();
    return {
      start: els.some(x => x.type === 'terminator' && !/\b(end|finish|stop|done)\b/i.test(x.text || '')),
      step: els.some(x => x.type === 'process' || x.type === 'subprocess'),
      choice: els.some(x => x.type === 'decision'),
      end: els.some(x => x.type === 'terminator' && /\b(end|finish|stop|done)\b/i.test(x.text || '')),
    };
  }
  function paintGuide() {
    if (guidePanel.hidden) return;
    const st = guideState(), cur = GUIDE.findIndex(([k]) => !st[k]);
    guidePanel.innerHTML = `<div class="wb-ph"><b>Make a flowchart</b><button type="button" data-close aria-label="Close">✕</button></div>
      <p class="wb-note">Four steps. Each <b>Add</b> puts the shape in the right place and connects it.</p>
      <ol class="wb-steps">${GUIDE.map(([k, t, s], i) => `<li class="${st[k] ? 'done' : i === cur ? 'cur' : ''}"><span class="wb-num">${st[k] ? '✓' : i + 1}</span><span>${t}<small>${s}</small></span><button type="button" data-g="${k}" ${st[k] ? 'disabled' : ''}>${st[k] ? 'Done' : 'Add'}</button></li>`).join('')}</ol>
      <p class="wb-note wb-gstate">${cur < 0 ? 'All four are there. Press <b>Check</b> to see if anything is missing.' : `Step ${cur + 1} of ${GUIDE.length}`}</p>
      <div class="wb-row2"><button type="button" class="wb-wide" data-g="tpl">Start from a template</button></div>`;
  }
  function lastInFlow() {
    const els = live(), order = [];
    const g = { nodes: els.filter(x => isShape(x) && !isContainer(x)) };
    const outs = id => els.filter(c => c.type === 'connector' && c.from?.id === id).map(c => c.to?.id);
    // walk from a Start; take the deepest shape that is not an End
    const startEl = g.nodes.find(x => x.type === 'terminator' && !/\b(end|finish|stop|done)\b/i.test(x.text || ''));
    const seen = new Set(), q = startEl ? [startEl.id] : [];
    while (q.length) { const id = q.shift(); if (seen.has(id)) continue; seen.add(id); const x = get(id); if (x) order.push(x); for (const n of outs(id)) q.push(n); }
    return [...order].reverse().find(x => !/\b(end|finish|stop|done)\b/i.test(x.text || '') || x.type !== 'terminator') || order.at(-1) || null;
  }
  guidePanel.addEventListener('click', e => {
    if (e.target.closest('[data-close]')) { guidePanel.hidden = true; paintCorners(); return; }
    const b = e.target.closest('[data-g]'); if (!b || b.disabled || readOnly) return;
    const k = b.dataset.g;
    if (k === 'tpl') { guidePanel.hidden = true; tcs.cats = new Set(['Flowchart & process']); openCenter('all'); return; }
    if (k === 'start') {
      const r = rect(), c = toWorld(r.width / 2, Math.min(r.height * 0.25, 140));
      const s = makeShape('terminator', { x: c.x - 80, y: c.y - 28 }, 'Start');
      add([s]); sel = new Set([s.id]); paintProps(); paintGuide(); return;
    }
    const last = lastInFlow();
    if (!last) { toast('Add the Start first'); return; }
    if (k === 'step') addAfter(last, 'b', 'process', { text: 'Receive request', edit: false });
    else if (k === 'choice') { addAfter(last, 'b', 'decision', { text: 'Approved?', edit: false }); toast('Now give the diamond a Yes and a No branch'); }
    else if (k === 'end') {
      const dec = live().find(x => x.type === 'decision' && !live().some(c => c.type === 'connector' && c.from?.id === x.id && /^yes$/i.test(c.label || '')));
      addAfter(dec || last, 'b', 'terminator', { text: 'End', label: dec ? 'Yes' : undefined, edit: false });
    }
    paintGuide();
  });
  const hintsOn = () => { try { return localStorage.getItem(HINTS_KEY) !== 'off'; } catch { return true; } };
  function paintHint() {
    hintBar.hidden = true;
    if (readOnly || interaction || editing || sel.size !== 1 || !hintsOn()) return;
    const t = get([...sel][0]); if (!t) return;
    const hs = nextHints(t, live()); if (!hs.length) return;
    hintBar.innerHTML = `<span class="wb-bulb" aria-hidden="true">💡</span>${hs.map((h, i) => h.kind === 'info' ? `<span>${esc(h.label)}</span>` : `<button type="button" data-hi="${i}">${esc(h.label)}</button>`).join('')}<button type="button" class="wb-x" data-hoff title="Don't show hints again" aria-label="Don't show hints again">✕</button>`;
    hintBar._hints = hs;
    hintBar.hidden = false;
    const b = bounds(t, get), a = toScreen(b.x + b.w / 2, b.y + b.h), top = toScreen(0, b.y).y, r = rect(), h = hintBar.offsetHeight;
    let y = a.y + 14;
    if (y + h > r.height - 70) y = top - h - 14;
    // never on top of the selection bar: go to the far side of it
    if (!props.hidden) {
      const pt = props.offsetTop, pb = pt + props.offsetHeight;
      if (y < pb + 6 && y + h > pt - 6) y = pt > top - 4 ? pb + 8 : pt - h - 8;
      if (y + h > r.height - 64) y = pt - h - 8;
    }
    hintBar.style.left = `${Math.max(8, Math.min(a.x - hintBar.offsetWidth / 2, r.width - hintBar.offsetWidth - 8))}px`;
    hintBar.style.top = `${Math.max(8, y)}px`;
  }
  hintBar.addEventListener('click', e => {
    if (e.target.closest('[data-hoff]')) { try { localStorage.setItem(HINTS_KEY, 'off'); } catch { /* blocked */ } hintBar.hidden = true; toast('Hints are off. Turn them on again under Guide.'); return; }
    const b = e.target.closest('[data-hi]'); if (!b) return;
    const h = hintBar._hints[+b.dataset.hi], t = get([...sel][0]); if (!h || !t) return;
    addAfter(t, h.dir || 'b', h.type, { text: h.text || (h.branch ? (h.branch === 'Yes' ? '' : '') : undefined) || undefined, label: h.branch });
  });
  function paintCheck() {
    if (checkPanel.hidden) return;
    const issues = checkFlow(live());
    checkPanel.innerHTML = `<div class="wb-ph"><b>Check this flowchart</b><button type="button" data-close aria-label="Close">✕</button></div>
      ${issues.length ? issues.map((it, i) => `<div class="wb-issue ${it.level}"><span aria-hidden="true">${it.level === 'bad' ? '⛔' : '⚠️'}</span><span>${esc(it.text)}</span>${it.fix ? `<button type="button" data-fix="${i}">Fix</button>` : it.ids.length ? `<button type="button" data-show="${i}">Show</button>` : ''}</div>`).join('')
        : '<div class="wb-issue ok"><span aria-hidden="true">✅</span><span>Looks good: a Start, an End, and every decision has its ways out.</span></div>'}`;
    checkPanel._issues = issues;
  }
  checkPanel.addEventListener('click', e => {
    if (e.target.closest('[data-close]')) { checkPanel.hidden = true; paintCorners(); return; }
    const issues = checkPanel._issues || [];
    const show = e.target.closest('[data-show]'), fix = e.target.closest('[data-fix]');
    const it = issues[+(show || fix)?.dataset[show ? 'show' : 'fix']];
    if (!it) return;
    if (show || (fix && it.fix === 'branches') || (fix && it.fix === 'next')) {
      if (it.ids[0]) { sel = new Set([it.ids[0]]); const b = bounds(get(it.ids[0]), get), r = rect(); view.x = r.width / 2 - (b.x + b.w / 2) * view.z; view.y = r.height / 2 - (b.y + b.h / 2) * view.z; render(); paintProps(); }
      return;
    }
    if (readOnly) return;
    if (it.fix === 'start') {
      const g = live().filter(x => FLOW_TYPES.has(x.type)).sort((p, q) => p.y - q.y || p.x - q.x)[0];
      if (!g) return;
      const s = makeShape('terminator', { x: g.x + g.w / 2 - 80, y: g.y - 130 }, 'Start');
      add([s, create('connector', { from: { id: s.id }, to: { id: g.id }, route: 'elbow', endHead: 'arrow', startHead: 'none' })], { select: false });
    } else if (it.fix === 'end') {
      const last = lastInFlow() || live().filter(x => FLOW_TYPES.has(x.type)).sort((p, q) => q.y - p.y)[0];
      if (last) addAfter(last, 'b', 'terminator', { text: 'End', edit: false });
    }
    paintCheck();
  });
  /// Called after every paint: keeps the flowchart helpers in step with the board.
  let flowWas = false;
  function paintFlowUi() {
    if (flowOn !== flowWas) { flowWas = flowOn; paintCorners(); }
    paintHint(); paintGuide(); paintCheck(); paintArrows();
    const n = flowOn ? checkFlow(live()).length : 0;
    const badge = topRight.querySelector('[data-c="check"] .wb-badge');
    if (badge) { badge.textContent = String(n); badge.hidden = !n; }
  }

  // ── Tidy up flow ─────────────────────────────────────────────────────────
  function tidyFlow(dir = 'TB') {
    if (readOnly) return;
    const chosen = [...sel].map(get).filter(Boolean);
    const scope = chosen.length > 1 ? live().filter(x => sel.has(x.id) || (x.type === 'connector' && sel.has(x.from?.id) && sel.has(x.to?.id))) : live();
    const shapes = scope.filter(x => isShape(x) && !isContainer(x) && !x.locked);
    if (shapes.length < 2) { toast('Select a flow (or nothing for the whole board) first'); return; }
    const b = unionBounds(shapes, get);
    const pos = layoutFlow(scope, { dir, origin: { x: b.x, y: b.y } });
    const stale = scope.filter(x => x.type === 'connector' && (x.waypoints?.length || x.from?.side || x.to?.side));
    if (!pos.size && !stale.length) { toast('Already tidy'); return; }
    if (pos.size) write([...pos.entries()].map(([id, p]) => { const x = get(id); return x ? bump(x, { x: p.x, y: p.y }) : null; }).filter(Boolean), { now: true });
    // automatic routes again: bends and fixed sides placed by hand no longer fit the new places
    const strip = end => (end?.id ? { id: end.id } : end);
    write(scope.filter(x => x.type === 'connector' && (x.waypoints?.length || x.from?.side || x.to?.side)).map(x => bump(x, { waypoints: undefined, from: strip(x.from), to: strip(x.to) })), { now: true });
    commitStop();
    toast(dir === 'TB' ? 'Flow tidied top to bottom. Ctrl Z undoes it.' : 'Flow tidied left to right. Ctrl Z undoes it.');
  }

  // ── Import: draw.io file or Mermaid text ─────────────────────────────────
  const importPanel = el('div', 'wb-panel wb-importp'); importPanel.hidden = true; importPanel.setAttribute('role', 'dialog'); importPanel.setAttribute('aria-label', 'Import a diagram');
  root.append(importPanel);
  importPanel.addEventListener('pointerdown', e => e.stopPropagation());
  const MERMAID_SAMPLE = 'flowchart TD\n  A([Start]) --> B[Receive request]\n  B --> C{Complete?}\n  C -->|Yes| D[Process order]\n  C -->|No| E[Ask for details]\n  E --> B\n  D --> F([End])';
  function toggleImport() {
    if (!importPanel.hidden) { importPanel.hidden = true; return; }
    for (const p of [framesPanel, timerPanel, libPanel, gallery, votePanel, exportPanel, shapePanel]) p.hidden = true;
    importPanel.innerHTML = `<div class="wb-ph"><b>Import a diagram</b><button type="button" data-close aria-label="Close">✕</button></div>
      <div class="wb-row2"><button type="button" class="wb-wide" data-drawio>Choose a draw.io file (.drawio, .xml)</button></div>
      <p class="wb-note">Or type or paste Mermaid text. The diagram is added beside what is on the board, as shapes you can edit.</p>
      <textarea class="wb-code" spellcheck="false" aria-label="Mermaid text">${esc(MERMAID_SAMPLE)}</textarea>
      <div class="wb-row2"><button type="button" class="wb-wide wb-pri" data-mermaid>Make diagram</button></div>`;
    importPanel.hidden = false;
    importPanel.querySelector('textarea').addEventListener('keydown', e => e.stopPropagation());
  }
  function placeImported(els) {
    if (!els.length) return;
    const b = unionBounds(live(), get), ib = unionBounds(els, id => els.find(x => x.id === id));
    const dx = (b ? b.x + b.w + 160 : 0) - (ib?.x ?? 0), dy = (b ? b.y : 0) - (ib?.y ?? 0);
    const placed = add(els.map(x => (x.type === 'connector' ? x : { ...x, x: x.x + dx, y: x.y + dy })), { select: false }) || [];
    fit(placed, { max: 1 });
    toast(`Added ${placed.filter(x => x.type !== 'connector').length} shapes. Double-click any text to change it.`);
  }
  importPanel.addEventListener('click', async e => {
    if (e.target.closest('[data-close]')) { importPanel.hidden = true; return; }
    if (readOnly) return;
    if (e.target.closest('[data-mermaid]')) {
      const r = fromMermaid(importPanel.querySelector('textarea').value);
      if (r.error) { toast(r.error); return; }
      importPanel.hidden = true; placeImported(r.elements); return;
    }
    if (e.target.closest('[data-drawio]')) {
      const input = document.createElement('input'); input.type = 'file'; input.accept = '.drawio,.xml,.dio,application/xml,text/xml';
      input.onchange = async () => {
        const f = input.files?.[0]; if (!f) return;
        if (f.size > 5 * 1024 * 1024) { toast('That file is larger than 5 MB'); return; }
        try {
          const inflate = async bytes => { const ds = new DecompressionStream('deflate-raw'); const s = new Blob([bytes]).stream().pipeThrough(ds); return await new Response(s).text(); };
          const model = await drawioModel(await f.text(), inflate);
          const r = fromDrawioModel(model);
          if (r.error) { toast(r.error); return; }
          importPanel.hidden = true; placeImported(r.elements);
        } catch (err) { toast(err?.message || 'Could not read that file'); }
      };
      input.click();
    }
  });

  function positionProps() {
    if (props.hidden) return;
    const b = unionBounds([...sel].map(get).filter(Boolean), get);
    if (!b) { props.hidden = true; return; }
    const a = toScreen(b.x, b.y), z = toScreen(b.x + b.w, b.y + b.h), r = rect();
    const w = props.offsetWidth, h = props.offsetHeight;
    let x = (a.x + z.x) / 2 - w / 2, y = a.y - h - 14;
    if (y < 8) y = z.y + 14;
    if (y + h > r.height - 64) y = Math.max(8, r.height - 64 - h);
    props.style.left = `${Math.max(8, Math.min(x, r.width - w - 8))}px`;
    props.style.top = `${y}px`;
  }
  props.addEventListener('pointerdown', e => e.stopPropagation());
  const onPropsAct = b => {
    if (b.dataset.act === 'propsmore') {
      if (!propsMore.hidden) { propsMore.hidden = true; return; }
      propsMore.hidden = false;
      const r = b.getBoundingClientRect(), rr = rect();
      propsMore.style.left = `${Math.max(8, Math.min(r.left - rr.left - propsMore.offsetWidth + r.width, rr.width - propsMore.offsetWidth - 8))}px`;
      const top = r.bottom - rr.top + 6;
      propsMore.style.top = `${top + propsMore.offsetHeight > rr.height - 8 ? Math.max(8, r.top - rr.top - propsMore.offsetHeight - 6) : top}px`;
      return;
    }
    props.dispatchEvent(new CustomEvent('wb-act', { detail: b }));
  };
  props.addEventListener('click', e => { const b = e.target.closest('button'); if (b) onPropsAct(b); });
  props.addEventListener('wb-act', e => {
    const b = e.detail; if (!b) return;
    const els = [...sel].map(get).filter(Boolean).filter(x => !x.locked || b.dataset.act === 'lock');
    const upd = fn => write(els.map(x => { const c = fn(x); return c ? bump(x, c) : null; }).filter(Boolean), { now: true });
    if (b.dataset.sticky) upd(x => x.type === 'sticky' ? { color: b.dataset.sticky } : null);
    else if (b.dataset.colour) { colourTab = b.dataset.colour; if (!colourPop.hidden && colourFor === 'sel') { colourPop.hidden = true; return; } openColours('sel', b); return; }
    else if (b.dataset.route) upd(x => x.type === 'connector' ? { route: b.dataset.route } : null);
    else if ('heads' in b.dataset) { toggleHeads(b); return; }
    else if ('dash' in b.dataset) upd(x => x.type === 'connector' ? { dashed: !x.dashed } : null);
    else if (b.dataset.style === 'dash') { const on = !els.find(x => isShape(x))?.dashed; upd(x => isShape(x) ? { dashed: on || undefined } : null); }
    else if (b.dataset.style === 'shadow') { const on = !els.find(x => isShape(x))?.shadow; upd(x => isShape(x) ? { shadow: on || undefined } : null); }
    else if (b.dataset.style === 'round') { const sharp = els.find(x => isShape(x))?.rounded !== false; upd(x => isShape(x) ? { rounded: sharp ? false : undefined } : null); }
    else if (b.dataset.act === 'data') { openDataEditor(els[0], b); return; }
    else if (b.dataset.act === 'laneadd') { const s = els[0], lanes = [...(s.lanes || [{ id: 'l1', name: 'Lane 1', size: 1 }])]; const n = lanes.length + 1; lanes.push({ id: `l${Date.now().toString(36)}`, name: `Lane ${n}`, size: 1 }); const grow = s.orient === 'vertical' ? { w: s.w + s.w / (n - 1) } : { h: s.h + s.h / (n - 1) }; upd(x => x.id === s.id ? { lanes, ...grow } : null); }
    else if (b.dataset.act === 'laneflip') upd(x => x.type === 'swimlane' ? { orient: x.orient === 'vertical' ? undefined : 'vertical', w: x.h * 2, h: x.w / 2 } : null);
    else if ('width' in b.dataset) { toggleWidthMenu(b, els); return; }
    else if (b.dataset.font) upd(x => (x.type === 'text' || x.type === 'sticky' || isShape(x)) ? { font: b.dataset.font } : null);
    else if (b.dataset.size) upd(x => {
      if (x.type === 'text') { const s = +b.dataset.size, lines = String(x.text).split('\n'); return { size: s, w: Math.max(...lines.map(l => measure(l, s, x.font || 'ipop'))) + 4, h: lines.length * s * 1.3 }; }
      return (x.type === 'sticky' || isShape(x)) ? { size: +b.dataset.size } : null;
    });
    else if (b.dataset.act === 'front') reorder(els, 'front');
    else if (b.dataset.act === 'back') reorder(els, 'back');
    else if (b.dataset.act === 'dup') duplicate(els);
    else if (b.dataset.act === 'lock') { const lock = !els.every(x => x.locked); upd(() => ({ locked: lock })); toast(lock ? 'Locked: it can no longer be moved by accident' : 'Unlocked'); }
    else if (b.dataset.act === 'del') remove(els);
    else if (b.dataset.act === 'lib') { saveToLibrary(els); return; }
    else if (b.dataset.act === 'task') { makeTasks(els); return; }
    else if (b.dataset.act === 'branch') { mindAdd(els[0], 'child'); return; }
    else if (b.dataset.act === 'group') { groupSel(true); return; }
    else if (b.dataset.act === 'copystyle') { copyStyle(els[0]); return; }
    else if (b.dataset.act === 'theme') { toggleThemes(b); return; }
    else if (b.dataset.act === 'pastestyle') { pasteStyle(); return; }
    else if (b.dataset.act === 'ungroup') { groupSel(false); return; }
    else if (b.dataset.act === 'arrange') { toggleArrange(b); return; }
    else if (b.dataset.act === 'link') { openLinkEditor(els[0], b); return; }
    else if (b.dataset.act === 'restack') { restack(els[0]); }
    else if (b.dataset.act === 'callout') { const f = els[0]; const c0 = makeShape('callout', { x: f.x + f.w + 30, y: f.y - 70 }, ''); const [placed] = add([{ ...c0, attach: f.id }]) || []; if (placed) editSoon2(placed); return; }
    commitStop();
    paintProps();
  });

  // ══ Round 4: Visio, network and ClickUp extras ════════════════════════════
  const CARRIES = new Set(['listBox', 'rack', 'fpRoom', 'umlPackage']);
  const insideOf = (x, f) => { const b = bounds(x, get); const cx = b.x + b.w / 2, cy = b.y + b.h / 2; return cx >= f.x && cx <= f.x + f.w && cy >= f.y && cy <= f.y + f.h; };
  const onMeta = () => { if (!destroyed) { cache.clear(); render(); paintPanelR4(); } };
  meta?.observe(onMeta);

  /// After a move: devices dropped in a rack snap to its slots; shapes dropped in a list are stacked.
  function afterMove(movedEls) {
    if (readOnly) return;
    const all2 = live(), out = [];
    for (const m of movedEls) {
      if (isNetDevice(m)) {
        const d = rackDrop(m, all2);
        if (d) out.push(bump(m, { x: Math.round(d.box.x), y: d.box.y, w: Math.round(d.box.w), h: d.box.h, rackU: d.u }));
        else if (m.rackU) { const s0 = STENCIL[m.type]; out.push(bump(m, { rackU: undefined, w: s0?.w || m.w, h: s0?.h || m.h })); }
      }
    }
    if (out.length) write(out, { now: true });
    const lists = new Set();
    for (const m of movedEls) for (const l of live().filter(x => x.type === 'listBox')) if (m.id === l.id || insideOf(m, l)) lists.add(l.id);
    for (const id of lists) restack(get(id));
  }
  function restack(list) {
    if (!list || list.locked) return;
    const { pos, height } = stackList(list, live());
    const out = [...pos].map(([id, p]) => { const x = get(id); return x && (x.x !== p.x || x.y !== p.y || x.w !== p.w) ? bump(x, p) : null; }).filter(Boolean);
    if (height !== list.h) out.push(bump(list, { h: height }));
    if (out.length) write(out, { now: true });
  }
  function addToRack(rack, t) {
    const c = rackContents(rack, live());
    const taken = new Set(); for (const r of c.rows) for (let k = 0; k < r.size; k++) taken.add(r.u + k);
    const proto = makeShape(t, { x: 0, y: 0 });
    const size = Math.max(1, Math.min(c.units, rackUnits(proto)));
    let u = 0; for (let k = 1; k + size - 1 <= c.units; k++) { let ok = true; for (let j = 0; j < size; j++) if (taken.has(k + j)) ok = false; if (ok) { u = k; break; } }
    if (!u) { toast(`This rack is full (${c.units} U)`); return; }
    const s = rackSlots(rack).slot(u, size);
    const [placed] = add([{ ...proto, x: Math.round(s.x), y: s.y, w: Math.round(s.w), h: s.h, rackU: u }]) || [];
    if (placed) { toast(`Added at U${u}${size > 1 ? `–${u + size - 1}` : ''}. ${c.free - size} U free.`); editSoon2(placed); }
  }

  // ── Round 4 panels (one at a time, on the right) ─────────────────────────
  const r4 = el('div', 'wb-panel wb-r4'); r4.hidden = true; r4.setAttribute('role', 'dialog');
  root.append(r4);
  r4.addEventListener('pointerdown', e => e.stopPropagation());
  r4.addEventListener('keydown', e => { e.stopPropagation(); if (e.key === 'Escape') { r4.hidden = true; root.focus({ preventScroll: true }); } });

  function togglePanelR4(kind) {
    if (!r4.hidden && r4Kind === kind) { r4.hidden = true; return; }
    for (const p of [framesPanel, timerPanel, libPanel, gallery, votePanel, exportPanel, cardPicker, guidePanel, checkPanel, importPanel, shapePanel]) p.hidden = true;
    r4Kind = kind; r4State = {}; r4.hidden = false; r4.setAttribute('aria-label', kind);
    if (kind === 'history') loadHistory();
    if (kind === 'org') r4State = { q: '', res: [] };
    paintPanelR4();
    r4.querySelector('input[type="search"], input:not([type]), select')?.focus({ preventScroll: true });
  }
  const head4 = t => `<div class="wb-ph"><b>${t}</b><button type="button" data-close aria-label="Close">✕</button></div>`;
  function paintPanelR4() {
    if (r4.hidden) return;
    const k = r4Kind;
    if (k === 'relayout') {
      r4.innerHTML = head4('Re-layout') + `<p class="wb-note">Moves the shapes${sel.size > 1 ? ' you selected' : ' on the board'} into a new arrangement. Ctrl Z undoes it.</p>
        <div class="wb-lay">${[['tree', 'Hierarchy', 'Parents above their children, like an org chart.'], ['radial', 'Radial', 'The main idea in the middle, the rest around it.'], ['organic', 'Organic', 'Related shapes pulled together, the rest spread out.'], ['flowTB', 'Flow top to bottom', 'Steps in order, top to bottom.'], ['flowLR', 'Flow left to right', 'Steps in order, left to right.'], ['network', 'Network', 'Internet → firewall → core → access → devices.']].map(([v, n, d]) => `<button type="button" data-lay="${v}"><b>${n}</b><span>${d}</span></button>`).join('')}</div>`;
    } else if (k === 'graphics') {
      const r = rulesNow();
      r4.innerHTML = head4('Data graphics') + `<p class="wb-note">Shows the shape data as a picture on every shape that has it: Status, Progress (%) and Duration. The rule is the same for everyone on this board.</p>
        <label class="wb-row">Show <select data-gshow ${readOnly ? 'disabled' : ''}>${[['off', 'Nothing (plain shapes)'], ['bar', 'Progress bar'], ['icon', 'Status icon'], ['color', 'Fill colour']].map(([v, n]) => `<option value="${v}" ${r.show === v ? 'selected' : ''}>${n}</option>`).join('')}</select></label>
        <label class="wb-row">Late after <select data-glate ${readOnly ? 'disabled' : ''}>${[1, 2, 3, 5, 7, 10, 14].map(n => `<option ${+r.lateDays === n ? 'selected' : ''}>${n}</option>`).join('')}</select> days</label>
        <div class="wb-legend"><span><i style="background:#34C759"></i>Done</span><span><i style="background:#FF9500"></i>In progress</span><span><i style="background:#FF3B30"></i>Late</span><span><i style="background:#8E8E93"></i>Not started</span></div>`;
    } else if (k === 'ipfind') {
      const q = r4State.q || '', hits = findDevice(live(), q);
      r4.innerHTML = head4('Find a device') + `<input class="wb-search" data-ipq value="${esc(q)}" placeholder="IP, hostname or name (10.10.6.16)" aria-label="IP, hostname or name">
        <div class="wb-cardlist">${q ? (hits.length ? hits.slice(0, 30).map(h => `<button type="button" data-goto="${h.id}"><b>${esc(String(h.text || SHAPE_LABEL[h.type] || '').split('\n')[0] || SHAPE_LABEL[h.type])}</b><span class="wb-mono">${esc((h.data || []).find(d => /^ip$/i.test(d.name))?.value || '')}</span></button>`).join('') : '<p class="wb-note">No device has that IP or name.</p>') : `<p class="wb-note">${live().filter(isNetDevice).length} network devices on this board. Fill in IP and hostname with ⓘ Shape data.</p>`}</div>`;
      const inp = r4.querySelector('[data-ipq]');
      inp.addEventListener('input', () => { r4State.q = inp.value; paintPanelR4(); const i2 = r4.querySelector('[data-ipq]'); i2.focus(); i2.setSelectionRange(i2.value.length, i2.value.length); });
      inp.addEventListener('keydown', ev => { if (ev.key === 'Enter') r4.querySelector('[data-goto]')?.click(); });
    } else if (k === 'report') {
      const types = r4State.types || 'all';
      const rep = reportRows(live(), { types: types === 'all' ? undefined : new Set(STENCILS.filter(s => s[2] === types).map(s => s[0])) });
      const cats = [...new Set(live().map(e => STENCIL[e.type]?.cat).filter(Boolean))];
      r4.innerHTML = head4('Report') + `<label class="wb-row">Shapes <select data-rtype><option value="all">All</option>${cats.map(c => `<option ${types === c ? 'selected' : ''}>${esc(c)}</option>`).join('')}</select></label>
        <div class="wb-rtable"><table><thead><tr>${rep.head.map(h => `<th>${esc(h)}</th>`).join('')}</tr></thead><tbody>${rep.rows.slice(0, 60).map(r => `<tr>${r.map(v => `<td>${esc(v)}</td>`).join('')}</tr>`).join('') || `<tr><td colspan="${rep.head.length}">No shapes yet.</td></tr>`}</tbody></table></div>
        <p class="wb-note">${rep.rows.length} shapes${rep.rows.length > 60 ? ' (first 60 shown; the file has all)' : ''}.</p>
        <div class="wb-row2"><button type="button" class="wb-wide" data-rcsv>CSV</button><button type="button" class="wb-wide wb-pri" data-rxlsx>Download Excel</button></div>`;
      r4State.rep = rep;
    } else if (k === 'comments') {
      const list = threads.filter(t => !t.resolvedAt).map(t => ({ t, a: anchorOf(t) })).filter(x => x.a);
      r4.innerHTML = head4(`Comments on this board · ${list.length}`) + (list.length ? `<div class="wb-cardlist">${list.map(({ t, a }) => { const on = a.el ? get(a.el) : null; return `<button type="button" data-thread2="${t.id}"><b>${esc(on ? String(on.text || on.title || on.label || (on.type === 'connector' ? 'Line' : SHAPE_LABEL[on.type]) || 'Shape').split('\n')[0].slice(0, 40) : 'On the board')}</b><span>${esc(t.text || '…')}</span><small>${esc(t.author || '')}${t.count > 1 ? ` · ${t.count - 1} ${t.count === 2 ? 'reply' : 'replies'}` : ''}</small></button>`; }).join('')}</div>` : '<p class="wb-note">No open comments. Press C (Comment) and click a shape to ask something about it.</p>');
    } else if (k === 'org') {
      r4.innerHTML = head4('Org chart from Directory') + `<p class="wb-note">Pick a person; their manager line and their team are added as cards with photos. Positions and NIK are not shown.</p>
        <input class="wb-search" data-orgq value="${esc(r4State.q || '')}" placeholder="Search a name" aria-label="Search a name">
        <div class="wb-cardlist">${(r4State.res || []).map((p, i) => `<button type="button" data-orgpick="${i}"><b>${esc(p.name)}</b><span>${esc(p.dept || '')}</span></button>`).join('') || (r4State.q?.length > 1 ? '<p class="wb-note">No one found.</p>' : '')}</div>
        ${r4State.busy ? '<p class="wb-note">Adding the team…</p>' : ''}`;
      const inp = r4.querySelector('[data-orgq]');
      inp.addEventListener('input', () => {
        r4State.q = inp.value; clearTimeout(r4State.t);
        r4State.t = setTimeout(async () => { if ((r4State.q || '').trim().length < 2) { r4State.res = []; paintPanelR4(); return; } try { r4State.res = await opts.people.search(r4State.q.trim()); } catch { r4State.res = []; } paintPanelR4(); const i2 = r4.querySelector('[data-orgq]'); i2?.focus(); i2?.setSelectionRange(i2.value.length, i2.value.length); }, 220);
      });
    } else if (k === 'tasks') {
      // the selected steps, or every flowchart step on the board when nothing is selected
      const picked = [...sel].map(get).filter(x => x && isShape(x) && !isContainer(x));
      const chosen = picked.length ? picked : live().filter(x => FLOW_TYPES.has(x.type) && !/^(decision|bpmnGateway|bpmnParallel)$/.test(x.type));
      const list = tasksFromShapes(live(), chosen, { chain: r4State.chain !== false });
      r4.innerHTML = head4('Make Projects tasks') + (list.length ? `<p class="wb-note">One task per step, in flow order. PIC comes from the shape data and is matched with the project's members; durations make due dates one after another (working days).</p>
        <div class="wb-cardlist">${list.map((t, i) => `<label class="wb-chk"><input type="checkbox" data-tk="${i}" checked><b>${i + 1}. ${esc(t.title)}</b><span>${esc([t.pic && `PIC ${t.pic}`, t.due].filter(Boolean).join(' · '))}</span></label>`).join('')}</div>
        <label class="wb-row"><input type="checkbox" data-chain ${r4State.chain !== false ? 'checked' : ''}> Chain due dates</label>
        <div class="wb-row2"><button type="button" class="wb-wide wb-pri" data-mktasks>Create ${list.length} task${list.length > 1 ? 's' : ''}…</button></div>` : '<p class="wb-note">There are no steps on this board yet. Add Process shapes (or select the ones you want), then press this again.</p>');
      r4State.list = list;
    } else if (k === 'history') {
      const v = r4State.versions;
      // the slider is built once and kept: rebuilding it while it is being dragged stopped the drag after one step
      if (!v || !v.length || !r4.querySelector('.wb-vslide')) {
        r4.innerHTML = head4('Version history') + (!v ? '<p class="wb-note">Loading…</p>' : !v.length ? '<p class="wb-note">No saved versions yet. A version is kept every few minutes while people edit.</p>'
          : `<input type="range" class="wb-vslide" min="0" max="${v.length}" value="${r4State.at ?? v.length}" aria-label="Version"><div class="wb-vinfo"></div>`);
        const sl = r4.querySelector('.wb-vslide');
        sl?.addEventListener('input', () => { r4State.at = +sl.value; paintVersionInfo(); showVersion(); });
      }
      if (v?.length) paintVersionInfo();
    } else if (k === 'changes') {
      const c = r4State.changes || sinceLast;
      r4.innerHTML = head4('Since you last opened') + (c ? `<p>${esc(c.who ? `${c.who} ` : '')}${esc(c.text)}</p><div class="wb-row2"><button type="button" class="wb-wide wb-pri" data-showchg>Show me on the board</button><button type="button" class="wb-wide" data-close>Dismiss</button></div>` : '<p class="wb-note">Nothing changed since you last opened this board.</p>');
    }
  }
  function paintVersionInfo() {
    const v = r4State.versions, box = r4.querySelector('.wb-vinfo'); if (!v || !box) return;
    const at = r4State.at ?? v.length, ver = at < v.length ? v[v.length - 1 - at] : null;
    box.innerHTML = `<p class="wb-vlabel"><b>${esc(ver ? fmtWhen(ver) : 'Now')}</b>${ver?.who ? ` · ${esc(ver.who)}` : ''}</p>`
      + (ver && r4State.diff ? `<div class="wb-legend"><span><i style="background:#34C759"></i>Added since (${r4State.diff.added.length})</span><span><i style="background:#FF9500"></i>Changed (${r4State.diff.changed.length})</span><span><i style="background:#FF3B30"></i>Removed (${r4State.diff.removed.length})</span></div>` : `<p class="wb-note">${ver ? 'Working out what changed…' : 'Slide back to see what the board looked like and what changed since.'}</p>`)
      + (ver && opts.history?.restore && !readOnly ? '<div class="wb-row2"><button type="button" class="wb-wide" data-vrestore>Restore this version…</button></div>' : '');
  }
  const fmtWhen = v => { try { return new Date(v.at).toLocaleString(undefined, { weekday: 'short', day: 'numeric', month: 'short', hour: '2-digit', minute: '2-digit' }); } catch { return String(v.at); } };
  r4.addEventListener('change', e => {
    const t = e.target;
    if (t.matches('[data-gshow],[data-glate]') && meta && !readOnly) {
      const r = { ...rulesNow() };
      if (t.matches('[data-gshow]')) r.show = t.value; else r.lateDays = +t.value;
      doc.transact(() => meta.set('graphics', r), ORIGIN);
      if (r.show !== 'off' && !live().some(x => x.data?.some(d => /status|progress|duration/i.test(d.name || '')))) toast('Add Status, Progress or Duration with ⓘ Shape data to see it on the shapes.');
    }
    if (t.matches('[data-rtype]')) { r4State.types = t.value; paintPanelR4(); }
    if (t.matches('[data-chain]')) { r4State.chain = t.checked; paintPanelR4(); }
  });
  r4.addEventListener('click', async e => {
    if (e.target.closest('[data-close]')) { if (r4Kind === 'changes') { sinceLast = null; paintCorners(); } r4.hidden = true; clearVersionView(); return; }
    const lay = e.target.closest('[data-lay]'); if (lay) { r4.hidden = true; relayout(lay.dataset.lay); return; }
    const go = e.target.closest('[data-goto]'); if (go) { focusOn(go.dataset.goto); return; }
    const th = e.target.closest('[data-thread2]'); if (th) { const t = threads.find(x => x.id === th.dataset.thread2), a = t && anchorOf(t); if (a) { const p = pinPos(a), r = rect(); view.x = r.width / 2 - p.x * view.z; view.y = r.height / 2 - p.y * view.z; if (a.el) { sel = new Set([a.el]); paintProps(); } render(); } opts.comments?.open?.(th.dataset.thread2); return; }
    if (e.target.closest('[data-rcsv]')) { download(new Blob([toCsv(r4State.rep)], { type: 'text/csv;charset=utf-8' }), 'csv'); return; }
    if (e.target.closest('[data-rxlsx]')) { download(new Blob([toXlsx(r4State.rep, 'Report')], { type: 'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet' }), 'xlsx'); return; }
    const op = e.target.closest('[data-orgpick]'); if (op) { addOrgChart(r4State.res[+op.dataset.orgpick]); return; }
    if (e.target.closest('[data-mktasks]')) {
      const keep = [...r4.querySelectorAll('[data-tk]')].filter(x => x.checked).map(x => r4State.list[+x.dataset.tk]);
      if (!keep.length) return;
      const res = await opts.tasks.createMany(keep).catch(() => null);
      if (!res?.length) return;
      // each step gets a task card beside it, joined by a dashed line
      const out = [];
      for (const { stepId, task } of res) {
        const s = get(stepId); if (!s) continue;
        taskCache.set(task.id, task);
        // beside the step, moved down until it overlaps nothing (other cards, callouts, shapes)
        const others = [...live(), ...out];
        const free = placeNext(s, 'r', { w: 240, h: 104 }, others.map(o => (o.type === 'callout' || o.type === 'taskCard' ? { ...o, type: 'process' } : o)), 60);
        const card = create('taskCard', { x: free.x, y: free.y, w: 240, h: 104, taskId: task.id, title: task.title });
        out.push(card, create('connector', { from: { id: s.id }, to: { id: card.id }, dashed: true, endHead: 'none' }));
      }
      add(out, { select: false });
      r4.hidden = true;
      toast(res.length === 1 ? 'Task created in Projects' : `${res.length} tasks created in Projects`);
      return;
    }
    if (e.target.closest('[data-vrestore]')) { const v = r4State.versions[r4State.versions.length - 1 - r4State.at]; if (v) { r4.hidden = true; clearVersionView(); opts.history.restore(v.id); } return; }
    if (e.target.closest('[data-showchg]')) { const c = r4State.changes || sinceLast; r4.hidden = true; sinceLast = null; paintCorners(); if (c) { const ids = [...c.added, ...c.changed].map(x => x.id).filter(id => get(id)); sel = new Set(ids); if (ids.length) fit(ids.map(get), { max: 1 }); paintProps(); render(); } return; }
  });
  function download(blob, ext) {
    const name = `${(opts.title?.() || 'whiteboard').replace(/[\\/:*?"<>|]+/g, ' ').trim() || 'whiteboard'}${ext === 'csv' || ext === 'xlsx' ? ' report' : ''}.${ext}`;
    const a = document.createElement('a'); a.href = URL.createObjectURL(blob); a.download = name; a.click();
    setTimeout(() => URL.revokeObjectURL(a.href), 4000);
  }
  function focusOn(id) {
    const t = get(id); if (!t) return;
    sel = new Set([id]);
    const b = bounds(t, get), r = rect();
    view.z = Math.max(view.z, 0.8);
    view.x = r.width / 2 - (b.x + b.w / 2) * view.z; view.y = r.height / 2 - (b.y + b.h / 2) * view.z;
    paintProps(); render();
  }
  function relayout(kind) {
    if (readOnly) return;
    if (kind === 'flowTB' || kind === 'flowLR') { tidyFlow(kind === 'flowLR' ? 'LR' : 'TB'); return; }
    const chosen = [...sel].map(get).filter(Boolean);
    // the network layout only moves network devices unless shapes are selected
    const netIds = kind === 'network' && chosen.length < 2 ? new Set(live().filter(x => isNetDevice(x) || x.type === 'netCloud').map(x => x.id)) : null;
    const scope = chosen.length > 1 ? live().filter(x => sel.has(x.id) || (x.type === 'connector' && sel.has(x.from?.id) && sel.has(x.to?.id)))
      : netIds ? live().filter(x => netIds.has(x.id) || (x.type === 'connector' && netIds.has(x.from?.id) && netIds.has(x.to?.id))) : live();
    const shapes = scope.filter(x => (isShape(x) || x.type === 'sticky' || x.type === 'taskCard' || x.type === 'noteCard') && !isContainer(x) && !x.locked);
    if (shapes.length < 2) { toast('Join shapes with lines first; the lines tell the layout who belongs to whom.'); return; }
    if (!scope.some(x => x.type === 'connector' && x.from?.id && x.to?.id)) { toast('Join the shapes with lines first; the layout follows the lines.'); return; }
    const b = unionBounds(shapes, get), ids = new Set(shapes.map(s => s.id));
    const pos = kind === 'tree' ? layoutTree(scope, { ids, origin: { x: b.x, y: b.y } }) : kind === 'radial' ? layoutRadial(scope, { ids }) : kind === 'network' ? layoutNetwork(scope, { ids, origin: { x: b.x, y: b.y } }) : layoutOrganic(scope, { ids });
    if (!pos.size) { toast('Already in that layout'); return; }
    write([...pos].map(([id, p]) => { const x = get(id); return x && !x.locked ? bump(x, { x: p.x, y: p.y }) : null; }).filter(Boolean), { now: true });
    const strip = end => (end?.id ? { id: end.id } : end);
    write(scope.filter(x => x.type === 'connector' && (x.waypoints?.length || x.from?.side || x.to?.side)).map(x => bump(x, { waypoints: undefined, from: strip(x.from), to: strip(x.to), route: kind === 'tree' || kind === 'network' ? 'elbow' : x.route })), { now: true });
    commitStop();
    fit(shapes.map(s => get(s.id)), { max: 1 });
    toast('Re-arranged. Ctrl Z undoes it.');
  }
  async function addOrgChart(person) {
    if (!person || readOnly || !opts.people?.team) return;
    r4State.busy = true; paintPanelR4();
    let people = [];
    try { people = await opts.people.team(person); } catch { /* none */ }
    r4State.busy = false;
    if (!people.length) { toast('Could not read this team from Directory'); paintPanelR4(); return; }
    const ub = unionBounds(live(), get);
    const at = ub ? { x: Math.round(ub.x + ub.w + 160), y: Math.round(ub.y) } : toWorld(rect().width / 2, 120);
    const els = orgChart(people, create, at);
    for (const p of people) if (p.photo) ensurePhoto(p.photo);
    const placed = add(els, { select: false }) || [];
    r4.hidden = true;
    fit(placed.filter(x => x.type === 'orgCard'), { max: 1 });
    toast(`Added ${people.length} people. Double-click a card to change it.`);
  }
  // person photos for org cards (loaded once, as data URLs so exports include them)
  function ensurePhoto(key) {
    if (!key || photoSeen.has(key) || !opts.people?.photo) return;
    photoSeen.add(key);
    Promise.resolve(opts.people.photo(key)).then(async url => { if (!url) return; const b = await (await fetch(url)).blob(); const d = await new Promise(ok => { const fr = new FileReader(); fr.onload = () => ok(fr.result); fr.readAsDataURL(b); }); files.set(`ph:${key}`, d); cache.clear(); render(); }).catch(() => {});
  }

  // ── PowerPoint export: each frame is a slide (in Present order); no frames = one slide of the whole board ──
  async function exportPptx() {
    const frames = framesInOrder();
    const all2 = live();
    const groups = frames.length ? frames.map(f => ({ title: f.name || 'Frame', box: { x: f.x, y: f.y, w: f.w, h: f.h }, els: all2.filter(x => x.id !== f.id && x.type !== 'frame' && frameOf(x, all2, get)?.id === f.id) }))
      : [{ title: opts.title?.() || 'Whiteboard', box: unionBounds(all2, get), els: all2.filter(x => x.type !== 'frame') }];
    if (!groups[0]?.box) { toast('Nothing to export'); return; }
    toast('Making the PowerPoint…');
    const files = await dataUrls();
    const slides = [];
    for (const g of groups) {
      const ids = new Set(g.els.map(x => x.id));
      const native = g.els.filter(x => pptxNative(x) && (x.type !== 'connector' || (ids.has(x.from?.id) && ids.has(x.to?.id))));
      const nset = new Set(native.map(x => x.id));
      const rest = g.els.filter(x => !nset.has(x.id) && x.type !== 'connector');
      let picture = null;
      if (rest.length) {
        const png = await toPng(rest, { scale: 2, theme: 'light', files, background: false, cardInfo: opts.cardInfo }).catch(() => null);
        const rb = unionBounds(rest, get);
        if (png && rb) picture = { bytes: new Uint8Array(await png.arrayBuffer()), box: { x: rb.x - 32, y: rb.y - 32, w: rb.w + 64, h: rb.h + 64 } };
      }
      slides.push({ title: g.title, box: g.box, els: native, picture });
    }
    download(new Blob([toPptx(slides, { title: opts.title?.() || 'Whiteboard' })], { type: 'application/vnd.openxmlformats-officedocument.presentationml.presentation' }), 'pptx');
  }

  // ── Version history with a visual diff ─────────────────────────────────────
  // opts.history: { list() -> [{ id, at, who }], scene(id) -> elements of this board in that version, restore(id) }
  const diffLayer = svgEl('g', { class: 'wb-difflayer' }, svg);

  async function loadHistory() {
    try { r4State.versions = (await opts.history.list()) || []; } catch { r4State.versions = []; }
    r4State.at = r4State.versions.length; paintPanelR4();
  }
  async function showVersion() {
    const v = r4State.versions; const i = r4State.at;
    if (i == null || i >= v.length) { clearVersionView(); r4State.diff = null; paintVersionInfo(); return; }
    const ver = v[v.length - 1 - i];
    let old = ver._scene;
    if (!old) { try { old = ver._scene = await opts.history.scene(ver.id); } catch { old = []; } }
    if (r4State.at !== i) return;
    const d = diffScenes(old || [], live());
    r4State.diff = d;
    versionView = { old: old || [], diff: d };
    paintVersionInfo(); paintDiff();
  }
  function clearVersionView() { versionView = null; diffLayer.innerHTML = ''; root.classList.remove('wb-vview'); }
  function paintDiff() {
    diffLayer.innerHTML = '';
    if (!versionView) { root.classList.remove('wb-vview'); return; }
    root.classList.add('wb-vview');
    diffLayer.setAttribute('transform', `translate(${view.x} ${view.y}) scale(${view.z})`);
    // the old picture faint underneath; removed things drawn red, added green, changed orange
    const oldMap = new Map(versionView.old.map(e => [e.id, e]));
    const ghost = svgEl('g', { opacity: 0.35 }, diffLayer);
    for (const e of versionView.old) if (!e.isDeleted) drawElement(ghost, e, id => oldMap.get(id), { theme, files: opts.files, cardInfo: opts.cardInfo });
    // lines are not boxed (a box around a long line covers half the board); their count is in the panel
    const mark = (e, get2, cls) => { if (e.type === 'connector') return; const b = bounds(e, get2); svgEl('rect', { x: b.x - 6, y: b.y - 6, width: b.w + 12, height: b.h + 12, rx: 8, class: cls, 'vector-effect': 'non-scaling-stroke' }, diffLayer); };
    for (const e of versionView.diff.removed) mark(e, id => oldMap.get(id), 'wb-diffr');
    for (const e of versionView.diff.added) mark(e, get, 'wb-diffa');
    for (const e of versionView.diff.changed) mark(e, get, 'wb-diffm');
  }

  // ── "What changed since I last opened" ───────────────────────────────────────
  // The last scene I saw is kept per person in this browser (ids + a signature); on open the difference is shown once.
  const SEEN_KEY = `ipop.wb.seen:${opts.seenKey || boardKey}`;
  const sceneSig = () => live().map(e => ({ id: e.id, type: e.type, x: Math.round(e.x || 0), y: Math.round(e.y || 0), w: Math.round(e.w || 0), h: Math.round(e.h || 0), text: e.text, title: e.title, name: e.name, label: e.label, fill: e.fill, stroke: e.stroke, data: e.data, from: e.from, to: e.to }));
  function checkSinceLast() {
    if (mode !== 'board' || !opts.seenKey) return;
    let before = null;
    try { before = JSON.parse(localStorage.getItem(SEEN_KEY) || 'null'); } catch { /* none */ }
    const nowSig = sceneSig();
    if (before?.els) {
      const d = diffScenes(before.els, nowSig);
      const text = diffSummary(d);
      if (text) { sinceLast = { ...d, text: `${text[0].toUpperCase()}${text.slice(1)}.` }; paintCorners(); }
    }
    saveSeen();
  }
  function saveSeen() { try { localStorage.setItem(SEEN_KEY, JSON.stringify({ at: Date.now(), els: sceneSig() })); } catch { /* full or blocked */ } }
  const seenTimer = setInterval(() => { if (!destroyed && document.visibilityState === 'visible') saveSeen(); }, 30000);
  setTimeout(() => { if (!destroyed) checkSinceLast(); }, 1500);

  // ── Editing operations ───────────────────────────────────────────────────
  function add(els, { select = true } = {}) {
    if (readOnly) return;
    if (count() + els.length > MAX_ELEMENTS) { toast(`This board is full (${MAX_ELEMENTS.toLocaleString()} things). Split it into another board.`); return; }
    let z = topZ(all());
    const placed = els.map(e => { z = zBetween(z || null, null); return { ...e, z }; });
    write(placed, { now: true });
    commitStop();
    if (dockReady) { paintDock(); popIn(placed.map(x => x.id)); markSaving(); }
    if (select) { sel = new Set(placed.filter(e => e.type !== 'connector' || placed.length === 1).map(e => e.id)); paintProps(); }
    if (count() > MAX_ELEMENTS * 0.9) toast('This board is getting very large. Think about moving part of it to a new board.');
    return placed;
  }
  function remove(els) {
    if (!els.length) return;
    const ids = new Set(els.map(e => e.id));
    // connectors attached to something that goes keep a free end where it was
    const extra = live().filter(c => c.type === 'connector' && !ids.has(c.id) && (ids.has(c.from?.id) || ids.has(c.to?.id))).map(c => {
      const fix = end => { if (!ids.has(end?.id)) return end; const t = get(end.id), p = t ? centre(t, get) : end; return { x: p.x, y: p.y }; };
      return bump(c, { from: fix(c.from), to: fix(c.to) });
    });
    // a quick fade before they go
    if (!reduceMotion()) for (const e of els) cache.get(e.id)?.node.classList.add('wb-fadeout');
    const go = () => { write([...els.map(e => bump(e, { isDeleted: true })), ...extra], { now: true }); sel.clear(); commitStop(); };
    if (reduceMotion()) go(); else setTimeout(go, 120);
  }
  function duplicate(els) {
    const map = new Map();
    const gmap = new Map();
    const regroup = g => (g ? (gmap.get(g) || (gmap.set(g, create('group').id), gmap.get(g))) : undefined);
    const copies = els.filter(e => e.type !== 'connector').map(e => { const c = { ...structuredClone(e), id: create(e.type).id, version: 1, locked: false, groupId: regroup(e.groupId) }; map.set(e.id, c.id); return moved(c, 24, 24); });
    // connectors between duplicated shapes are duplicated too, attached to the copies
    for (const c of els.filter(e => e.type === 'connector')) {
      const end = x => (x?.id && map.has(x.id) ? { id: map.get(x.id) } : x?.id ? x : { x: x.x + 24, y: x.y + 24 });
      copies.push({ ...structuredClone(c), id: create('connector').id, version: 1, from: end(c.from), to: end(c.to) });
    }
    add(copies);
  }
  function reorder(els, where) {
    const others = live().filter(e => !sel.has(e.id));
    let z = where === 'front' ? topZ(others) : bottomZ(others);
    const sorted = [...els].sort((p, q) => (p.z < q.z ? -1 : 1));
    const out = [];
    if (where === 'front') for (const e of sorted) { z = zBetween(z || null, null); out.push(bump(e, { z })); }
    else for (const e of sorted.reverse()) { z = z ? zBetween(null, z) : 'a0'; out.unshift(bump(e, { z })); }
    write(out, { now: true });
  }
  function doUndo() { if (dockReady) { undoStep(false); return; } flush(); undo.undo(); sel.clear(); render(); paintZoom(); }
  function doRedo() { if (dockReady) { undoStep(true); return; } flush(); undo.redo(); sel.clear(); render(); paintZoom(); }
  undo.on('stack-item-added', () => paintZoom());
  undo.on('stack-item-popped', () => paintZoom());

  // ── Text editing ─────────────────────────────────────────────────────────
  function editText(target, { selectAll = false } = {}) {
    if (readOnly || target.locked) return;
    // one writer per shape: two people typing in the same shape used to overwrite each other without a word
    const busy = typingBy(target.id);
    if (busy) { toast(`${String(busy.user.name || 'Someone').split(' ')[0]} is typing here. You can edit it when they're done.`); return; }
    stopEdit();
    // a shape that is tiny on screen (zoomed far out) is brought up to a readable size while typing, then put back
    // (only when the board has a size: a hidden board measures 0 and must not zoom to nothing)
    const r0 = rect(), tb = bounds(target, get), small = r0.width > 100 && r0.height > 100 && Math.min(tb.w, tb.h) * view.z < 80;
    const zoomBack = small ? { ...view } : null;
    if (small) {
      const r = rect(), z = Math.min(1.2, (r.width * 0.5) / Math.max(tb.w, 1), (r.height * 0.45) / Math.max(tb.h, 1));
      view = { z, x: r.width / 2 - (tb.x + tb.w / 2) * z, y: r.height * 0.42 - (tb.y + tb.h / 2) * z };
      render();
    }
    const ta = el('textarea', 'wb-edit');
    ta.setAttribute('aria-label', 'Text');
    ta.spellcheck = true;
    ta.value = target.text || '';
    const place = () => {
      const t = get(target.id); if (!t) return;
      const z = view.z, isText = t.type === 'text';
      const box = t.type === 'sticky' ? { x: t.x + 14, y: t.y + 12, w: t.w - 28, h: t.h - 24 } : isText ? { x: t.x, y: t.y, w: Math.max(t.w, 120), h: Math.max(t.h, (t.size || 20) * 1.3) } : { x: t.x + 10, y: t.y + 6, w: t.w - 20, h: t.h - 12 };
      const a = toScreen(box.x, box.y);
      const size = (t.size || (isText ? 20 : 16)) * z;
      Object.assign(ta.style, {
        left: `${a.x}px`, top: `${a.y}px`, width: `${Math.max(box.w * z, 60)}px`, height: `${Math.max(box.h * z, size * 1.5)}px`,
        fontSize: `${size}px`, lineHeight: '1.3', fontFamily: FONT_STACK[t.font || 'ipop'], fontWeight: isText ? 400 : 500,
        textAlign: t.align ? (t.align === 'center' ? 'center' : t.align === 'right' ? 'right' : 'left') : (t.type === 'sticky' || isText ? 'left' : 'center'), color: t.type === 'sticky' ? pal().stickyInk : (t.stroke && isText ? t.stroke : pal().ink),
        paddingTop: t.type === 'sticky' || isText ? '0' : `${Math.max(0, (box.h * z - size * 1.3) / 2)}px`,
      });
    };
    root.append(ta);
    editing = { id: target.id, ta, place, zoomBack };
    setMine({ typing: { id: target.id, at: now() } });
    place(); render();
    showFormatBar(target);
    ta.focus();
    if (selectAll) ta.select(); else ta.setSelectionRange(ta.value.length, ta.value.length);
    const grow = () => {
      const t = get(target.id); if (!t) return;
      placeFmtBar();
      if (t.type === 'text') {
        const s = t.size || 20, lines = ta.value.split('\n');
        const w = Math.max(40, ...lines.map(l => measure(l, s, t.font || 'ipop') + 6)), h = lines.length * s * 1.3;
        draft.set(t.id, bump(t, { text: ta.value, w, h }));
        place();
      }
      if (t.type === 'sticky') {
        // stickies grow taller when the text needs it (ClickUp behaviour)
        const lt = layoutText(ta.value, { w: t.w - 28, h: 1e9 }, { size: t.size || 16, font: t.font || 'ipop', weight: 500, fit: false });
        const need = lt.lines.length * lt.lh + 24;
        if (need > t.h) { draft.set(t.id, bump(t, { h: Math.ceil(need) })); place(); render(); }
      }
    };
    ta.addEventListener('input', grow);
    // keep the lock fresh while typing (others let it go after 30 s without a keystroke)
    ta.addEventListener('input', () => { if (now() - (editing?.typedAt || 0) > 4000 && editing) { editing.typedAt = now(); setMine({ typing: { id: target.id, at: now() } }); } });
    ta.addEventListener('keydown', e => {
      e.stopPropagation();
      // Ctrl Enter in a sticky: finish it and start the next one right beside it (brainstorming at speed)
      if (e.key === 'Enter' && (e.ctrlKey || e.metaKey) && get(target.id)?.type === 'sticky' && !readOnly) {
        e.preventDefault(); const cur = get(target.id); stopEdit();
        const n = create('sticky', { x: cur.x + cur.w + 24, y: cur.y, w: cur.w, h: cur.h, color: cur.color, text: '' });
        const [p] = add([n]) || []; if (p) editSoon2(p); return;
      }
      if (e.key === 'Escape' || (e.key === 'Enter' && (e.ctrlKey || e.metaKey))) { e.preventDefault(); stopEdit(); root.focus({ preventScroll: true }); return; }
      const mod = e.ctrlKey || e.metaKey, k = e.key.toLowerCase();
      if (mod && k === 'b') { e.preventDefault(); fmt('bold'); }
      else if (mod && k === 'i') { e.preventDefault(); fmt('italic'); }
      else if (mod && e.shiftKey && k === 'x') { e.preventDefault(); fmt('strike'); }
      else if (mod && k === 'k') { e.preventDefault(); fmt('link'); }
      // Enter on a bullet line starts the next bullet; on an empty bullet it ends the list
      else if (e.key === 'Enter' && !e.shiftKey && !mod) {
        const v = ta.value, s = ta.selectionStart, ls = v.lastIndexOf('\n', s - 1) + 1, line = v.slice(ls, s);
        if (/^\s*[-•]\s+\S/.test(line)) { e.preventDefault(); ta.setRangeText('\n- ', s, ta.selectionEnd, 'end'); ta.dispatchEvent(new Event('input')); }
        else if (/^\s*[-•]\s*$/.test(line)) { e.preventDefault(); ta.setRangeText('', ls, s, 'end'); ta.dispatchEvent(new Event('input')); }
      }
    });
    // focus moving to the format bar keeps the editor open
    ta.addEventListener('blur', () => setTimeout(() => { if (editing?.ta === ta && !fmtBar.contains(document.activeElement)) stopEdit(); }, 0));
  }
  // Formatting bar above the text being edited (bold, italic, strike, bullets, link, alignment). Text keeps light markers
  // (**bold**, *italic*, ~~strike~~, "- " bullets, [label](url)) so it stays one plain string per element.
  const fmtBar = el('div', 'wb-fmtbar'); fmtBar.hidden = true; fmtBar.setAttribute('role', 'toolbar'); fmtBar.setAttribute('aria-label', 'Text format');
  root.append(fmtBar);
  fmtBar.addEventListener('pointerdown', e => { e.stopPropagation(); if (!e.target.closest('.wb-fmtlink input')) e.preventDefault(); });   // keep the caret in the text
  function showFormatBar(target) {
    const t = get(target.id); if (!t) return;
    const a = t.align || (t.type === 'text' || t.type === 'sticky' ? 'left' : 'center');
    fmtBar.innerHTML = `<button type="button" data-f="bold" title="Bold (Ctrl B)" aria-label="Bold"><b>B</b></button><button type="button" data-f="italic" title="Italic (Ctrl I)" aria-label="Italic"><i>I</i></button><button type="button" data-f="strike" title="Strikethrough (Ctrl Shift X)" aria-label="Strikethrough"><s>S</s></button><span class="wb-sep"></span>`
      + `<button type="button" data-f="list" title="Bullet list" aria-label="Bullet list"><svg class="wb-i" viewBox="0 0 24 24"><path d="M9 6h11M9 12h11M9 18h11M4 6h.01M4 12h.01M4 18h.01"/></svg></button><button type="button" data-f="link" title="Link (Ctrl K)" aria-label="Link">${icon('link')}</button><span class="wb-sep"></span>`
      + ['left', 'center', 'right'].map(x => `<button type="button" data-al2="${x}" class="${a === x ? 'on' : ''}" title="Align ${x}" aria-label="Align ${x}" aria-pressed="${a === x}">${icon(x === 'left' ? 'alignleft' : x === 'center' ? 'alignhc' : 'alignright')}</button>`).join('');
    fmtBar.hidden = false;
    placeFmtBar();
  }
  function placeFmtBar() {
    if (fmtBar.hidden || !editing) return;
    // while typing, the selection bar is out of the way (the two used to sit on top of each other)
    props.hidden = true; propsMore.hidden = true;
    const r = editing.ta.getBoundingClientRect(), rr = rect();
    const w = fmtBar.offsetWidth, h = fmtBar.offsetHeight;
    let y = r.top - rr.top - h - 10; if (y < 8) y = r.bottom - rr.top + 10;
    fmtBar.style.left = `${Math.max(8, Math.min(r.left - rr.left + r.width / 2 - w / 2, rr.width - w - 8))}px`;
    fmtBar.style.top = `${y}px`;
  }
  function fmt(kind) {
    if (!editing) return;
    const ta = editing.ta, s = ta.selectionStart, en = ta.selectionEnd;
    let r;
    if (kind === 'bold') r = toggleMark(ta.value, s, en, '**');
    else if (kind === 'italic') r = toggleMark(ta.value, s, en, '*');
    else if (kind === 'strike') r = toggleMark(ta.value, s, en, '~~');
    else if (kind === 'list') r = toggleBullets(ta.value, s, en);
    else if (kind === 'link') {
      const sel2 = ta.value.slice(s, en).trim();
      if (/^(https?:\/\/|www\.)\S+$/i.test(sel2)) r = makeLink(ta.value, s, en, sel2.replace(/^www\./i, 'https://www.'));
      else { askTextLink(ta, s, en); return; }
    }
    if (!r) return;
    ta.value = r.value; ta.setSelectionRange(r.start, r.end); ta.focus();
    ta.dispatchEvent(new Event('input'));
  }
  // A small address box inside the format bar (no browser prompt): Enter links the selected words, Esc goes back to typing.
  function askTextLink(ta, s, en) {
    fmtBar.querySelector('.wb-fmtlink')?.remove();
    const box = el('div', 'wb-fmtlink');
    box.innerHTML = '<input type="url" placeholder="Paste a web address (https://…)" aria-label="Link address"><button type="button">Link</button>';
    fmtBar.append(box);
    const i = box.querySelector('input');
    i.addEventListener('pointerdown', e => e.stopPropagation(), true);
    const done = ok => {
      const v = i.value.trim().replace(/^www\./i, 'https://www.');
      box.remove();
      ta.focus(); ta.setSelectionRange(s, en);
      if (ok && /^https?:\/\/\S+$/i.test(v)) { const r = makeLink(ta.value, s, en, v); ta.value = r.value; ta.setSelectionRange(r.start, r.end); ta.dispatchEvent(new Event('input')); }
    };
    i.addEventListener('keydown', e => { e.stopPropagation(); if (e.key === 'Enter') { e.preventDefault(); done(true); } if (e.key === 'Escape') { e.preventDefault(); done(false); } });
    box.querySelector('button').addEventListener('click', () => done(true));
    setTimeout(() => i.focus(), 0);
  }
  fmtBar.addEventListener('click', e => {
    const b = e.target.closest('button'); if (!b || !editing || b.closest('.wb-fmtlink')) return;
    if (b.dataset.f) { fmt(b.dataset.f); return; }
    if (b.dataset.al2) {
      const t = get(editing.id); if (!t) return;
      draft.set(t.id, bump(t, { align: b.dataset.al2 }));
      editing.ta.style.textAlign = b.dataset.al2 === 'center' ? 'center' : b.dataset.al2 === 'right' ? 'right' : 'left';
      fmtBar.querySelectorAll('[data-al2]').forEach(x => { x.classList.toggle('on', x === b); x.setAttribute('aria-pressed', String(x === b)); });
      editing.ta.focus();
    }
  });

  /// Opens the text editor once the pointer that created the element is released. Opening it during pointerdown loses it:
  /// the browser then moves focus to the board (its default for a mouse press), which closes the editor at once.
  function editSoon(target) {
    const open = () => { root.removeEventListener('pointerup', open); root.removeEventListener('pointercancel', open); setTimeout(() => { if (!destroyed && get(target.id)) editText(get(target.id)); }, 0); };
    root.addEventListener('pointerup', open); root.addEventListener('pointercancel', open);
  }
  function stopEdit() {
    if (!editing) return;
    const { id, ta, zoomBack } = editing;
    editing = null;
    setMine({ typing: null });
    if (zoomBack) { view = zoomBack; render(); }
    fmtBar.hidden = true;
    const t = get(id), text = ta.value.replace(/\s+$/, '');
    ta.remove();
    if (t) {
      if (t.type === 'text' && !text.trim()) write([bump(t, { isDeleted: true })], { now: true });
      else if (text !== (t.text || '') || draft.has(id)) write([bump(t, { text })], { now: true });
      commitStop();
    }
    render(); paintProps();
  }

  // ── Pointer input ────────────────────────────────────────────────────────
  let pinch = null, spaceDown = false;

  const tolerance = () => 6 / view.z;
  root.addEventListener('pointerdown', onDown);
  root.addEventListener('pointermove', onMove);
  root.addEventListener('pointerup', onUp);
  root.addEventListener('pointercancel', onUp);
  root.addEventListener('dblclick', onDouble);
  root.addEventListener('wheel', onWheel, { passive: false });
  root.addEventListener('dragstart', e => e.preventDefault());
  root.addEventListener('contextmenu', e => { if (e.target.closest('.wb-svg')) e.preventDefault(); });

  function onDown(e) {
    if (!e.target.closest('.wb-arrows')) { arrowDrag = null; clearTimeout(arrowHold); }
    if (e.target.closest('.wb-tools,.wb-props,.wb-zoom,.wb-menu,.wb-panel,.wb-neat,.wb-edit,.wb-penpop,.wb-colourpop,.wb-arrows,.wb-hint')) return;
    if (editing) stopEdit();
    shapeMenu.hidden = true; penPop.hidden = true; colourPop.hidden = true;
    if (!boardMenu.hidden) { boardMenu.hidden = true; paintCorners(); }
    // Inside a page the board sits in a draggable block: without this the browser starts a native drag (or a text selection)
    // on the first move and cancels the pointer, which cut every pen stroke off after a few pixels.
    e.preventDefault();
    root.focus({ preventScroll: true });
    pointers.set(e.pointerId, local(e));
    try { root.setPointerCapture(e.pointerId); } catch { /* not capturable */ }
    if (pointers.size === 2) {
      // two fingers: zoom and move, and cancel whatever one finger started
      cancelInteraction();
      const [p1, p2] = [...pointers.values()];
      pinch = { d: Math.hypot(p1.sx - p2.sx, p1.sy - p2.sy), cx: (p1.sx + p2.sx) / 2, cy: (p1.sy + p2.sy) / 2, view: { ...view } };
      return;
    }
    if (pointers.size > 2) return;
    const { sx, sy } = local(e), w = toWorld(sx, sy);
    const touch = e.pointerType === 'touch';

    // handles of the current selection
    const h = e.target.closest?.('[data-handle]')?.dataset.handle, endKey = e.target.closest?.('[data-end]')?.dataset.end;
    if (h && !readOnly) { startResize(h, w, e.shiftKey); return; }
    if (endKey && !readOnly) { const c = get([...sel][0]); interaction = { kind: 'end', id: c.id, key: endKey }; return; }
    const segIdx = e.target.closest?.('[data-seg]')?.dataset.seg;
    if (segIdx && !readOnly) { const c = get([...sel][0]); interaction = { kind: 'seg', id: c.id, index: +segIdx, pts: connectorPoints(c, get) }; return; }
    if (e.target.closest?.('[data-reroute]') && !readOnly) { const c = get([...sel][0]); write([bump(c, { waypoints: undefined })], { now: true }); commitStop(); return; }
    if (!readOnly && sel.size === 1) {
      const c = get([...sel][0]);
      // dragging a line's label slides it along the line
      if (c?.type === 'connector' && c.label) { const pts = connectorPoints(c, get) || null; const at = pts ? (c.labelAt ?? 0.5) : null; if (pts) { const lp = (() => { let total = 0; const L = []; for (let i = 1; i < pts.length; i++) { const l = Math.hypot(pts[i].x - pts[i - 1].x, pts[i].y - pts[i - 1].y); L.push(l); total += l; } let want = total * at; for (let i = 1; i < pts.length; i++) { if (want <= L[i - 1] || i === pts.length - 1) { const k = L[i - 1] ? want / L[i - 1] : 0; return { x: pts[i - 1].x + (pts[i].x - pts[i - 1].x) * k, y: pts[i - 1].y + (pts[i].y - pts[i - 1].y) * k }; } want -= L[i - 1]; } return pts[0]; })(); if (Math.hypot(lp.x - w.x, lp.y - w.y) * view.z < 16) { interaction = { kind: 'label', id: c.id }; return; } } }
    }
    // a lane divider in a swimlane: drag to resize the lanes on both sides
    if (!readOnly) {
      for (const sw of live().filter(x => x.type === 'swimlane' && !x.locked)) {
        const ls = lanesOf(sw);
        for (let i = 0; i < ls.length - 1; i++) {
          const v = ls[i].vertical, edge = v ? ls[i].box.x + ls[i].box.w : ls[i].box.y + ls[i].box.h;
          const inside = v ? (w.y >= sw.y && w.y <= sw.y + sw.h) : (w.x >= sw.x && w.x <= sw.x + sw.w);
          if (inside && Math.abs((v ? w.x : w.y) - edge) * view.z < 5) { interaction = { kind: 'lane', id: sw.id, index: i, orig: sw }; return; }
        }
      }
    }

    stopFollow(); stopInertia();
    cmenu.hidden = true;
    if (e.pointerType === 'pen') maybeStylusTips();
    if (eyedropFor && e.button === 0) { const hit = hitTest(live(), w.x, w.y, get, tolerance()); if (hit) takeColour(hit); else { eyedropFor = null; root.classList.remove('wb-eyedrop'); } return; }
    if (zooming() && e.button === 0) { zoomDown(e, sx, sy); return; }
    // the stylus side button (barrel) erases while it is held, whatever tool is on
    if (e.pointerType === 'pen' && (e.buttons & 32 || e.button === 5) && !readOnly) { interaction = { kind: 'erase', erased: new Set(), barrel: true }; eraseAt(w); return; }
    if (tool === 'laser' && e.button === 0) { interaction = { kind: 'laser' }; laserAt(w); return; }
    if (tool === 'comment' && e.button === 0) {
      const hit = hitTest(live(), w.x, w.y, get, tolerance());
      startComment(w, hit && hit.type !== 'frame' ? hit : null);
      setTool('select');
      return;
    }
    if (votingOn() && e.button === 0 && !e.shiftKey) {
      const hit = hitTest(live(), w.x, w.y, get, tolerance());
      if (hit && (hit.type === 'sticky' || hit.type === 'taskCard' || hit.type === 'noteCard')) { castVote(hit, e.altKey); return; }
    }
    if (readOnly && e.button === 0) { const bd = e.target.closest?.('[data-link]') || e.target.closest?.('[data-href]'); if (bd) { openLink(bd.dataset?.link || bd.getAttribute('data-href')); return; } }
    if (tool === 'hand' || spaceDown || e.button === 1 || (readOnly && tool !== 'laser' && tool !== 'comment')) { interaction = { kind: 'pan', sx, sy, view: { ...view } }; panTrack = []; trackPan(sx, sy); root.classList.add('wb-panning'); return; }
    if (e.button === 2) return;
    if (readOnly) return;
    if (tool === 'pen' || tool === 'highlighter') {
      // palm rejection: once a stylus has been used on this board, a resting hand (touch) does not draw
      if (touch && penSeen) { interaction = { kind: 'pan', sx, sy, view: { ...view } }; return; }
      if (e.pointerType === 'pen') penSeen = true;
      const kind = penKind();
      const pressure = e.pointerType === 'pen' ? e.pressure || 0.5 : 0.5;
      const c = prefs.color[kind];
      const p = create('path', { x: w.x, y: w.y, points: [[0, 0, pressure]], stroke: isHex(c) ? c : null, width: +(prefs.size[kind] / view.z).toFixed(2), tool: kind,
        highlight: kind === 'highlighter' || undefined, simulate: e.pointerType !== 'pen' || undefined, opacity: prefs.opacity < 100 ? prefs.opacity / 100 : undefined,
        arrowEnd: (prefs.arrowEnd && kind !== 'highlighter') || undefined, dashed: (prefs.dashed && kind !== 'highlighter') || undefined });
      p.z = zBetween(topZ(all()) || null, null);
      interaction = { kind: 'draw', el: p, pen: e.pointerType === 'pen', raw: [[w.x, w.y]], last: { x: w.x, y: w.y } };
      draft.set(p.id, p); render();
      armHold(sx, sy);
      return;
    }
    if (tool === 'eraser') {
      if (touch && penSeen) { interaction = { kind: 'pan', sx, sy, view: { ...view } }; return; }
      interaction = { kind: 'erase', erased: new Set() }; eraseAt(w); return;
    }
    if (tool === 'stamp') { add([create('stamp', { x: w.x - 18, y: w.y - 18, w: 36, h: 36, emoji: stampEmoji })], { select: false }); return; }
    if (tool === 'sticky') { const s = create('sticky', { x: w.x - 100, y: w.y - 80, w: 200, h: 160, color: stickyColor, text: '' }); const [placed] = add([s]) || []; setTool('select'); if (placed) editSoon(placed); return; }
    if (tool === 'text') { const t = create('text', { x: w.x, y: w.y - 13, w: 40, h: 26, text: '', size: 20, font: 'ipop', stroke: inkNow() }); const [placed] = add([t]) || []; setTool('select'); if (placed) editSoon(placed); return; }
    if (SHAPES.includes(tool) || tool === 'frame') {
      const s = create(tool, tool === 'frame' ? { x: w.x, y: w.y, w: 1, h: 1, name: `Frame ${live().filter(x => x.type === 'frame').length + 1}`, order: live().filter(x => x.type === 'frame').length } : { x: w.x, y: w.y, w: 1, h: 1, text: '', stroke: inkNow() });
      s.z = zBetween(topZ(all()) || null, null);
      interaction = { kind: 'create', el: s, ox: w.x, oy: w.y };
      draft.set(s.id, s); render();
      return;
    }
    if (tool === 'connector') {
      const hit = hitTest(live(), w.x, w.y, get, tolerance());
      const from = hit && hit.type !== 'connector' && hit.type !== 'path' ? { id: hit.id } : { x: w.x, y: w.y };
      const c = create('connector', { from, to: { x: w.x, y: w.y }, route: 'straight', endHead: 'arrow', startHead: 'none', stroke: inkNow() });
      c.z = zBetween(topZ(all()) || null, null);
      interaction = { kind: 'connect', el: c };
      draft.set(c.id, c); render();
      return;
    }
    // select tool
    const hit = hitTest(live(), w.x, w.y, get, tolerance());
    // the link badge opens the link (also for people who can only view)
    const badge = e.target.closest?.('[data-link]');
    if (badge && e.button === 0) { openLink(badge.dataset.link); return; }
    const inText = e.target.closest?.('[data-href]');
    if (inText && e.button === 0 && (e.ctrlKey || e.metaKey)) { openLink(inText.getAttribute('data-href')); return; }
    if (hit) {
      const unit = pickInside ? new Set([hit.id]) : expandGroups(new Set([hit.id]), live());
      if (e.shiftKey) { const on = sel.has(hit.id); for (const id of unit) on ? sel.delete(id) : sel.add(id); paintProps(); render(); return; }
      if (!sel.has(hit.id)) sel = unit;
      let movers = [...sel].map(get).filter(x => x && !x.locked);
      if (!movers.length) { paintProps(); render(); return; }
      // Alt + drag: the copies move, the originals stay
      if (e.altKey && !readOnly) {
        const map = new Map(), copies = movers.filter(m => m.type !== 'connector').map(m => { const c = { ...structuredClone(m), id: create(m.type).id, version: 1, locked: false }; map.set(m.id, c.id); return c; });
        for (const c of live().filter(c => c.type === 'connector' && map.has(c.from?.id) && map.has(c.to?.id))) copies.push({ ...structuredClone(c), id: create('connector').id, version: 1, from: { ...c.from, id: map.get(c.from.id) }, to: { ...c.to, id: map.get(c.to.id) } });
        const placed = add(copies, { select: false }) || [];
        sel = new Set(placed.filter(x => x.type !== 'connector').map(x => x.id));
        movers = [...sel].map(get).filter(Boolean);
      }
      // moving a frame carries what sits inside it
      const ids = new Set(movers.map(m => m.id));
      for (const f of movers.filter(m => m.type === 'frame')) for (const x of live()) if (!ids.has(x.id) && !x.locked && frameOf(x, live(), get)?.id === f.id) { ids.add(x.id); }
      // lists, racks, rooms and packages carry what sits inside them too (round 4)
      for (const f of movers.filter(m => CARRIES.has(m.type))) for (const x of live()) if (!ids.has(x.id) && !x.locked && x.id !== f.id && x.type !== 'connector' && x.type !== 'frame' && insideOf(x, f)) ids.add(x.id);
      // a callout goes wherever its shape goes
      for (const x of live()) if (x.type === 'callout' && x.attach && ids.has(x.attach) && !ids.has(x.id) && !x.locked) ids.add(x.id);
      const orig = [...ids].map(id => get(id));
      interaction = { kind: 'move', start: w, orig, moved: false, others: live().filter(x => !ids.has(x.id) && x.type !== 'connector' && x.type !== 'path').slice(0, 400).map(x => bounds(x, get)) };
      props.hidden = true; render();
      return;
    }
    sel.clear(); props.hidden = true; pickInside = false;
    if (touch) { interaction = { kind: 'pan', sx, sy, view: { ...view } }; panTrack = []; trackPan(sx, sy); root.classList.add('wb-panning'); render(); return; }
    if (e.altKey) { interaction = { kind: 'lasso', pts: [[w.x, w.y]] }; render(); return; }
    interaction = { kind: 'marquee', ox: w.x, oy: w.y, marquee: { x: w.x, y: w.y, w: 0, h: 0 } };
    render();
  }

  function onMove(e) {
    if (pointers.has(e.pointerId)) pointers.set(e.pointerId, local(e));
    if (pinch && pointers.size >= 2) {
      const [p1, p2] = [...pointers.values()];
      const d = Math.hypot(p1.sx - p2.sx, p1.sy - p2.sy), cx = (p1.sx + p2.sx) / 2, cy = (p1.sy + p2.sy) / 2;
      const z = Math.min(4, Math.max(0.1, pinch.view.z * (d / (pinch.d || 1))));
      const wx = (pinch.cx - pinch.view.x) / pinch.view.z, wy = (pinch.cy - pinch.view.y) / pinch.view.z;
      view = { z, x: cx - wx * z, y: cy - wy * z };
      render(); return;
    }
    const it = interaction; if (!it) return;
    const { sx, sy } = local(e), w = toWorld(sx, sy);
    switch (it.kind) {
      case 'pan': view.x = it.view.x + sx - it.sx; view.y = it.view.y + sy - it.sy; trackPan(sx, sy); render(); break;
      case 'zoom': zoomMove(it, e, sx, sy); break;
      case 'lasso': { const last = it.pts.at(-1); if (Math.hypot(last[0] - w.x, last[1] - w.y) * view.z > 3) it.pts.push([w.x, w.y]); render(); break; }
      case 'draw': {
        if (it.snapped) break;            // became a neat shape while held: the rest of this press draws nothing
        // Coalesced events give smooth lines at high pointer rates; some browsers (and synthetic events) return none.
        const co = e.getCoalescedEvents?.() || [];
        const evs = co.length ? co : [e];
        const pts = it.el.points.slice();
        // Smoothing (stabiliser): each point moves part of the way towards the pointer. A stylus gets a lighter touch so
        // handwriting keeps its shape; the raw points are kept for "hold to make it neat".
        const k = 1 - (prefs.smooth / 100) * (it.pen ? 0.5 : 0.8);
        let moved2 = 0;
        for (const ev of evs) {
          const l = local(ev), p = toWorld(l.sx, l.sy);
          it.raw.push([p.x, p.y]);
          const nx = it.last.x + (p.x - it.last.x) * k, ny = it.last.y + (p.y - it.last.y) * k;
          moved2 += Math.hypot(nx - it.last.x, ny - it.last.y) * view.z;
          it.last = { x: nx, y: ny };
          pts.push([+(nx - it.el.x).toFixed(2), +(ny - it.el.y).toFixed(2), it.pen ? ev.pressure || 0.5 : 0.5]);
        }
        it.el = { ...it.el, points: pts };
        draft.set(it.el.id, it.el); render();
        if (moved2 > 2) armHold(sx, sy);
        break;
      }
      case 'erase': eraseAt(w); break;
      case 'laser': laserAt(w); break;
      case 'create': {
        let x = Math.min(it.ox, w.x), y = Math.min(it.oy, w.y), ww = Math.abs(w.x - it.ox), hh = Math.abs(w.y - it.oy);
        if (e.shiftKey) { const s = Math.max(ww, hh); ww = hh = s; if (w.x < it.ox) x = it.ox - s; if (w.y < it.oy) y = it.oy - s; }
        it.el = { ...it.el, x, y, w: ww, h: hh }; draft.set(it.el.id, it.el); render();
        break;
      }
      case 'connect': {
        const hit = hitTest(live().filter(x => x.id !== it.el.id && !isContainer(x)), w.x, w.y, get, tolerance());
        const target = hit && hit.type !== 'connector' && hit.type !== 'path' && hit.id !== it.el.from?.id ? hit : null;
        it.connectHover = target?.id; interaction.connectHover = target?.id;
        // near a side of a shape: the end sticks to that side (draw.io port)
        let side = null;
        if (target && isShape(target)) { const b = bounds(target, get), s = nearestSide(b, w), q = portPoint(b, s); if (Math.hypot(q.x - w.x, q.y - w.y) * view.z < 22) side = s; }
        interaction.connectSide = side;
        it.el = { ...it.el, to: target ? { id: target.id, ...(side ? { side } : {}) } : { x: w.x, y: w.y } };
        draft.set(it.el.id, it.el); render();
        break;
      }
      case 'end': {
        const c = get(it.id);
        const hit = hitTest(live().filter(x => x.id !== c.id && !isContainer(x)), w.x, w.y, get, tolerance());
        const target = hit && hit.type !== 'connector' && hit.type !== 'path' ? hit : null;
        interaction.connectHover = target?.id;
        let side = null;
        if (target && isShape(target)) { const b = bounds(target, get), s = nearestSide(b, w), q = portPoint(b, s); if (Math.hypot(q.x - w.x, q.y - w.y) * view.z < 22) side = s; }
        interaction.connectSide = side;
        write([bump(c, { [it.key]: target ? { id: target.id, ...(side ? { side } : {}) } : { x: w.x, y: w.y }, waypoints: undefined })]);
        break;
      }
      case 'seg': {
        // dragging a bend: the segment moves sideways; the route becomes waypoints the person owns
        const c = get(it.id), pts = it.pts.map(q => ({ ...q }));
        const i = it.index, a = pts[i - 1], b = pts[i];
        const horiz = Math.abs(a.y - b.y) < 0.5;
        if (horiz) { a.y = b.y = w.y; } else { a.x = b.x = w.x; }
        write([bump(c, { waypoints: pts.slice(1, -1).map(q => ({ x: Math.round(q.x), y: Math.round(q.y) })), route: c.route === 'straight' ? 'elbow' : c.route })]);
        break;
      }
      case 'label': {
        const c = get(it.id), pts = connectorPoints(c, get);
        if (pts) write([bump(c, { labelAt: +fractionAt(pts, w).toFixed(3) })]);
        break;
      }
      case 'lane': {
        const sw = get(it.id); if (!sw) break;
        const lanes = (sw.lanes || []).map(l => ({ ...l }));
        const ls = lanesOf(it.orig), vertical = it.orig.orient === 'vertical';
        const pos = vertical ? w.x : w.y, i = it.index;
        const start = vertical ? ls[i].box.x : ls[i].box.y, end = vertical ? ls[i + 1].box.x + ls[i + 1].box.w : ls[i + 1].box.y + ls[i + 1].box.h;
        const along = vertical ? it.orig.w : it.orig.h, total = lanes.reduce((s, l) => s + (l.size || 1), 0);
        const p = Math.max(start + 40, Math.min(end - 40, pos));
        const both = (lanes[i].size || 1) + (lanes[i + 1].size || 1);
        lanes[i].size = +((p - start) / along * total).toFixed(4); lanes[i + 1].size = +(both - lanes[i].size).toFixed(4);
        write([bump(sw, { lanes })]);
        break;
      }
      case 'marquee': {
        it.marquee = { x: Math.min(it.ox, w.x), y: Math.min(it.oy, w.y), w: Math.abs(w.x - it.ox), h: Math.abs(w.y - it.oy) };
        render(); break;
      }
      case 'move': {
        let dx = w.x - it.start.x, dy = w.y - it.start.y;
        if (!it.moved && Math.hypot(dx, dy) * view.z < 3) return;
        it.moved = true;
        const box = unionBounds(it.orig, get);
        it.guideLines = [];
        if (box && !e.altKey) {
          const g = guides({ x: box.x + dx, y: box.y + dy, w: box.w, h: box.h }, it.others, 5 / view.z);
          if (g.dx || g.lines.some(l => l.x1 === l.x2)) dx += g.dx; else dx = snap(box.x + dx) - box.x;
          if (g.dy || g.lines.some(l => l.y1 === l.y2)) dy += g.dy; else dy = snap(box.y + dy) - box.y;
          it.guideLines = g.lines;
        }
        interaction.guideLines = it.guideLines;
        if (box) interaction.distances = distanceLabels({ x: box.x + dx, y: box.y + dy, w: box.w, h: box.h }, it.others);
        if (!root.classList.contains('wb-lifting')) {
          root.classList.add('wb-lifting');
          // what moves is lifted: a deeper shadow while it is held
          if (!reduceMotion()) for (const o of it.orig) cache.get(o.id)?.node.classList.add('wb-lift');
        }
        write(it.orig.map(o => ({ ...moved(o, dx, dy), version: o.version + 1 })));
        break;
      }
      case 'resize': {
        const dx = w.x - it.start.x, dy = w.y - it.start.y;
        const nb = resizeBox(it.box, it.handle, dx, dy, { keepRatio: it.keepRatio || e.shiftKey });
        const sx2 = nb.w / (it.box.w || 1), sy2 = nb.h / (it.box.h || 1);
        write(it.orig.map(o => {
          if (o.type === 'connector') return null;
          if (o.type === 'path') {
            const pts = o.points.map(p => [p[0] * sx2, p[1] * sy2, p[2]]);
            return { ...o, x: nb.x + (o.x - it.box.x) * sx2, y: nb.y + (o.y - it.box.y) * sy2, points: pts, version: o.version + 1 };
          }
          const ob = bounds(o, get);
          const next = { x: nb.x + (ob.x - it.box.x) * sx2, y: nb.y + (ob.y - it.box.y) * sy2, w: Math.max(8, ob.w * sx2), h: Math.max(8, ob.h * sy2) };
          if (o.type === 'text') next.size = Math.max(8, Math.round((o.size || 20) * Math.min(sx2, sy2) * 10) / 10);
          return { ...o, ...next, version: o.version + 1 };
        }).filter(Boolean));
        break;
      }
    }
  }

  function onUp(e) {
    pointers.delete(e.pointerId);
    if (pinch) { if (pointers.size < 2) pinch = null; return; }
    const it = interaction; interaction = null;
    root.classList.remove('wb-panning'); root.classList.remove('wb-lifting');
    for (const n of world.querySelectorAll('.wb-lift')) n.classList.remove('wb-lift');
    if (!it) return;
    const { sx, sy } = local(e), w = toWorld(sx, sy);
    switch (it.kind) {
      case 'pan': startInertia(); break;
      case 'zoom': zoomUp(it, e, sx, sy); break;
      case 'lasso': if (it.pts.length > 3) lassoSelect(it.pts); break;
      case 'draw': {
        clearHold();
        draft.delete(it.el.id);
        if (it.snapped) break;                       // already replaced by a neat shape while held
        // the end of a smoothed stroke catches up with the pointer, so the line ends where the pen lifted
        const endPt = it.raw.at(-1);
        if (endPt && it.el.points.length > 1) it.el = { ...it.el, points: [...it.el.points, [+(endPt[0] - it.el.x).toFixed(2), +(endPt[1] - it.el.y).toFixed(2), it.el.points.at(-1)[2]]] };
        if (it.el.points.length === 1) it.el.points.push([0.1, 0.1, it.el.points[0][2]]);
        add([it.el], { select: false });
        break;
      }
      case 'erase': commitStop(); break;
      case 'create': {
        draft.delete(it.el.id);
        let s = it.el;
        if (s.w < 6 && s.h < 6) s = { ...s, x: w.x - (s.type === 'frame' ? 240 : 80), y: w.y - (s.type === 'frame' ? 160 : 50), w: s.type === 'frame' ? 480 : 160, h: s.type === 'frame' ? 320 : 100 };
        const [placed] = add([s]) || [];
        setTool('select');
        if (placed && s.type !== 'frame') editText(placed);
        break;
      }
      case 'connect': {
        draft.delete(it.el.id);
        const c = it.el;
        const a = c.from?.id ? centre(get(c.from.id), get) : c.from, b = c.to?.id ? centre(get(c.to.id), get) : c.to;
        if (!a || !b || Math.hypot(a.x - b.x, a.y - b.y) < 8) { render(); break; }
        add([c]);
        setTool('select');
        break;
      }
      case 'end': case 'seg': case 'label': case 'lane': commitStop(); break;
      case 'marquee': {
        const m = it.marquee;
        if (m.w * view.z > 4 || m.h * view.z > 4) sel = expandGroups(new Set(insideBox(live(), m, get).map(x => x.id)), live());
        break;
      }
      case 'move': if (it.moved) afterMove(it.orig.map(o => get(o.id)).filter(Boolean)); commitStop(); break;
      case 'resize': commitStop(); break;
    }
    render(); paintProps();
  }

  function cancelInteraction() {
    clearHold();
    const it = interaction; interaction = null;
    if (!it) return;
    if (it.el) draft.delete(it.el.id);
    if (it.kind === 'move' || it.kind === 'resize') { draft.clear(); }
    render();
  }

  function startResize(handle, w, shift) {
    const els = [...sel].map(get).filter(x => x && !x.locked);
    const box = unionBounds(els, get);
    if (!box) return;
    interaction = { kind: 'resize', handle, start: w, box, orig: els, keepRatio: shift || (els.length === 1 && els[0].type === 'image') || els.length > 1 };
    props.hidden = true;
  }

  function eraseAt(w) {
    const r = prefs.size.eraser / 2 / view.z;
    if (prefs.erMode === 'part') {
      // rub out only where the eraser passes: a stroke is split into the pieces that stay
      const out = [];
      for (const x of live()) {
        if (x.type !== 'path' || x.locked) continue;
        const runs = cutStroke(x, w.x, w.y, r);
        if (runs.length === 1 && runs[0] === x.points) continue;
        out.push(bump(x, { isDeleted: true }), ...strokePieces(x, runs).map(p => ({ ...p, z: x.z })));
      }
      if (out.length) write(out);
      return;
    }
    const victims = live().filter(x => (x.type === 'path' || x.type === 'connector') && !x.locked && !interaction?.erased?.has(x.id) && hitTest([x], w.x, w.y, get, r));
    if (!victims.length) return;
    victims.forEach(v => interaction?.erased?.add(v.id));
    write(victims.map(v => bump(v, { isDeleted: true })));
  }

  let pickInside = false;            // after a double-click on a group, clicks pick single members until the selection is cleared
  function onDouble(e) {
    if (readOnly || e.target.closest('.wb-tools,.wb-props,.wb-zoom,.wb-menu,.wb-panel,.wb-neat,.wb-edit,.wb-penpop,.wb-colourpop')) return;
    const { sx, sy } = local(e), w = toWorld(sx, sy);
    const hit = hitTest(live(), w.x, w.y, get, tolerance());
    if (hit?.type === 'swimlane') {
      const i = laneAt(hit, w.x, w.y), lane = lanesOf(hit)[i];
      if (lane) {
        const ta = el('input', 'wb-edit wb-frame-name'); ta.value = lane.name || ''; ta.setAttribute('aria-label', 'Lane name');
        const a = toScreen(lane.header.x + 6, lane.header.y + (lane.vertical ? 8 : lane.header.h / 2 - 12));
        Object.assign(ta.style, { left: `${a.x}px`, top: `${a.y}px`, width: '180px', height: '24px', fontSize: '14px', fontWeight: '600' });
        root.append(ta); ta.focus(); ta.select();
        const done = () => { const v = ta.value.trim(); ta.remove(); if (v && v !== lane.name) { const cur = get(hit.id); write([bump(cur, { lanes: (cur.lanes || []).map(l => (l.id === lane.id ? { ...l, name: v.slice(0, 60) } : l)) })], { now: true }); commitStop(); } };
        ta.addEventListener('blur', done); ta.addEventListener('keydown', ev => { ev.stopPropagation(); if (ev.key === 'Enter' || ev.key === 'Escape') ta.blur(); });
        return;
      }
    }
    if (hit && ['groupBox', 'listBox', 'rack', 'fpRoom', 'umlPackage'].includes(hit.type) && (hit.type !== 'rack' || w.y <= hit.y + 10)) {
      const ta = el('input', 'wb-edit wb-frame-name'); ta.value = hit.title || ''; ta.setAttribute('aria-label', 'Group title');
      const a = toScreen(hit.x + 8, hit.type === 'rack' ? hit.y - 28 : hit.y + 6);
      Object.assign(ta.style, { left: `${a.x}px`, top: `${a.y}px`, width: '220px', height: '24px', fontSize: '14px', fontWeight: '600' });
      root.append(ta); ta.focus(); ta.select();
      const done = () => { const v = ta.value.trim(); ta.remove(); write([bump(get(hit.id), { title: v.slice(0, 80) || undefined })], { now: true }); commitStop(); };
      ta.addEventListener('blur', done); ta.addEventListener('keydown', ev => { ev.stopPropagation(); if (ev.key === 'Enter' || ev.key === 'Escape') ta.blur(); });
      return;
    }
    if (hit?.type === 'frame') {
      const name = hit.name || 'Frame';
      const ta = el('input', 'wb-edit wb-frame-name'); ta.value = name; ta.setAttribute('aria-label', 'Frame name');
      const a = toScreen(hit.x, hit.y - 26);
      Object.assign(ta.style, { left: `${a.x}px`, top: `${a.y}px`, width: '220px', height: '24px', fontSize: '14px', fontWeight: '600' });
      root.append(ta); ta.focus(); ta.select();
      const done = () => { const v = ta.value.trim(); ta.remove(); if (v && v !== name) { write([bump(hit, { name: v.slice(0, 80) })], { now: true }); commitStop(); } };
      ta.addEventListener('blur', done); ta.addEventListener('keydown', ev => { ev.stopPropagation(); if (ev.key === 'Enter' || ev.key === 'Escape') ta.blur(); });
      return;
    }
    if (hit?.type === 'file' && hit.fileId && opts.openFile) { opts.openFile(hit.fileId, hit.name); return; }
    if (hit?.type === 'taskCard' && hit.taskId && opts.tasks?.open) { opts.tasks.open(hit.taskId); return; }
    if (hit?.type === 'noteCard' && hit.pageId && opts.pages?.open) { opts.pages.open(hit.pageId); return; }
    if (hit?.groupId && !pickInside && !(hit.type === 'sticky' || hit.type === 'text' || isShape(hit))) { pickInside = true; sel = new Set([hit.id]); paintProps(); render(); return; }
    if (hit?.type === 'link' && hit.url) { openLink(hit.url); return; }
    if (hit && (hit.type === 'sticky' || hit.type === 'text' || isShape(hit))) { pickInside = !!hit.groupId; sel = new Set([hit.id]); editText(hit); return; }
    if (hit?.type === 'connector') {
      const label = el('input', 'wb-edit'); label.value = hit.label || ''; label.placeholder = 'Label'; label.setAttribute('aria-label', 'Connector label');
      Object.assign(label.style, { left: `${sx - 70}px`, top: `${sy - 14}px`, width: '140px', height: '28px', textAlign: 'center' });
      root.append(label); label.focus();
      const done = () => { const v = label.value.trim(); label.remove(); write([bump(hit, { label: v || undefined })], { now: true }); commitStop(); };
      label.addEventListener('blur', done); label.addEventListener('keydown', ev => { ev.stopPropagation(); if (ev.key === 'Enter' || ev.key === 'Escape') label.blur(); });
      return;
    }
    if (!hit) { const t = create('text', { x: w.x, y: w.y - 13, w: 40, h: 26, text: '', size: 20, font: 'ipop', stroke: inkNow() }); const [placed] = add([t]) || []; if (placed) editText(placed); }
  }

  /// The nearest element under the pointer that can scroll in the wheel's direction, up to the board itself.
  function scrollableUnder(t, dy, dx) {
    for (let n = t; n && n !== root; n = n.parentElement) {
      if (!(n instanceof HTMLElement)) continue;
      const cs = getComputedStyle(n);
      const y = /(auto|scroll)/.test(cs.overflowY) && n.scrollHeight > n.clientHeight + 1;
      const x = /(auto|scroll)/.test(cs.overflowX) && n.scrollWidth > n.clientWidth + 1;
      if (y || x) return n;
    }
    return null;
  }
  // panels, menus and pop-ups float over the board: a wheel over them belongs to them, never to the canvas
  const OVER_UI = '.wb-panel, .wb-menu, .wb-picker, .wb-morep, .wb-ctxpop, .wb-ctx, .wb-dock, .wb-dockwrap, .wb-tcov, .wb-penpop, .wb-colourpop, .wb-emptygal, .wb-boardmenu, .wb-tools, .wb-zoom, .wb-corner, .wb-mini, .wb-hint, .wb-findbar, .wb-fmtbar, .wb-invite, .wb-presbar, .wb-followbar, .wb-edit';
  function onWheel(e) {
    const ui = e.target.closest?.(OVER_UI);
    if (ui) {
      // let the panel scroll itself; at its ends (or when it does not scroll) stop here so the canvas does not move,
      // and so Ctrl + wheel never zooms the browser page
      const sc = scrollableUnder(e.target, e.deltaY, e.deltaX);
      if (e.ctrlKey || e.metaKey) { e.preventDefault(); return; }
      if (!sc) { e.preventDefault(); return; }
      const atTop = sc.scrollTop <= 0 && e.deltaY < 0, atEnd = sc.scrollTop + sc.clientHeight >= sc.scrollHeight - 1 && e.deltaY > 0;
      if ((atTop || atEnd) && !e.deltaX) e.preventDefault();
      return;
    }
    e.preventDefault();
    stopFollow();
    const { sx, sy } = local(e);
    stopInertia();
    if (e.ctrlKey || e.metaKey) { zoomAt(Math.exp(-e.deltaY * (e.deltaMode ? 0.05 : 0.0018)), sx, sy); if (dockReady) showZoomHud(sx, sy); }
    else { view.x -= e.shiftKey && !e.deltaX ? e.deltaY : e.deltaX; view.y -= e.shiftKey && !e.deltaX ? 0 : e.deltaY; render(); }
    if (editing) editing.place();
  }

  // ── Hold to make it neat ─────────────────────────────────────────────────
  // Keep the pen still for a moment at the end of a stroke: a ring fills, then a rough circle, box, triangle or line becomes
  // a clean one (grill Q10). Never automatic; the toast offers Undo, which brings the hand-drawn stroke back.
  let holdTimer = 0, holdRing = null, neatUndo = null;
  function clearHold() { clearTimeout(holdTimer); holdTimer = 0; holdRing?.remove(); holdRing = null; }
  function armHold(sx, sy) {
    clearHold();
    const it = interaction;
    if (!prefs.snap || it?.kind !== 'draw' || it.snapped || it.el.tool === 'highlighter' || it.raw.length < 8) return;
    holdTimer = setTimeout(() => {
      if (interaction !== it) return;
      holdRing = el('div', 'wb-holdring'); holdRing.style.left = `${sx}px`; holdRing.style.top = `${sy}px`; root.append(holdRing);
      holdTimer = setTimeout(() => { clearHold(); if (interaction === it) snapStroke(it); }, 380);
    }, 260);
  }
  function snapStroke(it) {
    if (it.snapped) return;
    const shape = recognise(it.raw);
    const line = shape ? null : recogniseLine(it.raw);
    if (!shape && !line) return;
    it.snapped = true;
    draft.delete(it.el.id);
    const stroke = it.el.stroke;
    let made;
    if (shape) {
      const square = Math.abs(shape.w - shape.h) < Math.max(shape.w, shape.h) * 0.15;
      const s = square ? (shape.w + shape.h) / 2 : 0;
      const box = square ? { x: shape.x + shape.w / 2 - s / 2, y: shape.y + shape.h / 2 - s / 2, w: s, h: s } : { x: shape.x, y: shape.y, w: shape.w, h: shape.h };
      made = create(shape.type, { ...box, text: '', stroke, fill: 'none', strokeWidth: Math.max(1, Math.round(it.el.width * 0.6 * 10) / 10) });
      const names = { ellipse: square ? 'a circle' : 'an oval', rect: square ? 'a square' : 'a rectangle', triangle: 'a triangle' };
      showNeatToast(`Became ${names[shape.type]}`, it.el, made);
    } else {
      made = create('connector', { from: { x: line.x1, y: line.y1 }, to: { x: line.x2, y: line.y2 }, route: 'straight', endHead: 'none', startHead: 'none', stroke, strokeWidth: Math.max(1, Math.round(it.el.width * 0.6 * 10) / 10) });
      showNeatToast('Became a straight line', it.el, made);
    }
    add([made], { select: false });
  }
  function showNeatToast(text, raw, made) {
    neatUndo = { raw, made };
    neat.innerHTML = `<span>${text}</span><button type="button" data-n="undo">Undo</button>`;
    neat.hidden = false;
    neat.style.left = `calc(50% - ${neat.offsetWidth / 2}px)`; neat.style.top = '';
    clearTimeout(showNeatToast.t); showNeatToast.t = setTimeout(() => { neat.hidden = true; neatUndo = null; }, 5000);
  }
  neat.addEventListener('pointerdown', e => e.stopPropagation());
  neat.addEventListener('click', e => {
    if (!e.target.closest('[data-n="undo"]') || !neatUndo) return;
    neat.hidden = true;
    const { raw, made } = neatUndo; neatUndo = null;
    const cur = get(made.id);
    if (cur) write([bump(cur, { isDeleted: true })], { now: true });
    add([{ ...raw, id: create('path').id, version: 1 }], { select: false });
  });

  // ── Find on board (Ctrl F) ───────────────────────────────────────────────
  const findBar = el('div', 'wb-findbar'); findBar.hidden = true; findBar.setAttribute('role', 'search');
  findBar.innerHTML = `${icon('search')}<input type="search" placeholder="Find on board" aria-label="Find on board" spellcheck="false"><span class="wb-findn" aria-live="polite"></span><button type="button" data-fd="prev" title="Previous (Shift Enter)" aria-label="Previous">↑</button><button type="button" data-fd="next" title="Next (Enter)" aria-label="Next">↓</button><button type="button" data-fd="close" title="Close (Esc)" aria-label="Close">✕</button>`;
  root.append(findBar);
  findBar.addEventListener('pointerdown', e => e.stopPropagation());
  const findInput = findBar.querySelector('input'), findN = findBar.querySelector('.wb-findn');
  let findHits = [], findAt = -1;
  function openFind() {
    findBar.hidden = false;
    // under the top-right buttons, however many rows they take
    const tr = topRight.getBoundingClientRect(), rr = rect();
    findBar.style.top = `${Math.max(12, tr.bottom - rr.top + 8)}px`;
    findInput.focus(); findInput.select(); runFind();
  }
  function closeFind() { findBar.hidden = true; findHits = []; findAt = -1; paintFindMarks(); root.focus({ preventScroll: true }); }
  function runFind() {
    findHits = findOnBoard(live(), findInput.value, get);
    findAt = findHits.length ? 0 : -1;
    showFind();
  }
  function showFind() {
    findN.textContent = !findInput.value.trim() ? '' : findHits.length ? `${findAt + 1} of ${findHits.length}` : 'No match';
    paintFindMarks();
    const t = findHits[findAt]; if (!t) return;
    const b = bounds(t, get), r = rect();
    // zoom out if the match does not fit, never zoom in past 100 %
    const z = Math.min(Math.max(view.z, 0.5), (r.width - 120) / Math.max(b.w, 1), (r.height - 160) / Math.max(b.h, 1));
    view.z = Math.max(0.1, Math.min(view.z, z));
    view.x = r.width / 2 - (b.x + b.w / 2) * view.z; view.y = r.height / 2 - (b.y + b.h / 2) * view.z;
    render();
  }
  function paintFindMarks() { afterFind = () => {
    for (const n of root.querySelectorAll('.wb-findmark')) n.remove();
    findHits.forEach((h, i) => {
      const b = bounds(h, get), a = toScreen(b.x, b.y), z = toScreen(b.x + b.w, b.y + b.h);
      svgEl('rect', { x: a.x - 5, y: a.y - 5, width: z.x - a.x + 10, height: z.y - a.y + 10, rx: 6, class: `wb-findmark${i === findAt ? ' cur' : ''}` }, overlay);
    });
  }; render(); }
  let afterFind = () => {};
  findInput.addEventListener('input', runFind);
  findInput.addEventListener('keydown', e => {
    e.stopPropagation();
    if (e.key === 'Escape') { e.preventDefault(); closeFind(); return; }
    if (e.key === 'Enter' && findHits.length) { e.preventDefault(); findAt = (findAt + (e.shiftKey ? -1 : 1) + findHits.length) % findHits.length; showFind(); }
  });
  findBar.addEventListener('click', e => {
    const b = e.target.closest('[data-fd]'); if (!b) return;
    if (b.dataset.fd === 'close') { closeFind(); return; }
    if (!findHits.length) return;
    findAt = (findAt + (b.dataset.fd === 'prev' ? -1 : 1) + findHits.length) % findHits.length; showFind();
  });

  // ── Keyboard ─────────────────────────────────────────────────────────────
  root.addEventListener('keydown', e => {
    if (e.target !== root) return;
    const k = e.key.toLowerCase(), mod = e.ctrlKey || e.metaKey;
    if (e.key === ' ' && !spaceDown) { spaceDown = true; root.classList.add('wb-space'); e.preventDefault(); return; }
    if (k === 'z' && !mod && !e.altKey && !readOnly) { e.preventDefault(); if (!e.repeat && !zHeld) { zHeld = true; zDownAt = performance.now(); zUsed = false; root.classList.add('wb-zooming'); } return; }
    if (mod && k === 'z') { e.preventDefault(); if (!readOnly) (e.shiftKey ? doRedo : doUndo)(); return; }
    if (mod && k === 'y') { e.preventDefault(); if (!readOnly) doRedo(); return; }
    if (mod && k === 'a') { e.preventDefault(); sel = new Set(live().filter(x => x.type !== 'frame').map(x => x.id)); paintProps(); render(); return; }
    if (mod && (k === '=' || k === '+')) { e.preventDefault(); glideZoom(1.25); return; }
    if (mod && k === '-') { e.preventDefault(); glideZoom(1 / 1.25); return; }
    if (mod && k === '0') { e.preventDefault(); glideZoom(1 / view.z); return; }
    if (mod && (e.key === '/' || e.code === 'Slash')) { e.preventDefault(); openPalette(); return; }
    if (e.shiftKey && k === '!') { glideFit(); return; }
    if (e.shiftKey && e.code === 'Digit1') { e.preventDefault(); glideFit(); return; }
    if (e.shiftKey && e.code === 'Digit2' && sel.size) { e.preventDefault(); glideFit([...sel].map(get).filter(Boolean), { max: 2 }); return; }
    if (e.shiftKey && k === 'f' && !mod && mode === 'board' && opts.onFullscreen) { e.preventDefault(); toggleFullscreen(); return; }
    if (e.key === '?') { toggleKeys(); return; }
    if (mod && k === 'f') { e.preventDefault(); openFind(); return; }
    if (presenting && (e.key === 'ArrowRight' || e.key === 'PageDown' || e.key === ' ')) { e.preventDefault(); goSlide(presenting.index + 1); return; }
    if (presenting && (e.key === 'ArrowLeft' || e.key === 'PageUp')) { e.preventDefault(); goSlide(presenting.index - 1); return; }
    if (e.key === 'Escape' && eyedropFor) { eyedropFor = null; root.classList.remove('wb-eyedrop'); return; }
    if (e.key === 'Escape' && !cmenu.hidden) { cmenu.hidden = true; return; }
    if (e.key === 'Escape' && tool === 'zoom') { setTool('select'); return; }
    if (e.key === 'Escape' && (!penPop.hidden || !colourPop.hidden)) { penPop.hidden = true; colourPop.hidden = true; return; }
    if (e.key === 'Escape' && !boardMenu.hidden) { boardMenu.hidden = true; paintCorners(); return; }
    if (e.key === 'Escape' && !r4.hidden) { r4.hidden = true; clearVersionView(); return; }
    if (e.key === 'Escape') { if (presenting) { stopPresent(); return; } if (following != null) { followingPresenter = null; stopFollow(); return; } if (!keysPanel.hidden) { keysPanel.hidden = true; return; } if (!gallery.hidden) { gallery.hidden = true; return; } if (!libPanel.hidden) { libPanel.hidden = true; return; } if (fullscreen) { toggleFullscreen(); return; } sel.clear(); setTool('select'); paintProps(); render(); return; }
    if (readOnly) { if (k === 'h' || k === 'l' || k === 'v') setTool(k === 'h' ? 'hand' : k === 'v' ? 'select' : 'laser'); else if (k === 'c' && opts.comments?.canComment?.()) setTool('comment'); return; }
    const chosen = [...sel].map(get).filter(Boolean);
    if (e.key === 'Delete' || e.key === 'Backspace') { e.preventDefault(); remove(chosen.filter(x => !x.locked)); paintProps(); return; }
    if (mod && k === 'd') { e.preventDefault(); duplicate(chosen); return; }
    if (mod && e.shiftKey && k === 'c') { e.preventDefault(); if (chosen.length === 1) copyStyle(chosen[0]); return; }
    if (mod && e.shiftKey && k === 'v') { e.preventDefault(); pasteStyle(); return; }
    if (e.altKey && e.shiftKey && k === 't') { e.preventDefault(); tidyFlow('TB'); return; }
    if (mod && k === 'g') { e.preventDefault(); groupSel(!e.shiftKey); return; }
    if (mod && k === 'k' && chosen.length === 1 && chosen[0].type !== 'connector') { e.preventDefault(); openLinkEditor(chosen[0]); return; }
    if (mod && k === 'c') { if (chosen.length) { e.preventDefault(); copySel(chosen); } return; }
    if (mod && k === 'x') { if (chosen.length) { e.preventDefault(); copySel(chosen); remove(chosen.filter(x => !x.locked)); } return; }
    if (mod && k === 'v') return;   // handled by the paste event (files, pictures and copied board items)
    if (mod && e.key === ']') { e.preventDefault(); reorder(chosen, 'front'); commitStop(); return; }
    if (mod && e.key === '[') { e.preventDefault(); reorder(chosen, 'back'); commitStop(); return; }
    if (e.key === 'Tab' && chosen.length === 1 && (isShape(chosen[0]) || chosen[0].type === 'sticky')) { e.preventDefault(); mindAdd(chosen[0], 'child'); return; }
    if (e.key === 'Enter' && e.shiftKey && chosen.length === 1 && (isShape(chosen[0]) || chosen[0].type === 'sticky')) { e.preventDefault(); mindAdd(chosen[0], 'sibling'); return; }
    if (e.key === 'Enter' && chosen.length === 1 && (chosen[0].type === 'sticky' || chosen[0].type === 'text' || isShape(chosen[0]))) { e.preventDefault(); editText(chosen[0]); return; }
    if (e.key.startsWith('Arrow') && chosen.length) {
      e.preventDefault();
      const step = e.shiftKey ? 10 : 1, dx = e.key === 'ArrowLeft' ? -step : e.key === 'ArrowRight' ? step : 0, dy = e.key === 'ArrowUp' ? -step : e.key === 'ArrowDown' ? step : 0;
      write(chosen.filter(x => !x.locked).map(x => moved(x, dx, dy))); commitStop(); return;
    }
    if (mod || e.altKey) return;
    if ((e.key === '[' || e.key === ']') && (tool === 'pen' || tool === 'highlighter' || tool === 'eraser')) {
      const key = tool === 'eraser' ? 'eraser' : penKind(), v = prefs.size[key], step = v >= 10 ? 2 : 1;
      setSize(key, v + (e.key === ']' ? step : -step)); toast(`${key === 'eraser' ? 'Eraser' : PEN_TYPES[key].label} size ${prefs.size[key]} px`); return;
    }
    if (k === 's' && tool === 'sticky') { stickyColor = STICKY_COLORS[(STICKY_COLORS.indexOf(stickyColor) + 1) % STICKY_COLORS.length]; setTool('sticky'); return; }
    // R picks the rectangle; pressing it again while a shape tool is on walks through the other shapes (shown in the bar).
    if (k === 'r') { lastShape = SHAPES.includes(tool) ? SHAPES[(SHAPES.indexOf(tool) + 1) % SHAPES.length] : 'rect'; setTool(lastShape); return; }
    if (k === 'i') { if (opts.upload) pickFiles(); return; }
    if (k === 'n' || (e.shiftKey && k === 't')) { if (dockOn()) { openPicker(); paintDock(); } else toggleCardPicker(); return; }
    if (e.shiftKey && k === 'g') { openCenter(); return; }
    if (k === 'k' && !compact) { toggleEmojiPicker(toolbar.querySelector('[data-tool="emoji"]') || toolbar); return; }
    if (k === 'u' && opts.linkPreview && !compact) { askLinkCard(); return; }
    if (k === 'm') { prefs.ptype = 'highlighter'; savePrefs(prefs); setTool('highlighter'); return; }
    if (k === 'p') { if (prefs.ptype === 'highlighter') { prefs.ptype = 'pen'; savePrefs(prefs); } setTool('pen'); return; }
    if (KEYS[k]) { if (SHAPES.includes(KEYS[k])) lastShape = KEYS[k]; setTool(KEYS[k]); return; }
    if (/^[0-9]$/.test(k)) setTool(DIGITS[k]);
  });
  root.addEventListener('keyup', e => {
    if (e.key === ' ') { spaceDown = false; root.classList.remove('wb-space'); }
    // Z: a quick tap switches to the zoom tool; a hold that zoomed goes back to what you had
    if (e.key.toLowerCase() === 'z' && zHeld) { zHeld = false; root.classList.remove('wb-zooming'); if (performance.now() - zDownAt < 250 && !zUsed) setTool(tool === 'zoom' ? 'select' : 'zoom'); }
  });

  function copySel(els) {
    const ids = new Set(els.map(x => x.id));
    // connectors between copied shapes come along
    const extra = live().filter(c => c.type === 'connector' && !ids.has(c.id) && ids.has(c.from?.id) && ids.has(c.to?.id));
    clipboard = structuredClone([...els, ...extra]);
    try { navigator.clipboard?.writeText(JSON.stringify({ ipopBoard: 1, elements: clipboard })).catch(() => {}); } catch { /* blocked */ }
    toast(`Copied ${els.length === 1 ? '1 thing' : `${els.length} things`}`);
  }
  function pasteClip(at) {
    if (!clipboard?.length) return;
    const b = unionBounds(clipboard, id => clipboard.find(x => x.id === id));
    const r = rect(), c = at || toWorld(r.width / 2, r.height / 2);
    const dx = b ? c.x - (b.x + b.w / 2) : 24, dy = b ? c.y - (b.y + b.h / 2) : 24;
    const map = new Map(clipboard.map(x => [x.id, create(x.type).id]));
    const out = clipboard.map(x => {
      const copy = { ...structuredClone(x), id: map.get(x.id), version: 1, locked: false };
      if (x.type === 'connector') { const end = y => (y?.id && map.has(y.id) ? { id: map.get(y.id) } : { x: (y?.x ?? 0) + dx, y: (y?.y ?? 0) + dy }); return { ...copy, from: end(x.from), to: end(x.to) }; }
      return { ...copy, x: x.x + dx, y: x.y + dy };
    });
    add(out);
  }

  function toggleKeys() {
    if (!keysPanel.hidden) { keysPanel.hidden = true; return; }
    if (dockReady) {
      keysPanel.innerHTML = `<div class="wb-ph"><b>Keyboard shortcuts</b><button type="button" data-close aria-label="Close">✕</button></div><input class="wb-search" type="search" placeholder="Search shortcuts…" aria-label="Search shortcuts"><div class="wb-kbody"></div>`;
      paintKeys();
      const s = keysPanel.querySelector('.wb-search');
      s.addEventListener('input', () => paintKeys(s.value));
      s.addEventListener('keydown', ev => { ev.stopPropagation(); if (ev.key === 'Escape') { keysPanel.hidden = true; root.focus({ preventScroll: true }); } });
      keysPanel.classList.add('wb-keys3');
      // focus after this key press is done, or the "?" that opened the panel lands in the search box
      keysPanel.hidden = false; setTimeout(() => s.focus({ preventScroll: true }), 0);
      return;
    }
    const rows = [['V', 'Select'], ['H', 'Hand (or hold Space)'], ['P', 'Pen (click it again: pen, marker, pencil, size, colour)'], ['M', 'Highlighter'], ['E', 'Eraser (click it again: whole or part)'], ['[ / ]', 'Smaller / bigger pen or eraser'], ['S', 'Sticky note (again: next colour)'], ['T', 'Text'], ['R', 'Shape (again: next shape)'], ['O', 'Circle'], ['D', 'Diamond'], ['A', 'Connector'], ['F', 'Frame'], ['Shift F', 'Full screen'], ['L', 'Laser pointer'], ['K', 'Emoji and stickers'], ['U', 'Web link card (or paste an address)'], ['→ / ←', 'Next / previous slide while presenting'],
      ['1–9, 0', 'The old drawing keys'], ['Enter', 'Edit the selected text'], ['Ctrl D', 'Duplicate'], ['Ctrl G', 'Group (Shift: ungroup)'], ['Ctrl Shift C / V', 'Copy / paste style'], ['Alt Shift T', 'Tidy up flow'], ['Hover a shape', 'Blue arrows add the next shape'], ['Ctrl K', 'Link the selected thing'], ['Ctrl C / V', 'Copy / paste'], ['Ctrl Z / Y', 'Undo / redo'], ['Ctrl F', 'Find on board'], ['Ctrl ] / [', 'Front / back'], ['Arrows', 'Nudge (Shift: 24 px)'], ['Ctrl + wheel', 'Zoom'], ['Shift 1', 'Fit everything'], ['Alt while dragging', 'No snapping']];
    keysPanel.innerHTML = `<div class="wb-ph"><b>Keyboard shortcuts</b><button type="button" data-close aria-label="Close">✕</button></div><div class="wb-kgrid">${rows.map(([k, d]) => `<div><kbd>${k}</kbd><span>${d}</span></div>`).join('')}</div>`;
    keysPanel.hidden = false;
  }
  keysPanel.addEventListener('pointerdown', e => e.stopPropagation());
  keysPanel.addEventListener('click', e => { if (e.target.closest('[data-close]')) keysPanel.hidden = true; });

  // ── Start ────────────────────────────────────────────────────────────────
  paintToolbar();
  paintCorners();
  root.dataset.tool = 'select';
  let fitted = false;
  let wasPhone = isPhone();
  let wasTight = tight;
  const ro = new ResizeObserver(() => {
    if (isPhone() !== wasPhone) { wasPhone = isPhone(); paintToolbar(); }
    if (dockOn() !== !dock.hidden) paintToolbar();
    // all tools need about 40 px each; under that the bar switches to the short set + More
    tight = root.clientWidth > 0 && root.clientWidth < (mode === 'block' ? 640 : 560);
    if (tight !== wasTight) { wasTight = tight; root.classList.toggle('wb-tight', tight); moreOpen = false; moreMenu.hidden = true; paintToolbar(); }
    const r = rect();
    if (!fitted && r.width > 60 && r.height > 60) { fitted = true; if (!restoreView()) fit(); }
    else render();
    if (editing) editing.place();
  });
  ro.observe(root);
  render();

  /// Pictures as data URLs (a PNG made from an SVG cannot read blob: URLs).
  async function dataUrls() {
    const out = new Map();
    for (const [id, url] of files) {
      try { const b = await (await fetch(url)).blob(); out.set(id, await new Promise(ok => { const r = new FileReader(); r.onload = () => ok(r.result); r.readAsDataURL(b); })); } catch { /* left out */ }
    }
    return out;
  }
  const api = {
    root,
    /// Adds a starter layout beside what is already on the board. Returns how many things were added.
    applyTemplate(name, { howTo: withHow = true, examples = true } = {}) {
      if (!CANVAS_TEMPLATES[name] || readOnly) return 0;
      const b = unionBounds(live(), get);
      const items = buildTemplate(name, { x: b ? b.x + b.w + 120 : 0, y: b ? b.y : 0, howTo: withHow, examples });
      const placed = add(items) || [];
      if (dockReady) { glideFit(placed, { max: 1 }); const f = placed.find(x => x.type === 'frame'); if (f) setTimeout(() => flash([f.id], 1100), reduceMotion() ? 0 : 300); } else fit(placed, { max: 1 });
      return placed.length;
    },
    setReadOnly(v) { readOnly = !!v; if (readOnly) { stopEdit(); sel.clear(); setTool('select'); gallery.hidden = true; tcOverlay.hidden = true; ctxPop.hidden = true; picker.hidden = true; morePanel.hidden = true; penPop.hidden = true; colourPop.hidden = true; emojiPop.hidden = true; } paintToolbar(); paintCorners(); paintZoom(); render(); paintProps(); },
    /// Repaints the corner buttons (the page lock changed, for example).
    refreshCorners() { paintCorners(); },
    setTheme(t) { theme = t; cache.clear(); world.innerHTML = ''; root.classList.toggle('wb-dark', t === 'dark'); pvCache.clear(); paintToolbar(); render(); if (!tcOverlay.hidden) paintCenter(); },
    fit: () => fit(),
    tidyFlow: dir => tidyFlow(dir),
    focus: () => root.focus({ preventScroll: true }),
    /// PNG of the whole board (light background unless dark is asked for).
    png: async ({ scale = 2, dark = false } = {}) => toPng(live(), { scale, theme: dark ? 'dark' : 'light', files: await dataUrls(), cardInfo: opts.cardInfo }),
    svg: ({ dark = false } = {}) => { const o = toSvg(live(), { theme: dark ? 'dark' : 'light', files: opts.files }); return o ? new XMLSerializer().serializeToString(o.svg) : null; },
    count,
    flush,
    /// The page's comment threads (only those pinned to this board are drawn).
    setThreads(list) { threads = (list || []).map(t => ({ id: t.id, anchor: t.anchor, resolvedAt: t.resolvedAt, count: t.count || 1 })); paintPins(); },
    /// Put a Notes page or Projects tasks on the board, in the middle of the screen.
    addPageCard(page) { if (readOnly) return; const r = rect(); addPageCard(page, toWorld(r.width / 2, r.height / 2)); },
    addTaskCards(tasks) { if (readOnly || !tasks?.length) return; const r = rect(); const c = toWorld(r.width / 2, r.height / 2); addTaskCards(tasks, { x: c.x - 130, y: c.y - 52 }); },
    setFullscreen(v) { fullscreen = !!v; paintCorners(); requestAnimationFrame(() => fit()); },
    destroy() { clearInterval(viewSaver); try { saveView(); } catch { /* gone */ } springWatch.disconnect(); cancelAnimationFrame(camAnim); cancelAnimationFrame(inertiaRaf); clearInterval(seenTimer); try { saveSeen(); } catch { /* gone */ } meta?.unobserve(onMeta); clearTimeout(r4State.t); clearTimeout(arrowHold); document.removeEventListener('keydown', popKeys, true); clearTimeout(paintEmoji.t); unFocus?.(); unThreads?.(); vmap?.unobserve(onVote); clearInterval(liveTick); clearInterval(timerTick); clearTimeout(cursorTimer); clearTimeout(viewTimer); clearTimeout(laserFade); clearTimeout(showNeatToast.t); clearHold(); clearTimeout(flushTimer); aw?.off('change', onAw); if (aw) { const st = aw.getLocalState(); if (st?.wb?.board === boardKey) aw.setLocalStateField('wb', null); } for (const u of files.values()) { try { URL.revokeObjectURL(u); } catch { /* not ours */ } } document.removeEventListener('keydown', fsKeys, true); stopEdit(); flush(); destroyed = true; ymap.unobserve(onY); undo.destroy(); ro.disconnect(); cancelAnimationFrame(raf); root.remove(); },
  };
  return api;
}
