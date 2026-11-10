import { describe, expect, it } from 'vitest';
import { readProtoOutline } from './proto-outline';

// The keys are the ones `ProtoSchemaCompatibilityChecker` reports a finding under, taken from a
// real registry's answer: `.acme.Order/1` for a field, `.acme.Color/1` for an enum value, the
// bare name for a message. A scanner that keyed them any other way would tie nothing.

const get = (text: string, key: string): number | undefined =>
  readProtoOutline(text).lines.get(key);

describe('readProtoOutline', () => {
  const text = [
    'syntax = "proto3";', // 1
    'package acme;', // 2
    '', // 3
    'message Order {', // 4
    '  string id = 1;', // 5
    '  optional int32 qty = 2 [deprecated = true];', // 6
    '  map<string, int32> tags = 3;', // 7
    '  oneof payment {', // 8
    '    string card = 4;', // 9
    '    string iban = 5;', // 10
    '  }', // 11
    '  message Line {', // 12
    '    int64 sku = 1;', // 13
    '  }', // 14
    '  reserved 9, 10;', // 15
    '}', // 16
    'enum Color { RED = 0; BLUE = 1; }', // 17
  ].join('\n');

  it.each([
    ['a message', '.acme.Order', 4],
    ['a field', '.acme.Order/1', 5],
    ['a field with options', '.acme.Order/2', 6],
    ['a map field', '.acme.Order/3', 7],
    ['a field in a oneof, which belongs to the message around it', '.acme.Order/4', 9],
    ['a nested message', '.acme.Order.Line', 12],
    ['a field of a nested message, under the nested name', '.acme.Order.Line/1', 13],
    ['an enum', '.acme.Color', 17],
    ['an enum value on the line of its enum', '.acme.Color/1', 17],
  ])('finds %s', (_name, key, line) => {
    expect(get(text, key)).toBe(line);
  });

  it('does not take a reserved range, an option or the syntax line for a field', () => {
    const { lines } = readProtoOutline(text);

    expect(lines.has('.acme.Order/9')).toBe(false);
    expect([...lines.keys()].some((key) => key.includes('syntax'))).toBe(false);
  });

  it('keys by the package, and by nothing when there is none', () => {
    expect(get('package a.b.c;\nmessage M {\n int32 x = 7;\n}', '.a.b.c.M/7')).toBe(3);
    expect(get('message M {\n int32 x = 7;\n}', '.M/7')).toBe(2);
  });

  it('numbers lines through comments, block comments and strings', () => {
    const commented = [
      '// message Ghost { int32 x = 1; }', // 1
      '/* a block', // 2
      '   message Ghost2 { } */', // 3
      'message Real {', // 4
      '  string s = 1 [default = "}; message Fake {"];', // 5
      '}', // 6
    ].join('\n');

    expect(get(commented, '.Real/1')).toBe(5);
    expect(get(commented, '.Ghost')).toBeUndefined();
    expect(get(commented, '.Fake')).toBeUndefined();
  });

  it('reads negative and hexadecimal enum numbers as numbers', () => {
    const enums = 'enum E {\n NEG = -1;\n HEX = 0x10;\n}';

    expect(get(enums, '.E/-1')).toBe(2);
    expect(get(enums, '.E/16')).toBe(3);
  });

  it('does not take an option whose value is a number for a field', () => {
    // `option (my.opt) = 5;` has an `=` and a number, and is exactly what a field looks like.
    const text = ['message M {', '  option (my.opt) = 5;', '  int32 f = 1;', '}'].join(
      String.fromCharCode(10),
    );

    expect(get(text, '.M/5')).toBeUndefined();
    expect(get(text, '.M/1')).toBe(3);
  });

  it('does not read a comment after a statement as the start of the next one', () => {
    const commented = [
      'message M {',
      '  int32 a = 1; // see n = 7;',
      '  int32 b = 2; /* or m = 8; */',
      '}',
    ].join(String.fromCharCode(10));

    expect(get(commented, '.M/1')).toBe(2);
    expect(get(commented, '.M/2')).toBe(3);
    expect(get(commented, '.M/7')).toBeUndefined();
    expect(get(commented, '.M/8')).toBeUndefined();
  });

  it('ignores the body of an option block and of a service', () => {
    const noisy =
      'message M {\n option (x) = { a: 1 };\n int32 f = 1;\n}\nservice S {\n rpc Call(M) returns (M) { option (y) = 2; }\n}';

    expect(get(noisy, '.M/1')).toBe(3);
    expect([...readProtoOutline(noisy).lines.keys()].sort()).toEqual(['.M', '.M/1']);
  });

  it('keeps the first of two declarations of one key', () => {
    expect(get('message M {\n int32 a = 1;\n int32 b = 1;\n}', '.M/1')).toBe(2);
  });

  it('survives an unterminated string or comment', () => {
    expect(() => readProtoOutline('message M { string s = 1 [x = "oops')).not.toThrow();
    expect(() => readProtoOutline('/* never closed')).not.toThrow();
  });
});
