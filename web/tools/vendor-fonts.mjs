// Vendors Inter and JetBrains Mono from Google Fonts into `public/fonts/`.
//
// The web app serves its own typography (see `src/styles/fonts.css` for why — short version:
// this is a self-hosted registry and an air-gapped install is an ordinary way to run it). That
// means the woff2 files are committed, and committed binaries go stale without a way to
// reproduce them. This is that way.
//
//   npm run fonts:vendor
//
// Run it when a face needs a new weight range or Google ships a new version of one. It
// rewrites `public/fonts/*.woff2` and the `@font-face` block at the bottom of
// `src/styles/fonts.css`, leaving that file's explanatory header alone.
//
// Not part of the build, and not checked in CI the way `codes:check` is. Those codes are
// normative protocol that drifts silently; a font file does not drift — it is byte-identical
// until somebody deliberately re-runs this, and a CI job that fetched from Google on every
// build would reintroduce the network dependency this whole arrangement exists to remove.

import { mkdir, readFile, readdir, unlink, writeFile } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import { dirname, resolve } from 'node:path';

const here = dirname(fileURLToPath(import.meta.url));
const FONT_DIR = resolve(here, '../public/fonts');
const STYLESHEET = resolve(here, '../src/styles/fonts.css');

/*
 * The weight ranges the design uses. Inter carries the interface at 300–700; JetBrains Mono
 * only ever renders schema text and subject names, which are never lighter than regular and
 * never bolder than semibold.
 */
const REQUEST =
  'https://fonts.googleapis.com/css2' +
  '?family=Inter:wght@300..700' +
  '&family=JetBrains+Mono:wght@400..600' +
  '&display=swap';

/*
 * Google serves woff2 variable fonts to a browser and older formats to anything it does not
 * recognise, so the request has to look like a browser or this vendors ttf.
 */
const BROWSER_UA =
  'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) ' +
  'Chrome/131.0.0.0 Safari/537.36';

/** Everything above the first `@font-face`, which is prose this script must not rewrite. */
function header(stylesheet) {
  const start = stylesheet.indexOf('@font-face');
  return start === -1 ? stylesheet : stylesheet.slice(0, start);
}

const response = await fetch(REQUEST, { headers: { 'User-Agent': BROWSER_UA } });
if (!response.ok) {
  throw new Error(`Google Fonts answered ${response.status} for ${REQUEST}`);
}

const css = await response.text();

// Each block is preceded by a `/* subset */` comment, which is the only place the subset is
// named — the URLs themselves are opaque hashes.
const blocks = [...css.matchAll(/\/\*\s*([\w-]+)\s*\*\/\s*@font-face\s*\{([^}]*)\}/g)];
if (blocks.length === 0) {
  throw new Error('No @font-face blocks found. Google Fonts changed its CSS shape.');
}

await mkdir(FONT_DIR, { recursive: true });

// Removed first, so a subset Google stops serving does not linger as an orphan nobody
// notices — the stylesheet would stop referencing it and the file would sit there forever.
for (const file of await readdir(FONT_DIR)) {
  if (file.endsWith('.woff2')) {
    await unlink(resolve(FONT_DIR, file));
  }
}

const slug = (family) => family.toLowerCase().replace(/[^a-z0-9]+/g, '-');
const rules = [];
let bytes = 0;

for (const [, subset, body] of blocks) {
  const family = /font-family:\s*'([^']+)'/.exec(body)[1];
  const url = /src:\s*url\(([^)]+)\)/.exec(body)[1];
  const range = /unicode-range:\s*([^;]+);/.exec(body)[1].trim();
  const weight = /font-weight:\s*([^;]+);/.exec(body)[1].trim();

  const name = `${slug(family)}-${subset}.woff2`;
  const file = await fetch(url);
  if (!file.ok) {
    throw new Error(`${file.status} fetching ${name} from ${url}`);
  }

  const buffer = Buffer.from(await file.arrayBuffer());
  await writeFile(resolve(FONT_DIR, name), buffer);
  bytes += buffer.byteLength;
  console.log(`${name.padEnd(38)} ${(buffer.byteLength / 1024).toFixed(1)} kB`);

  rules.push(
    [
      '@font-face {',
      `  font-family: '${family}';`,
      '  font-style: normal;',
      `  font-weight: ${weight};`,
      '  font-display: swap;',
      `  src: url('/fonts/${name}') format('woff2');`,
      `  unicode-range: ${range};`,
      '}',
    ].join('\n'),
  );
}

const existing = await readFile(STYLESHEET, 'utf8').catch(() => '');
await writeFile(STYLESHEET, header(existing) + rules.join('\n\n') + '\n');

console.log(`\n${blocks.length} files, ${(bytes / 1024).toFixed(0)} kB on disk.`);
console.log(`Rewrote ${STYLESHEET}`);
