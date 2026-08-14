// Mirrors the domain's concordatCode catalogue into the protocol documentation.
//
// ADR-019 names the catalogue one of the five normative artifacts, and its acceptance test is
// that somebody writes a complete client "without reading a line of C#". A catalogue that only
// exists as ConcordatCodes.cs fails that test by definition, and a hand-copied Markdown table
// fails it a different way: it goes stale silently, and the first sign is an SDK author
// branching on a code the registry no longer emits.
//
// So it is generated from the source of truth and gated, the same shape as M3.4's contract
// drift detection and the web app's codes:check.
//
//   node scripts/generate-protocol-docs.mjs           rewrite the file
//   node scripts/generate-protocol-docs.mjs --check    exit 1 if it would differ

import { readFileSync, writeFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, resolve, relative } from 'node:path';

const here = dirname(fileURLToPath(import.meta.url));
const root = resolve(here, '..');
const SOURCE = resolve(root, 'src/core/Concordat.Domain/Results/ConcordatCodes.cs');
const TARGET = resolve(root, 'docs/protocol/concordat-codes.md');

/**
 * A doc-commented `public const string` and any `// ---- section` banner above it.
 *
 * The banners in the source separate the registry-side codes from the envelope read-side and
 * the format ones; keeping them means the generated table reads in the same order as the file
 * an implementer would otherwise have had to read.
 */
const ENTRY = /((?:^[ \t]*\/\/\/.*\r?\n)+)[ \t]*public\s+const\s+string\s+(\w+)\s*=\s*"([^"]+)"\s*;/gm;

/** Strips XML doc markup down to plain prose. */
function prose(block) {
  const text = block
    .split(/\r?\n/)
    .map((line) => line.replace(/^\s*\/\/\/\s?/, ''))
    .join('\n');

  const summary = /<summary>([\s\S]*?)<\/summary>/.exec(text);
  if (!summary) return '';

  return summary[1]
    // <see cref="ConcordatCodes.SchemaMalformed"/> -> SchemaMalformed
    .replace(/<see\s+cref="[^"]*?\.?(\w+)"\s*\/>/g, '`$1`')
    .replace(/<see\s+langword="(\w+)"\s*\/>/g, '`$1`')
    .replace(/<\/?c>/g, '`')
    .replace(/<\/?b>/g, '**')
    .replace(/<\/?para>/g, ' ')
    .replace(/&lt;/g, '<')
    .replace(/&gt;/g, '>')
    .replace(/&amp;/g, '&')
    .replace(/\s+/g, ' ')
    .trim();
}

function extract(csharp) {
  const codes = [];
  for (const match of csharp.matchAll(ENTRY)) {
    const [, docs, member, value] = match;
    codes.push({ member, value, meaning: prose(docs) });
  }
  return codes;
}

function render(codes) {
  const rows = codes
    .map((c) => `| \`${c.value}\` | ${c.meaning || '_(no summary in the source)_'} |`)
    .join('\n');

  const source = relative(root, SOURCE).replace(/\\/g, '/');

  return `<!-- GENERATED FILE — do not edit by hand. -->
<!-- Source: ${source} -->
<!-- Regenerate: node scripts/generate-protocol-docs.mjs -->

# \`concordatCode\` catalogue

**Normative.** One of the five artifacts ADR-019 names as the protocol.

Every failure the registry reports carries one of these in the \`concordatCode\` member of an
RFC 9457 Problem Details body. **They are the strings clients branch on**, so they are stable:
renaming one is a breaking protocol change, not a refactor. They are deliberately not derived
from any implementation's enum or class names — a language that spells its members differently
must still emit exactly these.

Codes are also used where no HTTP response is involved: the client and middleware raise some of
them locally, so that a Go consumer and a .NET consumer report the same condition with the same
token and an operator can alert across a fleet.

> This file is generated from \`${source}\` and checked in CI. If it disagrees with the
> registry, the registry is right and this file is stale — please open an issue.

## Codes

| Code | Meaning |
| --- | --- |
${rows}

**${codes.length} codes.**

## Rules for implementers

- **Branch on the code, never on the HTTP status.** Several codes share a status, and the
  status alone does not tell you whether a retry could succeed. The split that matters most is
  a contract violation against an unreachable registry — see the CLI's exit codes, where they
  are 1 and 3 precisely so a pipeline cannot confuse them.
- **Treat an unknown code as a failure you cannot interpret**, not as a generic error to
  swallow. New codes are additive, and an SDK that silently maps everything it does not
  recognise onto one bucket hides exactly the new condition its users need to see.
- **Do not invent codes.** If your SDK needs to report something the catalogue has no word
  for, that belongs in the catalogue, because the whole point is that every language reports
  the same condition identically.
`;
}

const csharp = readFileSync(SOURCE, 'utf8');
const codes = extract(csharp);

if (codes.length === 0) {
  console.error(`No codes found in ${SOURCE}. The parser and the source have diverged.`);
  process.exit(1);
}

const rendered = render(codes);
const check = process.argv.includes('--check');

if (check) {
  let current = '';
  try {
    current = readFileSync(TARGET, 'utf8');
  } catch {
    console.error(`${TARGET} does not exist. Run: node scripts/generate-protocol-docs.mjs`);
    process.exit(1);
  }

  if (current.replace(/\r\n/g, '\n') !== rendered) {
    console.error(
      'docs/protocol/concordat-codes.md is stale.\n' +
        'Run: node scripts/generate-protocol-docs.mjs',
    );
    process.exit(1);
  }

  console.log(`concordat-codes.md matches ConcordatCodes.cs (${codes.length} codes).`);
} else {
  writeFileSync(TARGET, rendered);
  console.log(`Wrote ${relative(root, TARGET)} (${codes.length} codes).`);
}
