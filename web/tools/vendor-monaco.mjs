// Vendors Monaco's stylesheet and icon font into `public/monaco/`.
//
// The editor's JavaScript is bundled by the build into a lazy chunk. Its CSS cannot be: the
// application builder emits the stylesheet that JavaScript imports as a file nothing links to,
// and `ng serve` papers over that by injecting it from the module — so a production build
// renders Monaco unstyled while the dev server, and every browser test run against it, looks
// right. The build is therefore told to drop CSS imported from JavaScript (`angular.json`,
// `loader`), and the stylesheet is served as a static file the editor links itself when it
// loads. One path, identical in development and production.
//
//   npm run monaco:vendor   rewrite public/monaco/ and the generated reference to it
//   npm run monaco:check    fail if they are not what vendoring would produce (CI)
//
// Not fetched from anywhere: both files come out of `node_modules/monaco-editor`, so this is
// deterministic and works offline (ADR-026). The CSS is Monaco's own prebuilt
// `min/vs/editor/editor.main.css` with one change — the icon font, which that file embeds as a
// `data:` URI, becomes a file beside it. The page's policy is `font-src 'self'`, which refuses a
// `data:` font, so the unmodified file would draw every icon as an empty box.
//
// Both files carry a hash of their content in their name, for the reason the build hashes its own
// output: they can be cached forever, and a new Monaco is a new URL rather than a stale one.
// `monaco-assets.generated.ts` says which URL is current.

import { createHash } from 'node:crypto';
import {
  existsSync,
  mkdirSync,
  readFileSync,
  readdirSync,
  unlinkSync,
  writeFileSync,
} from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const PACKAGE = resolve(here, '../node_modules/monaco-editor');
const OUT = resolve(here, '../public/monaco');
const REFERENCE = resolve(here, '../src/app/features/registry/ui/monaco-assets.generated.ts');

const check = process.argv.includes('--check');

/*
 * The icon font is Microsoft's Codicon set, licensed Creative Commons Attribution 4.0, which
 * requires that the creator, a copyright notice, a reference to the licence and a link to the
 * material travel with it. Monaco's own ThirdPartyNotices.txt does not carry that for the font —
 * its Creative Commons text is for an unrelated dependency — so it is written here, beside the file
 * it covers and served with it, the way ADR-026 does for the typefaces.
 */
const CODICON_NOTICE = [
  'Codicon icon font (codicon-*.ttf)',
  '',
  'Copyright (c) Microsoft Corporation',
  'https://github.com/microsoft/vscode-codicons',
  '',
  'Licensed under the Creative Commons Attribution 4.0 International License (CC BY 4.0):',
  'https://creativecommons.org/licenses/by/4.0/',
  '',
  'The font file is the one distributed inside the monaco-editor package, decoded from the data',
  'URI in its stylesheet and otherwise unmodified.',
  '',
].join('\n');

const source = readFileSync(resolve(PACKAGE, 'min/vs/editor/editor.main.css'), 'utf8');
const fontPattern = /url\(data:font\/ttf;base64,([A-Za-z0-9+/=]+)\)/g;
const fonts = [...source.matchAll(fontPattern)];

if (fonts.length !== 1) {
  throw new Error(
    `Expected exactly one embedded icon font in editor.main.css and found ${fonts.length}. ` +
      "Monaco's packaging has changed; this script needs to be taught the new shape.",
  );
}

const font = Buffer.from(fonts[0][1], 'base64');
const short = (bytes) => createHash('sha256').update(bytes).digest('hex').slice(0, 10);

const fontName = `codicon-${short(font)}.ttf`;
const stylesheet = source.replace(fontPattern, `url(${fontName})`);
const stylesheetName = `monaco-${short(stylesheet)}.css`;

const version = JSON.parse(readFileSync(resolve(PACKAGE, 'package.json'), 'utf8')).version;

const files = new Map([
  [fontName, font],
  [stylesheetName, Buffer.from(stylesheet)],
  ['LICENSE.txt', readFileSync(resolve(PACKAGE, 'LICENSE'))],
  ['ThirdPartyNotices.txt', readFileSync(resolve(PACKAGE, 'ThirdPartyNotices.txt'))],
  ['CODICON.txt', Buffer.from(CODICON_NOTICE)],
]);

const reference = `// GENERATED FILE — do not edit.
//
// Source:     node_modules/monaco-editor ${version}
// Regenerate: npm run monaco:vendor   (npm run monaco:check fails the build on drift)
//
// Where the editor's stylesheet is served from, relative to the document. The name carries a
// hash of the file's content, so it changes exactly when the file does. See
// \`tools/vendor-monaco.mjs\` for why this is a file and not an import.

export const MONACO_STYLESHEET = 'monaco/${stylesheetName}';
`;

const problems = [];

for (const [name, content] of files) {
  const path = resolve(OUT, name);

  if (!existsSync(path) || !readFileSync(path).equals(content)) {
    problems.push(`public/monaco/${name} is missing or out of date`);
  }
}

if (existsSync(OUT)) {
  for (const name of readdirSync(OUT)) {
    if (!files.has(name)) {
      problems.push(`public/monaco/${name} is left over from an earlier Monaco`);
    }
  }
}

if (!existsSync(REFERENCE) || readFileSync(REFERENCE, 'utf8') !== reference) {
  problems.push('monaco-assets.generated.ts does not name the current stylesheet');
}

if (check) {
  if (problems.length > 0) {
    console.error(`Monaco's vendored assets are stale (monaco-editor ${version}):`);
    for (const problem of problems) {
      console.error(`  - ${problem}`);
    }
    console.error('Run `npm run monaco:vendor` and commit the result.');
    process.exit(1);
  }

  console.log(`Monaco's vendored assets match monaco-editor ${version}.`);
} else {
  mkdirSync(OUT, { recursive: true });

  for (const name of readdirSync(OUT)) {
    if (!files.has(name)) {
      unlinkSync(resolve(OUT, name));
    }
  }

  for (const [name, content] of files) {
    writeFileSync(resolve(OUT, name), content);
  }

  writeFileSync(REFERENCE, reference);
  console.log(`Vendored monaco-editor ${version}: ${stylesheetName}, ${fontName}.`);
}
