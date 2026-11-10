// Fills one environment with a few thousand subjects, through the registry's own HTTP API, so the
// subject list can be looked at (and measured) the size it will one day be.
//
// Why this exists. The subject list is paged (ADR-045), can be searched by prefix (ADR-038) and can be
// ordered newest first (ADR-049), and every one of those decisions was argued from the plan the database
// chose at some size. A test with a dozen rows proves the order and the arithmetic; it cannot say whether
// the first page of an environment with five thousand subjects is fast, or whether the index is used. This
// is the environment to ask that of, and it is also the one to open the web app on when a screen "feels"
// slow: the subjects page, the approvals queue and the dashboard all read it.
//
// What it makes:
//   - N subjects (default 5000) named like a real estate's, under a handful of shared prefixes
//     (acme.orders.*, acme.billing.*, acme.shipping.*, ... globex.*, initech.*), in a skewed mix so
//     some prefixes are broad and one or two are narrow;
//   - a few of them (default 25) with a breaking second version, held at the approval gate, so the
//     approval queue and the "awaiting approval" pill have something to show;
//   - creation times spread over the last 90 days, with groups of subjects sharing the very same
//     instant, so the tiebreaker of "newest first" is exercised and not only specified.
//
// How. Subjects and versions go through the API (POST subjects, POST versions): the real code path, the
// real validation, the real audit entries. That is fast enough: a few milliseconds each, 5,000 in under a
// minute at 16 in flight. What the API cannot do is back-date a subject, because the registry stamps
// CreatedAt from its own clock. So the creation times are rewritten afterwards by ONE UPDATE statement
// that is piped to a command you name with --psql (for example `docker exec -i my-pg psql -U postgres -d
// concordat`). Without --psql nothing is rewritten and every subject was created in the same minute, which
// is still a fine list to page but a poor one to ask "newest" of; the script says so.
//
//   node scripts/seed-subjects.mjs --key KEY                          5000 subjects into `perf`, on :5062
//   node scripts/seed-subjects.mjs --key KEY --count 20000 --env big
//   node scripts/seed-subjects.mjs --key KEY --psql "docker exec -i concordat-a-pg psql -U postgres -d concordat"
//
//   --registry URL   the registry (default http://localhost:5062, or $CONCORDAT_REGISTRY)
//   --key KEY        an API key with subject:write and env:write (default $CONCORDAT_API_KEY)
//   --env NAME       the environment, created if it is not there (default perf)
//   --count N        how many subjects (default 5000)
//   --held N         how many get a breaking version held for approval (default 25)
//   --concurrency N  requests in flight (default 16)
//   --psql COMMAND   a command that reads SQL on stdin, to spread the creation times (see above)
//
// Safe to run twice, or after a run that stopped: a subject that already exists is given only the versions it
// lacks, and nothing else. Exit codes: 0 done; 1 a request failed; 64 bad arguments.

import { spawnSync } from 'node:child_process';
import { parseArgs } from 'node:util';

const { values } = parseArgs({
  options: {
    registry: { type: 'string', default: process.env.CONCORDAT_REGISTRY ?? 'http://localhost:5062' },
    key: { type: 'string', default: process.env.CONCORDAT_API_KEY },
    env: { type: 'string', default: 'perf' },
    count: { type: 'string', default: '5000' },
    held: { type: 'string', default: '25' },
    concurrency: { type: 'string', default: '16' },
    psql: { type: 'string' },
  },
});

const count = Number(values.count);
const held = Number(values.held);
const concurrency = Number(values.concurrency);

if (!values.key || !Number.isInteger(count) || count < 1 || !Number.isInteger(held) || held < 0 ||
    !Number.isInteger(concurrency) || concurrency < 1 || !/^[a-z0-9][a-z0-9-]*$/.test(values.env)) {
  console.error('usage: node scripts/seed-subjects.mjs --key KEY [--registry URL] [--env NAME] [--count N] [--held N] [--concurrency N] [--psql COMMAND]');
  process.exit(64);
}

const root = values.registry.replace(/\/+$/, '');
const base = `${root}/v1/environments/${values.env}`;
const headers = { Authorization: `Bearer ${values.key}`, 'Content-Type': 'application/json' };

// The namespaces, with weights: three broad ones (about a fifth each), then narrower ones, then two rare
// ones. A prefix search on a broad one returns hundreds of subjects, on a narrow one a handful.
const NAMESPACES = [
  ['acme.orders', 22],
  ['acme.billing', 20],
  ['acme.shipping', 16],
  ['acme.inventory', 12],
  ['acme.payments', 10],
  ['globex.crm', 8],
  ['globex.hr', 6],
  ['initech.tps', 4],
  ['initech.audit', 1.5],
  ['umbrella.labs', 0.5],
];
const TOTAL_WEIGHT = NAMESPACES.reduce((sum, [, weight]) => sum + weight, 0);

/** A deterministic 32-bit mix, so two runs name the same subjects and a re-run finds them again. */
function mix(n) {
  let x = (n + 0x9e3779b9) | 0;
  x = Math.imul(x ^ (x >>> 16), 0x85ebca6b);
  x = Math.imul(x ^ (x >>> 13), 0xc2b2ae35);
  return (x ^ (x >>> 16)) >>> 0;
}

const EVENTS = ['Created', 'Updated', 'Cancelled', 'Shipped', 'Refunded', 'Failed', 'Approved', 'Expired'];

function subjectName(i) {
  let pick = (mix(i) / 2 ** 32) * TOTAL_WEIGHT;
  let namespace = NAMESPACES[0][0];
  for (const [ns, weight] of NAMESPACES) {
    namespace = ns;
    if ((pick -= weight) < 0) break;
  }
  // The index keeps the name unique whatever the mix chose; the event word makes it read like a message.
  return `${namespace}.${EVENTS[mix(i + 7) % EVENTS.length]}${i}`;
}

const V1 = JSON.stringify({ type: 'object', properties: { id: { type: 'string' } } });
// Adds a required property: backward-breaking, so under the default policy it is held at the gate.
const V2_BREAKING = JSON.stringify({
  type: 'object',
  properties: { id: { type: 'string' } },
  required: ['id'],
});

/**
 * One request. The first registrations of a schema race to insert it (schemas are content-addressed and
 * global, ADR-015, and every seeded subject shares two), and the registry answers the loser
 * 409 concurrent_write_conflict, which says retry. So it is retried.
 */
async function call(method, url, body) {
  for (let attempt = 0; ; attempt++) {
    const response = await fetch(url, { method, headers, body: body === undefined ? undefined : JSON.stringify(body) });

    if (response.status === 409 && attempt < 8 && (await response.clone().text()).includes('concurrent_write_conflict')) {
      await new Promise((resolve) => setTimeout(resolve, 25 * (attempt + 1)));
      continue;
    }

    return response;
  }
}

async function must(response, what, ...ok) {
  if (ok.includes(response.status)) return;
  console.error(`${what}: ${response.status} ${await response.text()}`);
  process.exit(1);
}

/** Runs `work(i)` for 0..n-1 with at most `limit` in flight. */
async function pool(n, limit, work) {
  let next = 0;
  const lane = async () => {
    while (next < n) {
      const i = next++;
      await work(i);
    }
  };
  await Promise.all(Array.from({ length: Math.min(limit, n) }, lane));
}

const started = Date.now();

// The environment, created if it is not there (409 means it is).
await must(await call('POST', `${root}/v1/environments`, { name: values.env }), 'create environment', 201, 409);

// Which subjects get a held version: the first `held` of a deterministic shuffle, so they are scattered across
// namespaces and across the name order rather than being the first N created.
const heldSet = new Set(
  Array.from({ length: count }, (_, i) => i)
    .sort((a, b) => mix(a + 99) - mix(b + 99))
    .slice(0, Math.min(held, count)),
);

let created = 0;
let skipped = 0;
let done = 0;

await pool(count, concurrency, async (i) => {
  const name = subjectName(i);
  const made = await call('POST', `${base}/subjects`, { name, format: 'json', owner: `team-${mix(i + 3) % 12}` });
  await must(made, `create ${name}`, 201, 409);

  // A subject that is already there is looked at, not skipped: an earlier run that stopped half way may
  // have created it and not given it its versions. It is given only what it lacks.
  let versions = 0;
  if (made.status === 409) {
    skipped++;
    const existing = await call('GET', `${base}/subjects/${name}`);
    await must(existing, `read ${name}`, 200);
    versions = (await existing.json()).versions.length;
  } else {
    created++;
  }

  if (versions === 0) {
    await must(await call('POST', `${base}/subjects/${name}/versions`, { schema: V1, registeredBy: 'seed' }), `v1 ${name}`, 201, 200);
    versions = 1;
  }

  if (heldSet.has(i) && versions === 1) {
    await must(
      await call('POST', `${base}/subjects/${name}/versions`, {
        schema: V2_BREAKING,
        semanticVersion: '2.0.0',
        changelog: 'id is now required',
        registeredBy: 'seed',
      }),
      `held ${name}`,
      201,
      200,
    );
  }

  if (++done % 500 === 0) {
    console.log(`  ${done} / ${count}`);
  }
});

console.log(`${created} subjects created, ${skipped} already there, in ${((Date.now() - started) / 1000).toFixed(1)} s`);

if (!values.psql) {
  console.log(
    'Creation times are the registry\'s clock, all within the same minute. Pass --psql to spread them over 90 days.',
  );
} else {
  // Spread the creation times: the subjects ranked by a scrambled key (so the order they were created in,
  // the name order and the time order all differ), seven to a group, every group on one instant, the groups
  // a few minutes apart and reaching back about 90 days. Seven to a group is the ties the name tiebreaker
  // exists for. One statement, so it is atomic.
  const sql = `
    WITH ranked AS (
      SELECT subject_id,
             row_number() OVER (ORDER BY md5(name)) AS n
      FROM subject
      WHERE environment_id = (SELECT environment_id FROM environment WHERE name = '${values.env}')
    ), total AS (SELECT count(*) AS c FROM ranked)
    UPDATE subject s
       SET created_at = date_trunc('second', now() - interval '90 days' * (((r.n - 1) / 7) * 7.0 / t.c))
      FROM ranked r, total t
     WHERE s.subject_id = r.subject_id;
    ANALYZE subject;
  `;
  const [command, ...args] = values.psql.split(/\s+/);
  const run = spawnSync(command, args, { input: sql, encoding: 'utf8' });

  if (run.status !== 0) {
    console.error(`--psql failed (${run.status}): ${run.stderr}`);
    process.exit(1);
  }

  console.log('Creation times spread over the last 90 days, seven subjects to each instant.');
}
