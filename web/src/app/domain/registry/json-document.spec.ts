import { describe, expect, it } from 'vitest';
import {
  MAX_JSON_DEPTH,
  formatJsonDocument,
  locatePointer,
  parseJsonDocument,
} from './json-document';

// The reader exists because `JSON.parse` is both looser than the registry (it accepts a
// duplicate key) and lossy (it cannot give a number back as it was written). Each block below
// pins one of those, plus the grammar the registry's reader enforces.

describe('parseJsonDocument', () => {
  it('reads every kind of value', () => {
    const parsed = parseJsonDocument('{"a": [1, -2.5e3, "x", true, false, null, {}], "b": {}}');

    expect(parsed.ok).toBe(true);
  });

  it.each([
    ['a trailing comma in an object', '{"a": 1,}'],
    ['a trailing comma in an array', '[1, 2,]'],
    ['a comment', '{"a": 1 /* no */}'],
    ['a line comment', '{"a": 1} // no'],
    ['single quotes', "{'a': 1}"],
    ['an unquoted key', '{a: 1}'],
    ['a leading zero', '{"a": 01}'],
    ['a bare decimal point', '{"a": 1.}'],
    ['a leading plus', '{"a": +1}'],
    ['a hex number', '{"a": 0x10}'],
    ['NaN', '{"a": NaN}'],
    ['a raw newline in a string', '{"a": "x\ny"}'],
    ['an invalid escape', '{"a": "\\q"}'],
    ['a short unicode escape', '{"a": "\\u12"}'],
    ['an unterminated string', '{"a": "x'],
    ['an unterminated object', '{"a": 1'],
    ['content after the document', '{} {}'],
    ['a byte-order mark', '﻿{}'],
    ['nothing', ''],
  ])('refuses %s', (_name, text) => {
    expect(parseJsonDocument(text).ok).toBe(false);
  });

  it('accepts a bare scalar and surrounding whitespace, as the registry does', () => {
    // The JSON canonicaliser takes any well-formed document; whether the root should be an
    // object is a later question and the registry's.
    expect(parseJsonDocument(' \n\t42 \r\n').ok).toBe(true);
    expect(parseJsonDocument('true').ok).toBe(true);
    expect(parseJsonDocument('"s"').ok).toBe(true);
  });

  it('points at the fault, with 1-based line and column', () => {
    const parsed = parseJsonDocument('{\n  "a": 1,\n  "b": ?\n}');

    expect(parsed.ok).toBe(false);

    if (!parsed.ok) {
      expect(parsed.error.position).toMatchObject({ line: 3, column: 8 });
    }
  });

  describe('depth', () => {
    const nested = (levels: number) => '['.repeat(levels) + ']'.repeat(levels);

    it(`allows ${MAX_JSON_DEPTH} levels and refuses the next`, () => {
      // The registry's reader stops at `MaxDepth` 64 and `JSON.parse` does not, so a client
      // built on it would pass a document the registry answers with `schema_malformed`.
      expect(parseJsonDocument(nested(MAX_JSON_DEPTH)).ok).toBe(true);
      expect(parseJsonDocument(nested(MAX_JSON_DEPTH + 1)).ok).toBe(false);
    });

    it('refuses a pathological document without exhausting the stack', () => {
      expect(parseJsonDocument('['.repeat(200_000)).ok).toBe(false);
    });
  });

  describe('duplicate keys', () => {
    it('reports one, which JSON.parse silently resolves to the last', () => {
      const parsed = parseJsonDocument('{"type": "object", "type": "string"}');

      expect(parsed.ok && parsed.duplicate?.key).toBe('type');
    });

    it('finds one nested anywhere', () => {
      const parsed = parseJsonDocument('{"properties": {"id": {"a": 1, "a": 2}}}');

      expect(parsed.ok && parsed.duplicate?.key).toBe('a');
    });

    it('compares keys after their escapes are resolved', () => {
      // `"a"` and `"a"` are the same key, and are the same to the registry's reader.
      const parsed = parseJsonDocument('{"a": 1, "\\u0061": 2}');

      expect(parsed.ok && parsed.duplicate?.key).toBe('a');
    });

    it('does not mistake the same key in sibling objects for a duplicate', () => {
      const parsed = parseJsonDocument('{"x": {"id": 1}, "y": {"id": 2}}');

      expect(parsed.ok && parsed.duplicate).toBeNull();
    });

    it('does not mistake keys that differ only in case for a duplicate', () => {
      // The registry compares ordinally.
      const parsed = parseJsonDocument('{"Id": 1, "id": 2}');

      expect(parsed.ok && parsed.duplicate).toBeNull();
    });
  });

  // `JSON.parse` keeps a lone surrogate as a string it can no longer encode as UTF-8, and the
  // registry refuses the document. The cases are the ones the registry's own scanner is tested
  // on, and `schema-parity.spec.ts` holds the list against the real dry-run endpoint.
  describe('surrogate escapes', () => {
    const unpaired = (text: string) => {
      const parsed = parseJsonDocument(text);

      return parsed.ok ? parsed.unpaired : undefined;
    };

    it.each([
      ['a lone high half', '{"a": "\\ud800"}', 0xd800],
      ['a lone low half', '{"a": "\\udc00"}', 0xdc00],
      ['a high half followed by text', '{"a": "\\ud800x"}', 0xd800],
      ['a high half at the end of a string', '{"a": "x\\ud83d"}', 0xd83d],
      ['a pair written the wrong way round', '{"a": "\\ude00\\ud83d"}', 0xde00],
      ['a high half followed by another high half', '{"a": "\\ud83d\\ud83d\\ude00"}', 0xd83d],
      ['a high half followed by a non-surrogate escape', '{"a": "\\ud83d\\u0041"}', 0xd83d],
      ['a lone half in a key', '{"\\ud800": 1}', 0xd800],
      ['a lone half nested deep', '{"a": [{"b": ["\\udfff"]}]}', 0xdfff],
    ])('finds %s', (_label, text, unit) => {
      expect(unpaired(text)?.unit).toBe(unit);
    });

    it('points at the six characters of the escape, not at the string', () => {
      const text = '{"description": "see \\ud800 here"}';
      const found = unpaired(text);

      expect(found && text.slice(found.span.start, found.span.end)).toBe('\\ud800');
    });

    it('reports the first in document order', () => {
      // A key is read before its value, and both before anything after them.
      expect(unpaired('{"\\udc00": "\\ud800"}')?.unit).toBe(0xdc00);
      expect(unpaired('{"a": "\\ud800", "b": "\\udc00"}')?.unit).toBe(0xd800);
    });

    it.each([
      ['a pair', '{"a": "\\ud83d\\ude00"}'],
      ['a pair in capitals', '{"a": "\\uD83D\\uDE00"}'],
      ['a pair in mixed case', '{"a": "\\ud83D\\uDe00"}'],
      ['the first and the last pair', '{"a": "\\ud800\\udc00\\udbff\\udfff"}'],
      ['two pairs in a row', '{"a": "\\ud83d\\ude00\\ud83d\\ude00"}'],
      ['the character itself', '{"a": "\u{1F600}"}'],
      ['the code units either side of the block', '{"a": "\\ud7ff\\ue000"}'],
    ])('accepts %s', (_label, text) => {
      expect(unpaired(text)).toBeNull();
    });

    it('does not read an escaped backslash as the start of an escape', () => {
      // JSON text `"\\ud800"` is a backslash and then the letters u d 8 0 0. A scan that looks
      // for backslash-u without consuming the escaped backslash first would refuse it.
      expect(unpaired('{"a": "\\\\ud800"}')).toBeNull();
      expect(unpaired('{"a": "\\\\\\\\ud800"}')).toBeNull();
      expect(unpaired('{"a": "\\\\\\ud800"}')?.unit).toBe(0xd800);
    });

    it('leaves a document that does not read to the syntax error', () => {
      expect(parseJsonDocument('{"a": "\\ud800').ok).toBe(false);
    });
  });
});

describe('formatJsonDocument', () => {
  it('re-indents with two spaces and puts each member on its own line', () => {
    expect(formatJsonDocument('{"a":1,"b":[1,2],"c":{"d":null}}')).toBe(
      [
        '{',
        '  "a": 1,',
        '  "b": [',
        '    1,',
        '    2',
        '  ],',
        '  "c": {',
        '    "d": null',
        '  }',
        '}',
      ].join('\n'),
    );
  });

  it('keeps empty containers on one line', () => {
    expect(formatJsonDocument('{"a":{},"b":[]}')).toBe('{\n  "a": {},\n  "b": []\n}');
  });

  it('preserves number literals exactly', () => {
    // The registry hashes the number as written: `1.0` and `1` are different schemas, and a
    // value past 2^53 does not survive a round trip through a double. A Format button built
    // on JSON.stringify(JSON.parse(x)) would mint a different schema, silently.
    const source = '{"a":1.0,"b":12345678901234567890,"c":1E5,"d":-0,"e":0.10}';
    const formatted = formatJsonDocument(source)!;

    for (const literal of ['1.0', '12345678901234567890', '1E5', '-0', '0.10']) {
      expect(formatted).toContain(literal);
    }
  });

  it('preserves string escapes and member order', () => {
    const source = '{"z":"\\u00e9\\n\\/","a":"é"}';
    const formatted = formatJsonDocument(source)!;

    expect(formatted).toBe('{\n  "z": "\\u00e9\\n\\/",\n  "a": "é"\n}');
  });

  it('changes nothing but whitespace', () => {
    const source =
      '{"type":"object","properties":{"id":{"type":"string","maxLength":36}},"required":["id"]}';
    const strip = (text: string) => text.replace(/\s+(?=(?:[^"\\]|\\.|"(?:[^"\\]|\\.)*")*$)/g, '');

    expect(strip(formatJsonDocument(source)!)).toBe(strip(source));
  });

  it('is idempotent', () => {
    const once = formatJsonDocument('{"a":[1,{"b":2}],"c":"x"}')!;

    expect(formatJsonDocument(once)).toBe(once);
  });

  it('refuses to format a document that does not read', () => {
    // Guessing at the structure of a broken document is how a Format button destroys work.
    expect(formatJsonDocument('{"a": ')).toBeNull();
    expect(formatJsonDocument('{"a": 1, "a": 2,}')).toBeNull();
  });

  it('formats a document the registry would refuse for a duplicate key, leaving the finding for the checker', () => {
    // Formatting is whitespace; whether the document is acceptable is a separate question
    // and has its own message. Refusing to format here would hide the layout that makes the
    // duplicate easy to find.
    expect(formatJsonDocument('{"a":1,"a":2}')).toBe('{\n  "a": 1,\n  "a": 2\n}');
  });
});

describe('locatePointer', () => {
  const text = '{"a":{"b":[10,{"c":"x"}]},"d/e":1,"f~g":2}';
  const parsed = parseJsonDocument(text);
  const root = parsed.ok ? parsed.root : null;

  const slice = (pointer: string) => {
    const span = locatePointer(root!, pointer);

    return span === null ? null : text.slice(span.start, span.end);
  };

  it('finds the value a pointer names', () => {
    expect(slice('/a/b/1/c')).toBe('"x"');
    expect(slice('/a/b/0')).toBe('10');
    expect(slice('/a/b/1')).toBe('{"c":"x"}');
  });

  it('is the whole document for the empty pointer', () => {
    expect(slice('')).toBe(text);
  });

  it('reads the escapes RFC 6901 defines, in the right order', () => {
    expect(slice('/d~1e')).toBe('1');
    expect(slice('/f~0g')).toBe('2');
  });

  it('lands on the nearest thing that exists when the pointer names something missing', () => {
    // "must have required property" errors point at a member that is not there, and the useful
    // place to send the author is the object that lacks it.
    expect(slice('/a/b/1/missing')).toBe('{"c":"x"}');
    expect(slice('/nope/deeper')).toBe(text);
  });
});
