import { describe, expect, it } from 'vitest';
import { checkSchema } from './schema-check-engine';

// ajv is a different engine from the registry's, and everything it says is advice. These pin the
// two things that keep it from being stricter or looser than the registry in a way that matters:
// it does not apply 2020-12 rules to another dialect, and it does not pretend to agree about
// what it cannot know (formats, references).

const check = (schema: string, payload: string | null = null) => checkSchema({ schema, payload });

describe('checkSchema — the schema', () => {
  it('flags nothing in an ordinary schema', () => {
    const result = check('{"type":"object","properties":{"id":{"type":"string"}}}');

    expect(result.skipped).toBeNull();
    expect(result.advice).toEqual([]);
  });

  it('flags a keyword that is not valid under the metaschema, naming where and what is allowed', () => {
    // The registry refuses a new schema for this (schema_invalid, ADR-039). It is advice all the
    // same: ajv is a different engine, and advice that could veto would one day be wrong in the
    // strict direction.
    const result = check('{"type":"bogus"}');

    expect(result.advice).toHaveLength(1);
    expect(result.advice[0]).toMatchObject({ code: 'ajv_metaschema', pointer: '/type' });
    expect(result.advice[0]!.message).toContain('"object"');
  });

  it('flags a $ref into the document that points at nothing, which no validator can compile', () => {
    const result = check('{"$ref":"#/$defs/Adress","$defs":{"Address":{"type":"string"}}}');

    expect(result.advice).toHaveLength(1);
    expect(result.advice[0]).toMatchObject({ code: 'ajv_reference', pointer: '' });
    expect(result.advice[0]!.message).toContain('#/$defs/Adress');
  });

  it('does not flag a reference it cannot judge, or one that leads somewhere', () => {
    // A concordat:// address is the registry's to resolve, a remote one is nobody's here and an
    // anchor is not what the registry judges. Saying "missing" about any of them would be a
    // confident claim about something this has no way to know.
    for (const schema of [
      '{"$ref":"concordat://dev/acme.Customer/3"}',
      '{"$ref":"https://example.com/other.json"}',
      '{"$ref":"#node"}',
      '{"$defs":{"a":{"type":"string"}},"$ref":"#/$defs/a"}',
      '{"$defs":{"a/b":{}},"$ref":"#/$defs/a~1b"}',
    ]) {
      expect(check(schema).advice, schema).toEqual([]);
    }
  });

  it('flags a pattern that is not a regular expression, which the registry refuses when no engine accepts it', () => {
    // pattern_unparseable (ADR-044). Advice all the same: ajv compiles with the u flag, which is stricter
    // than the registry's rule, so what it finds is what ajv found.
    const result = check('{"type":"string","pattern":"("}');

    expect(result.advice).toHaveLength(1);
    expect(result.advice[0]).toMatchObject({ code: 'ajv_pattern', pointer: '' });
    expect(result.advice[0]!.message).toContain('not a regular expression');
  });

  it('flags a pattern under patternProperties and under a nested schema as well', () => {
    for (const schema of [
      '{"type":"object","patternProperties":{"(":{"type":"string"}}}',
      '{"properties":{"a":{"items":{"type":"string","pattern":"a{3,2}"}}}}',
      '{"pattern":"[z-a]"}',
      '{"pattern":"*abc"}',
    ]) {
      expect(
        check(schema).advice.map((a) => a.code),
        schema,
      ).toEqual(['ajv_pattern']);
    }
  });

  it('does not flag a pattern ajv compiles, including the ones another engine would not', () => {
    // '[^]' is the class of everything in ECMA-262, which the registry warns about for .NET and ajv
    // accepts; a lookahead and a \uXXXX escape are valid here and only RE2 refuses them.
    for (const schema of [
      '{"pattern":"[^]"}',
      '{"pattern":"^(?=.*[A-Z]).+$"}',
      '{"pattern":"^[\\u4e00-\\u9fff]+$"}',
      '{"pattern":"^[a-z0-9_.]+$"}',
    ]) {
      expect(check(schema).advice, schema).toEqual([]);
    }
  });

  it('says what is wrong with the schema before it says anything about a pattern', () => {
    const result = check('{"type":"bogus","pattern":"("}');

    expect(result.advice.map((a) => a.code)).toEqual(['ajv_metaschema']);
  });

  it('prefers a reference to a pattern, because the reference is what stops compilation first', () => {
    const result = check('{"$ref":"#/$defs/nope","pattern":"("}');

    expect(result.advice.map((a) => a.code)).toEqual(['ajv_reference']);
  });
  it('says what is wrong with the schema before it says anything about a reference', () => {
    // A document that is not a schema has no references worth speaking of, and the metaschema
    // finding is the one to fix first.
    const result = check('{"type":"bogus","$ref":"#/$defs/nope"}');

    expect(result.advice.map((a) => a.code)).toEqual(['ajv_metaschema']);
  });

  it('says one thing per location, not the metaschema’s three', () => {
    // A wrong `type` comes back as "not one of the allowed values", "must be array" and "must
    // match a schema in anyOf". Only the first says what to change.
    const result = check('{"type":"bogus","properties":{"a":{"minLength":"x"}}}');

    expect(result.advice.map((a) => a.pointer).sort()).toEqual([
      '/properties/a/minLength',
      '/type',
    ]);
    expect(result.advice.every((a) => !a.message.includes('anyOf'))).toBe(true);
  });

  it('accepts both spellings of the dialect, as the registry does', () => {
    for (const dialect of [
      'https://json-schema.org/draft/2020-12/schema',
      'https://json-schema.org/draft/2020-12/schema#',
    ]) {
      expect(check(`{"$schema":"${dialect}","type":"object"}`).skipped).toBeNull();
    }
  });

  it('does not judge a document that declares another dialect', () => {
    // Handing it to ajv would either throw or apply 2020-12 rules to a draft-07 document —
    // the exact mistake the registry refuses it for. Say what happened instead.
    const result = check('{"$schema":"http://json-schema.org/draft-07/schema#","type":"object"}');

    expect(result.skipped).toContain('draft-07');
    expect(result.advice).toEqual([]);
  });

  it('says it did not look at a document that is not JSON yet, rather than that it found nothing', () => {
    const result = check('{"type": ');

    expect(result.skipped).toContain('not well-formed');
  });

  it('says so for a document that is not a schema at all', () => {
    expect(check('42').skipped).toContain('object or a boolean');
    expect(check('null').skipped).toContain('object or a boolean');
  });

  it('takes a boolean schema', () => {
    expect(check('true').skipped).toBeNull();
    expect(check('false').advice).toEqual([]);
  });

  it('is not strict about keywords the registry accepts', () => {
    // ajv's strict mode rejects unknown keywords and formats; the registry stores them.
    const result = check('{"type":"string","format":"no-such-format","x-owner":"billing"}');

    expect(result.skipped).toBeNull();
    expect(result.advice).toEqual([]);
  });

  it('records what was checked, so a stale answer can be recognised', () => {
    expect(check('{"type":"object"}').checked).toBe('{"type":"object"}');
  });
});

describe('checkSchema — a sample message', () => {
  const schema =
    '{"type":"object","required":["id"],"properties":{"id":{"type":"string"},"n":{"type":"integer","minimum":1}}}';

  it('has no payload outcome when none was supplied', () => {
    expect(check(schema).payload).toBeNull();
  });

  it('accepts a conforming message', () => {
    expect(check(schema, '{"id":"a","n":3}').payload).toEqual({ kind: 'valid' });
  });

  it('lists each way a message does not conform, with where', () => {
    const payload = check(schema, '{"n":0}').payload;

    expect(payload).toMatchObject({ kind: 'invalid', omitted: 0 });
    expect(payload?.kind === 'invalid' && payload.errors.map((e) => e.path).sort()).toEqual([
      '',
      '/n',
    ]);
  });

  it('says a message that is not JSON is not JSON', () => {
    expect(check(schema, '{"id":').payload).toMatchObject({ kind: 'not_json' });
  });

  it('caps a long list of errors and counts the rest', () => {
    const items = JSON.stringify(Array.from({ length: 100 }, () => 'x'));
    const payload = check('{"type":"array","items":{"type":"integer"}}', items).payload;

    expect(payload).toMatchObject({ kind: 'invalid', omitted: 75 });
    expect(payload?.kind === 'invalid' && payload.errors).toHaveLength(25);
  });

  it('does not claim to agree with the registry about formats', () => {
    // The registry's engine has its own list of formats. Pretending ajv's agree is the one
    // place this would be confidently wrong, so formats are not checked at all.
    const payload = check('{"type":"string","format":"email"}', '"not an email"').payload;

    expect(payload).toEqual({ kind: 'valid' });
  });

  it('says a message cannot be judged against a reference only the registry resolves', () => {
    // Neither "valid" nor "invalid": the registry resolves `concordat://` references from the
    // version's reference list, and there is no registry here to ask.
    const result = check('{"$ref":"concordat://dev/acme.Address/1"}', '{"a":1}');

    expect(result.payload).toMatchObject({ kind: 'unavailable' });
    expect(result.payload?.kind === 'unavailable' && result.payload.reason).toContain(
      'concordat://dev/acme.Address/1',
    );
  });

  it('still checks the schema itself when a reference blocks the message', () => {
    const result = check('{"$ref":"concordat://dev/x/1","type":"bogus"}', '{}');

    expect(result.advice.map((a) => a.pointer)).toEqual(['/type']);
  });

  it('follows a reference inside the document', () => {
    const result = check('{"$ref":"#/$defs/id","$defs":{"id":{"type":"string"}}}', '42');

    expect(result.payload).toMatchObject({ kind: 'invalid' });
  });

  it('says a pattern that does not compile is why the message could not be checked', () => {
    const payload = check('{"type":"string","pattern":"(["}', '"x"').payload;

    expect(payload).toMatchObject({ kind: 'unavailable' });
    expect(payload?.kind === 'unavailable' && payload.reason).toContain('does not compile');
  });
});
