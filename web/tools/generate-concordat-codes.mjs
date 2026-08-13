// Mirrors the domain's concordatCode catalogue into TypeScript.
//
// ADR-019 makes those strings normative protocol: the API puts them in RFC 9457 Problem
// Details and clients branch on them. A hand-maintained copy in the web app is a copy that
// goes stale, and the failure is silent — a new code lands, the UI's switch has no arm for
// it, and the user gets a blank error panel instead of an explanation. So the copy is
// generated, and `npm run codes:check` fails the build when it drifts, the same shape as
// the build-time contract drift detection in M3.4.
//
//   npm run codes:generate   rewrite the file
//   npm run codes:check      fail if the file is not what generation would produce

import { readFileSync, writeFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, resolve } from 'node:path';

const here = dirname(fileURLToPath(import.meta.url));
const SOURCE = resolve(here, '../../src/core/Concordat.Domain/Results/ConcordatCodes.cs');
const TARGET = resolve(here, '../src/app/core/http/concordat-codes.generated.ts');

/** `public const string SchemaIdMalformed = "schema_id_malformed";` */
const CONST_PATTERN = /public\s+const\s+string\s+(\w+)\s*=\s*"([^"]+)"\s*;/g;

function extract(csharp) {
  const codes = [];
  for (const [, member, value] of csharp.matchAll(CONST_PATTERN)) {
    codes.push({ member, value });
  }
  return codes;
}

function render(codes) {
  const entries = codes.map((c) => `  '${c.value}',`).join('\n');

  return `// GENERATED FILE — do not edit.
//
// Source:    src/core/Concordat.Domain/Results/ConcordatCodes.cs
// Regenerate: npm run codes:generate   (npm run codes:check fails the build on drift)
//
// The catalogue of stable error codes the domain can emit (ADR-019). These are protocol,
// not diagnostics: they arrive as the \`concordatCode\` member of an RFC 9457 Problem
// Details body and callers branch on them.

/** Every \`concordatCode\` the domain can emit, in catalogue order. */
export const DOMAIN_CONCORDAT_CODES = [
${entries}
] as const;

/** A code from the domain catalogue. */
export type DomainConcordatCode = (typeof DOMAIN_CONCORDAT_CODES)[number];
`;
}

function main() {
  const check = process.argv.includes('--check');

  let csharp;
  try {
    csharp = readFileSync(SOURCE, 'utf8');
  } catch {
    // Deliberately fatal rather than skipped. A silent skip would let the check pass in
    // exactly the situation it is meant to catch: the catalogue moved and nobody noticed.
    console.error(`Cannot read the code catalogue at ${SOURCE}.`);
    process.exit(1);
  }

  const codes = extract(csharp);
  if (codes.length === 0) {
    console.error(`No codes found in ${SOURCE}. The declaration shape probably changed.`);
    process.exit(1);
  }

  const rendered = render(codes);

  if (!check) {
    writeFileSync(TARGET, rendered, 'utf8');
    console.log(`Wrote ${codes.length} codes to ${TARGET}.`);
    return;
  }

  let current = '';
  try {
    current = readFileSync(TARGET, 'utf8');
  } catch {
    /* falls through to the mismatch branch */
  }

  // Normalise line endings: git may check the file out with CRLF on Windows, and a newline
  // convention is not drift.
  if (current.replace(/\r\n/g, '\n') !== rendered.replace(/\r\n/g, '\n')) {
    console.error(
      'concordat-codes.generated.ts is out of date with ConcordatCodes.cs.\n' +
        'Run `npm run codes:generate` and commit the result.',
    );
    process.exit(1);
  }

  console.log(`concordat-codes.generated.ts matches ConcordatCodes.cs (${codes.length} codes).`);
}

main();
