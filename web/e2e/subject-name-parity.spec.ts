import { expect, test } from '@playwright/test';
import { checkNewSubject, type NewSubjectDraft } from '../src/app/domain/registry/new-subject';
import { isRegistryWhitespace } from '../src/app/domain/registry/registry-text';
import { checkSubjectName, slugSubjectName } from '../src/app/domain/registry/subject-name';
import { accepted, createSubjectRaw, ensureEnvironment, ownerCredential } from './registry';
import { NOT_STRIPPED, STRIPPED } from './trim-table';

/**
 * What a new subject's fields have to be, held against the registry's own answer.
 *
 * <b>This is the proof behind "the form is never stricter or looser than the registry"</b> for
 * the create screen — the same rule `schema-parity.spec.ts` proves for the editor, and for the
 * same reason: a client that blocks a name the registry would take stops somebody creating a
 * subject they are entitled to, and one that lets through what it refuses only costs a round
 * trip. Neither shows in a unit test, whose expectation is its author's belief about the
 * registry. Here every row is sent to the real create endpoint and the verdicts compared.
 *
 * <b>It is not a dry run, and that shapes everything.</b> There is no way to ask whether a create
 * would succeed without doing it, and a subject cannot be undone — so this runs in an environment
 * of its own (`e2e-names`), every accepted row is a subject that stays there, and the table is
 * *fixed* rather than named per run: a second run finds each accepted name already taken and the
 * registry answers 409 `subject_already_exists`, which is only reached once the name has passed
 * validation. So a 409 is as good as a 201 for "the registry took this name", and the 409's own
 * message names what the registry made of it after its trim, which is how a re-run still proves
 * the trim and not only the first.
 *
 * It runs in plain Node, importing the domain functions directly. `domain/` is framework-free for
 * exactly this reason, and a parity table driven through the form would be a hundred slow typing
 * tests that prove the same thing the unit tests plus this one prove faster; the form's own
 * wiring is `new-subject.spec.ts`.
 */
const ENVIRONMENT = 'e2e-names';

const at = (...codes: number[]) => String.fromCharCode(...codes);
const hex = (code: number) => code.toString(16).padStart(4, '0');

interface Row {
  readonly label: string;
  readonly text: string;
  /**
   * The registry cannot read the request at all, so no name rule is ever reached.
   *
   * A lone surrogate is the case: `JSON.stringify` writes it as an escape, which is well-formed
   * JSON, and the registry's request reader refuses the *envelope* with a bare 400 before the
   * name is looked at. That is a refusal and never a crash, which is what is asserted; it carries
   * no `concordatCode` because no rule of the registry's own was applied.
   */
  readonly unreadable?: boolean;
}

/** A name row, with the name text as sent. */
const row = (label: string, text: string, unreadable = false): Row => ({
  label,
  text,
  ...(unreadable ? { unreadable } : {}),
});

const NAME_ROWS: readonly Row[] = [
  // The grammar.
  row('a plain word', 'names.plain'),
  row('a dotted path', 'names.orders.OrderCreated'),
  row('digits and underscores', 'names.9.9_x'),
  row('a segment of one underscore', 'names._'),
  row('a leading underscore', 'names._internal'),
  row('digits first', '9names.9'),
  row('upper case', 'NAMES.UPPER'),
  row('a segment called latest', 'names.latest'),
  row('a segment called versions', 'names.versions'),
  row('a name called new', 'names.new'),

  // Length, counted after the trim.
  row('exactly 512 characters', 'n' + 'a'.repeat(511)),
  row('513 characters', 'n' + 'a'.repeat(512)),
  row('512 characters with padding on each side', ' ' + 'p' + 'b'.repeat(511) + ' '),
  row('513 characters once the padding is gone', ' ' + 'q' + 'b'.repeat(512) + ' '),

  // What it refuses.
  row('empty', ''),
  row('blank', '   '),
  row('only a dot', '.'),
  row('a leading dot', '.names.dot'),
  row('a trailing dot', 'names.dot.'),
  row('a double dot', 'names..dot'),
  row('a hyphen', 'names-hyphen'),
  row('a space inside', 'names space'),
  row('a slash', 'names/slash'),
  row('a backslash', 'names' + at(92) + 'slash'),
  row('a colon', 'names:colon'),
  row('a plus', 'names+plus'),
  row('a comma', 'names,comma'),
  row('an accented letter', 'names.caf' + at(0xe9)),
  row('an accented letter in decomposed form', 'names.cafe' + at(0x301)),
  row('a CJK character', 'names.' + at(0x4e2d)),
  row('an Arabic-Indic digit', 'names.' + at(0x663)),
  row('a full-width letter', 'names.' + at(0xff41)),
  row('a Kelvin sign, which /w/iu would call a word character', 'names.' + at(0x212a)),
  row('a long s, the other one', 'names.' + at(0x17f)),
  row('a NUL inside', 'names.nul' + at(0) + 'x'),
  row('a newline inside', 'names.line\nbreak'),
  row('a tab inside', 'names.tab\tinside'),
  row('a percent escape', 'names.%41'),
  row('a dollar', 'names.$x'),
  row('an at sign', 'names.@x'),
  row('an emoji', 'names.' + at(0xd83d, 0xde00)),
  row('a lone high surrogate', 'names.' + at(0xd800), true),

  // Whitespace: padded on both sides, each is the name `names.ws<hex>` once trimmed.
  ...STRIPPED.map((code) =>
    row(
      `padded with U+${hex(code)}, which the registry strips`,
      at(code) + `names.ws${hex(code)}` + at(code),
    ),
  ),
  // And leading only, trailing only: the trim is per end.
  row('padded in front only', '  names.front'),
  row('padded behind only', 'names.behind  '),
  row('padded with a mix', ' \t' + at(0x85, 0xa0) + 'names.mixed' + at(0x3000) + '\n'),

  // Characters on the other side of the line.
  ...NOT_STRIPPED.flatMap((code) => [
    row(
      `U+${hex(code)} in front, which the registry does not strip`,
      at(code) + `names.nopad${hex(code)}`,
    ),
    row(
      `U+${hex(code)} behind, which the registry does not strip`,
      `names.nopad${hex(code)}b` + at(code),
    ),
  ]),
];

/**
 * Titles for the slug, with what is expected of the result.
 *
 * The slug's job is the narrower one: whatever it produces is a name the registry takes, or
 * nothing. So each non-empty answer is created for real.
 */
const TITLES: readonly string[] = [
  'Order created',
  'order-created',
  '  Order   Created  ',
  'Order, Created!',
  'acme orders / Order Created',
  'acme.orders.Order Created',
  'acme . orders',
  '..acme..orders..',
  'Caf' + at(0xe9) + ' orders',
  'na' + at(0xef) + 've',
  at(0xff21) + 'cme',
  'order (v2)',
  '100% sure',
  'Acme.Orders+Created',
  'acme:orders:created',
  '_internal events',
  'x._y z',
  'Stra' + at(0xdf) + 'e',
  at(0xd83d, 0xde00) + ' launch',
  'Orders, refunds and returns',
];

/** A draft with every field valid, for varying one at a time. */
const base = (overrides: Partial<NewSubjectDraft>): NewSubjectDraft => ({
  name: 'names.base',
  format: 'json',
  owner: 'e2e',
  contentModel: 'OPEN',
  mode: null,
  surface: null,
  ...overrides,
});

test.describe('the create form against the registry', () => {
  test.beforeAll(async () => {
    await ensureEnvironment(await ownerCredential(), ENVIRONMENT);
  });

  test('the name: never stricter than the registry, and it says what the registry says', async () => {
    const credential = await ownerCredential();
    const disagreements: string[] = [];

    for (const { label, text, unreadable } of NAME_ROWS) {
      const verdict = await createSubjectRaw(credential, ENVIRONMENT, {
        name: text,
        format: 'json',
        owner: 'e2e',
      });
      const client = checkSubjectName(text);

      // A document is accepted or refused with a code, and a name is no different: a crash is
      // neither, and it must not be able to hide as "refused".
      if (verdict.status >= 500) {
        disagreements.push(`${label}: the registry answered ${verdict.status}.`);
        continue;
      }

      if (unreadable) {
        if (verdict.status !== 400 || client.ok) {
          disagreements.push(
            `${label}: expected a refused request and a refusing form; got ${verdict.status} ` +
              `and ${client.ok ? 'an accepting' : 'a refusing'} form.`,
          );
        }
      } else if (accepted(verdict)) {
        if (!client.ok) {
          // The expensive direction: somebody could have created this and cannot.
          disagreements.push(
            `${label}: the form refuses it (${client.message}); the registry took it as '${verdict.name}'.`,
          );
        } else if (client.name !== verdict.name) {
          disagreements.push(
            `${label}: the form would send '${client.name}'; the registry holds '${verdict.name}'.`,
          );
        }
      } else if (verdict.code !== 'subject_name_invalid') {
        disagreements.push(
          `${label}: refused as ${verdict.code} (${verdict.status}), which is not a name refusal.`,
        );
      } else if (client.ok) {
        disagreements.push(`${label}: the form accepts it; the registry refuses it.`);
      }
    }

    expect(disagreements, disagreements.join('\n')).toEqual([]);
  });

  test('the whitespace table is the registry’s, character by character', async () => {
    // The previous test shows each row agrees; this says the *table itself* is the one that was
    // measured, so a character added to it without a row above is a failure rather than a guess.
    const measured = new Set([...STRIPPED]);
    const claimed = new Set<number>();

    for (let code = 0; code < 0x10000; code++) {
      if (isRegistryWhitespace(code)) {
        claimed.add(code);
      }
    }

    const unmeasured = [...claimed].filter((code) => !measured.has(code));

    // The one exception is the range ends the table writes as a span rather than listing 2000
    // to 200a one by one; they are all in STRIPPED above. Anything else is unmeasured.
    expect(unmeasured.map((code) => `U+${hex(code)}`)).toEqual([]);
    expect(
      [...measured].filter((code) => !claimed.has(code)).map((code) => `U+${hex(code)}`),
    ).toEqual([]);
    for (const code of NOT_STRIPPED) {
      expect(isRegistryWhitespace(code), `U+${hex(code)}`).toBe(false);
    }
  });

  test('the slug only ever suggests a name the registry takes', async () => {
    const credential = await ownerCredential();
    const disagreements: string[] = [];

    for (const title of TITLES) {
      const slug = slugSubjectName(title);

      if (slug === '') {
        continue;
      }

      const verdict = await createSubjectRaw(credential, ENVIRONMENT, {
        name: slug,
        format: 'json',
        owner: 'e2e',
      });

      if (!accepted(verdict)) {
        disagreements.push(
          `${JSON.stringify(title)} -> ${JSON.stringify(slug)}: the registry answered ` +
            `${verdict.status} ${verdict.code ?? ''}.`,
        );
      }
    }

    expect(disagreements, disagreements.join('\n')).toEqual([]);
  });

  test('a title with nothing a name can be made of suggests nothing', () => {
    for (const title of ['', '   ', '!!!', at(0x4e2d, 0x6587), '...']) {
      expect(slugSubjectName(title)).toBe('');
    }
  });

  test('the owner and the policy: the same refusals, with the same codes', async () => {
    const credential = await ownerCredential();
    const disagreements: string[] = [];

    /** The registry's code for each field a refusal can be about. */
    const codeFor = {
      name: 'subject_name_invalid',
      owner: 'actor_id_invalid',
      compatibility: 'invalid_request',
    };

    const rows: { label: string; draft: NewSubjectDraft }[] = [
      { label: 'an owner of one character', draft: base({ owner: 'e' }) },
      { label: 'an owner that is a team', draft: base({ owner: 'team-payments' }) },
      { label: 'an owner that is an email', draft: base({ owner: 'alice@example.com' }) },
      { label: 'an owner with a line break inside', draft: base({ owner: 'a\nb' }) },
      { label: 'an owner in another script', draft: base({ owner: at(0x65e5, 0x672c, 0x8a9e) }) },
      { label: 'an empty owner', draft: base({ owner: '' }) },
      { label: 'a blank owner', draft: base({ owner: '   ' }) },
      {
        label: 'an owner of only the stripped characters',
        draft: base({ owner: at(0x85, 0x3000, 0x09) }),
      },
      { label: 'an owner of 256 characters', draft: base({ owner: 'o'.repeat(256) }) },
      { label: 'an owner of 257 characters', draft: base({ owner: 'o'.repeat(257) }) },
      {
        label: 'an owner of 256 characters with padding',
        draft: base({ owner: ' ' + 'p'.repeat(256) + ' ' }),
      },
      {
        label: 'an owner of 257 once the padding is gone',
        draft: base({ owner: ' ' + 'q'.repeat(257) + ' ' }),
      },
      {
        label: 'an owner padded with a next-line character',
        draft: base({ owner: at(0x85) + 'padded' + at(0x85) }),
      },
      { label: 'an owner behind a byte-order mark', draft: base({ owner: at(0xfeff) + 'bom' }) },
      { label: 'a whole policy', draft: base({ mode: 'FULL_TRANSITIVE', surface: 'SOURCE' }) },
      { label: 'the weakest policy', draft: base({ mode: 'NONE', surface: 'WIRE' }) },
      { label: 'a mode and no surface', draft: base({ mode: 'BACKWARD' }) },
      { label: 'a surface and no mode', draft: base({ surface: 'WIRE_JSON' }) },
      { label: 'avro', draft: base({ format: 'avro' }) },
      { label: 'protobuf, closed', draft: base({ format: 'protobuf', contentModel: 'CLOSED' }) },
    ];

    for (const [index, { label, draft }] of rows.entries()) {
      // One subject name per row, so one row's refusal never depends on another's creation.
      const named = { ...draft, name: `names.fields.row${index}` };
      const verdict = await createSubjectRaw(credential, ENVIRONMENT, {
        name: named.name,
        format: named.format,
        owner: named.owner,
        contentModel: named.contentModel,
        ...(named.mode === null ? {} : { compatibilityMode: named.mode }),
        ...(named.surface === null ? {} : { compatibilitySurface: named.surface }),
      });
      const refusals = checkNewSubject(named, []).filter((f) => f.severity === 'refusal');

      if (verdict.status >= 500) {
        disagreements.push(`${label}: the registry answered ${verdict.status}.`);
      } else if (accepted(verdict) && refusals.length > 0) {
        disagreements.push(
          `${label}: the form refuses it (${refusals.map((f) => f.code).join(', ')}); the registry took it.`,
        );
      } else if (!accepted(verdict) && refusals.length === 0) {
        disagreements.push(
          `${label}: the form accepts it; the registry refused it as ${verdict.code}.`,
        );
      } else if (!accepted(verdict) && !refusals.some((f) => codeFor[f.field] === verdict.code)) {
        disagreements.push(
          `${label}: the registry says ${verdict.code}; the form says ${refusals.map((f) => f.code).join(', ')}.`,
        );
      }
    }

    expect(disagreements, disagreements.join('\n')).toEqual([]);
  });
});
