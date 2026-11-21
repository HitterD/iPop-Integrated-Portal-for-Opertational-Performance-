// A drawing is a set of Excalidraw elements. In the shared Yjs document each element is ONE entry of a Y.Map
// (element id -> element JSON), so two people drawing at the same time touch different keys and both strokes survive;
// two people editing the SAME element resolve by Excalidraw's own rule (higher version wins, nonce breaks ties).
// Everything here is pure (no React, no DOM) so it can be tested without a browser.

const KEEP = new Set(['id', 'type', 'version', 'versionNonce', 'isDeleted', 'index']);

/// Whether `incoming` should replace `current` (same id). Excalidraw's reconciliation rule.
export function isNewer(current, incoming) {
  if (!current) return true;
  if (incoming.version !== current.version) return incoming.version > current.version;
  return incoming.versionNonce < current.versionNonce;   // deterministic tie-break, identical on every client
}

/// Stripped copy that is safe to store and send: plain JSON only (no functions, no undefined).
export function plain(el) { return JSON.parse(JSON.stringify(el)); }

/// Writes the changes between `prev` (what the map held) and `elements` (what the editor has now) into the Y.Map.
/// Returns the number of entries written. Run it inside doc.transact(…, origin) so remote observers can ignore it.
export function pushElements(ymap, elements, prev = new Map()) {
  let written = 0;
  for (const el of elements) {
    const before = prev.get(el.id);
    if (before && before.version === el.version && before.versionNonce === el.versionNonce) continue;
    ymap.set(el.id, plain(el));
    prev.set(el.id, { version: el.version, versionNonce: el.versionNonce });
    written++;
  }
  return written;
}

/// The current scene from the map, including deleted elements (they must stay so a late joiner doesn't resurrect them).
export function readElements(ymap) {
  const out = [];
  ymap.forEach(v => out.push(v));
  // Excalidraw orders by fractional index; fall back to insertion-independent id order so every client agrees.
  out.sort((a, b) => (a.index ?? '').localeCompare(b.index ?? '') || String(a.id).localeCompare(String(b.id)));
  return out;
}

/// Yjs decides a same-key conflict by client id, NOT by Excalidraw's version, so after remote changes arrive the shared map
/// may hold an OLDER copy of an element than this client's scene. For every changed key where the local copy is newer,
/// write it back; all clients do the same, so the map converges on the highest version. Returns how many were re-asserted.
export function reassert(ymap, localById, changedKeys) {
  let n = 0;
  for (const key of changedKeys) {
    const local = localById.get(key);
    if (!local) continue;
    const remote = ymap.get(key);
    if (!remote || isNewer(remote, local)) { ymap.set(key, plain(local)); n++; }
  }
  return n;
}

/// Elements people can see (deleted ones are kept only as tombstones).
export const visible = elements => elements.filter(e => !e.isDeleted);

/// Tombstones older than `keepVersions` edits are dropped when the map grows large, so a long-lived drawing doesn't carry
/// every stroke ever erased. A tombstone is only safe to drop when nobody can still send an older live copy; callers pass
/// `compact: true` from the periodic snapshot (all clients have the tombstone by then).
export function compact(ymap, { maxTombstones = 500 } = {}) {
  const dead = [];
  ymap.forEach((v, k) => { if (v.isDeleted) dead.push(k); });
  if (dead.length <= maxTombstones) return 0;
  const drop = dead.slice(0, dead.length - maxTombstones);
  drop.forEach(k => ymap.delete(k));
  return drop.length;
}

/// Light check that a stored value is an element we can render (a corrupt entry must never crash the page).
export function isElement(v) {
  return !!v && typeof v === 'object' && typeof v.id === 'string' && typeof v.type === 'string' && Number.isFinite(v.version);
}

export const pickMeta = el => Object.fromEntries(Object.entries(el).filter(([k]) => KEEP.has(k)));
