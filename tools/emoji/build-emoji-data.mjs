// Builds src/IPop.Web/wwwroot/data/emoji/emoji-<version>.json for the shared EmojiPicker.
//
//   node tools/emoji/build-emoji-data.mjs
//
// Sources (pinned, both under the Unicode License v3; see wwwroot/data/emoji/LICENSE.txt):
//   - emoji-test.txt, Unicode Emoji 18.0: the complete list of fully-qualified emoji, their
//     group (picker tab), order, emoji version and CLDR short name.
//   - CLDR 48.2.3 English annotations (+ derived annotations): search keywords.
// Both were final/stable releases when pinned: Unicode 18.0.0 is the final data directory
// (Public/18.0.0, same bytes as Public/emoji/latest), CLDR 48.2.3 a non-prerelease tag
// (2026-09-30). The script refuses an emoji-test.txt whose header names another version.
//
// Output row: { e: glyph, n: name, k?: "extra keywords", c: category index, v: emoji version,
//               t?: [5 skin-tone variants, light to dark], tv?: tone version when newer than v }
// Skin-tone variants are folded into their base emoji and kept only when all five uniform-tone
// fully-qualified sequences exist, so every tone the picker offers is a real, valid sequence.
import { writeFileSync, mkdirSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const EMOJI_VERSION = "18.0";
const CLDR_VERSION = "48.2.3";
const EMOJI_TEST = `https://www.unicode.org/Public/${EMOJI_VERSION}.0/emoji/emoji-test.txt`;
const CLDR = `https://raw.githubusercontent.com/unicode-org/cldr-json/${CLDR_VERSION}/cldr-json`;
const ANNOTATIONS = `${CLDR}/cldr-annotations-full/annotations/en/annotations.json`;
const DERIVED = `${CLDR}/cldr-annotations-derived-full/annotationsDerived/en/annotations.json`;

const CATEGORIES = ["smileys-people", "animals-nature", "food-drink", "activities", "travel-places", "objects", "symbols", "flags"];
const GROUP_TO_CATEGORY = {
  "Smileys & Emotion": 0, "People & Body": 0, "Animals & Nature": 1, "Food & Drink": 2,
  "Activities": 3, "Travel & Places": 4, "Objects": 5, "Symbols": 6, "Flags": 7,
};
const TONES = ["1F3FB", "1F3FC", "1F3FD", "1F3FE", "1F3FF"];

async function get(url) {
  const res = await fetch(url);
  if (!res.ok) throw new Error(`${url}: HTTP ${res.status}`);
  return res.text();
}

const [testTxt, annJson, derJson] = await Promise.all([get(EMOJI_TEST), get(ANNOTATIONS), get(DERIVED)]);
const annotations = { ...JSON.parse(derJson).annotationsDerived.annotations, ...JSON.parse(annJson).annotations.annotations };
const keywordsFor = glyph => {
  const a = annotations[glyph] ?? annotations[glyph.replaceAll("️", "")];
  return a ? [...(a.default ?? []), ...(a.tts ?? [])] : [];
};

const versionLine = testTxt.match(/^# Version: ([\d.]+)/m);
if (!versionLine || versionLine[1] !== EMOJI_VERSION) throw new Error("unexpected emoji-test.txt version");

const bases = [];               // in file order
const baseByKey = new Map();    // FE0F-less, tone-less code point key -> base row
const toned = [];               // [key, toneIndex, glyph]
let group = null;
for (const line of testTxt.split("\n")) {
  const g = line.match(/^# group: (.+)$/);
  if (g) { group = g[1].trim(); continue; }
  const m = line.match(/^([0-9A-F ]+?)\s*; fully-qualified\s*# (\S+) E([\d.]+) (.+)$/);
  if (!m || !(group in GROUP_TO_CATEGORY)) continue;
  const cps = m[1].trim().split(/\s+/);
  const glyph = String.fromCodePoint(...cps.map(h => parseInt(h, 16)));
  const key = cps.filter(c => c !== "FE0F" && !TONES.includes(c)).join(" ");
  const tones = cps.filter(c => TONES.includes(c));
  if (tones.length === 0) {
    const row = { e: glyph, n: m[4].trim(), c: GROUP_TO_CATEGORY[group], v: Number(m[3]) };
    bases.push(row);
    if (!baseByKey.has(key)) baseByKey.set(key, row);
  } else if (tones.every(t => t === tones[0])) {
    toned.push([key, TONES.indexOf(tones[0]), glyph, Number(m[3])]);
  }
}

for (const [key, tone, glyph, version] of toned) {
  const base = baseByKey.get(key);
  if (!base) continue;
  base.tones ??= [];
  base.tones[tone] ??= glyph;
  base.toneVersion = Math.max(base.toneVersion ?? 0, version);
}

const rows = bases.map(b => {
  const nameWords = new Set(b.n.toLowerCase().split(/[^\p{L}\p{N}]+/u).filter(Boolean));
  const extra = [...new Set(keywordsFor(b.e).flatMap(k => k.toLowerCase().split(/[^\p{L}\p{N}]+/u)).filter(w => w && !nameWords.has(w)))];
  const row = { e: b.e, n: b.n, c: b.c, v: b.v };
  if (extra.length) row.k = extra.join(" ");
  if (b.tones && TONES.every((_, i) => b.tones[i])) {
    row.t = b.tones;
    if (b.toneVersion > b.v) row.tv = b.toneVersion;
  }
  return row;
});

const out = {
  version: EMOJI_VERSION,
  cldr: CLDR_VERSION,
  source: "Unicode emoji-test.txt + CLDR English annotations, Unicode License v3 (see LICENSE.txt)",
  categories: CATEGORIES,
  emoji: rows,
};
const here = dirname(fileURLToPath(import.meta.url));
const target = join(here, "..", "..", "src", "IPop.Web", "wwwroot", "data", "emoji", `emoji-${EMOJI_VERSION}.json`);
mkdirSync(dirname(target), { recursive: true });
writeFileSync(target, JSON.stringify(out));
console.log(`${rows.length} emoji, ${rows.filter(r => r.t).length} with skin tones -> ${target}`);
