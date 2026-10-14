// node --test scripts/dependency-audit.test.mjs
//
// The parsing and formatting are the part of the audit job that can be wrong without anything
// failing loudly: a regex that stops matching after a NuGet release reports "restore clean" on
// the very night an advisory lands. These tests pin it against output shaped exactly like what
// `dotnet restore` printed on 2026-10-14 (Linux and Windows path styles), so a change to either
// the parser or NuGet's wording is a red test and not a silent pass.

import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { mkdtempSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join, resolve } from 'node:path';
import { test } from 'node:test';
import { fileURLToPath } from 'node:url';

import {
  commentBody,
  excerpt,
  fence,
  issueBody,
  issueTitle,
  LOG_LINES,
  parseArgs,
  parseRestoreOutput,
  report,
  UsageError,
} from './dependency-audit.mjs';

const script = resolve(dirname(fileURLToPath(import.meta.url)), 'dependency-audit.mjs');
const ROOT = '/home/runner/work/Concordat/Concordat';

// A solution restore appends " [<solution path>]" to every diagnostic (seen on the real
// Concordat.slnx restore); a single-project restore does not. The parser must cope with both.
const pkcs = (project, code, severity, ghsa, mode = 'error') =>
  `${ROOT}/${project} : ${mode} ${code}: ${mode === 'error' ? 'Warning As Error: ' : ''}Package 'System.Security.Cryptography.Pkcs' 10.0.0 has a known ${severity} severity vulnerability, https://github.com/advisories/${ghsa} [${ROOT}/Concordat.slnx]`;

const LINUX_LOG = [
  '  Determining projects to restore...',
  `  Restored ${ROOT}/src/core/Concordat.Domain/Concordat.Domain.csproj (in 212 ms).`,
  pkcs('src/hosts/Concordat.Api/Concordat.Api.csproj', 'NU1902', 'moderate', 'GHSA-2fmv-rv2c-59x7'),
  pkcs('src/hosts/Concordat.Api/Concordat.Api.csproj', 'NU1903', 'high', 'GHSA-846f-f764-76mx'),
  `  Failed to restore ${ROOT}/src/hosts/Concordat.Api/Concordat.Api.csproj (in 1.2 sec).`,
  // The same two advisories again from a second project: one finding, two projects.
  pkcs('src/infra/Concordat.Infrastructure/Concordat.Infrastructure.csproj', 'NU1902', 'moderate', 'GHSA-2fmv-rv2c-59x7'),
  pkcs('src/infra/Concordat.Infrastructure/Concordat.Infrastructure.csproj', 'NU1903', 'high', 'GHSA-846f-f764-76mx'),
  `${ROOT}/src/infra/Concordat.Infrastructure/Concordat.Infrastructure.csproj : error NU1903: Warning As Error: Package 'System.Security.Cryptography.Xml' 10.0.11 has a known high severity vulnerability, https://github.com/advisories/GHSA-7hqp-7c5m-4vq6`,
].join('\n');

test('parses advisories, de-duplicates across projects and strips the checkout path', () => {
  const { findings, auditUnavailable, otherErrors } = parseRestoreOutput(LINUX_LOG, { cwd: ROOT });

  assert.equal(auditUnavailable, false);
  assert.deepEqual(otherErrors, []);
  assert.deepEqual(
    findings.map((f) => [f.code, f.package, f.version, f.severity, f.advisory.split('/').pop()]),
    [
      ['NU1902', 'System.Security.Cryptography.Pkcs', '10.0.0', 'moderate', 'GHSA-2fmv-rv2c-59x7'],
      ['NU1903', 'System.Security.Cryptography.Pkcs', '10.0.0', 'high', 'GHSA-846f-f764-76mx'],
      ['NU1903', 'System.Security.Cryptography.Xml', '10.0.11', 'high', 'GHSA-7hqp-7c5m-4vq6'],
    ],
  );
  assert.deepEqual(findings[0].projects, [
    'src/hosts/Concordat.Api/Concordat.Api.csproj',
    'src/infra/Concordat.Infrastructure/Concordat.Infrastructure.csproj',
  ]);
});

test('parses the Windows form, CRLF line endings and backslash paths included', () => {
  const log = [
    '  Determining projects to restore...',
    'C:\\work\\repo\\src\\a\\a.csproj : error NU1904: Warning As Error: Package \'Foo.Bar\' 1.2.3 has a known critical severity vulnerability, https://github.com/advisories/GHSA-aaaa-bbbb-cccc',
    '',
  ].join('\r\n');

  const { findings } = parseRestoreOutput(log, { cwd: 'C:\\work\\repo' });

  assert.equal(findings.length, 1);
  assert.equal(findings[0].code, 'NU1904');
  assert.equal(findings[0].severity, 'critical');
  assert.deepEqual(findings[0].projects, ['src\\a\\a.csproj']);
});

test('a warning-level finding counts: it is the one TreatWarningsAsErrors promotes later', () => {
  const log = pkcs('src/a/a.csproj', 'NU1903', 'high', 'GHSA-846f-f764-76mx', 'warning');

  const { findings } = parseRestoreOutput(log, { cwd: ROOT });

  assert.equal(findings.length, 1);
  assert.equal(findings[0].package, 'System.Security.Cryptography.Pkcs');
});

test('NU1900 is "audit could not run", never a finding', () => {
  const log = `${ROOT}/p.csproj : error NU1900: Warning As Error: Error occurred while getting package vulnerability data: Unable to load the service index for source https://api.nuget.org/v3/index.json.`;

  const { findings, auditUnavailable } = parseRestoreOutput(log, { cwd: ROOT });

  assert.deepEqual(findings, []);
  assert.equal(auditUnavailable, true);
});

test('an error that is not an advisory is reported as such', () => {
  const log = `${ROOT}/p.csproj : error NU1101: Unable to find package Nope. No packages exist with this id in source(s): nuget.org`;

  const { findings, otherErrors } = parseRestoreOutput(log, { cwd: ROOT });

  assert.deepEqual(findings, []);
  assert.equal(otherErrors.length, 1);
  assert.match(otherErrors[0], /NU1101/);
});

test('a clean restore parses to nothing', () => {
  const { findings, auditUnavailable, otherErrors } = parseRestoreOutput(
    '  Determining projects to restore...\n  All projects are up-to-date for restore.\n',
  );

  assert.deepEqual([findings, auditUnavailable, otherErrors], [[], false, []]);
});

test('the title names the codes and packages, not versions or advisories, and is stable', () => {
  const { findings } = parseRestoreOutput(LINUX_LOG, { cwd: ROOT });

  const title = issueTitle(findings);

  assert.equal(
    title,
    'Dependency audit: NU1902, NU1903 in System.Security.Cryptography.Pkcs, System.Security.Cryptography.Xml',
  );
  // The idempotency key: the same findings in a different order are the same issue.
  assert.equal(issueTitle([...findings].reverse()), title);
});

test('a long package list is truncated so the title stays under GitHub\'s limit', () => {
  const findings = Array.from({ length: 12 }, (_, i) => ({
    code: 'NU1903',
    package: `Some.Rather.Long.Package.Name.Number${String(i).padStart(2, '0')}`,
    version: '1.0.0',
    severity: 'high',
    advisory: `https://github.com/advisories/GHSA-${i}`,
    projects: ['a.csproj'],
  }));

  const title = issueTitle(findings);

  assert.match(title, / and \d+ more$/);
  assert.ok(title.includes('Number00'), 'the first packages are still named');
  assert.ok(title.length <= 256, `title is ${title.length} characters`);
});

test('fence is longer than any backtick run in the text it wraps', () => {
  const wrapped = fence('before ```` after');

  assert.ok(wrapped.startsWith('`````text\n'), wrapped);
  assert.ok(wrapped.endsWith('\n`````'), wrapped);
  assert.ok(fence('plain').startsWith('```text\n'));
});

test('the excerpt drops "Restored" chatter, caps the lines and says how many were cut', () => {
  const chatter = Array.from({ length: 30 }, (_, i) => `  Restored ${ROOT}/src/p${i}/p${i}.csproj (in ${i} ms).`);
  const diagnostics = Array.from({ length: 50 }, (_, i) => `${ROOT}/src/x${i}.csproj : error NU1903: line ${i}`);

  const text = excerpt([...chatter, ...diagnostics].join('\n'), { cwd: ROOT });
  const lines = text.split('\n');

  assert.equal(lines.length, LOG_LINES + 1);
  assert.equal(lines[0], 'src/x0.csproj : error NU1903: line 0');
  assert.equal(lines.at(-1), '... 10 more line(s) not shown');
  assert.ok(!text.includes('Restored'));
  assert.ok(!text.includes(ROOT));
});

test('the issue body carries the table, the fixing guidance, the run link and a fenced log', () => {
  const { findings } = parseRestoreOutput(LINUX_LOG, { cwd: ROOT });

  const body = issueBody(findings, { output: LINUX_LOG, cwd: ROOT, runUrl: 'https://github.com/o/r/actions/runs/42' });

  assert.match(body, /\| NU1903 \| high \| `System\.Security\.Cryptography\.Xml` 10\.0\.11 \|/);
  assert.match(body, /\[this run\]\(https:\/\/github\.com\/o\/r\/actions\/runs\/42\)/);
  assert.match(body, /Pin the package forward/);
  assert.match(body, /detects; it does not fix/);
  assert.match(body, /```text\n[\s\S]*GHSA-846f-f764-76mx[\s\S]*\n```/);
});

test('a package reaching many projects shows three and a count, not seven paths', () => {
  const finding = {
    code: 'NU1903',
    package: 'P',
    version: '1',
    severity: 'high',
    advisory: 'https://github.com/advisories/GHSA-x',
    projects: ['a.csproj', 'b.csproj', 'c.csproj', 'd.csproj', 'e.csproj'],
  };

  const body = issueBody([finding], { output: '', runUrl: undefined });

  assert.match(body, /`a\.csproj`, `b\.csproj`, `c\.csproj` and 2 more \|/);
  assert.ok(!body.includes('d.csproj'));
});

test('the comment is short and says which advisories are still flagged', () => {
  const { findings } = parseRestoreOutput(LINUX_LOG, { cwd: ROOT });

  const comment = commentBody(findings, { date: new Date('2026-10-15T05:41:00Z'), runUrl: 'https://x/run/1' });

  assert.match(comment, /^Still flagged on 2026-10-15 in \[this run\]\(https:\/\/x\/run\/1\)\./);
  assert.equal(comment.split('\n').filter((l) => l.startsWith('- ')).length, 3);
  assert.ok(comment.split('\n').length < 10);
});

// ---- the gh sequence: this is the idempotency the workflow promises -------------------------

/** A stand-in for `gh` that records every call and answers `issue list` from a fixed set. */
function fakeGh(openIssues) {
  const calls = [];
  const run = (args, input) => {
    calls.push({ args, input });
    if (args[0] === 'issue' && args[1] === 'list') return JSON.stringify(openIssues);
    if (args[0] === 'issue' && args[1] === 'create') return 'https://github.com/o/r/issues/99\n';
    return '';
  };
  return { run, calls };
}

const options = { label: 'dependency-audit' };

test('with no open issue of that title it ensures the label, then opens one', () => {
  const gh = fakeGh([{ number: 7, title: 'Some other open issue' }]);

  report('T1', 'BODY', 'COMMENT', options, gh.run);

  assert.deepEqual(
    gh.calls.map((c) => c.args.slice(0, 2).join(' ')),
    ['label create', 'issue list', 'issue create'],
  );
  assert.ok(gh.calls[0].args.includes('--force'), 'the label must be create-or-update');
  assert.ok(gh.calls[1].args.includes('open') && gh.calls[1].args.includes('dependency-audit'));
  assert.equal(gh.calls[2].input, 'BODY');
  assert.ok(gh.calls[2].args.includes('--label'));
});

test('with an open issue of exactly that title it comments and opens nothing', () => {
  const gh = fakeGh([
    { number: 3, title: 'T1 but longer' },
    { number: 5, title: 'T1' },
  ]);

  report('T1', 'BODY', 'COMMENT', options, gh.run);

  const verbs = gh.calls.map((c) => c.args.slice(0, 2).join(' '));
  assert.deepEqual(verbs, ['label create', 'issue list', 'issue comment']);
  assert.equal(gh.calls[2].args[2], '5');
  assert.equal(gh.calls[2].input, 'COMMENT');
});

// ---- the command line ----------------------------------------------------------------------

test('arguments', () => {
  const parsed = parseArgs(['--dry-run', '--from-log', 'x.log', '--solution', 'a.slnx', '--label', 'l']);
  assert.equal(parsed.dryRun, true);
  assert.equal(parsed.fromLog, 'x.log');
  assert.equal(parsed.solution, 'a.slnx');
  assert.equal(parsed.label, 'l');
  assert.equal(parseArgs(['--self-check']).selfCheck, true);
  assert.equal(parseArgs([]).selfCheck, false);
  assert.throws(() => parseArgs(['--nope']), UsageError);
  assert.throws(() => parseArgs(['--cwd']), UsageError);
});

/**
 * Runs the real script against a saved log. PATH is emptied so that any attempt to call `gh`
 * (or anything else) fails with ENOENT: a dry run that reached for GitHub would exit 2 here.
 */
function cli(logText, ...extra) {
  const dir = mkdtempSync(join(tmpdir(), 'dependency-audit-'));
  try {
    const log = join(dir, 'restore.log');
    writeFileSync(log, logText);
    return spawnSync(process.execPath, [script, '--from-log', log, '--cwd', ROOT, ...extra], {
      env: { PATH: '', GITHUB_ACTIONS: 'true' },
      encoding: 'utf8',
    });
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
}

test('cli: an advisory exits 1 and the dry run prints the issue without touching gh', () => {
  const result = cli(LINUX_LOG, '--dry-run');

  assert.equal(result.status, 1, result.stderr);
  assert.match(result.stdout, /::error::Dependency audit: NU1902, NU1903 in System\.Security\.Cryptography\.Pkcs/);
  assert.match(result.stdout, /nothing was sent to GitHub/);
  assert.match(result.stdout, /would ensure label "dependency-audit"/);
  assert.match(result.stdout, /GHSA-2fmv-rv2c-59x7/);
});

test('cli: a clean log exits 0', () => {
  const result = cli('  Determining projects to restore...\n  All projects are up-to-date for restore.\n', '--dry-run');

  assert.equal(result.status, 0, result.stderr);
  assert.match(result.stdout, /restore clean/);
});

test('cli: NU1900 exits 2 and opens no issue', () => {
  const result = cli(`${ROOT}/p.csproj : error NU1900: Error occurred while getting package vulnerability data`, '--dry-run');

  assert.equal(result.status, 2);
  assert.doesNotMatch(result.stdout, /nothing was sent/);
});

test('cli: a restore error that is not an advisory exits 2 and opens no issue', () => {
  const result = cli(`${ROOT}/p.csproj : error NU1101: Unable to find package Nope.`, '--dry-run');

  assert.equal(result.status, 2);
  assert.match(result.stderr, /not because of an advisory/);
});

test('cli: a bad argument exits 64', () => {
  const result = spawnSync(process.execPath, [script, '--wat'], { env: { PATH: '' }, encoding: 'utf8' });

  assert.equal(result.status, 64);
});
