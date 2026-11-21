// Builds iPop's own whiteboard into src/IPop.Web/wwwroot/js/notes/canvas/ (no React, no Excalidraw).
// Loaded lazily, only on pages that show a drawing or are a whiteboard. Deterministic output (no dates), checked in CI.
import { build } from 'esbuild';
import { mkdirSync, rmSync, copyFileSync, readFileSync, readdirSync, writeFileSync, statSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';
import { gzipSync } from 'node:zlib';

const here = dirname(fileURLToPath(import.meta.url));
const out = join(here, '..', '..', 'src', 'IPop.Web', 'wwwroot', 'js', 'notes', 'canvas');
rmSync(out, { recursive: true, force: true });
mkdirSync(out, { recursive: true });

await build({
  entryPoints: [join(here, 'src', 'canvas', 'index.js')],
  bundle: true, format: 'esm', minify: true, target: 'es2020', outfile: join(out, 'canvas.js'), logLevel: 'error', legalComments: 'eof',
});
copyFileSync(join(here, 'src', 'canvas', 'canvas.css'), join(out, 'canvas.css'));
// The handwriting font for text written in the old drawings ("Excalifont", SIL OFL) is reused from the old bundle folder.
const files = readdirSync(out);
writeFileSync(join(out, 'manifest.json'), JSON.stringify({ entry: 'canvas.js', files: files.length + 1 }, null, 2));
const js = readFileSync(join(out, 'canvas.js'));
console.log(`canvas bundle: ${(js.length / 1024).toFixed(0)} KB (${(gzipSync(js).length / 1024).toFixed(0)} KB gzip) -> ${out}`);
