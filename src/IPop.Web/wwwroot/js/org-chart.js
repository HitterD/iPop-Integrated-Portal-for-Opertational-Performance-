// Admin → Organization → Chart editor (port of docs/mockups/ipop-apple/org-chart.html).
// The module owns the canvas DOM: positions as boxes with reporting lines, a list view, a side panel,
// draft/undo/publish, paste import and zoom-first navigation (wheel = zoom at cursor, drag = pan).
// AdminOrgChart.razor loads the data, publishes and toasts; everything else lives here.
// Every class is prefixed oc- because the app's global CSS already styles .btn/.card/.tag/... .

const PALETTE = ['#2e9d6b', '#2563eb', '#7c3aed', '#db2777', '#0891b2', '#0e7490', '#ea580c', '#a8760c', '#65a30d', '#c2442e', '#64748b', '#9333ea', '#0f766e', '#b45309'];
const AVATAR = ['#2e7d5b', '#7d5ba6', '#8a6d3b', '#b3543f', '#4f6d8f', '#5b7d2e'];   // AvatarPalette.Colors
const ZMIN = .08, ZMAX = 2;

const esc = s => String(s ?? '').replace(/[&<>"]/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]));
const norm = s => String(s || '').toLowerCase().normalize('NFKD').replace(/[^a-z0-9 ]/g, ' ').replace(/\s+/g, ' ').trim();
const clampZ = z => Math.min(ZMAX, Math.max(ZMIN, z));
function avatarColor(nik) { let h = 17; for (const c of nik || '') h = (Math.imul(h, 31) + c.charCodeAt(0)) | 0; return AVATAR[(h & 0x7fffffff) % AVATAR.length]; }
function initials(name) { const w = String(name || '').split(' ').filter(x => x && /[\p{L}\p{N}]/u.test(x[0])); return (w.slice(0, 2).map(x => x[0]).join('') || '?').toUpperCase(); }

class Editor {
  constructor(root, dotnet, data) {
    this.root = root; this.dotnet = dotnet;
    this.draft = null; this.editing = false; this.undoStack = [];
    this.sel = null; this.view = 'chart'; this.query = ''; this.zoom = 1; this.tx = 0; this.ty = 0; this.panned = false;
    this.collapsed = new Set(); this.impMode = 'list'; this.modalResolve = null; this.dragId = null;
    this.build();
    this.load(data, true);
  }

  // ───────────────────────── data ─────────────────────────
  load(data, first) {
    this.version = data.version;
    this.photos = data.photos || {};
    this.units = {}; (data.units || []).forEach((u, i) => this.units[u.code] = { name: u.name, color: PALETTE[i % PALETTE.length] });
    this.plants = [['ALL', 'All plants'], ...(data.plants || []).map(p => [p.code, p.name])];
    this.people = data.people || [];
    this.byNik = Object.fromEntries(this.people.map(e => [e.nik, e]));
    this.pub = (data.positions || []).map(p => ({ id: p.id, title: p.title, unit: p.unit || null, parent: p.parentId || null, people: [...p.people], plant: p.plant || '*', order: p.order }));
    if (first) {
      const own = this.plants.find(([c]) => c !== 'ALL' && this.pub.some(p => p.plant === c));
      this.plant = own ? own[0] : 'ALL';
      this.$('plant').innerHTML = this.plants.map(([c, n]) => `<option value="${esc(c)}">${esc(n)}</option>`).join('');
      this.$('plant').value = this.plant;
      this.reindex(); this.collapseDefault();
    }
    if (!this.plants.some(([c]) => c === this.plant)) this.plant = 'ALL';
    if (this.sel && !this.get(this.sel)) this.sel = null;
    this.render();
    if (first) requestAnimationFrame(() => this.fit());
  }

  person(nik) { return this.byNik[nik] || { nik, name: nik, active: false }; }
  unit(code) { return this.units[code] || { name: code ? code : 'No division', color: '#94908a' }; }
  S() { return this.editing ? this.draft : this.pub; }
  get(id) { return this.idx.byId.get(id); }
  kids(id) { return this.idx.kids.get(id ?? null) || []; }
  inPlant(p) { return this.plant === 'ALL' || p.plant === '*' || p.plant === this.plant; }
  desc(id) { return this.kids(id).flatMap(k => [k, ...this.desc(k.id)]); }
  descCount(id) { let n = this.idx.descN.get(id); if (n == null) { n = this.kids(id).reduce((s, k) => s + 1 + this.descCount(k.id), 0); this.idx.descN.set(id, n); } return n; }
  ancestors(p) { const a = []; const seen = new Set(); while (p && p.parent && !seen.has(p.parent)) { seen.add(p.parent); p = this.get(p.parent); if (p) a.push(p); } return a; }
  rolesOf(nik) { return this.idx.roles.get(nik) || []; }

  /** Lookups rebuilt once per render so a few thousand boxes stay O(n). */
  reindex() {
    const all = this.S(), byId = new Map(all.map(p => [p.id, p])), kids = new Map(), roles = new Map();
    all.forEach(p => {
      p.people.forEach(n => { if (!roles.has(n)) roles.set(n, []); roles.get(n).push(p); });
      if (!this.inPlant(p)) return;
      const key = p.parent && byId.has(p.parent) ? p.parent : null;
      if (!kids.has(key)) kids.set(key, []);
      kids.get(key).push(p);
    });
    kids.forEach(l => l.sort((a, b) => a.order - b.order || a.title.localeCompare(b.title)));
    const q = norm(this.query), hits = new Set();
    if (q.length >= 2) all.forEach(p => { if (this.inPlant(p) && (norm(p.title).includes(q) || p.people.some(n => norm(this.person(n).name).includes(q) || n.includes(q)))) hits.add(p.id); });
    this.idx = { byId, kids, roles, hits, descN: new Map() };
  }

  collapseDefault() {
    this.collapsed.clear();
    // Start at the top: everything below the first management row is folded.
    this.S().forEach(p => { if (this.ancestors(p).length >= 1 && this.S().some(c => c.parent === p.id)) this.collapsed.add(p.id); });
  }

  // ───────────────────────── DOM ─────────────────────────
  $(id) { return this.root.querySelector(`[data-oc="${id}"]`); }
  build() {
    this.root.innerHTML = `
      <div class="oc-toolbar">
        <div class="oc-ttl"><b>Organization chart</b><span data-oc="ttlSub"></span></div>
        <div class="oc-seg"><button data-view="chart" class="on" title="Boxes"><i data-lucide="network"></i></button><button data-view="tree" title="List"><i data-lucide="list-tree"></i></button></div>
        <div class="oc-sp"></div>
        <label class="oc-field"><i data-lucide="factory"></i><select data-oc="plant" aria-label="Plant"></select></label>
        <label class="oc-field"><i data-lucide="search"></i><input data-oc="q" placeholder="Find a person or position" aria-label="Find"><span class="oc-cnt" data-oc="qCnt"></span></label>
        <button class="oc-btn ghost" data-act="expandAll" title="Expand all"><i data-lucide="unfold-vertical"></i></button>
        <button class="oc-btn ghost" data-act="collapseAll" title="Collapse all"><i data-lucide="fold-vertical"></i></button>
        <button class="oc-btn" data-act="import"><i data-lucide="list-tree"></i>Import</button>
        <button class="oc-btn" data-act="export" title="CSV you can edit and import again"><i data-lucide="download"></i>Export</button>
        <button class="oc-btn primary" data-act="edit" data-oc="editBtn"><i data-lucide="pencil"></i>Edit chart</button>
      </div>
      <div class="oc-draftbar" data-oc="draftbar">
        <span class="oc-ddot"></span><b>Editing a draft</b><span data-oc="draftCnt"></span>
        <span class="oc-hint">· Drag a box onto another to change who it reports to · drag to the side to reorder · hover for +</span>
        <div class="oc-sp"></div>
        <button class="oc-btn" data-act="undo" data-oc="undoBtn"><i data-lucide="undo-2"></i>Undo</button>
        <button class="oc-btn" data-act="discard">Discard</button>
        <button class="oc-btn primary" data-act="publish" data-oc="publishBtn"><i data-lucide="upload"></i>Publish</button>
      </div>
      <div class="oc-body">
        <div class="oc-canvas" data-oc="canvas"><div class="oc-zoom" data-oc="zoom"><div class="oc-chart" data-oc="oc"></div></div></div>
        <div class="oc-treewrap" data-oc="tree" hidden></div>
        <aside class="oc-panel" data-oc="panel"></aside>
        <div class="oc-zctl" data-oc="zoomer">
          <button data-z="in" title="Zoom in (+)"><i data-lucide="plus"></i></button>
          <button data-z="100" data-oc="zLbl" class="oc-zlbl" title="Back to 100%">100%</button>
          <button data-z="out" title="Zoom out (−)"><i data-lucide="minus"></i></button>
          <button data-z="fit" title="Fit the whole chart on screen (0)"><i data-lucide="scan"></i></button>
        </div>
        <div class="oc-zhint" data-oc="zhint">Scroll to zoom · drag to move · double-click to zoom in · 0 = fit</div>
      </div>
      <div class="oc-legend" data-oc="legend"></div>
      <div class="oc-scrim" data-oc="scrim"><div class="oc-modal" data-oc="modal" role="dialog" aria-modal="true"></div></div>`;
    this.bind();
  }

  icons() { window.ipop?.icons(); }

  render() {
    this.reindex();
    const r = this.root;
    r.classList.toggle('editing', this.editing);
    this.$('draftbar').classList.toggle('on', this.editing);
    this.$('editBtn').style.display = this.editing ? 'none' : '';
    const n = this.undoStack.length;
    this.$('draftCnt').textContent = n ? `${n} unpublished change${n > 1 ? 's' : ''} · only you can see them` : 'No changes yet';
    this.$('undoBtn').disabled = !n;
    this.$('canvas').hidden = this.view !== 'chart';
    this.$('tree').hidden = this.view !== 'tree';
    this.$('zoomer').style.display = this.$('zhint').style.display = this.view === 'chart' ? '' : 'none';
    this.$('ttlSub').textContent = this.view === 'chart' ? 'Positions and reporting lines' : 'The same structure as a list' + (this.editing ? ' — drag rows to move' : '');
    r.querySelectorAll('[data-view]').forEach(b => b.classList.toggle('on', b.dataset.view === this.view));
    this.view === 'chart' ? this.renderChart() : this.renderTree();
    this.renderPanel();
    this.renderLegend();
    this.icons();
  }

  renderChart() {
    const roots = this.kids(null), oc = this.$('oc');
    const plantHasOwn = this.plant === 'ALL' ? this.S().length > 0 : this.S().some(p => p.plant === this.plant);
    let html = roots.length ? `<ul>${roots.map(p => this.li(p)).join('')}</ul>` : '';
    if (!plantHasOwn) {
      const pn = this.plants.find(p => p[0] === this.plant)?.[1] ?? '';
      const top = roots[0];
      html += `<div class="oc-empty"><i data-lucide="network"></i><h4>${this.S().length ? `No structure for ${esc(pn)} yet` : 'The chart is empty'}</h4>
        <div>${top ? 'Only the shared top positions are shown. ' : ''}Paste the structure from a screenshot or a spreadsheet, or start adding positions.</div>
        <button class="oc-btn" data-act="import"><i data-lucide="list-tree"></i>Import from list</button>
        <button class="oc-btn primary" data-act="addTop"><i data-lucide="plus"></i>${top ? `Add position under ${esc(top.title)}` : 'Add the top position'}</button></div>`;
      oc.classList.add('col');
    } else oc.classList.remove('col');
    oc.innerHTML = html;
    this.applyView();
  }

  li(p) {
    const ks = this.kids(p.id), open = !this.collapsed.has(p.id);
    let sub = '';
    if (ks.length && open) {
      const compact = ks.length >= 2 && ks.every(k => !this.kids(k.id).length);
      sub = compact ? `<div class="oc-stack">${ks.map(k => `<div class="oc-sw">${this.card(k, 'y')}</div>`).join('')}</div>` : `<ul>${ks.map(k => this.li(k)).join('')}</ul>`;
    }
    return `<li>${this.card(p, 'x')}${sub}</li>`;
  }

  av(e, cls = '') {
    const url = this.photos[e.nik];
    return url ? `<span class="oc-av ${cls}" style="background:${avatarColor(e.nik)} url('${esc(url)}') center/cover no-repeat;color:transparent">${esc(initials(e.name))}</span>`
      : `<span class="oc-av ${cls}" style="background:${avatarColor(e.nik)}">${esc(initials(e.name))}</span>`;
  }

  card(p, axis) {
    const ppl = p.people.map(n => this.person(n)), ks = this.kids(p.id), open = !this.collapsed.has(p.id);
    const isRoot = !p.parent;
    let avatar, name;
    if (!ppl.length) { avatar = `<span class="oc-av empty"><i data-lucide="user-x"></i></span>`; name = 'Vacant'; }
    else if (ppl.length >= 3) { avatar = `<span class="oc-avs">${ppl.slice(0, 3).map(e => this.av(e)).join('')}</span>`; name = `${ppl.length} people`; }
    else { avatar = this.av(ppl[0]); name = ppl.map(e => e.name).join(', '); }
    const roles = ppl.length === 1 ? this.rolesOf(ppl[0].nik).length : 0;
    const badge = roles > 1 ? `<span class="oc-badge">+${roles - 1} role</span>` : '';
    const below = this.descCount(p.id);
    return `<div class="oc-pc ${ppl.length ? '' : 'vacant'} ${isRoot ? 'root' : ''} ${this.sel === p.id ? 'sel' : ''} ${this.idx.hits.has(p.id) ? 'hit' : ''}" data-id="${esc(p.id)}" data-axis="${axis}"
        draggable="${this.editing}" style="--u:${this.unit(p.unit).color}" title="${esc(p.title)}" tabindex="0">
      ${avatar}<div class="oc-tx"><b>${esc(name)}</b><span>${esc(p.title)}</span></div>${badge}
      ${ks.length ? `<button class="oc-kids" data-toggle="${esc(p.id)}" title="${open ? 'Collapse' : 'Expand'} · ${below} positions below"><i data-lucide="${open ? 'chevron-up' : 'chevron-down'}"></i>${below}</button>` : ''}
      ${this.editing ? `<button class="oc-add" data-add="${esc(p.id)}" title="Add a position reporting to this one"><i data-lucide="plus"></i></button>` : ''}
    </div>`;
  }

  renderTree() {
    const rows = [];
    const walk = (p, d) => {
      const ks = this.kids(p.id), open = !this.collapsed.has(p.id), ppl = p.people.map(n => this.person(n));
      rows.push(`<div class="oc-tr ${this.sel === p.id ? 'sel' : ''} ${this.idx.hits.has(p.id) ? 'hit' : ''}" data-id="${esc(p.id)}" data-axis="y" style="--d:${d};--u:${this.unit(p.unit).color}" draggable="${this.editing}">
        <i data-lucide="grip-vertical" class="oc-grip"></i>
        <button class="oc-car ${ks.length ? '' : 'none'}" data-toggle="${esc(p.id)}"><i data-lucide="${open ? 'chevron-down' : 'chevron-right'}"></i></button>
        <span class="oc-dot"></span><span class="oc-t">${esc(p.title)}</span>
        <span class="oc-who ${ppl.length ? '' : 'vac'}">${ppl.length ? esc(ppl.length > 3 ? ppl.length + ' people' : ppl.map(e => e.name).join(', ')) : 'Vacant'}</span>
        ${ks.length ? `<span class="oc-n">${this.descCount(p.id)}</span>` : ''}
        <button class="oc-tadd" data-add="${esc(p.id)}" title="Add a position below"><i data-lucide="plus"></i></button>
      </div>`);
      if (open) ks.forEach(k => walk(k, d + 1));
    };
    this.kids(null).forEach(r => walk(r, 0));
    this.$('tree').innerHTML = rows.join('') || `<div class="oc-empty"><h4>The chart is empty</h4><button class="oc-btn primary" data-act="addTop"><i data-lucide="plus"></i>Add the top position</button></div>`;
  }

  renderLegend() {
    const vis = this.S().filter(p => this.inPlant(p));
    const used = [...new Set(vis.map(p => p.unit))];
    this.$('legend').innerHTML = used.map(u => `<span><i style="background:${this.unit(u).color}"></i>${esc(this.unit(u).name)}</span>`).join('') +
      `<span class="oc-lsum">${vis.length} positions · ${vis.filter(p => !p.people.length).length} vacant · ${new Set(vis.flatMap(p => p.people)).size} people placed</span>`;
  }

  renderPanel() {
    const panel = this.$('panel'), p = this.sel && this.get(this.sel);
    if (!p) { panel.classList.remove('open'); panel.innerHTML = ''; return; }
    panel.classList.add('open');
    const parent = p.parent && this.get(p.parent), ppl = p.people.map(n => this.person(n)), ks = this.kids(p.id);
    const ro = !this.editing;
    const u = this.unit(p.unit);
    const unitOpts = `<option value="">No division</option>` + Object.entries(this.units).map(([k, x]) => `<option value="${esc(k)}" ${k === p.unit ? 'selected' : ''}>${esc(x.name)}</option>`).join('');
    const plantOpts = `<option value="*" ${p.plant === '*' ? 'selected' : ''}>Every plant</option>` + this.plants.filter(([c]) => c !== 'ALL').map(([c, n]) => `<option value="${esc(c)}" ${c === p.plant ? 'selected' : ''}>${esc(n)}</option>`).join('');
    const banned = new Set([p.id, ...this.allDesc(p.id)]);
    const parentOpts = `<option value="">Top of the chart</option>` + this.S().filter(x => !banned.has(x.id) && this.inPlant(x)).sort((a, b) => a.title.localeCompare(b.title))
      .map(x => `<option value="${esc(x.id)}" ${x.id === p.parent ? 'selected' : ''}>${esc(x.title)}${x.people.length ? ' · ' + esc(this.person(x.people[0]).name) : ''}</option>`).join('');
    panel.style.setProperty('--u', u.color);
    panel.innerHTML = `
      <div class="oc-ph"><div class="oc-sp"><div class="oc-unit"><i></i>${esc(u.name)}</div><h3>${esc(p.title)}</h3></div>
        <button class="oc-icon" data-act="close" title="Close"><i data-lucide="x"></i></button></div>
      <div class="oc-pb">
        ${ro ? '' : `<div class="oc-pf"><label>Position title</label><input data-oc="fTitle" value="${esc(p.title)}" maxlength="200"></div>
        <div class="oc-pf"><label>Division</label><select data-oc="fUnit">${unitOpts}</select></div>
        <div class="oc-pf"><label>Plant</label><select data-oc="fPlant">${plantOpts}</select></div>`}
        <div class="oc-pf"><label>Reports to</label>${ro
          ? `<div class="oc-ro">${parent ? `${esc(parent.title)} <small>· ${esc(parent.people.map(n => this.person(n).name).join(', ') || 'Vacant')}</small>` : 'Top of the chart'}</div>`
          : `<select data-oc="fParent">${parentOpts}</select>`}</div>
        <div class="oc-pf"><label>People (${ppl.length})</label>
          ${ppl.length ? ppl.map(e => {
            const other = this.rolesOf(e.nik).filter(r => r.id !== p.id);
            return `<div class="oc-prow">${this.av(e, 'sm')}<div class="oc-nm"><b>${esc(e.name)}</b><span>NIK ${esc(e.nik)}${e.active ? '' : ' · <span class="oc-gone">inactive</span>'}${other.length ? ` · <span class="oc-also">also ${esc(other.map(r => r.title).join(', '))}</span>` : ''}</span></div>
              ${ro ? '' : `<button class="oc-x" data-remove="${esc(e.nik)}" title="Remove from this position"><i data-lucide="x"></i></button>`}</div>`;
          }).join('') : `<div class="oc-vnote"><i data-lucide="user-x"></i>Vacant — nobody holds this position yet.</div>`}
          ${ro ? '' : `<div class="oc-finder"><div class="oc-pf"><input data-oc="fFind" placeholder="Add person — search name or NIK" autocomplete="off"></div><div class="oc-res" data-oc="fRes"></div></div>`}
        </div>
        ${ks.length ? `<div class="oc-pf"><label>Direct reports (${ks.length})</label><div class="oc-reports">${ks.map(k => `<button data-goto="${esc(k.id)}" style="--u:${this.unit(k.unit).color}"><span class="oc-dot"></span>${esc(k.title)}<span class="oc-who">${esc(k.people.length > 2 ? k.people.length + ' people' : k.people.map(n => this.person(n).name).join(', ') || 'Vacant')}</span></button>`).join('')}</div></div>` : ''}
      </div>
      <div class="oc-pfoot">${ro
        ? `<span class="oc-rohint"><i data-lucide="lock"></i>Published chart</span><div class="oc-sp"></div><button class="oc-btn primary" data-act="edit"><i data-lucide="pencil"></i>Edit</button>`
        : `<button class="oc-btn" data-add="${esc(p.id)}"><i data-lucide="plus"></i>Add position below</button><div class="oc-sp"></div>
           <button class="oc-btn danger" data-act="delete"><i data-lucide="trash-2"></i>Delete</button>`}
      </div>`;
    if (!ro) {
      this.$('fTitle').addEventListener('change', e => { const v = e.target.value.trim().slice(0, 200); if (v && v !== p.title) this.mutate(() => this.get(p.id).title = v); });
      this.$('fUnit').addEventListener('change', e => this.mutate(() => this.get(p.id).unit = e.target.value || null));
      this.$('fPlant').addEventListener('change', e => this.mutate(() => this.get(p.id).plant = e.target.value));
      this.$('fParent').addEventListener('change', e => this.reparent(p.id, e.target.value || null));
      this.$('fFind').addEventListener('input', e => this.renderFinder(p, e.target.value));
    }
  }

  /** Every position below id regardless of the plant filter (cycle checks must see all of them). */
  allDesc(id) {
    const out = [], stack = [id];
    while (stack.length) { const at = stack.pop(); this.S().forEach(x => { if (x.parent === at && !out.includes(x.id)) { out.push(x.id); stack.push(x.id); } }); }
    return out;
  }

  renderFinder(p, q) {
    q = norm(q);
    const res = this.$('fRes');
    if (q.length < 2) { res.innerHTML = ''; return; }
    const list = this.people.filter(e => e.active && !p.people.includes(e.nik) && (norm(e.name).includes(q) || e.nik.includes(q))).slice(0, 8);
    res.innerHTML = list.length ? list.map(e => {
      const r = this.rolesOf(e.nik);
      return `<div class="oc-prow" data-pick="${esc(e.nik)}">${this.av(e, 'sm')}<div class="oc-nm"><b>${esc(e.name)}</b><span>NIK ${esc(e.nik)}</span></div>${r.length ? `<span class="oc-tag in">in: ${esc(r[0].title)}${r.length > 1 ? ' +' + (r.length - 1) : ''}</span>` : '<span class="oc-tag">unplaced</span>'}</div>`;
    }).join('') : `<div class="oc-prow oc-muted">No active employee matches “${esc(q)}”.</div>`;
  }

  // ───────────────────────── viewport (zoom first, pan second) ─────────────────────────
  applyView() {
    this.$('zoom').style.transform = `translate(${this.tx}px, ${this.ty}px) scale(${this.zoom})`;
    this.$('zLbl').textContent = Math.round(this.zoom * 100) + '%';
    const c = this.$('canvas'), g = Math.max(8, 22 * this.zoom);
    c.style.backgroundPosition = `${this.tx}px ${this.ty}px, 0 0`;
    c.style.backgroundSize = `${g}px ${g}px, auto`;
    c.classList.toggle('far', this.zoom < .55);
  }
  animated(fn) {
    const z = this.$('zoom');
    z.classList.add('anim'); fn();
    clearTimeout(this.animT); this.animT = setTimeout(() => z.classList.remove('anim'), 380);
  }
  /** Zoom keeping the canvas point (cx, cy) still — the cursor, or the centre for buttons/keys. */
  zoomAt(z, cx, cy) {
    const c = this.$('canvas');
    if (cx == null) { cx = c.clientWidth / 2; cy = c.clientHeight / 2; }
    z = clampZ(z);
    this.tx = cx - (cx - this.tx) * z / this.zoom; this.ty = cy - (cy - this.ty) * z / this.zoom; this.zoom = z;
    this.applyView();
  }
  /** Whole chart on screen; never enlarges past 100%. */
  fit() {
    const c = this.$('canvas'), el = this.$('zoom');
    if (!c?.clientWidth || !el?.offsetWidth) return;
    this.zoom = clampZ(Math.min(c.clientWidth / el.offsetWidth, c.clientHeight / el.offsetHeight, 1));
    this.tx = (c.clientWidth - el.offsetWidth * this.zoom) / 2;
    this.ty = Math.max(0, (c.clientHeight - el.offsetHeight * this.zoom) / 2 * .4);
    this.applyView();
  }
  /** Centre a box, zooming in to at least `minZ` so it is readable. */
  focusCard(id, minZ = .9) {
    const el = this.root.querySelector(`.oc-pc[data-id="${CSS.escape(id)}"]`); if (!el) return;
    const c = this.$('canvas').getBoundingClientRect(), r = el.getBoundingClientRect();
    const ux = (r.left + r.width / 2 - c.left - this.tx) / this.zoom, uy = (r.top + r.height / 2 - c.top - this.ty) / this.zoom;
    this.zoom = clampZ(Math.max(this.zoom, minZ));
    this.tx = c.width / 2 - ux * this.zoom; this.ty = c.height / 2 - uy * this.zoom;
    this.applyView();
  }
  /** Re-render without the clicked box jumping (expanding a branch re-centres the tree). */
  keepAnchor(id, fn) {
    const at = () => this.root.querySelector(`.oc-pc[data-id="${CSS.escape(id)}"]`)?.getBoundingClientRect();
    const a = at(); fn(); const b = at();
    if (a && b) { this.tx += a.left - b.left; this.ty += a.top - b.top; this.applyView(); }
  }
  reveal(id) {
    this.ancestors(this.get(id)).forEach(a => this.collapsed.delete(a.id)); this.render();
    if (this.view === 'chart') this.animated(() => this.focusCard(id));
    else this.root.querySelector(`[data-id="${CSS.escape(id)}"]`)?.scrollIntoView({ block: 'center' });
  }

  // ───────────────────────── mutations ─────────────────────────
  startEdit() { this.draft = structuredClone(this.pub); this.editing = true; this.undoStack = []; this.render(); }
  mutate(fn) { this.undoStack.push(JSON.stringify(this.draft)); fn(); this.render(); }
  undo() { if (!this.undoStack.length) return; this.draft = JSON.parse(this.undoStack.pop()); this.render(); if (this.sel && !this.get(this.sel)) { this.sel = null; this.render(); } this.toast('Undone'); }
  newId() { return 'n' + Date.now().toString(36) + Math.random().toString(36).slice(2, 6); }

  addChild(parentId) {
    if (!this.editing) this.startEdit();
    const parent = parentId && this.get(parentId);
    const id = this.newId();
    const plant = this.plant !== 'ALL' ? this.plant : (parent ? parent.plant : '*');
    this.mutate(() => {
      const sibs = this.draft.filter(x => x.parent === parentId);
      this.draft.push({ id, title: 'New position', unit: parent ? parent.unit : null, parent: parentId, people: [], plant, order: Math.max(0, ...sibs.map(s => s.order)) + 1 });
      if (parentId) this.collapsed.delete(parentId);
      this.sel = id;
    });
    if (this.view === 'chart') this.animated(() => this.focusCard(id, .8));
    setTimeout(() => { const t = this.$('fTitle'); if (t) { t.focus(); t.select(); } }, 30);
  }

  async pickPerson(nik) {
    const p = this.get(this.sel), e = this.person(nik), other = this.rolesOf(nik).filter(r => r.id !== p.id);
    let mode = 'add';
    if (other.length) {
      mode = await this.ask(`${e.name} already holds ${other.map(r => r.title).join(', ')}`,
        `Move them to “${p.title}”, or let them hold both positions (concurrent role)?`,
        [{ label: 'Cancel', value: null }, { label: 'Hold both', value: 'both' }, { label: 'Move here', value: 'move', primary: true }]);
      if (!mode) return;
    }
    this.mutate(() => {
      if (mode === 'move') other.forEach(r => { const d = this.get(r.id); d.people = d.people.filter(n => n !== nik); });
      this.get(p.id).people.push(nik);
    });
    this.toast(mode === 'move' ? `${e.name} moved from ${other[0].title}` : `${e.name} added`);
    this.$('fFind')?.focus();
  }

  async deletePos() {
    const p = this.get(this.sel), ks = this.draft.filter(x => x.parent === p.id), parent = p.parent && this.get(p.parent);
    const ok = await this.ask(`Delete “${p.title}”?`,
      ks.length ? `Its ${ks.length} direct report${ks.length > 1 ? 's' : ''} will report to ${parent ? `“${parent.title}”` : 'the top of the chart'} instead. People in it become unplaced.` : 'People in it become unplaced.',
      [{ label: 'Cancel', value: false }, { label: 'Delete', value: true, primary: true }]);
    if (!ok) return;
    this.mutate(() => { ks.forEach(x => x.parent = p.parent); this.draft.splice(this.draft.findIndex(x => x.id === p.id), 1); this.sel = null; });
  }

  async askUnit(p, newParent) {
    if (!newParent || !newParent.parent || !newParent.unit || newParent.unit === p.unit) return false;
    const n = this.allDesc(p.id).filter(id => this.get(id)?.unit === p.unit).length;
    return this.ask(`Also move to ${this.unit(newParent.unit).name}?`,
      `“${p.title}” is in ${this.unit(p.unit).name}. Moving it under “${newParent.title}” can also move it${n ? ` and ${n} position${n > 1 ? 's' : ''} below it` : ''} to ${this.unit(newParent.unit).name}.`,
      [{ label: 'Keep division', value: false }, { label: 'Move division too', value: true, primary: true }]);
  }
  applyUnit(id, oldUnit, newUnit) { [id, ...this.allDesc(id)].forEach(x => { const d = this.get(x); if (d && d.unit === oldUnit) d.unit = newUnit; }); }

  async reparent(id, parentId) {
    const p = this.get(id), np = parentId && this.get(parentId);
    const moveUnit = await this.askUnit(p, np);
    if (moveUnit === undefined) { this.render(); return; }
    this.mutate(() => {
      const d = this.get(id), old = d.unit;
      d.parent = parentId;
      d.order = Math.max(0, ...this.draft.filter(x => x.parent === parentId && x.id !== id).map(x => x.order)) + 1;
      if (moveUnit) this.applyUnit(id, old, np.unit);
    });
  }

  async move(id, targetId, zone) {
    const p = this.get(id), t = this.get(targetId);
    if (id === targetId || this.allDesc(id).includes(targetId)) { this.toast('A position cannot report to itself or to someone below it'); return; }
    const newParent = zone === 'in' ? t : (t.parent ? this.get(t.parent) : null);
    const moveUnit = await this.askUnit(p, newParent);
    if (moveUnit === undefined) return;
    const pid = newParent ? newParent.id : null;
    this.mutate(() => {
      const d = this.get(id), old = d.unit;
      d.parent = pid;
      if (zone === 'in') d.order = Math.max(0, ...this.draft.filter(x => x.parent === pid && x.id !== id).map(x => x.order)) + 1;
      else {
        const sibs = this.draft.filter(x => x.parent === pid && x.id !== id).sort((a, b) => a.order - b.order);
        const i = sibs.findIndex(x => x.id === targetId);
        sibs.splice(zone === 'before' ? i : i + 1, 0, d);
        sibs.forEach((x, k) => x.order = k + 1);
      }
      if (moveUnit) this.applyUnit(id, old, newParent.unit);
      if (pid) this.collapsed.delete(pid);
    });
    this.toast(zone === 'in' ? `“${p.title}” now reports to “${newParent.title}”` : 'Order changed');
  }

  async discard() {
    const n = this.undoStack.length;
    if (n && !(await this.ask('Discard the draft?', `${n} change${n > 1 ? 's' : ''} will be thrown away. The published chart stays as it is.`, [{ label: 'Keep editing', value: false }, { label: 'Discard', value: true, primary: true }]))) return;
    this.editing = false; this.draft = null; this.undoStack = []; this.render();
    if (this.sel && !this.get(this.sel)) { this.sel = null; this.render(); }
  }

  async publish(force) {
    if (!this.undoStack.length && !force) { this.editing = false; this.draft = null; this.render(); return; }
    const n = this.undoStack.length;
    if (!force && !(await this.ask('Publish changes?', `${n} change${n > 1 ? 's' : ''} become the official structure. Position and manager in profiles and the Directory are updated from the chart; people not on the chart lose theirs.`, [{ label: 'Cancel', value: false }, { label: 'Publish', value: true, primary: true }]))) return;
    const body = {
      version: this.version, force: !!force,
      positions: this.draft.map(p => ({ id: p.id, title: p.title, parentId: p.parent, unit: p.unit, plant: p.plant === '*' ? null : p.plant, sortOrder: p.order, people: p.people })),
    };
    const btn = this.$('publishBtn'); btn.disabled = true;
    let r;
    try { r = await this.dotnet.invokeMethodAsync('Publish', JSON.stringify(body)); }
    catch { r = { status: 0, message: 'Could not reach the server.' }; }
    finally { btn.disabled = false; }
    if (r.status === 409) {
      const go = await this.ask('Someone else published first', 'The chart was published by another admin after you started editing. Publishing now replaces their version with yours.', [{ label: 'Cancel', value: false }, { label: 'Replace with mine', value: true, primary: true }]);
      if (go) return this.publish(true);
      return;
    }
    if (r.status !== 200) { this.toast(r.message || 'Publish failed', 'Error'); return; }
    // Leave edit mode first so the host's update() loads the new chart (new boxes get server ids;
    // a selected new box simply deselects).
    this.editing = false; this.draft = null; this.undoStack = [];
    this.render();
    this.toast('Published · profiles and the Directory now use the chart', 'Success');
    this.dotnet.invokeMethodAsync('Refresh').catch(() => { });
  }

  // ───────────────────────── import ─────────────────────────
  matchName(s) {
    const nik = (s.match(/\b(\d{4,})\b/) || [])[1];
    if (nik && this.byNik[nik]) return { hit: this.byNik[nik] };
    const q = norm(s.replace(/\(.*?\)/g, ''));
    const act = this.people.filter(e => e.active);
    const exact = act.filter(e => norm(e.name) === q);
    if (exact.length === 1) return { hit: exact[0] };
    if (exact.length > 1) return { why: `${exact.length} people have this name — add the NIK in brackets` };
    const pre = act.filter(e => norm(e.name).startsWith(q));
    if (q.length >= 3 && pre.length === 1) return { hit: pre[0] };
    return { why: 'No active employee with this name — fix it here or assign later' };
  }
  splitNames(s) { s = (s || '').trim(); if (!s || /^\(?(vacant|kosong)\)?$|^-$/i.test(s)) return []; return s.split(/\s*[,;]\s*/).filter(Boolean); }
  parseIndented(text) {
    const roots = [], stack = [];
    text.split(/\r?\n/).forEach(line => {
      if (!line.trim()) return;
      const ind = line.match(/^[\t ]*/)[0].replace(/\t/g, '  ').length;
      const [title, names] = line.trim().replace(/^[-•*]\s*/, '').split(/\s+[—–:-]\s+|\s*[—–]\s*/);
      const node = { title: title.trim(), names: this.splitNames(names), kids: [], ind };
      while (stack.length && stack[stack.length - 1].ind >= ind) stack.pop();
      (stack.length ? stack[stack.length - 1].kids : roots).push(node);
      stack.push(node);
    });
    return roots;
  }
  parseCsv(text) {
    const sep = /\t/.test(text.split(/\r?\n/)[0]) ? '\t' : ',';
    const cells = l => sep === '\t' ? l.split('\t').map(c => c.trim()) : (l.match(/("([^"]*(?:""[^"]*)*)"|[^,]*)(,|$)/g) || []).map(c => c.replace(/,$/, '').replace(/^"|"$/g, '').replace(/""/g, '"').trim());
    const rows = text.trim().split(/\r?\n/).map(cells);
    rows.shift();
    const nodes = rows.filter(r => r[0]).map(r => ({ title: r[0], names: this.splitNames(r[1]), parentTitle: r[2], unitName: r[3], kids: [] }));
    const roots = [];
    nodes.forEach(n => { const par = n.parentTitle && nodes.find(m => m !== n && norm(m.title) === norm(n.parentTitle)); (par ? par.kids : roots).push(n); });
    return roots;
  }
  unitByName(s) { const q = norm(s); if (!q) return undefined; return Object.entries(this.units).find(([k, u]) => norm(k) === q || norm(u.name) === q)?.[0]; }

  openImport() {
    const vis = this.S().filter(p => this.inPlant(p)).sort((a, b) => this.ancestors(a).length - this.ancestors(b).length || a.title.localeCompare(b.title));
    const posOpts = `<option value="">Top of the chart</option>` + vis.map((p, i) => `<option value="${esc(p.id)}" ${i === 0 ? 'selected' : ''}>${esc(p.title)}</option>`).join('');
    const unitOpts = `<option value="">No division</option>` + Object.entries(this.units).map(([k, u]) => `<option value="${esc(k)}">${esc(u.name)}</option>`).join('');
    const ph = this.impMode === 'list'
      ? 'Plant Manager — Name\n  Finance Head — Name\n    Finance Staff — Name, Name (123456)\n  HR Head — (vacant)'
      : 'Position,Name,Reports to,Division\nPlant Manager,Name,,Operational\nFinance Head,Name,Plant Manager,DAFT';
    this.openModal(`<h3>Import structure</h3>
      <p>Paste an indented list (one position per line, people after “—”) or a CSV/Excel copy. Add the NIK in brackets when two people share a name. Nothing changes until you add it to the draft and publish.</p>
      <div class="oc-tabs2"><button data-imp="list" class="${this.impMode === 'list' ? 'on' : ''}">Indented list</button><button data-imp="csv" class="${this.impMode === 'csv' ? 'on' : ''}">CSV / Excel</button></div>
      <div class="oc-imp">
        <div class="oc-impl">
          <textarea data-oc="impText" spellcheck="false" placeholder="${esc(ph)}">${esc(this.impText || '')}</textarea>
          <div class="oc-opts"><div class="oc-pf"><label>Attach under</label><select data-oc="impUnder">${posOpts}</select></div><div class="oc-pf"><label>Division</label><select data-oc="impUnit">${unitOpts}</select></div></div>
        </div>
        <div class="oc-pv" data-oc="impPv"></div>
      </div>
      <div class="oc-acts"><button class="oc-btn" data-close>Cancel</button><button class="oc-btn primary" data-oc="impGo"><i data-lucide="git-pull-request-arrow"></i>Add to draft</button></div>`, true);
    const upd = () => { this.impText = this.$('impText').value; this.previewImport(); };
    this.$('impText').addEventListener('input', upd); this.$('impUnder').addEventListener('change', upd);
    this.root.querySelectorAll('[data-imp]').forEach(b => b.onclick = () => { this.impMode = b.dataset.imp; this.openImport(); });
    this.$('impGo').onclick = () => this.applyImport();
    upd();
    this.$('impText').focus();
  }
  parseImport() { return (this.impMode === 'list' ? this.parseIndented(this.$('impText').value) : this.parseCsv(this.$('impText').value)); }
  previewImport() {
    const roots = this.parseImport();
    const under = this.$('impUnder').value || null;
    let nPos = 0, nNew = 0, nPeople = 0, nBad = 0;
    const lines = [];
    const walk = (n, d, parentId, known) => {
      nPos++;
      const existing = known && this.S().find(p => p.parent === parentId && norm(p.title) === norm(n.title));
      if (!existing) nNew++;
      const matched = [];
      const chips = n.names.length ? n.names.map(s => { const m = this.matchName(s); nPeople++; if (!m.hit) nBad++; else matched.push(m.hit.nik); return m.hit ? `<span class="oc-chip" title="NIK ${esc(m.hit.nik)}">${esc(m.hit.name)}</span>` : `<span class="oc-chip bad" title="${esc(m.why)}">⚠ ${esc(s)}</span>`; }).join('') : '<span class="oc-chip vac">vacant</span>';
      const gone = existing ? existing.people.filter(k => !matched.includes(k)).map(k => this.person(k).name) : [];
      lines.push(`<div class="oc-pl" style="--d:${d}"><b>${esc(n.title)}</b>${existing ? '<span class="oc-chip upd">exists · update</span>' : '<span class="oc-chip new">new</span>'}${chips}${gone.length ? `<span class="oc-chip vac" title="Will be removed from this position">replaces ${esc(gone.join(', '))}</span>` : ''}</div>`);
      n.kids.forEach(k => walk(k, d + 1, existing ? existing.id : null, !!existing));
    };
    roots.forEach(r => walk(r, 0, under, true));
    this.$('impPv').innerHTML = `<div class="oc-pvsum"><span><b>${nPos}</b> positions (${nNew} new)</span><span><b>${nPeople - nBad}</b> people matched</span>${nBad ? `<span class="oc-warn"><b>${nBad}</b> not found</span>` : ''}</div>
      <div class="oc-muted oc-under">Under: ${under ? esc(this.get(under).title) : 'top of the chart'}</div>${lines.join('') || '<div class="oc-muted">Nothing to import yet.</div>'}`;
    this.$('impGo').disabled = !nPos;
  }
  applyImport() {
    const roots = this.parseImport();
    const under = this.$('impUnder').value || null, unit = this.$('impUnit').value || null;
    this.closeModal();
    if (!roots.length) return;
    if (!this.editing) this.startEdit();
    const underPos = under && this.get(under);
    const plant = this.plant !== 'ALL' ? this.plant : (underPos ? underPos.plant : '*');
    let bad = 0, first = null;
    this.mutate(() => {
      const walk = (n, parentId) => {
        let p = this.draft.find(x => x.parent === parentId && norm(x.title) === norm(n.title));
        if (!p) {
          p = { id: this.newId(), title: n.title.slice(0, 200), unit: this.unitByName(n.unitName) ?? unit, parent: parentId, people: [], plant, order: 100 + this.draft.length };
          this.draft.push(p);
        }
        p.people = [];
        n.names.forEach(s => { const m = this.matchName(s); if (m.hit && !p.people.includes(m.hit.nik)) p.people.push(m.hit.nik); if (!m.hit) bad++; });
        first = first || p.id;
        if (parentId) this.collapsed.delete(parentId);
        n.kids.forEach(k => walk(k, p.id));
      };
      roots.forEach(r => walk(r, under));
      this.sel = first;
    });
    this.impText = '';
    this.toast(`Imported into the draft${bad ? ` · ${bad} name${bad > 1 ? 's' : ''} not matched — assign them in the panel` : ''}`, bad ? 'Warning' : 'Success');
    if (this.view === 'chart') this.animated(() => this.focusCard(first, .7));
  }

  exportCsv() {
    const q = c => `"${String(c ?? '').replace(/"/g, '""')}"`;
    const rows = [['Position', 'Name', 'Reports to', 'Division', 'Plant', 'NIK']];
    const walk = p => {
      const parent = p.parent && this.get(p.parent);
      rows.push([p.title, p.people.map(n => this.person(n).name).join(', '), parent ? parent.title : '', p.unit ? this.unit(p.unit).name : '', p.plant === '*' ? 'Every plant' : p.plant, p.people.join(', ')]);
      this.kids(p.id).forEach(walk);
    };
    this.kids(null).forEach(walk);
    const csv = '﻿' + rows.map(r => r.map(q).join(',')).join('\r\n');
    const b64 = btoa(unescape(encodeURIComponent(csv)));
    const d = new Date(), pad = n => String(n).padStart(2, '0');
    window.ipop.download(b64, `org-chart-${this.plant.toLowerCase()}-${d.getFullYear()}${pad(d.getMonth() + 1)}${pad(d.getDate())}.csv`);
  }

  // ───────────────────────── modal / toast ─────────────────────────
  openModal(html, big) { const m = this.$('modal'); m.className = 'oc-modal' + (big ? ' big' : ''); m.innerHTML = html; this.$('scrim').classList.add('on'); this.icons(); }
  closeModal(v) { this.$('scrim').classList.remove('on'); if (this.modalResolve) { const r = this.modalResolve; this.modalResolve = null; r(v); } }
  modalOpen() { return this.$('scrim').classList.contains('on'); }
  ask(title, body, buttons) {
    if (this.modalResolve) this.closeModal(undefined);
    this.openModal(`<h3>${esc(title)}</h3><p>${esc(body)}</p><div class="oc-acts">${buttons.map((b, i) => `<button class="oc-btn ${b.primary ? 'primary' : ''}" data-ans="${i}">${esc(b.label)}</button>`).join('')}</div>`);
    this.root.querySelectorAll('[data-ans]').forEach(b => b.onclick = () => this.closeModal(buttons[+b.dataset.ans].value));
    this.root.querySelector('[data-ans].primary')?.focus();
    return new Promise(r => this.modalResolve = r);
  }
  toast(msg, kind = 'Info') { this.dotnet.invokeMethodAsync('Toast', msg, kind).catch(() => { }); }

  /** Escape from the page: modal first, then the side panel. Returns true when something closed. */
  escape() {
    if (this.modalOpen()) { this.closeModal(undefined); return true; }
    if (this.sel) { this.sel = null; this.render(); return true; }
    return false;
  }

  // ───────────────────────── events ─────────────────────────
  bind() {
    const r = this.root, ac = this.ac = new AbortController(), o = { signal: ac.signal };
    r.addEventListener('click', e => this.onClick(e), o);
    this.$('plant').addEventListener('change', e => { this.plant = e.target.value; this.sel = null; this.render(); this.animated(() => this.fit()); }, o);
    this.$('q').addEventListener('input', e => {
      this.query = e.target.value;
      this.reindex();
      const h = this.S().filter(p => this.idx.hits.has(p.id));
      h.forEach(p => this.ancestors(p).forEach(a => this.collapsed.delete(a.id)));
      this.$('qCnt').textContent = norm(this.query).length >= 2 ? `${h.length} found` : '';
      this.render();
      if (h[0] && this.view === 'chart') this.animated(() => this.focusCard(h[0].id, .8));
      else if (h[0]) r.querySelector(`[data-id="${CSS.escape(h[0].id)}"]`)?.scrollIntoView({ block: 'center' });
    }, o);
    this.$('q').addEventListener('keydown', e => { if (e.key === 'Enter') { const h = this.S().find(p => this.idx.hits.has(p.id)); if (h) { this.sel = h.id; this.render(); } } }, o);
    document.addEventListener('keydown', e => {
      if (!r.isConnected || !r.offsetParent) return;
      const typing = e.target.matches?.('input,textarea,select');
      if ((e.ctrlKey || e.metaKey) && e.key === 'z' && this.editing && !typing && !this.modalOpen()) { e.preventDefault(); this.undo(); }
      if (this.view === 'chart' && !e.ctrlKey && !e.metaKey && !e.altKey && !typing && !this.modalOpen()) {
        if (e.key === '+' || e.key === '=') this.animated(() => this.zoomAt(this.zoom * 1.3));
        if (e.key === '-') this.animated(() => this.zoomAt(this.zoom / 1.3));
        if (e.key === '0') this.animated(() => this.fit());
      }
      if (e.key === 'Escape' && (this.modalOpen() || this.sel)) { e.preventDefault(); this.escape(); return; }
      if (e.key === 'Enter' && e.target.classList?.contains('oc-pc')) { this.sel = e.target.dataset.id; this.render(); }
    }, o);
    this.$('scrim').addEventListener('mousedown', e => { if (e.target === this.$('scrim')) this.closeModal(undefined); }, o);

    // drag & drop (chart boxes and list rows share the same rules)
    const zoneOf = (el, e) => {
      const b = el.getBoundingClientRect(), y = el.dataset.axis === 'y';
      const f = y ? (e.clientY - b.top) / b.height : (e.clientX - b.left) / b.width;
      return f < .25 ? 'before' : f > .75 ? 'after' : 'in';
    };
    const clearDrop = () => r.querySelectorAll('.drop-in,.drop-before,.drop-after').forEach(x => x.classList.remove('drop-in', 'drop-before', 'drop-after'));
    r.addEventListener('dragstart', e => {
      const el = e.target.closest?.('.oc-pc,.oc-tr'); if (!el || !this.editing) return;
      this.dragId = el.dataset.id; el.classList.add('dragging'); e.dataTransfer.effectAllowed = 'move'; e.dataTransfer.setData('text/plain', this.dragId);
    }, o);
    r.addEventListener('dragend', () => { r.querySelectorAll('.dragging').forEach(x => x.classList.remove('dragging')); clearDrop(); this.dragId = null; }, o);
    r.addEventListener('dragover', e => {
      const el = e.target.closest?.('.oc-pc,.oc-tr'); if (!el || !this.dragId || el.dataset.id === this.dragId) return;
      e.preventDefault(); clearDrop();
      const z = zoneOf(el, e);
      el.classList.add(z === 'in' ? 'drop-in' : 'drop-' + z);
    }, o);
    r.addEventListener('drop', e => {
      const el = e.target.closest?.('.oc-pc,.oc-tr'); if (!el || !this.dragId) return;
      e.preventDefault(); const z = zoneOf(el, e); clearDrop();
      this.move(this.dragId, el.dataset.id, z);
    }, o);

    // Scroll wheel / trackpad pinch = zoom at the cursor. Drag anywhere = move (in edit mode, dragging a box moves the position instead).
    const c = this.$('canvas'); let sx, sy, stx, sty, on = false;
    c.addEventListener('wheel', e => {
      e.preventDefault();
      const b = c.getBoundingClientRect();
      this.zoomAt(this.zoom * Math.exp(-e.deltaY * (e.ctrlKey ? .01 : .0018)), e.clientX - b.left, e.clientY - b.top);
    }, { passive: false, signal: ac.signal });
    c.addEventListener('pointerdown', e => {
      if (e.button !== 0 || e.target.closest('button,.oc-empty') || (this.editing && e.target.closest('.oc-pc'))) return;
      on = true; this.panned = false; sx = e.clientX; sy = e.clientY; stx = this.tx; sty = this.ty;
    }, o);
    window.addEventListener('pointermove', e => {
      if (!on) return;
      const dx = e.clientX - sx, dy = e.clientY - sy;
      if (!this.panned && Math.hypot(dx, dy) < 4) return;
      this.panned = true; c.classList.add('panning');
      this.tx = stx + dx; this.ty = sty + dy; this.applyView();
    }, o);
    window.addEventListener('pointerup', () => { on = false; c.classList.remove('panning'); setTimeout(() => this.panned = false, 0); }, o);
    c.addEventListener('dblclick', e => {
      if (e.target.closest('button')) return;
      const card = e.target.closest('.oc-pc');
      if (card) { this.animated(() => this.focusCard(card.dataset.id, Math.max(1, this.zoom))); return; }
      const b = c.getBoundingClientRect();
      this.animated(() => this.zoomAt(this.zoom * 1.6, e.clientX - b.left, e.clientY - b.top));
    }, o);
  }

  onClick(e) {
    if (this.panned) { this.panned = false; return; }  // the end of a drag-to-move is not a click
    const t = e.target.closest('[data-toggle],[data-add],[data-act],[data-remove],[data-pick],[data-goto],[data-view],[data-close],[data-z],.oc-pc,.oc-tr');
    if (!t || !this.root.contains(t)) return;
    const d = t.dataset;
    if (d.toggle) { const id = d.toggle; this.keepAnchor(id, () => { this.collapsed.has(id) ? this.collapsed.delete(id) : this.collapsed.add(id); this.render(); }); return; }
    if (d.add) { this.addChild(d.add); return; }
    if (d.remove) { const p = this.get(this.sel); this.mutate(() => p.people = p.people.filter(n => n !== d.remove)); return; }
    if (d.pick) { this.pickPerson(d.pick); return; }
    if (d.goto) { this.sel = d.goto; this.reveal(this.sel); return; }
    if (d.view) { this.view = d.view; this.render(); if (this.view === 'chart') requestAnimationFrame(() => this.fit()); return; }
    if (d.z) { const z = d.z; this.animated(() => z === 'fit' ? this.fit() : this.zoomAt(z === 'in' ? this.zoom * 1.3 : z === 'out' ? this.zoom / 1.3 : 1)); return; }
    if ('close' in d) { this.closeModal(undefined); return; }
    switch (d.act) {
      case 'close': this.sel = null; this.render(); return;
      case 'edit': this.startEdit(); return;
      case 'delete': this.deletePos(); return;
      case 'import': this.openImport(); return;
      case 'export': this.exportCsv(); return;
      case 'undo': this.undo(); return;
      case 'discard': this.discard(); return;
      case 'publish': this.publish(false); return;
      case 'expandAll': this.collapsed.clear(); this.render(); this.animated(() => this.fit()); return;
      case 'collapseAll': this.collapseDefault(); this.render(); this.animated(() => this.fit()); return;
      case 'addTop': { const top = this.kids(null)[0]; this.addChild(top ? top.id : null); return; }
    }
    if (t.classList.contains('oc-pc') || t.classList.contains('oc-tr')) { this.sel = d.id; this.render(); this.clearPanel(d.id); }
  }

  /** Slide the canvas so a selected box is not hidden behind the side panel. */
  clearPanel(id) {
    if (this.view !== 'chart') return;
    const el = this.root.querySelector(`.oc-pc[data-id="${CSS.escape(id)}"]`); if (!el) return;
    const c = this.$('canvas').getBoundingClientRect(), r = el.getBoundingClientRect();
    const over = r.right - (c.right - this.$('panel').offsetWidth - 24);
    if (over > 0) this.animated(() => { this.tx -= over; this.applyView(); });
  }

  dispose() { this.ac.abort(); clearTimeout(this.animT); this.root.innerHTML = ''; }
}

const editors = new Map();
let nextId = 0;
export function mount(root, dotnet, json) { const id = ++nextId; editors.set(id, new Editor(root, dotnet, JSON.parse(json))); return id; }
/** New published data (after Publish or a directoryChanged). An open draft is kept on its old base version,
    so publishing it after someone else did still asks before replacing their chart. */
export function update(id, json) {
  const ed = editors.get(id); if (!ed) return;
  const data = JSON.parse(json);
  if (ed.editing) { ed.photos = data.photos || ed.photos; return; }
  ed.load(data, false);
}
export function dispose(id) { editors.get(id)?.dispose(); editors.delete(id); }
