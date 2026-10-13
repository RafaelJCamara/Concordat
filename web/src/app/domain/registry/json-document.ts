/**
 * A strict JSON reader that keeps what `JSON.parse` throws away.
 *
 * <b>Why not `JSON.parse`.</b> It answers "is this JSON" and nothing else this screen needs:
 *
 * - It accepts a duplicate key (the last one wins), where the registry refuses the document
 *   (`schema_malformed`) because parsers disagree on which value wins. A client built on it is
 *   *looser* than the registry, and the author learns about the problem from a 400.
 * - It has no depth limit short of the JS stack, where the registry's reader stops at 64 levels.
 * - It gives no positions, so an editor cannot underline anything.
 * - It cannot be reversed. Re-serialising a parsed document rewrites number literals — `1.0`
 *   becomes `1`, `12345678901234567890` becomes `12345678901234567000` — and the registry
 *   preserves them verbatim: they change the schema id, and the second one corrupts a
 *   `maximum`. A Format button built on `JSON.stringify(JSON.parse(x))` would quietly mint a
 *   different schema from the one the author wrote.
 *
 * So the document is read into a small tree that keeps each literal's raw text and offsets,
 * and formatting re-emits those slices. Only whitespace is ever rewritten.
 *
 * Pure TypeScript, no framework: it has to run in a worker and in a plain Node test.
 */

/** Where something is in the text. `line` and `column` are 1-based; `column` counts UTF-16 units. */
export interface JsonPosition {
  readonly offset: number;
  readonly line: number;
  readonly column: number;
}

/** The deepest nesting the registry's reader accepts (`System.Text.Json`'s default `MaxDepth`). */
export const MAX_JSON_DEPTH = 64;

export interface Span {
  /** Offset of the first character, inclusive. */
  readonly start: number;
  /** Offset one past the last character. */
  readonly end: number;
}

/** One `"key": value` pair. */
export interface JsonMember extends Span {
  /** The key with its escapes resolved, which is what duplicate detection compares. */
  readonly key: string;
  readonly keySpan: Span;
  readonly value: JsonNode;
}

/** A parsed value. Scalars keep their source text; containers keep their children. */
export type JsonNode =
  | (Span & { readonly kind: 'object'; readonly members: readonly JsonMember[] })
  | (Span & { readonly kind: 'array'; readonly items: readonly JsonNode[] })
  | (Span & { readonly kind: 'string'; readonly value: string })
  | (Span & { readonly kind: 'number' | 'literal' });

/** Why a document could not be read, and where. */
export interface JsonSyntaxError {
  readonly message: string;
  readonly position: JsonPosition;
}

/** The outcome of reading a document. */
export type JsonParse =
  | { readonly ok: true; readonly root: JsonNode; readonly duplicate: JsonDuplicate | null }
  | { readonly ok: false; readonly error: JsonSyntaxError };

/** An object that names one key twice. */
export interface JsonDuplicate {
  readonly key: string;
  /** The second occurrence: the one the registry would not choose between. */
  readonly span: Span;
}

class Stop extends Error {
  constructor(
    message: string,
    readonly offset: number,
  ) {
    super(message);
  }
}

const WHITESPACE = new Set([' ', '\t', '\n', '\r']);
const NUMBER = /-?(?:0|[1-9]\d*)(?:\.\d+)?(?:[eE][+-]?\d+)?/y;
const LITERAL = /true|false|null/y;

/**
 * Reads a document under the same grammar as `System.Text.Json`'s default reader: RFC 8259,
 * no comments, no trailing commas, no leading zeros, nesting no deeper than
 * {@link MAX_JSON_DEPTH}. Stops at the first fault, as the registry does.
 */
export function parseJsonDocument(text: string): JsonParse {
  let at = 0;
  let duplicate: JsonDuplicate | null = null;

  const skip = (): void => {
    while (at < text.length && WHITESPACE.has(text.charAt(at))) {
      at++;
    }
  };

  const describe = (): string =>
    at >= text.length ? 'the end of the document' : `'${text.charAt(at)}'`;

  const expect = (char: string): void => {
    if (text.charAt(at) !== char) {
      throw new Stop(`Expected '${char}' but found ${describe()}.`, at);
    }

    at++;
  };

  const readString = (): { readonly span: Span; readonly value: string } => {
    const start = at;
    expect('"');

    for (;;) {
      if (at >= text.length) {
        throw new Stop('The string is not terminated.', start);
      }

      const char = text.charAt(at);

      if (char === '"') {
        at++;
        break;
      }

      if (char < ' ') {
        throw new Stop('A control character in a string must be escaped.', at);
      }

      if (char === '\\') {
        const escape = text.charAt(at + 1);

        if (escape === 'u') {
          if (!/^[0-9A-Fa-f]{4}$/.test(text.slice(at + 2, at + 6))) {
            throw new Stop("'\\u' must be followed by four hexadecimal digits.", at);
          }

          at += 6;
        } else if (escape !== '' && '"\\/bfnrt'.includes(escape)) {
          at += 2;
        } else {
          throw new Stop(`'\\${escape}' is not a valid escape.`, at);
        }

        continue;
      }

      at++;
    }

    const span = { start, end: at };

    // The slice is already known to be a well-formed JSON string, so this cannot throw.
    return { span, value: JSON.parse(text.slice(start, at)) as string };
  };

  const readValue = (depth: number): JsonNode => {
    skip();

    const start = at;
    const char = text.charAt(at);

    if (char === '{' || char === '[') {
      // The registry's reader refuses the 65th level, not the 64th.
      if (depth + 1 > MAX_JSON_DEPTH) {
        throw new Stop(`Nesting is deeper than ${MAX_JSON_DEPTH} levels.`, at);
      }

      return char === '{' ? readObject(depth + 1) : readArray(depth + 1);
    }

    if (char === '"') {
      const { span, value } = readString();

      return { kind: 'string', ...span, value };
    }

    NUMBER.lastIndex = at;
    const number = NUMBER.exec(text);

    if (number !== null) {
      at += number[0].length;

      // `01` and `1.` are not numbers: a literal has to end where the token does.
      if (/[\w.]/.test(text.charAt(at) ?? '')) {
        throw new Stop(`'${number[0]}${text.charAt(at)}' is not a valid number.`, start);
      }

      return { kind: 'number', start, end: at };
    }

    LITERAL.lastIndex = at;
    const literal = LITERAL.exec(text);

    if (literal !== null) {
      at += literal[0].length;

      if (/\w/.test(text.charAt(at) ?? '')) {
        throw new Stop(`'${literal[0]}${text.charAt(at)}' is not a valid value.`, start);
      }

      return { kind: 'literal', start, end: at };
    }

    throw new Stop(`Expected a value but found ${describe()}.`, at);
  };

  const readObject = (depth: number): JsonNode => {
    const start = at;
    const members: JsonMember[] = [];
    const seen = new Set<string>();

    expect('{');
    skip();

    if (text.charAt(at) === '}') {
      at++;

      return { kind: 'object', start, end: at, members };
    }

    for (;;) {
      skip();

      if (text.charAt(at) !== '"') {
        throw new Stop(`Expected a property name but found ${describe()}.`, at);
      }

      const memberStart = at;
      const key = readString();

      skip();
      expect(':');

      const value = readValue(depth);

      members.push({ key: key.value, keySpan: key.span, value, start: memberStart, end: at });

      if (seen.has(key.value)) {
        // Only the first is kept: it is what the registry names, and one underline is
        // enough to send the author to the right place.
        duplicate ??= { key: key.value, span: key.span };
      }

      seen.add(key.value);

      skip();

      if (text.charAt(at) === ',') {
        at++;
        continue;
      }

      expect('}');

      return { kind: 'object', start, end: at, members };
    }
  };

  const readArray = (depth: number): JsonNode => {
    const start = at;
    const items: JsonNode[] = [];

    expect('[');
    skip();

    if (text.charAt(at) === ']') {
      at++;

      return { kind: 'array', start, end: at, items };
    }

    for (;;) {
      items.push(readValue(depth));
      skip();

      if (text.charAt(at) === ',') {
        at++;
        continue;
      }

      expect(']');

      return { kind: 'array', start, end: at, items };
    }
  };

  try {
    const root = readValue(0);

    skip();

    if (at < text.length) {
      throw new Stop(`Unexpected ${describe()} after the end of the document.`, at);
    }

    return { ok: true, root, duplicate };
  } catch (error) {
    if (error instanceof Stop) {
      return {
        ok: false,
        error: { message: error.message, position: positionAt(text, error.offset) },
      };
    }

    throw error;
  }
}

/** The line and column of an offset. */
export function positionAt(text: string, offset: number): JsonPosition {
  let line = 1;
  let lineStart = 0;

  for (let i = 0; i < offset && i < text.length; i++) {
    if (text[i] === '\n') {
      line++;
      lineStart = i + 1;
    }
  }

  return { offset, line, column: offset - lineStart + 1 };
}

/** The value of a top-level member, or undefined when the root is not an object or lacks it. */
export function rootMember(root: JsonNode, key: string): JsonMember | undefined {
  return root.kind === 'object' ? root.members.find((m) => m.key === key) : undefined;
}

/**
 * Re-indents a document, changing nothing but whitespace.
 *
 * Member order, escapes and number literals are the source's own, character for character —
 * the property that makes it safe to offer on a document whose id is a hash of its content.
 * Empty containers stay on one line. Returns null for a document that does not read, because
 * guessing at structure to format a broken one is how a Format button destroys work.
 */
export function formatJsonDocument(text: string, indent = 2): string | null {
  const parsed = parseJsonDocument(text);

  if (!parsed.ok) {
    return null;
  }

  const pad = (level: number): string => ' '.repeat(indent * level);

  const emit = (node: JsonNode, level: number): string => {
    switch (node.kind) {
      case 'object': {
        if (node.members.length === 0) {
          return '{}';
        }

        const lines = node.members.map(
          (m) =>
            `${pad(level + 1)}${text.slice(m.keySpan.start, m.keySpan.end)}: ${emit(m.value, level + 1)}`,
        );

        return `{\n${lines.join(',\n')}\n${pad(level)}}`;
      }

      case 'array': {
        if (node.items.length === 0) {
          return '[]';
        }

        const lines = node.items.map((item) => `${pad(level + 1)}${emit(item, level + 1)}`);

        return `[\n${lines.join(',\n')}\n${pad(level)}]`;
      }

      default:
        return text.slice(node.start, node.end);
    }
  };

  return emit(parsed.root, 0);
}

/**
 * Finds where a JSON Pointer lands in a parsed document.
 *
 * Returns the span of the value the pointer names, or — when the pointer names something that is
 * not there, as a "must have required property" error does — the deepest value that does exist
 * along it, because pointing at the nearest real thing is more useful than pointing nowhere.
 * Null only when the document has nothing to point at.
 */
export function locatePointer(root: JsonNode, pointer: string): Span | null {
  let node: JsonNode = root;

  if (pointer !== '') {
    for (const raw of pointer.split('/').slice(1)) {
      // RFC 6901: `~1` is `/` and `~0` is `~`, in that order.
      const segment = raw.replaceAll('~1', '/').replaceAll('~0', '~');
      let next: JsonNode | undefined;

      if (node.kind === 'object') {
        next = node.members.find((m) => m.key === segment)?.value;
      } else if (node.kind === 'array') {
        next = node.items[Number(segment)];
      }

      if (next === undefined) {
        break;
      }

      node = next;
    }
  }

  return { start: node.start, end: node.end };
}
