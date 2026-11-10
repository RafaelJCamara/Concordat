import { describe, expect, it } from 'vitest';
import {
  canonicalise,
  diffSchemas,
  SCHEMA_DIFF_LIMITS,
  type DiffRow,
  type SchemaDiffReady,
  type SchemaDiffRequest,
} from './schema-diff';

// What a reviewer needs from the diff, and the paths are the registry's own: every `path` below
// is one a running registry reported for a change of this shape (`#/properties/id` for JSON
// Schema, `#/fields/customer/fields/age` for Avro, `.acme.Order/1` for Protobuf).

function ready(request: Partial<SchemaDiffRequest> & Pick<SchemaDiffRequest, 'before' | 'after'>) {
  const result = diffSchemas({ format: 'json', findings: [], ...request });

  expect(result.status).toBe('ready');
  return result as SchemaDiffReady;
}

/** Every row on show, in order. */
function shown(result: SchemaDiffReady): DiffRow[] {
  return result.segments.flatMap((segment) => (segment.kind === 'rows' ? [...segment.rows] : []));
}

/** The text of the rows of one kind. */
function texts(result: SchemaDiffReady, kind: DiffRow['kind']): string[] {
  return shown(result)
    .filter((row) => row.kind === kind)
    .map((row) => row.text.trim());
}

/** The rows a finding was put on. */
function rowsWith(result: SchemaDiffReady, id: number): DiffRow[] {
  return shown(result).filter((row) => row.findings.includes(id));
}

const BASE = '{"type":"object","properties":{"id":{"type":"string"}}}';

describe('canonicalise', () => {
  it('lays JSON out one member per line, in key order, with the source’s own literals', () => {
    const { lines } = canonicalise('json', '{"b": 1.0, "a": [true, {}], "c": {"z": "x\\u0041"}}');

    expect(lines).toEqual([
      '{',
      '  "a": [',
      '    true,',
      '    {}',
      '  ],',
      '  "b": 1.0,',
      '  "c": {',
      '    "z": "x\\u0041"',
      '  }',
      '}',
    ]);
  });

  it('keeps array order, which is significant', () => {
    expect(canonicalise('json', '{"enum":["b","a"]}').lines).toEqual([
      '{',
      '  "enum": [',
      '    "b",',
      '    "a"',
      '  ]',
      '}',
    ]);
  });

  it('reads Protobuf as the line-oriented text it is: line endings and trailing blanks go', () => {
    expect(canonicalise('protobuf', 'a;  \r\nb;\r\n').lines).toEqual(['a;', 'b;']);
    expect(canonicalise('protobuf', 'a;\rb;').lines).toEqual(['a;', 'b;']);
  });

  it('compares a document that is not JSON as text, with nothing to tie a finding to', () => {
    const doc = canonicalise('json', 'not json\nat all');

    expect(doc.lines).toEqual(['not json', 'at all']);
    expect(doc.locate('#/properties/id')).toBeNull();
  });
});

describe('diffSchemas: what counts as a change', () => {
  it('says nothing changed when only whitespace and key order did', () => {
    const result = ready({
      before: '{"type":"object","properties":{"id":{"type":"string"},"a":{"type":"number"}}}',
      after: `{
        "properties": { "a": {"type": "number"}, "id": {"type": "string"} },
        "type": "object"
      }`,
    });

    expect(result.added).toBe(0);
    expect(result.removed).toBe(0);
    expect(shown(result)).toEqual([]);
  });

  it('does count 1.0 against 1, because the registry gives them different ids (ADR-028)', () => {
    const result = ready({ before: '{"maximum":1}', after: '{"maximum":1.0}' });

    expect(texts(result, 'removed')).toEqual(['"maximum": 1']);
    expect(texts(result, 'added')).toEqual(['"maximum": 1.0']);
  });

  it('shows a removed line as removed and an added line as added', () => {
    const result = ready({ before: BASE, after: BASE.replace('string', 'integer') });

    expect(texts(result, 'removed')).toEqual(['"type": "string"']);
    expect(texts(result, 'added')).toEqual(['"type": "integer"']);
    expect(result.added).toBe(1);
    expect(result.removed).toBe(1);
  });

  it('numbers each side by its own document', () => {
    const rows = shown(
      ready({
        before: '{"a":1,"b":2}',
        after: '{"a":1,"aa":5,"ab":6,"b":3}',
      }),
    );

    const byText = (text: string) => rows.find((row) => row.text.trim() === text);

    expect(byText('"a": 1,')).toMatchObject({ kind: 'context', oldNumber: 2, newNumber: 2 });
    expect(byText('"aa": 5,')).toMatchObject({ kind: 'added', oldNumber: null, newNumber: 3 });
    expect(byText('"b": 2')).toMatchObject({ kind: 'removed', oldNumber: 3, newNumber: null });
    expect(byText('"b": 3')).toMatchObject({ kind: 'added', oldNumber: null, newNumber: 5 });
  });
});

describe('diffSchemas: collapsing what did not change', () => {
  /** A long object with a change in the middle: `p00` to `p39`, with p20 edited. */
  function long(value: string): string {
    const members = Array.from({ length: 40 }, (_, i) => {
      const key = `p${String(i).padStart(2, '0')}`;
      return `"${key}":${i === 20 ? value : '0'}`;
    });

    return `{${members.join(',')}}`;
  }

  it('shows three lines of context either side and folds the rest', () => {
    const result = ready({ before: long('1'), after: long('2') });

    expect(result.segments.map((s) => s.kind)).toEqual(['folded', 'rows', 'folded']);

    const [first, middle, last] = result.segments;
    // Line 1 is `{`; p20 is line 22. Three lines of context sit above it and three below.
    expect(first).toEqual({ kind: 'folded', oldStart: 1, newStart: 1, count: 18 });
    expect(middle?.kind === 'rows' && middle.rows.map((r) => r.kind)).toEqual([
      'context',
      'context',
      'context',
      'removed',
      'added',
      'context',
      'context',
      'context',
    ]);
    expect(last).toEqual({ kind: 'folded', oldStart: 26, newStart: 26, count: 17 });
  });

  it('honours the context it is given', () => {
    const result = diffSchemas(
      { format: 'json', before: long('1'), after: long('2'), findings: [] },
      { ...SCHEMA_DIFF_LIMITS, context: 1 },
    ) as SchemaDiffReady;

    expect(shown(result)).toHaveLength(4);
  });

  it('keeps the new document’s lines so a folded stretch can be opened', () => {
    const result = ready({ before: long('1'), after: long('2') });
    const folded = result.segments[0];

    expect(folded?.kind).toBe('folded');

    if (folded?.kind === 'folded') {
      expect(result.lines.slice(folded.newStart - 1, folded.newStart - 1 + folded.count)).toEqual(
        canonicalise('json', long('2')).lines.slice(0, folded.count),
      );
    }
  });

  it('never folds fewer than two lines: one is shown instead', () => {
    // Two edits whose contexts leave exactly one unchanged line between them.
    const doc = (a: string, b: string) =>
      `{${Array.from({ length: 14 }, (_, i) => `"k${String(i).padStart(2, '0')}":${i === 2 ? a : i === 10 ? b : 0}`).join(',')}}`;
    const result = ready({ before: doc('1', '1'), after: doc('2', '2') });

    // Row 0 is `{`; the edits are lines 4 and 12, and context reaches 7 and 9: one line (8) is
    // left between them, and it is on show.
    expect(result.segments.some((s) => s.kind === 'folded' && s.count < 2)).toBe(false);
    expect(shown(result).some((row) => row.text.trim() === '"k06": 0,')).toBe(true);
    expect(shown(result).some((row) => row.text.trim() === '"k07": 0,')).toBe(true);
  });

  it('folds exactly two unchanged lines, which is the least a fold hides', () => {
    // Edits eight lines apart leave two unchanged lines between their contexts.
    const doc = (a: string, b: string) =>
      `{${Array.from({ length: 14 }, (_, i) => `"k${String(i).padStart(2, '0')}":${i === 2 ? a : i === 11 ? b : 0}`).join(',')}}`;
    const result = ready({ before: doc('1', '1'), after: doc('2', '2') });

    expect(result.segments.filter((s) => s.kind === 'folded' && s.count === 2)).toHaveLength(1);
  });

  it('folds nothing when the whole document is within reach of the change', () => {
    const result = ready({ before: BASE, after: BASE.replace('string', 'integer') });

    expect(result.segments.map((s) => s.kind)).toEqual(['rows']);
  });
});

describe('diffSchemas: tying a finding to its line', () => {
  const before =
    '{"type":"object","properties":{"id":{"type":"string"},"nested":{"type":"object","properties":{"a":{"type":"integer"}}}}}';
  const after =
    '{"type":"object","properties":{"id":{"type":"integer"},"nested":{"type":"object","properties":{"a":{"type":"string"}},"required":["a"]},"total":{"type":"number"}},"required":["total"]}';

  it('puts a JSON Schema finding on the line of the member its pointer names', () => {
    const result = ready({
      before,
      after,
      findings: [
        { id: 1, path: '#/properties/id' },
        { id: 2, path: '#/required' },
        { id: 3, path: '#/properties/nested/properties/a' },
      ],
    });

    expect(rowsWith(result, 1).map((r) => r.text.trim())).toEqual(['"id": {']);
    expect(rowsWith(result, 2).map((r) => [r.kind, r.text.trim()])).toEqual([
      ['added', '"required": ['],
    ]);
    expect(rowsWith(result, 3).map((r) => r.text.trim())).toEqual(['"a": {']);
    expect(result.unplaced).toEqual([]);
  });

  it('puts two findings about one path on the one line', () => {
    const result = ready({
      before,
      after,
      findings: [
        { id: 1, path: '#/properties/id' },
        { id: 2, path: '#/properties/id' },
      ],
    });

    expect(rowsWith(result, 1)).toHaveLength(1);
    expect(rowsWith(result, 1)[0]?.findings).toEqual([1, 2]);
  });

  it('puts a finding about something the change removed on the removed line', () => {
    const result = ready({
      before: '{"properties":{"gone":{"type":"string"},"kept":{"type":"string"}}}',
      after: '{"properties":{"kept":{"type":"string"}}}',
      findings: [{ id: 7, path: '#/properties/gone' }],
    });

    expect(rowsWith(result, 7).map((r) => [r.kind, r.text.trim()])).toEqual([
      ['removed', '"gone": {'],
    ]);
  });

  it('reads ~01 as the key ~1, not as a slash: ~1 is decoded before ~0 (RFC 6901 §4)', () => {
    const result = ready({
      before: '{"properties":{"~1":{"type":"string"},"/":{"type":"string"}}}',
      after: '{"properties":{"~1":{"type":"integer"},"/":{"type":"string"}}}',
      findings: [{ id: 1, path: '#/properties/~01' }],
    });

    expect(rowsWith(result, 1).map((r) => r.text.trim())).toEqual(['"~1": {']);
  });

  it('reads RFC 6901 escapes and array indexes in a pointer', () => {
    const result = ready({
      before: '{"properties":{"a/b":{"type":"string"},"c~d":{"enum":["x","y"]}}}',
      after: '{"properties":{"a/b":{"type":"integer"},"c~d":{"enum":["x","z"]}}}',
      findings: [
        { id: 1, path: '#/properties/a~1b' },
        { id: 2, path: '#/properties/c~0d/enum/1' },
      ],
    });

    expect(rowsWith(result, 1).map((r) => r.text.trim())).toEqual(['"a/b": {']);
    expect(rowsWith(result, 2).map((r) => [r.kind, r.text.trim()])).toEqual([['added', '"z"']]);
  });

  it('shows a line a finding sits on even when it is far from any change', () => {
    // The change is at the bottom; the finding names the top. Without the finding that line
    // would be inside the fold, and the registry's finding would point at nothing visible.
    const members = Array.from({ length: 30 }, (_, i) => `"p${String(i).padStart(2, '0')}":0`);
    const doc = (last: string) => `{${members.join(',')},"zz":${last}}`;

    const result = ready({
      before: doc('1'),
      after: doc('2'),
      findings: [{ id: 1, path: '#/p02' }],
    });

    expect(rowsWith(result, 1).map((r) => r.text.trim())).toEqual(['"p02": 0,']);
  });

  it('lists a finding it cannot place, rather than dropping it or guessing', () => {
    const result = ready({
      before,
      after,
      findings: [
        { id: 1, path: '#/properties/nothing/here' },
        { id: 2, path: 'not a pointer' },
        { id: 3, path: '#/properties/id' },
      ],
    });

    expect(result.unplaced).toEqual([1, 2]);
    expect(rowsWith(result, 3)).toHaveLength(1);
  });

  describe('Avro, whose paths are names', () => {
    const avro = (idType: string, ageType: string, extra = '') =>
      `{"type":"record","name":"Order","namespace":"acme","fields":[{"name":"id","type":"${idType}"},{"name":"customer","type":{"type":"record","name":"Cust","fields":[{"name":"age","type":"${ageType}"}]}}${extra}]}`;

    it('walks fields through the record that is a field’s type', () => {
      const result = ready({
        format: 'avro',
        before: avro('string', 'int'),
        after: avro('long', 'string', ',{"name":"total","type":"double"}'),
        findings: [
          { id: 1, path: '#/fields/id' },
          { id: 2, path: '#/fields/customer/fields/age' },
          { id: 3, path: '#/fields/total' },
        ],
      });

      // The line that names the field, not the brace that opens it.
      expect(rowsWith(result, 1).map((r) => r.text.trim())).toEqual(['"name": "id",']);
      expect(rowsWith(result, 2).map((r) => r.text.trim())).toEqual(['"name": "age",']);
      expect(rowsWith(result, 3).map((r) => [r.kind, r.text.trim()])).toEqual([
        ['added', '"name": "total",'],
      ]);
      expect(result.unplaced).toEqual([]);
    });

    it('finds a record inside a union, and an array’s items', () => {
      const doc = (t: string) =>
        `{"type":"record","name":"R","fields":[{"name":"u","type":["null",{"type":"record","name":"In","fields":[{"name":"x","type":"${t}"}]}]},{"name":"l","type":{"type":"array","items":"${t}"}}]}`;
      const result = ready({
        format: 'avro',
        before: doc('int'),
        after: doc('string'),
        findings: [
          { id: 1, path: '#/fields/u/fields/x' },
          { id: 2, path: '#/fields/l/items' },
        ],
      });

      expect(rowsWith(result, 1).map((r) => r.text.trim())).toEqual(['"name": "x",']);
      expect(rowsWith(result, 2).map((r) => [r.kind, r.text.trim()])).toEqual([
        ['added', '"items": "string",'],
      ]);
    });

    it('does not read an Avro path as an index', () => {
      const result = ready({
        format: 'avro',
        before: avro('string', 'int'),
        after: avro('long', 'int'),
        findings: [{ id: 1, path: '#/fields/0' }],
      });

      expect(result.unplaced).toEqual([1]);
    });
  });

  describe('Protobuf, whose paths are a message and a field number', () => {
    const proto = (idType: string, qtyType: string, tail = '') =>
      [
        'syntax = "proto3";',
        'package acme;',
        'message Order {',
        `  ${idType} id = 1;`,
        `  ${qtyType} qty = 2;`,
        '  map<string, int32> tags = 3;',
        '}',
        tail,
      ].join('\n');

    it('puts a finding on the field with that number', () => {
      const result = ready({
        format: 'protobuf',
        before: proto('string', 'int32'),
        after: proto('int64', 'string'),
        findings: [
          { id: 1, path: '.acme.Order/1' },
          { id: 2, path: '.acme.Order/2' },
          { id: 3, path: '.acme.Order/3/key' },
        ],
      });

      expect(rowsWith(result, 1).map((r) => [r.kind, r.text.trim()])).toEqual([
        ['added', 'int64 id = 1;'],
      ]);
      expect(rowsWith(result, 2).map((r) => [r.kind, r.text.trim()])).toEqual([
        ['added', 'string qty = 2;'],
      ]);
      expect(rowsWith(result, 3).map((r) => r.text.trim())).toEqual([
        'map<string, int32> tags = 3;',
      ]);
    });

    it('puts a finding about a removed field on its removed line', () => {
      const result = ready({
        format: 'protobuf',
        before: proto('string', 'int32'),
        after: proto('string', 'int32').replace('  int32 qty = 2;\n', ''),
        findings: [{ id: 1, path: '.acme.Order/2' }],
      });

      expect(rowsWith(result, 1).map((r) => [r.kind, r.text.trim()])).toEqual([
        ['removed', 'int32 qty = 2;'],
      ]);
    });

    it('puts a finding about a message that is gone on the baseline’s message line', () => {
      const result = ready({
        format: 'protobuf',
        before: proto('string', 'int32', 'message Extra {}'),
        after: proto('string', 'int32'),
        findings: [{ id: 1, path: '.acme.Extra' }],
      });

      expect(rowsWith(result, 1).map((r) => [r.kind, r.text.trim()])).toEqual([
        ['removed', 'message Extra {}'],
      ]);
    });
  });
});

describe('diffSchemas: the guards', () => {
  const small = { maxLines: 10, maxChanges: 4, context: 3 };
  const request = (before: string, after: string): SchemaDiffRequest => ({
    format: 'protobuf',
    before,
    after,
    findings: [],
  });

  it('refuses a document with more lines than the limit, on either side', () => {
    const eleven = Array.from({ length: 11 }, (_, i) => `l${i}`).join('\n');
    const three = 'a\nb\nc';

    expect(diffSchemas(request(eleven, three), small)).toEqual({
      status: 'too-large',
      reason: 'lines',
      limit: 10,
    });
    expect(diffSchemas(request(three, eleven), small)).toMatchObject({ reason: 'lines' });
  });

  it('accepts a document of exactly the limit', () => {
    const ten = Array.from({ length: 10 }, (_, i) => `l${i}`).join('\n');

    expect(diffSchemas(request(ten, ten), small).status).toBe('ready');
  });

  it('refuses a change of more lines than the limit, and accepts exactly the limit', () => {
    expect(diffSchemas(request('a\nb\nc', 'x\ny\nz'), small)).toEqual({
      status: 'too-large',
      reason: 'changes',
      limit: 4,
    });
    expect(diffSchemas(request('a\nb', 'x\ny'), small).status).toBe('ready');
  });
});
