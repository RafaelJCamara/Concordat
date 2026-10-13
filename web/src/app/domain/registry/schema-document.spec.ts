import { describe, expect, it } from 'vitest';
import { MAX_SCHEMA_BYTES, checkSchemaText } from './schema-document';

// Every case is a refusal the registry makes, or a document it takes. The e2e suite holds the
// same table against the real dry-run endpoint (`schema-parity.spec.ts`); this file is the
// fast half, and would catch a typo in a URI or a changed limit without a registry.

const codes = (text: string, format: Parameters<typeof checkSchemaText>[1] = 'json') =>
  checkSchemaText(text, format).map((f) => f.code);

describe('checkSchemaText', () => {
  it('finds nothing wrong with an ordinary schema', () => {
    expect(codes('{"type":"object","properties":{"id":{"type":"string"}}}')).toEqual([]);
  });

  it('says an empty or blank document is empty, and only that', () => {
    expect(codes('')).toEqual(['schema_body_empty']);
    expect(codes(' \n\t ')).toEqual(['schema_body_empty']);
  });

  it('measures size in UTF-8 bytes, not characters', () => {
    // 'é' is one character and two bytes, so a document the character count says fits can be
    // over the limit — and the registry measures bytes.
    const filler = 'é'.repeat(MAX_SCHEMA_BYTES / 2 + 1);

    expect(filler.length).toBeLessThan(MAX_SCHEMA_BYTES);
    expect(codes(`"${filler}"`)).toEqual(['schema_too_large']);
    expect(codes(`"${'a'.repeat(MAX_SCHEMA_BYTES - 2)}"`)).toEqual([]);
  });

  it('reports malformed JSON with a position', () => {
    const finding = checkSchemaText('{"type": ', 'json')[0]!;

    expect(finding.code).toBe('schema_malformed');
    expect(finding.position).not.toBeNull();
  });

  it('reports a duplicate key, which JSON.parse would not', () => {
    const finding = checkSchemaText('{"type":"object","type":"string"}', 'json')[0]!;

    expect(finding.code).toBe('schema_malformed');
    expect(finding.message).toContain("'type'");
  });

  describe('a surrogate escape with no partner', () => {
    it('is the registry’s own refusal, schema_malformed, not a new code', () => {
      const [finding] = checkSchemaText('{"a":"\\ud800"}', 'json');

      expect(finding?.code).toBe('schema_malformed');
      expect(finding?.severity).toBe('refusal');
      expect(finding?.message).toContain('\\uD800');
    });

    it('underlines the escape itself, in the text as the editor holds it', () => {
      // Leading whitespace is trimmed before the registry sees the text, and positions are
      // still reported in what the author is looking at.
      const text = '  {"note": "ok \\udc00 ok"}';
      const [finding] = checkSchemaText(text, 'json');
      const start = finding?.position?.offset ?? -1;

      expect(text.slice(start, start + (finding?.length ?? 0))).toBe('\\udc00');
    });

    it('is reported ahead of a duplicate key, as the registry does, and both are kept', () => {
      const findings = checkSchemaText('{"a":"\\ud800","a":2}', 'json');

      expect(findings.map((f) => f.code)).toEqual(['schema_malformed', 'schema_malformed']);
      expect(findings[0]?.message).toContain('surrogate');
      expect(findings[1]?.message).toContain('Duplicate');
    });

    it('applies to Avro, which is JSON, and not to Protobuf, which is not', () => {
      expect(codes('{"type":"record","name":"R","doc":"\\ud800","fields":[]}', 'avro')).toEqual([
        'schema_malformed',
      ]);
      expect(codes('syntax = "proto3"; // \\ud800', 'protobuf')).toEqual([]);
    });

    it('accepts a pair, and a description that merely mentions one', () => {
      expect(codes('{"a":"\\ud83d\\ude00"}')).toEqual([]);
      expect(codes('{"description":"write \\\\ud800 as an escape"}')).toEqual([]);
    });
  });

  describe('dialect', () => {
    it.each([
      ['https://json-schema.org/draft/2020-12/schema'],
      ['https://json-schema.org/draft/2020-12/schema#'],
    ])('accepts %s', (dialect) => {
      expect(codes(`{"$schema":"${dialect}"}`)).toEqual([]);
    });

    it.each([
      ['http://json-schema.org/draft-07/schema#'],
      ['https://json-schema.org/draft/2019-09/schema'],
      ['https://json-schema.org/draft-04/schema'],
      ['HTTPS://json-schema.org/draft/2020-12/schema'],
      ['https://json-schema.org/draft/2020-12/schema/'],
      [''],
    ])('refuses %j', (dialect) => {
      // Compared exactly, as the registry does: it is a whole-string match against two
      // spellings, not a family of acceptable URIs.
      expect(codes(`{"$schema":"${dialect}"}`)).toEqual(['schema_dialect_unsupported']);
    });

    it('treats an absent $schema as 2020-12 without comment', () => {
      expect(codes('{"type":"object"}')).toEqual([]);
    });

    it('ignores a $schema that is not a string, as the registry does', () => {
      expect(codes('{"$schema": 7}')).toEqual([]);
      expect(codes('{"$schema": null}')).toEqual([]);
    });

    it('reads only the root, not a nested $schema', () => {
      expect(
        codes('{"properties":{"x":{"$schema":"http://json-schema.org/draft-07/schema#"}}}'),
      ).toEqual([]);
    });

    it('reads $schema after its escapes are resolved', () => {
      // `/` is a slash: the registry reads the decoded value, so must this.
      expect(
        codes('{"$schema":"https:\\u002f\\u002fjson-schema.org/draft/2020-12/schema"}'),
      ).toEqual([]);
    });

    it('does not apply to a non-JSON-Schema format', () => {
      // An Avro document may have a `$schema` attribute; the dialect check is JSON Schema's.
      expect(codes('{"type":"record","name":"R","fields":[],"$schema":"x"}', 'avro')).toEqual([]);
    });
  });

  describe('Avro', () => {
    it('shares the JSON rules for malformed text and duplicate keys', () => {
      expect(codes('{"type": ', 'avro')).toEqual(['schema_malformed']);
      expect(codes('{"type":"record","type":"enum"}', 'avro')).toEqual(['schema_malformed']);
    });
  });

  describe('Protobuf', () => {
    it('is not read as JSON', () => {
      // A .proto file is not JSON and is parsed by a different grammar that is the
      // registry's to judge. Calling it malformed here would refuse every Protobuf schema.
      expect(codes('syntax = "proto3"; message M { string id = 1; }', 'protobuf')).toEqual([]);
    });

    it('still has an empty and a size check', () => {
      expect(codes('  ', 'protobuf')).toEqual(['schema_body_empty']);
      expect(codes('a'.repeat(MAX_SCHEMA_BYTES + 1), 'protobuf')).toEqual(['schema_too_large']);
    });
  });
});
