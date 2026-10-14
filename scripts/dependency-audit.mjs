// Restores the solution against LIVE NuGet vulnerability data and, when that surfaces an
// advisory, files (or updates) one GitHub issue about it.
//
// Why this exists. NuGet audit runs at restore, and Directory.Build.props turns every warning
// into an error, so an advisory published against any package -- including a transitive one
// nobody chose -- fails the next restore with no change to the repository. That is how
// System.Security.Cryptography.Pkcs and .Xml broke a fresh docker build a day after the same
// sources had built (82656ca). CI would have caught it only on the next push; a quiet week
// would have left it to the first person who touched the repo to find out, mid-change, that
// their change was not what broke the build.
//
// What this does NOT do: fix anything. It detects and reports. The fix is a human decision
// (pin forward, or take a newer parent), and the repository's rule is pin, never suppress.
//
//   node scripts/dependency-audit.mjs                       restore, report to GitHub via `gh`
//   node scripts/dependency-audit.mjs --dry-run             restore, print what it would do
//   node scripts/dependency-audit.mjs --from-log FILE       skip the restore, read a saved log
//   node scripts/dependency-audit.mjs --cwd DIR             restore in DIR (default: repo root)
//   node scripts/dependency-audit.mjs --solution NAME       what to restore (default Concordat.slnx)
//   node scripts/dependency-audit.mjs --label NAME          issue label (default dependency-audit)
//   node scripts/dependency-audit.mjs --self-check          prove the parser still reads live NuGet
//
// Exit codes: 0 clean; 1 an advisory was found (the run is red on purpose, so Actions shows it
// and the owner is mailed, in addition to the issue); 2 the restore failed for a reason that is
// not an advisory; 3 the self-check could not see a known advisory; 64 bad arguments.
// An advisory exits 1 even if the restore itself exited 0,
// because audit findings are warnings until TreatWarningsAsErrors promotes them, and a finding
// that is not fatal today is the one that gets promoted by someone's next .editorconfig edit.
//
// Node rather than bash or PowerShell: it has to run on ubuntu-latest and on the Windows
// development machine, and both of the other options would need a second implementation.
// Node is already a prerequisite of the repository (scripts/generate-protocol-docs.mjs).

import { spawn, spawnSync } from 'node:child_process';
import { mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

export const DEFAULT_LABEL = 'dependency-audit';
const LABEL_COLOR = 'b60205';
const LABEL_DESCRIPTION = 'NuGet audit flagged a package: raised by the scheduled dependency-audit workflow';

/** How much of the restore output goes into an issue body. */
export const LOG_LINES = 40;

/** GitHub rejects a title over 256 characters; the package list is cut to fit well inside that. */
const MAX_TITLE_LENGTH = 200;

/**
 * NU1901..NU1904 are the audit severities (low, moderate, high, critical). NU1900 is the
 * different thing: the audit could not fetch its data. It is deliberately not matched here.
 * Treating "nuget.org was unreachable for a minute" as "a package is vulnerable" would open an
 * issue for a network blip, and the next night's run would then comment "still failing" on it.
 */
const ADVISORY_LINE =
  /^(?<project>.*?)\s*:\s*(?<level>error|warning)\s+(?<code>NU190[1-4]):\s+(?:Warning As Error:\s+)?Package '(?<package>[^']+)' (?<version>\S+) has a known (?<severity>\w+) severity vulnerability,\s+(?<url>\S+)/;

/** The audit-data-unavailable diagnostic, which is a different failure from a finding. */
const AUDIT_UNAVAILABLE = /\b(?:error|warning)\s+NU1900:/;

/** Any MSBuild-formatted error, advisory or not. */
const ANY_ERROR = /\berror\s+[A-Z]+\d+:/;

/** Lines that say a project restored fine; they bury the diagnostics in a 27-project solution. */
const NOISE = /^\s*(?:Restored\s+.*\(in\s|Determining projects to restore|All projects are up-to-date)/;

/**
 * Parses `dotnet restore` output into advisory findings.
 *
 * Every project that references a flagged package repeats the same diagnostic (the API and
 * Infrastructure both resolved Pkcs on 2026-10-14), so findings are de-duplicated by
 * code, package, version and advisory, and the projects that carried it are collected instead.
 *
 * @param {string} text the combined stdout and stderr of the restore
 * @param {{ cwd?: string }} [options] a path prefix to strip so the report names `src/...`
 * @returns {{ findings: Finding[], auditUnavailable: boolean, otherErrors: string[] }}
 */
export function parseRestoreOutput(text, options = {}) {
  const prefixes = options.cwd ? pathPrefixes(options.cwd) : [];
  const byKey = new Map();
  const otherErrors = [];
  let auditUnavailable = false;

  for (const raw of text.split(/\r?\n/)) {
    const line = raw.trimEnd();
    const advisory = ADVISORY_LINE.exec(line);

    if (advisory) {
      const { project, code, package: id, version, severity, url } = advisory.groups;
      const key = [code, id, version, url].join('|');
      let finding = byKey.get(key);
      if (!finding) {
        finding = { code, package: id, version, severity, advisory: url, projects: [] };
        byKey.set(key, finding);
      }
      const shown = stripPrefix(project, prefixes);
      if (!finding.projects.includes(shown)) finding.projects.push(shown);
      continue;
    }

    if (AUDIT_UNAVAILABLE.test(line)) {
      auditUnavailable = true;
      continue;
    }

    if (ANY_ERROR.test(line)) otherErrors.push(stripPrefix(line, prefixes));
  }

  const findings = [...byKey.values()].sort(
    (a, b) =>
      a.package.localeCompare(b.package) ||
      a.code.localeCompare(b.code) ||
      a.advisory.localeCompare(b.advisory),
  );
  return { findings, auditUnavailable, otherErrors };
}

/** @typedef {{code:string, package:string, version:string, severity:string, advisory:string, projects:string[]}} Finding */

/** Both slash styles: MSBuild prints whichever the platform and the project file use. */
function pathPrefixes(cwd) {
  const BACKSLASH = String.fromCharCode(92);
  const forward = cwd.split(BACKSLASH).join('/').replace(/\/+$/, '');
  return [forward + '/', forward.split('/').join(BACKSLASH) + BACKSLASH];
}

/** Removes the checkout directory wherever it appears, so the report names src/... not /home/runner/... */
function stripPrefix(value, prefixes) {
  return prefixes.reduce((text, prefix) => text.split(prefix).join(''), value);
}

/**
 * The issue title, which is also the idempotency key: an open issue with exactly this title
 * is the same problem, so the next run comments on it instead of opening a second one.
 *
 * It names the codes and the packages but not the versions or advisory ids. A new advisory
 * against a package already named leaves the title unchanged and so lands as a comment on the
 * open issue (the comment lists it); a different package is a different problem and a new issue.
 *
 * @param {Finding[]} findings
 */
export function issueTitle(findings) {
  const codes = unique(findings.map((f) => f.code));
  const packages = unique(findings.map((f) => f.package));
  const head = `Dependency audit: ${codes.join(', ')} in `;

  // Cut by length, not by count: package ids run to fifty characters, so six of them can pass
  // GitHub's limit and the issue would then fail to open on exactly the night it is needed.
  let shown = [];
  for (const id of packages) {
    const hidden = packages.length - shown.length - 1;
    const tail = hidden > 0 ? ` and ${hidden} more` : '';
    if (shown.length > 0 && (head + [...shown, id].join(', ') + tail).length > MAX_TITLE_LENGTH) break;
    shown = [...shown, id];
  }
  const more = packages.length > shown.length ? ` and ${packages.length - shown.length} more` : '';
  return head + shown.join(', ') + more;
}

function unique(values) {
  return [...new Set(values)].sort();
}

/** A code fence longer than any run of backticks inside the text, so the text cannot close it. */
export function fence(text) {
  const longest = Math.max(0, ...[...text.matchAll(/`+/g)].map((m) => m[0].length));
  const ticks = '`'.repeat(Math.max(3, longest + 1));
  return `${ticks}text\n${text}\n${ticks}`;
}

/**
 * The first lines of the restore output, minus the "Restored ..." chatter.
 *
 * Literally the first 40 lines would, in a 27-project solution, mostly be projects that restored
 * fine, with the diagnostics cut off below the fold. An issue whose log does not show the
 * failure is no better than no log.
 *
 * @param {string} output
 * @param {{ cwd?: string, limit?: number }} [options]
 */
export function excerpt(output, options = {}) {
  const limit = options.limit ?? LOG_LINES;
  const prefixes = options.cwd ? pathPrefixes(options.cwd) : [];
  const lines = output
    .split(/\r?\n/)
    .map((line) => stripPrefix(line.trimEnd(), prefixes))
    .filter((line) => line.trim() !== '' && !NOISE.test(line));
  const kept = lines.slice(0, limit);
  const omitted = lines.length - kept.length;
  return omitted > 0 ? [...kept, `... ${omitted} more line(s) not shown`].join('\n') : kept.join('\n');
}

/**
 * Three projects and a count. The Pkcs and Xml advisories of 2026-10-14 each reached seven
 * projects through the transitive graph; seven paths in one table cell make the table
 * unreadable, and which project carries a package matters less than which package it is.
 */
function projectsCell(projects) {
  const shown = projects
    .slice(0, 3)
    .map((p) => `\`${p}\``)
    .join(', ');
  return projects.length > 3 ? `${shown} and ${projects.length - 3} more` : shown;
}

/** @param {Finding[]} findings */
function findingsTable(findings) {
  const rows = findings.map(
    (f) =>
      `| ${f.code} | ${f.severity} | \`${f.package}\` ${f.version} | ${f.advisory} | ${projectsCell(f.projects)} |`,
  );
  return ['| Code | Severity | Package | Advisory | Projects |', '| --- | --- | --- | --- | --- |', ...rows].join('\n');
}

/**
 * @param {Finding[]} findings
 * @param {{ output: string, cwd?: string, solution?: string, runUrl?: string }} context
 */
export function issueBody(findings, context) {
  return [
    `NuGet audit flagged ${findings.length} package version(s) during a fresh \`dotnet restore ${context.solution ?? 'Concordat.slnx'} --force\`${runLink(context.runUrl)}.`,
    '',
    findingsTable(findings),
    '',
    'An audit finding is a build error in this repository (`TreatWarningsAsErrors`), and audit reads the vulnerability data live, so the next fresh restore -- a push, a pull request, a docker build -- fails with no change to the repository.',
    '',
    '**What to do.** Pin the package forward in the transitive-pins group of `Directory.Packages.props` to the patched release (see 82656ca for the shape of it). Do not suppress the advisory. `dotnet list package --vulnerable --include-transitive` shows which project resolves it and which version fixes it.',
    '',
    '**What this report is.** The scheduled `dependency-audit` workflow detects; it does not fix, and it does not close this issue. It comments here while the same packages are still flagged, and closing the issue is yours once the restore is clean.',
    '',
    `<details><summary>Restore output (first ${LOG_LINES} lines, "Restored ..." lines omitted)</summary>`,
    '',
    fence(excerpt(context.output, { cwd: context.cwd })),
    '',
    '</details>',
  ].join('\n');
}

/**
 * A comment is deliberately short: the issue already carries the log, and a nightly comment
 * that repeats forty lines would bury the one thing that changed.
 *
 * @param {Finding[]} findings
 * @param {{ runUrl?: string, date?: Date }} [context]
 */
export function commentBody(findings, context = {}) {
  const day = (context.date ?? new Date()).toISOString().slice(0, 10);
  const list = findings.map((f) => `- ${f.code} \`${f.package}\` ${f.version} (${f.severity}): ${f.advisory}`);
  return [`Still flagged on ${day}${runLink(context.runUrl)}.`, '', ...list].join('\n');
}

function runLink(url) {
  return url ? ` in [this run](${url})` : '';
}

/** @param {string[]} argv */
export function parseArgs(argv) {
  const options = {
    dryRun: false,
    selfCheck: false,
    fromLog: undefined,
    cwd: resolve(dirname(fileURLToPath(import.meta.url)), '..'),
    solution: 'Concordat.slnx',
    label: DEFAULT_LABEL,
  };
  const valued = { '--from-log': 'fromLog', '--cwd': 'cwd', '--solution': 'solution', '--label': 'label' };

  for (let i = 0; i < argv.length; i++) {
    const arg = argv[i];
    if (arg === '--dry-run') {
      options.dryRun = true;
    } else if (arg === '--self-check') {
      options.selfCheck = true;
    } else if (arg in valued) {
      const value = argv[++i];
      if (value === undefined) throw new UsageError(`${arg} needs a value`);
      options[valued[arg]] = arg === '--cwd' ? resolve(value) : value;
    } else {
      throw new UsageError(`unknown argument ${arg}`);
    }
  }
  return options;
}

export class UsageError extends Error {}

/** Runs the restore, streaming its output as it arrives and returning all of it. */
function restore(options) {
  return new Promise((done, fail) => {
    const child = spawn('dotnet', ['restore', options.solution, '--force'], {
      cwd: options.cwd,
      env: { ...process.env, DOTNET_NOLOGO: 'true', DOTNET_CLI_TELEMETRY_OPTOUT: 'true' },
    });
    let output = '';
    const take = (stream, sink) =>
      stream.on('data', (chunk) => {
        output += chunk;
        sink.write(chunk);
      });
    take(child.stdout, process.stdout);
    take(child.stderr, process.stderr);
    child.on('error', fail);
    child.on('close', (code) => done({ output, exitCode: code ?? 1 }));
  });
}

/** Calls `gh`, feeding the body on stdin so quoting and argument-length limits never apply. */
function gh(args, input) {
  const result = spawnSync('gh', args, { input, encoding: 'utf8', maxBuffer: 16 * 1024 * 1024 });
  if (result.error) throw result.error;
  if (result.status !== 0) {
    throw new Error(`gh ${args.join(' ')} failed (${result.status}): ${result.stderr || result.stdout}`);
  }
  return result.stdout;
}

/**
 * Raises or updates the issue. `gh label create --force` is create-or-update, so the label
 * exists afterwards whether or not it did before and a second run changes nothing; without it
 * `gh issue create --label` fails on a fresh repository and the first advisory goes unreported.
 */
export function report(title, body, comment, options, run = gh) {
  run(['label', 'create', options.label, '--force', '--color', LABEL_COLOR, '--description', LABEL_DESCRIPTION]);

  // Listed by label and compared exactly, rather than found with `--search`: search tokenises
  // the title, so a title of package names can match, or miss, on punctuation.
  const open = JSON.parse(
    run(['issue', 'list', '--state', 'open', '--label', options.label, '--limit', '200', '--json', 'number,title']),
  );
  const existing = open.find((issue) => issue.title === title);

  if (existing) {
    run(['issue', 'comment', String(existing.number), '--body-file', '-'], comment);
    return `Commented on #${existing.number}: ${title}`;
  } else {
    const url = run(['issue', 'create', '--title', title, '--label', options.label, '--body-file', '-'], body);
    return `Opened ${url.trim()}: ${title}`;
  }
}

function dryRun(title, body, comment, options) {
  console.log('');
  console.log('--- dry run: nothing was sent to GitHub ---');
  console.log(`would ensure label "${options.label}" exists (gh label create --force)`);
  console.log(`would look for an OPEN issue labelled "${options.label}" titled exactly:`);
  console.log(`  ${title}`);
  console.log('');
  console.log('if one exists, it would comment:');
  console.log(indent(comment));
  console.log('');
  console.log('otherwise it would open an issue with this body:');
  console.log(indent(body));
}

function indent(text) {
  return text
    .split('\n')
    .map((line) => `    ${line}`)
    .join('\n');
}

/**
 * A package version that is, and will stay, on NuGet's advisory list: Pkcs 10.0.0 carries two
 * published advisories (GHSA-2fmv-rv2c-59x7, GHSA-846f-f764-76mx). Advisories are only
 * occasionally withdrawn; if both ever are, this check fails and says so, and the fix is to pick
 * another old version of anything with a published advisory.
 */
const CANARY_PACKAGE = 'System.Security.Cryptography.Pkcs';
const CANARY_VERSION = '10.0.0';

/**
 * Restores a throwaway project that references the canary and demands that the parser finds it.
 *
 * Without this the job has a silent failure mode that no test here can cover: NuGet rewords or
 * reformats its diagnostic, the regex stops matching, and from then on every night reports
 * "restore clean" however many advisories there are. The unit tests pin the parser to output
 * captured on 2026-10-14; this pins it to what NuGet prints today. It is the only part that
 * needs the network, which is why it is a flag and not part of the tests.
 */
export async function selfCheck() {
  const dir = mkdtempSync(join(tmpdir(), 'dependency-audit-canary-'));
  try {
    writeFileSync(
      join(dir, 'canary.csproj'),
      [
        '<Project Sdk="Microsoft.NET.Sdk">',
        '  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>',
        `  <ItemGroup><PackageReference Include="${CANARY_PACKAGE}" Version="${CANARY_VERSION}" /></ItemGroup>`,
        '</Project>',
      ].join('\n'),
    );
    const { output } = await restore({ cwd: dir, solution: 'canary.csproj' });
    const { findings, auditUnavailable } = parseRestoreOutput(output, { cwd: dir });

    if (findings.some((f) => f.package === CANARY_PACKAGE)) {
      console.log(`dependency-audit: self-check passed, ${findings.length} canary finding(s) parsed.`);
      return 0;
    }
    console.error(
      auditUnavailable
        ? 'dependency-audit: self-check could not run: NuGet audit data was unavailable (NU1900).'
        : `dependency-audit: self-check FAILED. ${CANARY_PACKAGE} ${CANARY_VERSION} has published advisories but the parser found none in the restore output above. NuGet's diagnostic format has probably changed, or the advisories were withdrawn.`,
    );
    return 3;
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
}

export async function main(argv, env = process.env) {
  let options;
  try {
    options = parseArgs(argv);
  } catch (error) {
    if (!(error instanceof UsageError)) throw error;
    console.error(`dependency-audit: ${error.message}`);
    return 64;
  }

  if (options.selfCheck) return selfCheck();

  let output;
  let exitCode = 0;
  if (options.fromLog) {
    output = readFileSync(options.fromLog, 'utf8');
  } else {
    ({ output, exitCode } = await restore(options));
  }

  const { findings, auditUnavailable, otherErrors } = parseRestoreOutput(output, { cwd: options.cwd });

  if (findings.length === 0) {
    if (auditUnavailable) {
      // Not an advisory, so no issue; but not a pass either, because "audit could not run" says
      // nothing about whether anything is vulnerable. The red run is the signal.
      annotate(env, 'error', 'NuGet audit could not load its vulnerability data (NU1900); nothing was checked. Re-run the workflow.');
      console.error('dependency-audit: NU1900, the audit data was unavailable. Nothing was checked.');
      return 2;
    }
    if (exitCode !== 0 || otherErrors.length > 0) {
      annotate(env, 'error', `dotnet restore failed for a reason that is not an advisory: ${otherErrors[0] ?? 'see the log'}`);
      console.error('dependency-audit: the restore failed, but not because of an advisory. No issue opened.');
      return 2;
    }
    console.log('dependency-audit: restore clean, no advisories.');
    return 0;
  }

  const title = issueTitle(findings);
  const runUrl =
    env.GITHUB_SERVER_URL && env.GITHUB_REPOSITORY && env.GITHUB_RUN_ID
      ? `${env.GITHUB_SERVER_URL}/${env.GITHUB_REPOSITORY}/actions/runs/${env.GITHUB_RUN_ID}`
      : undefined;
  const body = issueBody(findings, { output, cwd: options.cwd, solution: options.solution, runUrl });
  const comment = commentBody(findings, { runUrl });

  console.error(`dependency-audit: ${findings.length} flagged package version(s).`);
  console.error(`dependency-audit: ${title}`);
  annotate(env, 'error', title);

  if (options.dryRun) dryRun(title, body, comment, options);
  else console.log(report(title, body, comment, options));
  return 1;
}

function annotate(env, level, message) {
  if (env.GITHUB_ACTIONS === 'true') console.log(`::${level}::${message.replace(/\r?\n/g, ' ')}`);
}

// Importable for the tests without running a restore.
if (process.argv[1] && import.meta.url === pathToFileURL(resolve(process.argv[1])).href) {
  main(process.argv.slice(2)).then(
    (code) => process.exit(code),
    (error) => {
      console.error(error);
      process.exit(2);
    },
  );
}
