/**
 * Where the things a Protobuf compatibility finding names are, in the `.proto` text.
 *
 * <b>Why this exists.</b> The registry reports a Protobuf finding at `.acme.Order/3` — the fully
 * qualified message and a field number — or at `.acme.Color/1` for an enum value, or at
 * `.acme.Order` for the message itself. None of that is a JSON Pointer, and none of it is a line.
 * To tie a finding to the line it is about, the text is read once and each declaration is
 * recorded under the same key the registry's `ProtoSchemaCompatibilityChecker` would build.
 *
 * <b>A scanner, not a parser, and it is not the registry's.</b> It reads only what is needed to
 * name declarations: comments and strings are skipped, braces nest, and a statement of the form
 * `… = <number>` inside a message or an enum is a field or a value. It does not validate, so a
 * document the registry would refuse yields whatever the scanner could see; the worst outcome of
 * a misreading is a finding listed as "not tied to a line", never a finding on the wrong line
 * that the reader cannot tell is wrong, because a key the scan did not produce simply is not found.
 *
 * Pure TypeScript, because it runs in a worker.
 */

/** Declarations by the registry's key; each value is a 1-based line number. */
export interface ProtoOutline {
  readonly lines: ReadonlyMap<string, number>;
}

interface Token {
  readonly text: string;
  /** 1-based. */
  readonly line: number;
}

/** What a `{` opened. `oneof` is transparent: its fields belong to the message around it. */
interface Frame {
  readonly kind: 'message' | 'enum' | 'oneof' | 'other';
  /** The declaration's key prefix: the full name for a message or an enum, inherited by a oneof. */
  readonly name: string;
}

/** Statements that contain `=` or a number without declaring a field. */
const NOT_A_FIELD = new Set([
  'syntax',
  'edition',
  'import',
  'option',
  'reserved',
  'extensions',
  'package',
  'rpc',
]);

const NUMBER = /^-?(?:0[xX][0-9a-fA-F]+|\d+)$/;

/** Reads the declarations of a `.proto` document. */
export function readProtoOutline(text: string): ProtoOutline {
  const lines = new Map<string, number>();
  const frames: Frame[] = [];
  let prefix = '';
  let statement: Token[] = [];

  const current = (): Frame | undefined => frames[frames.length - 1];

  for (const token of tokenize(text)) {
    switch (token.text) {
      case '{': {
        const [keyword, name] = statement.map((t) => t.text);
        const parent = current();
        const owner = parent?.kind === 'message' || parent?.kind === 'enum' ? parent.name : prefix;

        if ((keyword === 'message' || keyword === 'enum') && name !== undefined) {
          const full = `${parent?.kind === 'message' ? parent.name : prefix}.${name}`;
          frames.push({ kind: keyword, name: full });
          // The first declaration of a name wins, as the registry's dictionary of messages does.
          if (!lines.has(full)) {
            lines.set(full, statement[0]?.line ?? token.line);
          }
        } else if (keyword === 'oneof' && parent !== undefined) {
          frames.push({ kind: 'oneof', name: owner });
        } else {
          frames.push({ kind: 'other', name: owner });
        }

        statement = [];
        break;
      }

      case '}':
        frames.pop();
        statement = [];
        break;

      case ';': {
        const first = statement[0];

        if (first?.text === 'package' && statement.length > 1) {
          prefix = `.${statement
            .slice(1)
            .map((t) => t.text)
            .join('')}`;
        } else if (first !== undefined && !NOT_A_FIELD.has(first.text)) {
          const owner = current();
          const equals = statement.findIndex((t) => t.text === '=');
          const number = statement[equals + 1]?.text;

          if (
            owner !== undefined &&
            owner.kind !== 'other' &&
            equals > 0 &&
            number !== undefined &&
            NUMBER.test(number)
          ) {
            const key = `${owner.name}/${Number(number)}`;

            if (!lines.has(key)) {
              lines.set(key, first.line);
            }
          }
        }

        statement = [];
        break;
      }

      default:
        statement.push(token);
    }
  }

  return { lines };
}

/**
 * Cuts the text into words and the punctuation that matters, dropping comments and strings.
 *
 * A string is kept as one token (`"…"`) so that a `;` or `{` inside one is not read as syntax.
 */
function* tokenize(text: string): Generator<Token> {
  let line = 1;
  let at = 0;

  while (at < text.length) {
    const char = text.charAt(at);

    if (char === '\n') {
      line++;
      at++;
    } else if (/\s/.test(char)) {
      at++;
    } else if (text.startsWith('//', at)) {
      while (at < text.length && text.charAt(at) !== '\n') {
        at++;
      }
    } else if (text.startsWith('/*', at)) {
      const end = text.indexOf('*/', at + 2);
      const stop = end === -1 ? text.length : end + 2;

      for (let i = at; i < stop; i++) {
        if (text.charAt(i) === '\n') {
          line++;
        }
      }

      at = stop;
    } else if (char === '"' || char === "'") {
      const start = at;
      at++;

      while (at < text.length && text.charAt(at) !== char && text.charAt(at) !== '\n') {
        at += text.charAt(at) === '\\' ? 2 : 1;
      }

      at++;
      yield { text: text.slice(start, at), line };
    } else if (/[A-Za-z0-9_.+-]/.test(char)) {
      const start = at;

      while (at < text.length && /[A-Za-z0-9_.+-]/.test(text.charAt(at))) {
        at++;
      }

      yield { text: text.slice(start, at), line };
    } else {
      yield { text: char, line };
      at++;
    }
  }
}
