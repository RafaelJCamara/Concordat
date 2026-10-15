// Measures which regular expressions each engine will compile, so that what this repository says about a
// pattern's portability is a measurement and not a memory.
//
//   node scripts/regex-oracle/measure.mjs            # rewrites the corpus the .NET tests read
//   node scripts/regex-oracle/measure.mjs --check    # fails if the committed corpus is not what the engines say today
//
// Engines: Go's regexp (RE2), Node's RegExp with and without the `u` flag (ECMA-262), and Python's `re`.
// .NET is not here: the tests ask the real `Regex` at run time. Needs `go`, `node` and `python` on the path.
import { execFileSync } from 'node:child_process';
import { readFileSync, writeFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const patternsFile = join(here, 'patterns.json');
const corpusFile = join(here, '..', '..', 'tests', 'Concordat.Formats.Json.Tests', 'RegexCorpus', 'engines.json');

const patterns = JSON.parse(readFileSync(patternsFile, 'utf8'));

function run(command, args) {
  return JSON.parse(execFileSync(command, args, { encoding: 'utf8' }));
}

const go = run('go', ['run', join(here, 'oracle.go'), patternsFile]);
const python = run('python', ['-I', join(here, 'oracle.py'), patternsFile]);

function ecma(flags) {
  return patterns.map((p) => {
    try {
      new RegExp(p, flags);
      return true;
    } catch {
      return false;
    }
  });
}

const ecmaU = ecma('u');
const ecmaLegacy = ecma('');

const corpus = {
  engines: { go: go.version, node: process.version, python: python.version },
  patterns: patterns.map((pattern, i) => ({
    pattern,
    re2: go.verdicts[i],
    ecmaU: ecmaU[i],
    ecmaLegacy: ecmaLegacy[i],
    python: python.verdicts[i],
  })),
};

const text = JSON.stringify(corpus, null, 1) + '\n';

if (process.argv.includes('--check')) {
  const committed = readFileSync(corpusFile, 'utf8');
  // Versions are recorded for the reader and are not what drifts; the verdicts are.
  const strip = (t) => JSON.stringify(JSON.parse(t).patterns);
  if (strip(committed) !== strip(text)) {
    console.error('The regex corpus is out of date. Run: node scripts/regex-oracle/measure.mjs');
    process.exit(1);
  }
  console.log(`The regex corpus matches the engines (${go.version}, node ${process.version}, ${python.version}).`);
} else {
  writeFileSync(corpusFile, text, 'utf8');
  console.log(`Wrote ${patterns.length} patterns to ${corpusFile}`);
}
