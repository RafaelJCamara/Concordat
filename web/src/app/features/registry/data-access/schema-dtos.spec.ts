import { describe, expect, it } from 'vitest';
import { ConcordatError } from '../../../core/http/problem-details';
import { toSchemaDocument, type SchemaDto } from './schema-dtos';

// The mapping is nearly a rename, so the test that earns its place is the strict one: an
// unrecognised format has to fail here rather than reach a template. That guard is shared
// with `subject-dtos` now, and this file is what proves schemas actually go through it.

function dto(overrides: Partial<SchemaDto> = {}): SchemaDto {
  return {
    schemaId: '0123456789abcdef0123456789abcdef',
    format: 'json',
    schema: '{"type":"object"}',
    references: [],
    ...overrides,
  };
}

describe('toSchemaDocument', () => {
  it('renames the wire field to the domain one', () => {
    // `schema` on the wire, `text` in the domain. The rename exists because "the schema" is
    // ambiguous in a type that is itself a schema; `text` says which half it is.
    expect(toSchemaDocument(dto()).text).toBe('{"type":"object"}');
  });

  it('carries the id and format through', () => {
    const document = toSchemaDocument(dto({ format: 'avro' }));

    expect(document.schemaId).toBe('0123456789abcdef0123456789abcdef');
    expect(document.format).toBe('avro');
  });

  it('maps references, keeping the referring document’s own name for the target', () => {
    // `name` is the spelling used inside the document and is not derivable from the subject:
    // two documents can reference the same registered schema under different local names.
    const document = toSchemaDocument(
      dto({ references: [{ name: 'Money', subject: 'common.money', version: 2 }] }),
    );

    expect(document.references).toEqual([{ name: 'Money', subject: 'common.money', version: 2 }]);
  });

  it('refuses a format this build does not know', () => {
    // The `DISMISSED` lesson, applied to formats: guessing would render a document as JSON
    // that the registry called something else, and the reader would never be told.
    expect(() => toSchemaDocument(dto({ format: 'thrift' }))).toThrow(ConcordatError);
  });

  it('names the field and the value when it refuses', () => {
    try {
      toSchemaDocument(dto({ format: 'thrift' }));
      expect.unreachable('should have thrown');
    } catch (error) {
      expect((error as ConcordatError).detail).toContain("'thrift'");
      expect((error as ConcordatError).detail).toContain("'format'");
      expect((error as ConcordatError).code).toBe('registry_refused');
    }
  });
});
