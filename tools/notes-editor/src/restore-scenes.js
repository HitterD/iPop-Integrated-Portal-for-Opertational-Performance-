// Restoring a version must bring the drawings back too, not just the text. Every scene ("draw:<id>") of the restored version is
// written over the live one, and anything in the live scene that the version did not have is marked deleted (kept as a
// tombstone, so people who still have the page open see it disappear instead of it coming back from their copy).
//
// Versions are compared the way the live sync is: an element's `version` only ever grows, so a restored element is written
// with a version ABOVE both copies, otherwise the other clients would keep their newer copy and the restore would not stick.

const nextVersion = (a, b) => Math.max(a?.version ?? 0, b?.version ?? 0) + 1;
const nonce = () => Math.floor(Math.random() * 2 ** 31);

/// Returns how many elements were written. `live` and `old` are Y.Docs.
export function restoreScenes(live, old) {
  let written = 0;
  // "draw:<id>" = Excalidraw scenes, "wb:<id>" = iPop canvas scenes; both use the same version / versionNonce rule.
  const names = new Set([...live.share.keys(), ...old.share.keys()].filter(k => k.startsWith('draw:') || k.startsWith('wb:')));
  for (const name of names) {
    const liveMap = live.getMap(name), oldMap = old.getMap(name);
    const keys = new Set([...liveMap.keys(), ...oldMap.keys()]);
    for (const id of keys) {
      const was = oldMap.get(id), now = liveMap.get(id);
      if (was) {
        // present in the restored version: bring it back (undeleted), above every version that exists now
        const same = now && !now.isDeleted && JSON.stringify(strip(now)) === JSON.stringify(strip(was));
        if (same) continue;
        liveMap.set(id, { ...was, isDeleted: false, version: nextVersion(was, now), versionNonce: nonce() });
        written++;
      } else if (now && !now.isDeleted) {
        // drawn after the restored version: remove it
        liveMap.set(id, { ...now, isDeleted: true, version: nextVersion(now), versionNonce: nonce() });
        written++;
      }
    }
  }
  return written;
}

const strip = e => { const { version, versionNonce, updated, ...rest } = e; return rest; };
