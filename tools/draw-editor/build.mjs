// Builds the lazy-loaded drawing bundle into src/IPop.Web/wwwroot/js/notes/draw/.
//   entry:  draw.js (ESM) + code-split chunks; loaded with import() only on pages that have a drawing or are a whiteboard
//   css:    Excalidraw's own stylesheet, restyled by draw-theme.css (iPop colours, no Excalidraw branding)
//   fonts:  only the families the English UI and the hand-drawn look need (the CJK font alone is 12 MB and is left out)
import { build } from 'esbuild';
import { cpSync, mkdirSync, rmSync, readdirSync, existsSync, copyFileSync, readFileSync, writeFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';

const here = dirname(fileURLToPath(import.meta.url));
const out = join(here, '..', '..', 'src', 'IPop.Web', 'wwwroot', 'js', 'notes', 'draw');
const pkg = join(here, 'node_modules', '@excalidraw', 'excalidraw', 'dist', 'prod');
const FONT_FAMILIES = ['Excalifont', 'Virgil', 'Nunito', 'Assistant', 'Cascadia', 'Liberation', 'Lilita', 'ComicShanns'];

rmSync(out, { recursive: true, force: true });
mkdirSync(out, { recursive: true });

// Excalidraw can turn Mermaid text into shapes (a dialog we do not offer). It is a dynamic import of ~3 MB of diagram engines
// that nobody would ever download, so it is replaced by an empty module.
const noMermaid = { name: 'no-mermaid', setup(b) {
  b.onResolve({ filter: /^@excalidraw\/mermaid-to-excalidraw/ }, () => ({ path: 'mermaid-stub', namespace: 'stub' }));
  b.onLoad({ filter: /.*/, namespace: 'stub' }, () => ({ contents: 'export const parseMermaidToExcalidraw = async () => { throw new Error("Mermaid import is not available"); }; export default {};', loader: 'js' }));
} };

const r = await build({
  plugins: [noMermaid],
  entryPoints: [join(here, 'src', 'draw.js')],
  bundle: true, format: 'esm', splitting: true, minify: true, target: 'es2020', outdir: out,
  entryNames: 'draw', chunkNames: 'c-[hash]',
  define: { 'process.env.NODE_ENV': '"production"', 'process.env.IS_PREACT': '"false"' },
  loader: { '.woff2': 'file', '.woff': 'file', '.png': 'file', '.svg': 'file' },
  metafile: true, logLevel: 'error',
});

// Excalidraw's UI strings: English only (the app is English); the other locales would add ~3 MB of chunks that are never used.
for (const f of readdirSync(out)) {
  if (/^(?:[a-z]{2}(?:-[A-Z]{2})?|percentages)-.*\.js$/.test(f) && f !== 'draw.js') rmSync(join(out, f), { force: true });
}

// Excalidraw falls back to https://esm.sh/... for any asset it cannot find next to the bundle (the CJK font that we leave out).
// An intranet app must never call out to the internet, so the fallback is pointed at this folder instead: a missing font is a
// local 404 and the browser's own fallback font is used.
let fallbackPatched = 0;
for (const f of readdirSync(out)) {
  if (!f.endsWith('.js')) continue;
  const path = join(out, f);
  const src = readFileSync(path, 'utf8');
  // The template literal nests another one, so a regex is fragile: cut from the opening `https://esm.sh/ to the closing /dist/prod/`.
  const start = src.indexOf('`https://esm.sh/');
  let next = src;
  if (start >= 0) {
    const endMarker = '/dist/prod/`';
    const end = src.indexOf(endMarker, start);
    if (end > start && end - start < 200) next = src.slice(0, start) + '(new URL("./",import.meta.url).href)' + src.slice(end + endMarker.length);
  }
  if (next !== src) { writeFileSync(path, next); fallbackPatched++; }
}
if (!fallbackPatched) throw new Error('Excalidraw asset fallback (esm.sh) was not found to patch: the package changed, review build.mjs');
for (const f of readdirSync(out)) if (f.endsWith('.js') && /https:\/\/esm\.sh/.test(readFileSync(join(out, f), 'utf8'))) throw new Error(`${f} still references esm.sh`);

mkdirSync(join(out, 'fonts'), { recursive: true });
for (const fam of FONT_FAMILIES) {
  const src = join(pkg, 'fonts', fam);
  if (existsSync(src)) cpSync(src, join(out, 'fonts', fam), { recursive: true });
}
copyFileSync(join(pkg, 'index.css'), join(out, 'excalidraw.css'));
if (existsSync(join(here, 'src', 'draw-theme.css'))) copyFileSync(join(here, 'src', 'draw-theme.css'), join(out, 'draw-theme.css'));

const files = readdirSync(out, { recursive: true }).filter(f => /\.(js|css|woff2)$/.test(String(f)));
const total = files.reduce((s, f) => s + readFileSync(join(out, String(f))).length, 0);
writeFileSync(join(out, 'manifest.json'), JSON.stringify({ entry: 'draw.js', files: files.length }, null, 2));
console.log(`draw bundle: ${files.length} files, ${(total / 1048576).toFixed(2)} MB -> ${out}`);
