import { expect, test } from '@playwright/test';
import { dryRun, ensureEmptySubject, ownerCredential } from './registry';
import { findingCodes, setSchema, waitForSubject } from './schema-editor';

/**
 * The editor's refusals, held against the registry's own.
 *
 * <b>This is the proof behind "never stricter or looser than the registry".</b> A client that
 * blocks a submit the registry would have accepted stops somebody registering a valid schema;
 * one that lets through what the registry refuses only costs a round trip. Neither is checked
 * by a unit test, because a unit test's expectation is its author's belief about the registry.
 * Here every document is sent to the registry's dry-run endpoint first and the editor's verdict
 * is compared with the answer it actually gave.
 *
 * <b>Two directions, asserted separately, because they fail differently.</b>
 *
 * - *Never stricter:* whenever the editor says "the registry will refuse this", the registry
 *   refuses, and for the same reason.
 * - *Not looser, for the rules it claims:* whenever the registry refuses with a code the editor
 *   knows, the editor said so too. Rules the editor deliberately leaves to the registry are
 *   listed with the reason they are left — a Protobuf grammar, Avro's type rules — and for
 *   those the assertion is the opposite one: the editor stays quiet, so nobody is blocked by a
 *   check that is not the registry's.
 *
 * The registry only refuses, and the dry run writes nothing, so this leaves no versions behind.
 * It does create three empty subjects, once, named `acme.e2e.parity.*`.
 */

const ENVIRONMENT = 'dev';
const SUBJECTS = {
  json: 'acme.e2e.parity.json',
  avro: 'acme.e2e.parity.avro',
  protobuf: 'acme.e2e.parity.proto',
} as const;

type Format = keyof typeof SUBJECTS;

/** The codes the editor can say on its own, from the text alone. */
const CLIENT_CODES = new Set([
  'schema_too_large',
  'schema_malformed',
  'schema_dialect_unsupported',
]);

interface Case {
  readonly label: string;
  readonly text: string;
  /**
   * Whether the editor is expected to reproduce the registry's refusal. False for rules it
   * leaves to the registry on purpose, with `why` saying what they are.
   */
  readonly mirrored: boolean;
  readonly why?: string;
}

const nested = (levels: number) => '['.repeat(levels) + ']'.repeat(levels);
const nestedObjects = (levels: number) =>
  '{"a":'.repeat(levels - 1) + '{}' + '}'.repeat(levels - 1);

const ok = (label: string, text: string): Case => ({ label, text, mirrored: true });

const JSON_CASES: readonly Case[] = [
  ok('an empty object', '{}'),
  ok('a boolean schema', 'true'),
  ok('a bare scalar', '42'),
  ok('whitespace around the document', ' \n\t{}\r\n '),
  ok('unbalanced braces', '{"a":'),
  ok('a trailing comma', '{"a":1,}'),
  ok('a comment', '{"a":1 /* c */}'),
  ok('a line comment', '{"a":1}\n// c'),
  ok('single quotes', "{'a':1}"),
  ok('a leading zero', '{"a":01}'),
  ok('a leading plus', '{"a":+1}'),
  ok('a byte-order mark', '﻿{}'),
  ok('a duplicate key', '{"type":"object","type":"string"}'),
  ok('a duplicate key written with an escape', '{"a":1,"\\u0061":2}'),
  ok('a nested duplicate key', '{"properties":{"x":{"a":1,"a":2}}}'),
  ok('the same key in sibling objects', '{"x":{"id":1},"y":{"id":2}}'),
  ok('keys differing only in case', '{"Id":1,"id":2}'),
  ok('64 levels of arrays', nested(64)),
  ok('65 levels of arrays', nested(65)),
  ok('64 levels of objects', nestedObjects(64)),
  ok('65 levels of objects', nestedObjects(65)),
  ok('the 2020-12 dialect', '{"$schema":"https://json-schema.org/draft/2020-12/schema"}'),
  ok(
    'the 2020-12 dialect with a trailing #',
    '{"$schema":"https://json-schema.org/draft/2020-12/schema#"}',
  ),
  ok('draft-07', '{"$schema":"http://json-schema.org/draft-07/schema#"}'),
  ok('draft 2019-09', '{"$schema":"https://json-schema.org/draft/2019-09/schema"}'),
  ok('the dialect URI in upper case', '{"$schema":"HTTPS://json-schema.org/draft/2020-12/schema"}'),
  ok(
    'the dialect URI with a trailing slash',
    '{"$schema":"https://json-schema.org/draft/2020-12/schema/"}',
  ),
  ok('an empty dialect', '{"$schema":""}'),
  ok('a $schema that is not a string', '{"$schema":7}'),
  ok(
    'a draft-07 $schema that is not at the root',
    '{"properties":{"x":{"$schema":"http://json-schema.org/draft-07/schema#"}}}',
  ),
  ok(
    'a dialect written with escapes',
    '{"$schema":"https:\\u002f\\u002fjson-schema.org/draft/2020-12/schema"}',
  ),
  ok('a number written with a trailing zero', '{"maximum":1.0}'),
  ok('a number too large for a double', '{"maximum":12345678901234567890}'),
  ok('exactly 512 KiB', '"' + 'a'.repeat(512 * 1024 - 2) + '"'),
  ok('one byte over 512 KiB', '"' + 'a'.repeat(512 * 1024 - 1) + '"'),
  ok(
    'multi-byte characters that fit in characters but not in bytes',
    '"' + 'é'.repeat(262_144) + '"',
  ),
  {
    label: 'a keyword that is not valid under the metaschema',
    text: '{"type":"bogus"}',
    mirrored: false,
    why: 'the registry does not validate against the metaschema, so neither may a blocking check',
  },
  {
    label: 'a regular expression that does not compile',
    text: '{"type":"string","pattern":"(["}',
    mirrored: false,
    why: 'the registry accepts it; the pattern is only compiled when a message is validated',
  },
  {
    label: 'a reference to a subject that does not exist',
    text: '{"$ref":"concordat://dev/acme.Nope/1"}',
    mirrored: false,
    why: 'references are resolved from the reference list sent with the version, not from the text',
  },
];

const AVRO_CASES: readonly Case[] = [
  ok('a record', '{"type":"record","name":"R","fields":[{"name":"id","type":"string"}]}'),
  ok('unbalanced braces', '{"type": '),
  ok('a duplicate key', '{"type":"record","type":"enum","name":"R","fields":[]}'),
  ok('nesting past 64 levels', nested(65)),
  ok(
    'a $schema attribute, which Avro has no dialect check for',
    '{"type":"record","name":"R","fields":[],"$schema":"http://json-schema.org/draft-07/schema#"}',
  ),
  {
    label: 'JSON that is not Avro',
    text: '{"type":"object"}',
    mirrored: false,
    why: "Avro's type rules are the registry's; the editor only reads JSON",
  },
];

const PROTOBUF_CASES: readonly Case[] = [
  ok('a message', 'syntax = "proto3"; message M { string id = 1; }'),
  ok('a document over 512 KiB', 'a'.repeat(512 * 1024 + 1)),
  {
    label: 'JSON, which is not Protobuf',
    text: '{"type":"object"}',
    mirrored: false,
    why: "a .proto file is not JSON and its grammar is the registry's to judge; reading it as JSON would refuse every valid one",
  },
  {
    label: 'text that is not Protobuf at all',
    text: 'this is not proto',
    mirrored: false,
    why: "the Protobuf grammar is the registry's to judge",
  },
  {
    label: 'a duplicate-looking key in a .proto',
    text: '{"a":1,"a":2}',
    mirrored: false,
    why: 'Protobuf is not read as JSON, so there are no keys to duplicate',
  },
];

const FORMATS: readonly { format: Format; cases: readonly Case[] }[] = [
  { format: 'json', cases: JSON_CASES },
  { format: 'avro', cases: AVRO_CASES },
  { format: 'protobuf', cases: PROTOBUF_CASES },
];

test.describe('the schema editor against the registry', () => {
  test.beforeAll(async () => {
    const credential = await ownerCredential();

    for (const [format, subject] of Object.entries(SUBJECTS)) {
      await ensureEmptySubject(credential, ENVIRONMENT, subject, format as Format);
    }
  });

  for (const { format, cases } of FORMATS) {
    test(`${format}: is never stricter than the registry, and says what the registry says`, async ({
      page,
    }) => {
      const credential = await ownerCredential();
      const subject = SUBJECTS[format];

      await page.goto(`/subjects/${subject}/versions/new`);
      await waitForSubject(page);

      const disagreements: string[] = [];

      for (const item of cases) {
        // What the page sends is the trimmed text, so that is what the registry is asked about.
        // It matters for exactly one case: JavaScript's trim() strips a leading byte-order
        // mark, which the registry's reader refuses, so the raw text would be a different question.
        const registry = await dryRun(credential, ENVIRONMENT, subject, item.text.trim());

        await setSchema(page, item.text);

        const said = await findingCodes(page, 'refusal');
        const registryCode = registry.code;
        const known = registryCode !== null && CLIENT_CODES.has(registryCode);

        // Never stricter: a refusal the registry does not make is a document somebody could
        // have registered and cannot.
        for (const code of said) {
          if (registryCode === null) {
            disagreements.push(
              `${item.label}: the editor refuses it as ${code}; the registry accepted it (${registry.status}).`,
            );
          } else if (registryCode !== code) {
            disagreements.push(
              `${item.label}: the editor says ${code}; the registry says ${registryCode}.`,
            );
          }
        }

        if (item.mirrored) {
          // Not looser, for the rules the editor claims.
          if (registryCode !== null && known && !said.includes(registryCode)) {
            disagreements.push(
              `${item.label}: the registry refuses it as ${registryCode}; the editor said ${JSON.stringify(said)}.`,
            );
          }
        } else if (said.length > 0) {
          disagreements.push(
            `${item.label}: the editor claims a rule it leaves to the registry (${item.why}); it said ${JSON.stringify(said)}.`,
          );
        }
      }

      expect(disagreements, disagreements.join('\n')).toEqual([]);
    });
  }

  test('the registry still crashes on a lone surrogate escape, which the editor does not claim to know', async ({
    page,
  }) => {
    // Recorded, not asserted as a rule. `"\ud800"` is well-formed JSON and the registry answers
    // 500 with no Problem Details body — a defect there, not a refusal to mirror. If this
    // starts failing because the registry now answers 400, delete the case: the editor was
    // right not to guess.
    const credential = await ownerCredential();
    const verdict = await dryRun(credential, ENVIRONMENT, SUBJECTS.json, '{"a":"\\ud800"}');

    await page.goto(`/subjects/${SUBJECTS.json}/versions/new`);
    await waitForSubject(page);
    await setSchema(page, '{"a":"\\ud800"}');

    expect(verdict.status).toBe(500);
    expect(await findingCodes(page, 'refusal')).toEqual([]);
  });
});
