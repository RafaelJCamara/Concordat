import { expect, test } from '@playwright/test';
import {
  COMPATIBILITY_MODES,
  COMPATIBILITY_SURFACES,
  type CompatibilityPolicy,
} from '../src/app/domain/registry/compatibility';
import {
  POLICY_WRITE_SCOPES,
  SCHEMA_WRITE_SCOPES,
  SCOPES,
  grants,
} from '../src/app/domain/identity/scope';
import { checkOwnerEdit, checkPolicyEdit } from '../src/app/domain/registry/subject-settings';
import {
  createSubjectRaw,
  issueKey,
  ownerCredential,
  patchSubjectRaw,
  putPolicyRaw,
  readAudit,
  readSubject,
  retireSubject,
  revokeKey,
} from './registry';
import { NOT_STRIPPED, STRIPPED } from './trim-table';

/**
 * What an owner and a compatibility policy have to be to change a subject, held against the
 * registry's own answer.
 *
 * <b>This is the proof behind "the form is never stricter or looser than the registry"</b> for
 * the two controls on a subject's page — the same rule `subject-name-parity.spec.ts` proves for
 * creating one, and for the same reason: a client that blocks a change the registry would take
 * stops somebody doing something they are entitled to, and one that lets through what it refuses
 * only costs a round trip. A unit test's expectation is its author's belief about the registry;
 * here every row is sent to the real `PATCH` and `PUT` and the verdicts compared.
 *
 * <b>A change cannot be dry-run, and a subject cannot be deleted.</b> So this makes two subjects
 * for the run (`acme.e2e.settingsparity.<timestamp>`, and a second it deprecates and then retires)
 * and changes them over and over; every owner row is another write to the same subject. The rows
 * are compared against what the registry holds *before* each one, because "unchanged" is a
 * verdict too: the form has nothing to send for an owner the registry would leave alone.
 *
 * It runs in plain Node, importing the domain functions directly: `domain/` is framework-free for
 * exactly this reason. The forms' own wiring is `subject-settings.spec.ts`.
 */
const ENVIRONMENT = 'dev';
const RUN = Date.now();
const MAIN = `acme.e2e.settingsparity.${RUN}`;
const LIFE = `acme.e2e.settingslife.${RUN}`;

const at = (...codes: number[]) => String.fromCharCode(...codes);
const hex = (code: number) => code.toString(16).padStart(4, '0');

/** One thing somebody could type into the owner field. */
interface OwnerRow {
  readonly label: string;
  readonly text: string;
}

const row = (label: string, text: string): OwnerRow => ({ label, text });

const OWNER_ROWS: readonly OwnerRow[] = [
  // What the registry puts no rule on.
  row('a person', 'alice'),
  row('a team', 'team-payments'),
  row('an email', 'alice@example.com'),
  row('a line break inside', 'a\nb'),
  row('a tab inside', 'a\tb'),
  row('another script', at(0x65e5, 0x672c, 0x8a9e)),
  row('an emoji', 'team ' + at(0xd83d, 0xde00)),
  row('upper case', 'TEAM-UPPER'),
  row('a single character', 'x'),

  // The length, counted after the trim.
  row('256 characters', 'o'.repeat(256)),
  row('257 characters', 'p'.repeat(257)),
  row('256 characters with padding on each side', ' ' + 'q'.repeat(256) + ' '),
  row('257 characters once the padding is gone', ' ' + 'r'.repeat(257) + ' '),

  // Nothing to say: the registry answers 200 and changes nothing, and the form has no request.
  row('empty', ''),
  row('blank', '   '),
  row('only characters the registry strips', at(0x85, 0x3000, 0x09, 0x0a)),

  // Padded on both sides with each character the registry strips: each is the owner
  // `strip<hex>` once trimmed, so a change that happened also shows what it stored.
  ...STRIPPED.filter((code) => code !== 0x20).map((code) =>
    row(
      `padded with U+${hex(code)}, which the registry strips`,
      at(code) + `strip${hex(code)}` + at(code),
    ),
  ),
  row('padded in front only', '  front'),
  row('padded behind only', 'behind  '),

  // The other side of the line: kept when they are on the end, so they are part of the owner.
  ...NOT_STRIPPED.flatMap((code) => [
    row(
      `U+${hex(code)} in front, which the registry does not strip`,
      at(code) + `keep${hex(code)}`,
    ),
    row(`U+${hex(code)} behind, which the registry does not strip`, `keep${hex(code)}b` + at(code)),
  ]),
];

test.describe('the owner and the policy against the registry', () => {
  test.beforeAll(async () => {
    const credential = await ownerCredential();

    for (const name of [MAIN, LIFE]) {
      const created = await createSubjectRaw(credential, ENVIRONMENT, {
        name,
        format: 'json',
        owner: 'e2e-start',
      });

      expect(created.status, `creating ${name}`).toBe(201);
    }
  });

  test('the owner: never stricter than the registry, and sends what it would store', async () => {
    const credential = await ownerCredential();
    const disagreements: string[] = [];

    for (const { label, text } of OWNER_ROWS) {
      const before = (await readSubject(credential, ENVIRONMENT, MAIN)).owner;
      const verdict = await patchSubjectRaw(credential, ENVIRONMENT, MAIN, { owner: text });
      const after = (await readSubject(credential, ENVIRONMENT, MAIN)).owner;
      const client = checkOwnerEdit(before, text);

      // A change is applied or refused with a code, and a crash is neither. It must not be able
      // to hide as "refused": whitespace used to answer 500, which is the row this is here for.
      if (verdict.status >= 500) {
        disagreements.push(`${label}: the registry answered ${verdict.status}.`);
      } else if (verdict.status === 400) {
        if (verdict.code !== 'actor_id_invalid') {
          disagreements.push(
            `${label}: refused as ${verdict.code}, which is not an owner refusal.`,
          );
        } else if (!client.findings.some((f) => f.severity === 'refusal') || client.send !== null) {
          disagreements.push(`${label}: the registry refuses it; the form would send it.`);
        }
      } else if (verdict.status !== 200) {
        disagreements.push(
          `${label}: the registry answered ${verdict.status}, which is not 200 or 400.`,
        );
      } else if (after !== before) {
        // The expensive direction: somebody could have made this change and the form would not.
        if (client.send === null) {
          disagreements.push(
            `${label}: the form has nothing to send (${client.findings.map((f) => f.code)}); ` +
              `the registry changed the owner to '${after}'.`,
          );
        } else if (client.send !== after) {
          disagreements.push(
            `${label}: the form would send '${client.send}'; the registry stored '${after}'.`,
          );
        }
      } else if (client.send !== null) {
        disagreements.push(
          `${label}: the form would send '${client.send}'; the registry left the owner alone.`,
        );
      }

      // A refusal changes nothing, and neither does a request with nothing in it.
      if (verdict.status >= 400 && after !== before) {
        disagreements.push(
          `${label}: refused, and the owner moved from '${before}' to '${after}'.`,
        );
      }
    }

    expect(disagreements, disagreements.join('\n')).toEqual([]);
  });

  test('an owner that is already the one held is nothing to send, and the registry agrees it is nothing', async () => {
    const credential = await ownerCredential();

    await patchSubjectRaw(credential, ENVIRONMENT, MAIN, { owner: 'steady' });

    for (const typed of ['steady', '  steady  ', at(0x85) + 'steady']) {
      const before = (await readSubject(credential, ENVIRONMENT, MAIN)).owner;

      expect(checkOwnerEdit(before, typed).send, JSON.stringify(typed)).toBeNull();
      expect((await patchSubjectRaw(credential, ENVIRONMENT, MAIN, { owner: typed })).status).toBe(
        200,
      );
      expect((await readSubject(credential, ENVIRONMENT, MAIN)).owner).toBe('steady');
    }

    // Case is part of an owner: the form lets this one through, and the registry stores it.
    expect(checkOwnerEdit('steady', 'Steady').send).toBe('Steady');
    await patchSubjectRaw(credential, ENVIRONMENT, MAIN, { owner: 'Steady' });
    expect((await readSubject(credential, ENVIRONMENT, MAIN)).owner).toBe('Steady');
  });

  test('a body no form builds is a refusal or a no-op and never a crash', async () => {
    const credential = await ownerCredential();
    const before = (await readSubject(credential, ENVIRONMENT, MAIN)).owner;
    const disagreements: string[] = [];
    const written = async () =>
      (await readAudit(credential, ENVIRONMENT, MAIN)).filter(
        (row) => row.action === 'SUBJECT_UPDATED',
      ).length;
    const entriesBefore = await written();

    const bodies: [label: string, body: unknown, expected: number][] = [
      ['an empty object', {}, 200],
      ['a null owner', { owner: null }, 200],
      ['a number for the owner', { owner: 5 }, 400],
      ['an array for the owner', { owner: ['a'] }, 400],
      ['a string for deprecate', { owner: 'x', deprecate: 'yes' }, 400],
      ['not JSON', 'owner=x', 400],
    ];

    for (const [label, body, expected] of bodies) {
      const verdict = await patchSubjectRaw(credential, ENVIRONMENT, MAIN, body);

      if (verdict.status !== expected) {
        disagreements.push(
          `${label}: expected ${expected}, the registry answered ${verdict.status}.`,
        );
      }
    }

    expect(disagreements, disagreements.join('\n')).toEqual([]);
    expect((await readSubject(credential, ENVIRONMENT, MAIN)).owner).toBe(before);
    // The two that answered 200 (`{}` and a null owner) changed nothing, and the trail is not told
    // otherwise: they used to write a row reading "owner changed". A refused body never did.
    expect(await written()).toBe(entriesBefore);
  });

  test('a lone surrogate in an owner is refused as an unreadable request, and the form lets it through', async () => {
    // The one place the form is looser, and it costs a round trip and nothing else: the request
    // envelope is refused with a bare 400 before any rule runs, so it carries no code for the form
    // to have agreed with. Measured here so that it is a known case and not a surprise.
    const credential = await ownerCredential();
    const before = (await readSubject(credential, ENVIRONMENT, MAIN)).owner;
    const typed = 'team ' + at(0xd800);
    const verdict = await patchSubjectRaw(credential, ENVIRONMENT, MAIN, { owner: typed });

    expect(verdict.status).toBe(400);
    expect(verdict.code).toBeNull();
    expect(checkOwnerEdit(before, typed).send).toBe(typed);
    expect((await readSubject(credential, ENVIRONMENT, MAIN)).owner).toBe(before);
  });

  test('the policy: every mode with every surface is accepted, answered and stored as sent', async () => {
    const credential = await ownerCredential();
    const disagreements: string[] = [];

    for (const mode of COMPATIBILITY_MODES) {
      for (const surface of COMPATIBILITY_SURFACES) {
        const before = (await readSubject(credential, ENVIRONMENT, MAIN)).compatibilityPolicy;
        const verdict = await putPolicyRaw(credential, ENVIRONMENT, MAIN, { mode, surface });
        const after = (await readSubject(credential, ENVIRONMENT, MAIN)).compatibilityPolicy;
        const client = checkPolicyEdit(before as CompatibilityPolicy, { mode, surface });
        const label = `${mode} / ${surface}`;

        if (verdict.status !== 200) {
          disagreements.push(`${label}: the registry answered ${verdict.status} ${verdict.code}.`);
        } else if (verdict.policy?.mode !== mode || verdict.policy.surface !== surface) {
          disagreements.push(`${label}: the registry answered ${JSON.stringify(verdict.policy)}.`);
        } else if (after.mode !== mode || after.surface !== surface) {
          disagreements.push(`${label}: stored as ${JSON.stringify(after)}.`);
        }

        // Unless it is what the subject already has, the form sends exactly this.
        const same = before.mode === mode && before.surface === surface;

        if (
          same
            ? client.send !== null
            : JSON.stringify(client.send) !== JSON.stringify({ mode, surface })
        ) {
          disagreements.push(`${label}: the form would send ${JSON.stringify(client.send)}.`);
        }
      }
    }

    expect(disagreements, disagreements.join('\n')).toEqual([]);
  });

  test('the policy: half of one is refused by both, in either direction, and changes nothing', async () => {
    const credential = await ownerCredential();
    const disagreements: string[] = [];
    await putPolicyRaw(credential, ENVIRONMENT, MAIN, { mode: 'FULL', surface: 'WIRE' });
    const before = (await readSubject(credential, ENVIRONMENT, MAIN)).compatibilityPolicy;

    const halves: [string | null, string | null][] = [
      ...COMPATIBILITY_MODES.map((mode): [string, null] => [mode, null]),
      ...COMPATIBILITY_SURFACES.map((surface): [null, string] => [null, surface]),
    ];

    for (const [mode, surface] of halves) {
      const verdict = await putPolicyRaw(credential, ENVIRONMENT, MAIN, { mode, surface });
      const client = checkPolicyEdit(before as CompatibilityPolicy, {
        mode: mode as CompatibilityPolicy['mode'],
        surface: surface as CompatibilityPolicy['surface'],
      });
      const label = `${mode ?? 'no mode'} / ${surface ?? 'no surface'}`;

      if (verdict.status !== 400 || verdict.code !== 'invalid_request') {
        disagreements.push(`${label}: the registry answered ${verdict.status} ${verdict.code}.`);
      }

      if (client.send !== null || !client.findings.some((f) => f.severity === 'refusal')) {
        disagreements.push(`${label}: the form would send it, or refuses nothing.`);
      }
    }

    expect(disagreements, disagreements.join('\n')).toEqual([]);
    expect((await readSubject(credential, ENVIRONMENT, MAIN)).compatibilityPolicy).toEqual(before);
  });

  test('the policy: clearing it is a request of two nulls, and inheriting twice is nothing to send', async () => {
    const credential = await ownerCredential();
    await putPolicyRaw(credential, ENVIRONMENT, MAIN, { mode: 'FORWARD', surface: 'SOURCE' });

    const pinned = (await readSubject(credential, ENVIRONMENT, MAIN)).compatibilityPolicy;
    const toInherit = checkPolicyEdit(pinned as CompatibilityPolicy, { mode: null, surface: null });

    expect(toInherit.send).toEqual({ mode: null, surface: null });

    const verdict = await putPolicyRaw(credential, ENVIRONMENT, MAIN, {
      mode: null,
      surface: null,
    });

    expect(verdict.status).toBe(200);
    expect(verdict.policy).toEqual({ mode: null, surface: null });

    const inheriting = (await readSubject(credential, ENVIRONMENT, MAIN)).compatibilityPolicy;

    expect(inheriting).toEqual({ mode: null, surface: null });
    expect(
      checkPolicyEdit(inheriting as CompatibilityPolicy, { mode: null, surface: null }).send,
    ).toBeNull();
  });

  test('what a select cannot send is refused or read the way the registry says, and is held here', async () => {
    // The form only ever sends a token from the list, so none of this can come from it. It is
    // measured so that the day a select becomes a text box the answers are already known — and
    // so the registry's own refusals (an undefined number, a comma list) have a client-side
    // test, which the registry fix that added them had only on its own side.
    const credential = await ownerCredential();
    await putPolicyRaw(credential, ENVIRONMENT, MAIN, { mode: 'FULL', surface: 'WIRE' });
    const held = (await readSubject(credential, ENVIRONMENT, MAIN)).compatibilityPolicy;
    const disagreements: string[] = [];

    const refused: [string, unknown][] = [
      ['an undefined mode number', { mode: '99', surface: 'WIRE' }],
      ['an undefined surface number', { mode: 'FULL', surface: '99' }],
      ['zero, which is no member', { mode: '0', surface: 'WIRE' }],
      ['a comma list of modes', { mode: 'BACKWARD,FORWARD', surface: 'WIRE' }],
      ['a comma list of surfaces', { mode: 'FULL', surface: 'WIRE,SOURCE' }],
      ['a negative number', { mode: '-1', surface: 'WIRE' }],
      ['an unknown word', { mode: 'SIDEWAYS', surface: 'WIRE' }],
      ['a mode and a blank surface', { mode: 'FULL', surface: '   ' }],
    ];

    for (const [label, body] of refused) {
      const verdict = await putPolicyRaw(credential, ENVIRONMENT, MAIN, body);

      if (verdict.status !== 400 || verdict.code !== 'invalid_request') {
        disagreements.push(`${label}: the registry answered ${verdict.status} ${verdict.code}.`);
      }
    }

    // Not a code, a type: the request cannot be read, and that is a refusal and not a crash.
    for (const [label, body] of [
      ['a number where a token goes', { mode: 5, surface: 'WIRE' }],
      ['not JSON', 'mode=FULL'],
    ] as [string, unknown][]) {
      const verdict = await putPolicyRaw(credential, ENVIRONMENT, MAIN, body);

      if (verdict.status !== 400) {
        disagreements.push(`${label}: the registry answered ${verdict.status}.`);
      }
    }

    expect(disagreements, disagreements.join('\n')).toEqual([]);
    expect((await readSubject(credential, ENVIRONMENT, MAIN)).compatibilityPolicy).toEqual(held);

    // And what it reads as the policy it means, which a select never sends.
    const spellings: [string, { mode: string; surface: string }, CompatibilityPolicy][] = [
      ['lower case', { mode: 'backward', surface: 'wire' }, { mode: 'BACKWARD', surface: 'WIRE' }],
      [
        'with the underscores left out',
        { mode: 'BACKWARDTRANSITIVE', surface: 'WIREJSON' },
        { mode: 'BACKWARD_TRANSITIVE', surface: 'WIRE_JSON' },
      ],
      [
        'with padding',
        { mode: ' FORWARD ', surface: ' SOURCE ' },
        { mode: 'FORWARD', surface: 'SOURCE' },
      ],
      [
        'as numbers that are members',
        { mode: '2', surface: '1' },
        { mode: 'BACKWARD', surface: 'WIRE' },
      ],
    ];

    for (const [label, body, expected] of spellings) {
      const verdict = await putPolicyRaw(credential, ENVIRONMENT, MAIN, body);

      expect(verdict.status, label).toBe(200);
      expect(verdict.policy, label).toEqual(expected);
    }

    // Two blank members are both absent, which clears: what the form sends as two nulls.
    expect(
      (await putPolicyRaw(credential, ENVIRONMENT, MAIN, { mode: ' ', surface: ' ' })).policy,
    ).toEqual({ mode: null, surface: null });
  });

  test('the two controls are gated on different scopes, and the client reads the registry’s', async () => {
    // One key per scope the registry grants, each holding only that. The question is whether the
    // lists the page reads (`SCHEMA_WRITE_SCOPES` for the owner, `POLICY_WRITE_SCOPES` for the
    // policy) admit exactly the keys the registry does — `subject:admin` implies `subject:write`
    // server-side, and the lists name both, so the page has to agree without being told.
    const credential = await ownerCredential();
    const keys: { id: string; scope: string; secret: string }[] = [];
    const disagreements: string[] = [];

    try {
      for (const scope of SCOPES) {
        const key = await issueKey(credential, `settings-parity-${scope}-${RUN}`, [scope]);

        keys.push({ ...key, scope });
      }

      for (const { scope, secret } of keys) {
        const owner = await patchSubjectRaw(secret, ENVIRONMENT, MAIN, { owner: `by-${scope}` });
        const policy = await putPolicyRaw(secret, ENVIRONMENT, MAIN, { mode: null, surface: null });
        const mayOwner = grants([scope], SCHEMA_WRITE_SCOPES);
        const mayPolicy = grants([scope], POLICY_WRITE_SCOPES);

        if ((owner.status === 200) !== mayOwner) {
          disagreements.push(
            `${scope}: the registry answered the owner change ${owner.status}; the page ` +
              `${mayOwner ? 'offers' : 'does not offer'} it.`,
          );
        }

        if ((policy.status === 200) !== mayPolicy) {
          disagreements.push(
            `${scope}: the registry answered the policy change ${policy.status}; the page ` +
              `${mayPolicy ? 'offers' : 'does not offer'} it.`,
          );
        }

        for (const verdict of [owner, policy]) {
          if (verdict.status !== 200 && verdict.status !== 403) {
            disagreements.push(`${scope}: answered ${verdict.status}, not 200 or 403.`);
          }

          if (verdict.status === 403 && verdict.code !== 'insufficient_scope') {
            disagreements.push(`${scope}: refused as ${verdict.code}.`);
          }
        }
      }
    } finally {
      for (const { id } of keys) {
        await revokeKey(credential, id);
      }
    }

    expect(disagreements, disagreements.join('\n')).toEqual([]);
    // The two lists are different, which is what lets a writer see one control and not the other.
    expect(SCHEMA_WRITE_SCOPES).not.toEqual(POLICY_WRITE_SCOPES);
  });

  test('a deprecated subject takes both changes and a retired one refuses both with 409', async () => {
    // Why the page offers both for DEPRECATED and neither for RETIRED, from the registry's own
    // answers. Retired is terminal, so a page that hides the controls for it cannot be wrong
    // because it is stale.
    const credential = await ownerCredential();

    expect((await patchSubjectRaw(credential, ENVIRONMENT, LIFE, { deprecate: true })).status).toBe(
      200,
    );
    expect((await readSubject(credential, ENVIRONMENT, LIFE)).lifecycle).toBe('DEPRECATED');

    const owner = await patchSubjectRaw(credential, ENVIRONMENT, LIFE, {
      owner: 'after-deprecation',
    });
    const policy = await putPolicyRaw(credential, ENVIRONMENT, LIFE, {
      mode: 'FULL',
      surface: 'WIRE',
    });

    expect([owner.status, policy.status]).toEqual([200, 200]);
    // And a change is not a re-activation: it is still deprecated.
    expect((await readSubject(credential, ENVIRONMENT, LIFE)).lifecycle).toBe('DEPRECATED');

    await retireSubject(credential, ENVIRONMENT, LIFE);

    // Retiring is recorded as whoever did it, as the deprecation before it was, and not as the
    // owner of the subject it ended (which it named until the owner was no longer the actor).
    const trail = await readAudit(credential, ENVIRONMENT, LIFE);
    const retirement = trail.filter((row) => row.action === 'SUBJECT_RETIRED');
    const deprecation = trail.find((row) => row.detail === 'deprecated');

    expect(retirement).toHaveLength(1);
    expect(deprecation).toBeDefined();
    expect(retirement[0]!.actor).toBe(deprecation!.actor);
    expect(retirement[0]!.actor).not.toBe('after-deprecation');

    const retiredOwner = await patchSubjectRaw(credential, ENVIRONMENT, LIFE, {
      owner: 'too-late',
    });
    const retiredPolicy = await putPolicyRaw(credential, ENVIRONMENT, LIFE, {
      mode: 'NONE',
      surface: 'WIRE',
    });

    expect([retiredOwner.status, retiredOwner.code]).toEqual([409, 'lifecycle_transition_invalid']);
    expect([retiredPolicy.status, retiredPolicy.code]).toEqual([
      409,
      'lifecycle_transition_invalid',
    ]);

    const after = await readSubject(credential, ENVIRONMENT, LIFE);

    expect(after.owner).toBe('after-deprecation');
    expect(after.compatibilityPolicy).toEqual({ mode: 'FULL', surface: 'WIRE' });
  });
});
