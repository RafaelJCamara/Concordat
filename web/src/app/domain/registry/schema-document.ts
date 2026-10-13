import type { EditorFinding } from './editor-finding';
import { parseJsonDocument, positionAt, rootMember } from './json-document';
import type { SchemaFormat } from './wire-tokens';

/**
 * The only JSON Schema dialects the registry accepts, spelled as it spells them.
 *
 * The specification publishes the identifier with a trailing `#` and the wild uses it both
 * ways; refusing a document over a URI detail its author never chose would be absurd, so the
 * registry takes both and so does this. Compared exactly, case and all, as the registry does:
 * `HTTPS://json-schema.org/…` is a dialect it does not recognise.
 */
export const SUPPORTED_DIALECTS: readonly string[] = [
  'https://json-schema.org/draft/2020-12/schema',
  'https://json-schema.org/draft/2020-12/schema#',
];

/** `Schema.MaxBodyBytes`, in UTF-8 bytes. */
export const MAX_SCHEMA_BYTES = 512 * 1024;

/**
 * The registry's own refusal codes for a document, which are the only ones a client can know
 * from the text alone. They are the registry's spellings (`ConcordatCodes`) on purpose, so a
 * test can hold both ends to one list and a message can name the code the registry would
 * answer with.
 */
export type DocumentFindingCode =
  'schema_body_empty' | 'schema_too_large' | 'schema_malformed' | 'schema_dialect_unsupported';

/** Something about a document the registry would certainly refuse. */
export interface DocumentFinding extends EditorFinding {
  readonly severity: 'refusal';
  readonly code: DocumentFindingCode;
}

/**
 * What the registry will refuse about this text, and nothing else.
 *
 * <b>Every finding here is a certain refusal.</b> That is the contract that lets the screen
 * block a submit on them: a client check that is stricter than the registry stops somebody
 * registering a document the registry would have taken, and one that is looser only costs a
 * round trip. So this is deliberately the *intersection* — the checks `Canonicalize` and the
 * dialect check make, ported rule for rule — and not "what a well-behaved schema looks like".
 * Whether a document is a *good* schema is advice, and lives elsewhere (`schema-checker`).
 *
 * <b>Takes what is in the editor and judges what would be sent.</b> The page sends the text
 * trimmed, so the checks run on the trimmed text — a leading byte-order mark, which JavaScript's
 * `trim` strips and the registry's reader would refuse, never reaches the registry and so is no
 * finding — but every position is reported in the *editor's* text, leading whitespace included.
 * Positions in the trimmed text would put each underline a few characters to the left of what
 * it points at, by exactly the length of the indentation above it.
 *
 * Mirrors, in the registry's order: `schema_body_empty`, `schema_too_large` (measured in
 * UTF-8 bytes, not characters), `schema_malformed` (not JSON, nesting past 64, a surrogate
 * escape with no partner, a duplicate key), then the dialect. JSON Schema and Avro are both JSON and share the first three;
 * Protobuf is parsed by a different grammar that is the registry's to judge, so it gets the
 * first two only. Only JSON Schema has a dialect.
 */
export function checkSchemaText(
  editorText: string,
  format: SchemaFormat,
): readonly DocumentFinding[] {
  const text = editorText.trim();
  const leading = editorText.length - editorText.trimStart().length;
  const place = (offset: number) => positionAt(editorText, leading + offset);

  if (text === '') {
    return [
      {
        severity: 'refusal',
        code: 'schema_body_empty',
        message: 'A schema body is required.',
        position: null,
        length: 0,
      },
    ];
  }

  const bytes = new TextEncoder().encode(text).length;

  if (bytes > MAX_SCHEMA_BYTES) {
    return [
      {
        severity: 'refusal',
        code: 'schema_too_large',
        message: `A schema body may be at most ${MAX_SCHEMA_BYTES} bytes; this one is ${bytes}.`,
        position: null,
        length: 0,
      },
    ];
  }

  if (format === 'protobuf') {
    return [];
  }

  const parsed = parseJsonDocument(text);

  if (!parsed.ok) {
    return [
      {
        severity: 'refusal',
        code: 'schema_malformed',
        message: `Not well-formed JSON: ${parsed.error.message}`,
        position: place(parsed.error.position.offset),
        length: 1,
      },
    ];
  }

  const findings: DocumentFinding[] = [];

  if (parsed.unpaired !== null) {
    const { unit, span } = parsed.unpaired;

    // First, as in the registry, which also refuses to quote the string it found. The underline
    // is the escape itself rather than the whole string: a long description with one bad
    // escape in it is findable only if the finding says which six characters.
    findings.push({
      severity: 'refusal',
      code: 'schema_malformed',
      message:
        `The surrogate escape \\u${unit.toString(16).toUpperCase().padStart(4, '0')} has no ` +
        'partner. A surrogate is only a character as a high half followed directly by a low ' +
        'half, both written as escapes; alone, the string has no UTF-8 spelling and parsers ' +
        'disagree about what it is, so this document has no single meaning.',
      position: place(span.start),
      length: span.end - span.start,
    });
  }

  if (parsed.duplicate !== null) {
    const { key, span } = parsed.duplicate;

    findings.push({
      severity: 'refusal',
      code: 'schema_malformed',
      message:
        `Duplicate object key '${key}'. JSON parsers disagree on which value wins, so this ` +
        'document has no single meaning.',
      position: place(span.start),
      length: span.end - span.start,
    });
  }

  if (format === 'json') {
    const dialect = rootMember(parsed.root, '$schema');

    // Only a string at the root is read, and anything else is left alone — an absent `$schema`
    // means 2020-12 by assumption, with no warning, because one that fires on almost every
    // schema anyone writes is noise.
    if (dialect?.value.kind === 'string') {
      const declared = JSON.parse(text.slice(dialect.value.start, dialect.value.end)) as string;

      if (!SUPPORTED_DIALECTS.includes(declared)) {
        findings.push({
          severity: 'refusal',
          code: 'schema_dialect_unsupported',
          message:
            `Dialect '${declared}' is not supported; Concordat implements JSON Schema draft ` +
            "2020-12 only. Keywords changed meaning between drafts — 'items' most visibly — so " +
            'validating this document under 2020-12 would apply rules it was not written against.',
          position: place(dialect.value.start),
          length: dialect.value.end - dialect.value.start,
        });
      }
    }
  }

  return findings;
}
