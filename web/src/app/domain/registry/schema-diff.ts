import { diffLines, LineOp } from './line-diff';
import { parseJsonDocument, type JsonNode, type JsonMember } from './json-document';
import { readProtoOutline } from './proto-outline';
import type { SchemaFormat } from './wire-tokens';

/**
 * What a held version changes in the schema, as lines (ADR-050).
 *
 * The registry's compatibility engine says *what kind* of change a version is and where, in its
 * own vocabulary; this says what moved in the document itself. They are two views of one change
 * and the screen shows both, so a finding is tied to the line it is about.
 *
 * Pure TypeScript, no framework: it runs in a worker and in a plain Node test.
 */

/** A finding to tie to a line: the registry's own `path`, and an id the caller can find it by. */
export interface SchemaDiffFinding {
  readonly id: number;
  readonly path: string;
}

/** Plain data, because it crosses a `postMessage` boundary. */
export interface SchemaDiffRequest {
  readonly format: SchemaFormat;
  /** The baseline's document, as registered. */
  readonly before: string;
  /** The held version's document, as registered. */
  readonly after: string;
  readonly findings: readonly SchemaDiffFinding[];
}

/** One line of the merged view. */
export interface DiffRow {
  readonly kind: 'context' | 'added' | 'removed';
  /** The line's number in the baseline, or null for an added line. 1-based. */
  readonly oldNumber: number | null;
  /** The line's number in the held version, or null for a removed line. 1-based. */
  readonly newNumber: number | null;
  readonly text: string;
  /** The ids of the findings the registry reported about this line. */
  readonly findings: readonly number[];
}

/**
 * A stretch of the merged view: lines on show, or unchanged lines folded away.
 *
 * A folded stretch carries where it starts and how long it is, not its lines: they are the new
 * document's own, which {@link SchemaDiffReady.lines} holds, and copying twenty thousand of them
 * into row objects for a reader who may never expand them is the cost this avoids.
 */
export type DiffSegment =
  | { readonly kind: 'rows'; readonly rows: readonly DiffRow[] }
  | {
      readonly kind: 'folded';
      /** The baseline's line number of the first folded line. */
      readonly oldStart: number;
      /** The held version's line number of the first folded line. */
      readonly newStart: number;
      readonly count: number;
    };

/** The line diff was made. */
export interface SchemaDiffReady {
  readonly status: 'ready';
  readonly segments: readonly DiffSegment[];
  /** The held version's canonical lines, for the stretches that were folded. */
  readonly lines: readonly string[];
  readonly added: number;
  readonly removed: number;
  /** The ids of findings whose path could not be placed on any line of either document. */
  readonly unplaced: readonly number[];
}

/** The documents are not shown line by line, and why. */
export interface SchemaDiffTooLarge {
  readonly status: 'too-large';
  /** `lines`: a document is longer than the limit. `changes`: more lines changed than the limit. */
  readonly reason: 'lines' | 'changes';
  readonly limit: number;
}

/** The outcome of {@link diffSchemas}. */
export type SchemaDiffResult = SchemaDiffReady | SchemaDiffTooLarge;

/** What the view will and will not do. A parameter, so the tests need not build a huge document. */
export interface SchemaDiffLimits {
  /** The longest a document may be, in lines, on either side. */
  readonly maxLines: number;
  /** The most lines that may be added plus removed. */
  readonly maxChanges: number;
  /** Unchanged lines shown either side of a change. */
  readonly context: number;
}

/**
 * The limits the app uses (ADR-050).
 *
 * The registry accepts a schema of up to 512 KiB, which pretty-printed is a few tens of thousands
 * of lines; `maxLines` is above any document it would accept, so it only trips on something the
 * registry's own limit should have stopped. `maxChanges` is the one that bites: past a thousand
 * changed lines nobody is reviewing a diff, they are reading a new document, and the algorithm
 * is quadratic in exactly that number.
 */
export const SCHEMA_DIFF_LIMITS: SchemaDiffLimits = {
  maxLines: 100_000,
  maxChanges: 1_000,
  context: 3,
};

/** A folded stretch must hide at least this many lines, or the lines are shown instead. */
const MIN_FOLD = 2;

/**
 * Compares two versions of a schema line by line.
 *
 * Both documents are put in a canonical form first (see {@link canonicalise}), so a difference of
 * whitespace or key order — which the registry does not treat as a difference either — is not a
 * line in the diff.
 */
export function diffSchemas(
  request: SchemaDiffRequest,
  limits: SchemaDiffLimits = SCHEMA_DIFF_LIMITS,
): SchemaDiffResult {
  const before = canonicalise(request.format, request.before);
  const after = canonicalise(request.format, request.after);

  if (Math.max(before.lines.length, after.lines.length) > limits.maxLines) {
    return { status: 'too-large', reason: 'lines', limit: limits.maxLines };
  }

  const script = diffLines(before.lines, after.lines, limits.maxChanges);

  if (script === null) {
    return { status: 'too-large', reason: 'changes', limit: limits.maxChanges };
  }

  // Which row each line of each document is on, so a finding's line finds its row.
  const rowOfOld = new Int32Array(before.lines.length + 1).fill(-1);
  const rowOfNew = new Int32Array(after.lines.length + 1).fill(-1);
  const oldNumber = new Int32Array(script.length);
  const newNumber = new Int32Array(script.length);
  let oldAt = 0;
  let newAt = 0;
  let added = 0;
  let removed = 0;

  for (let row = 0; row < script.length; row++) {
    const op = script[row];

    if (op !== LineOp.Added) {
      oldNumber[row] = ++oldAt;
      rowOfOld[oldAt] = row;
    }

    if (op !== LineOp.Removed) {
      newNumber[row] = ++newAt;
      rowOfNew[newAt] = row;
    }

    if (op === LineOp.Added) {
      added++;
    } else if (op === LineOp.Removed) {
      removed++;
    }
  }

  // A finding goes on the held version's line when it has one and on the baseline's when it does
  // not: the held version is the document under review, and a path that exists only in the
  // baseline is something the change took away, which is its removed line.
  const findingsOnRow = new Map<number, number[]>();
  const unplaced: number[] = [];

  for (const finding of request.findings) {
    const newLine = after.locate(finding.path);
    const oldLine = newLine === null ? before.locate(finding.path) : null;
    const row =
      newLine !== null ? rowOfNew[newLine] : oldLine !== null ? rowOfOld[oldLine] : undefined;

    if (row === undefined || row < 0) {
      unplaced.push(finding.id);
      continue;
    }

    const existing = findingsOnRow.get(row);

    if (existing === undefined) {
      findingsOnRow.set(row, [finding.id]);
    } else {
      existing.push(finding.id);
    }
  }

  // Shown: every change, every line a finding sits on, and the context round both.
  const shown = new Uint8Array(script.length);

  for (let row = 0; row < script.length; row++) {
    if (script[row] !== LineOp.Same || findingsOnRow.has(row)) {
      const from = Math.max(0, row - limits.context);
      const to = Math.min(script.length - 1, row + limits.context);

      shown.fill(1, from, to + 1);
    }
  }

  // A fold that would hide a single line saves nothing and costs a button to find it.
  for (let row = 0; row < script.length;) {
    if (shown[row] === 1) {
      row++;
      continue;
    }

    let end = row;
    while (end < script.length && shown[end] === 0) {
      end++;
    }

    if (end - row < MIN_FOLD) {
      shown.fill(1, row, end);
    }

    row = end;
  }

  const rowAt = (row: number): DiffRow => {
    const op = script[row];
    const from = oldNumber[row] ?? 0;
    const to = newNumber[row] ?? 0;

    return {
      kind: op === LineOp.Added ? 'added' : op === LineOp.Removed ? 'removed' : 'context',
      oldNumber: op === LineOp.Added ? null : from,
      newNumber: op === LineOp.Removed ? null : to,
      text: (op === LineOp.Removed ? before.lines[from - 1] : after.lines[to - 1]) ?? '',
      findings: findingsOnRow.get(row) ?? [],
    };
  };

  const segments: DiffSegment[] = [];
  let rows: DiffRow[] = [];

  for (let row = 0; row < script.length;) {
    if (shown[row] === 1) {
      rows.push(rowAt(row));
      row++;
      continue;
    }

    if (rows.length > 0) {
      segments.push({ kind: 'rows', rows });
      rows = [];
    }

    let end = row;
    while (end < script.length && shown[end] === 0) {
      end++;
    }

    segments.push({
      kind: 'folded',
      oldStart: oldNumber[row] ?? 0,
      newStart: newNumber[row] ?? 0,
      count: end - row,
    });
    row = end;
  }

  if (rows.length > 0) {
    segments.push({ kind: 'rows', rows });
  }

  return { status: 'ready', segments, lines: after.lines, added, removed, unplaced };
}

/** A document in the form that is compared, and a way to find a registry path in it. */
export interface CanonicalDocument {
  readonly lines: readonly string[];
  /** The 1-based line a registry finding's `path` names, or null when it names nothing here. */
  readonly locate: (path: string) => number | null;
}

/**
 * Puts a document in the form two versions are compared in.
 *
 * - <b>JSON Schema and Avro</b> are JSON. Both are re-emitted with two-space indent and object
 *   members in code-unit order, which is the order the registry's own canonical form puts them in
 *   (ADR-015): a version that only reorders keys has the same schema id, and a diff that showed
 *   it would contradict the registry. Array order is kept, since it is significant. Number
 *   literals and string escapes are the source's own text, never re-serialised, because the
 *   registry treats `1.0` and `1` as different schemas and so must a reviewer (ADR-028).
 * - <b>Protobuf</b> is line-oriented and is compared as it was written, with the line endings
 *   normalised and trailing whitespace trimmed.
 *
 * A document that does not read as JSON, which the registry would not have stored, is compared as
 * text, with nothing to tie a finding to.
 */
export function canonicalise(format: SchemaFormat, text: string): CanonicalDocument {
  if (format === 'protobuf') {
    const outline = readProtoOutline(text);
    const lines = splitLines(text);

    return {
      lines,
      locate: (path) => {
        // A map field's key is reported one segment below the field, at `<field>/key`.
        const own = outline.lines.get(path) ?? outline.lines.get(path.replace(/\/key$/, ''));
        return own ?? null;
      },
    };
  }

  const parsed = parseJsonDocument(text);

  if (!parsed.ok) {
    return { lines: splitLines(text), locate: () => null };
  }

  const lineOf = new Map<JsonNode, number>();
  const lines: string[] = [];
  emit(text, parsed.root, 0, '', '', lines, lineOf);

  return {
    lines,
    locate: (path) =>
      format === 'avro'
        ? locateAvro(parsed.root, lineOf, path)
        : locateJson(parsed.root, lineOf, path),
  };
}

/** Lines of a text with `\r\n` and `\r` read as one break each, trailing blanks off every line. */
function splitLines(text: string): string[] {
  const lines = text.split(/\r\n|\r|\n/).map((line) => line.trimEnd());

  // A file ends with a newline; that is not an empty last line.
  if (lines.length > 1 && lines[lines.length - 1] === '') {
    lines.pop();
  }

  return lines;
}

/** Writes a value as indented lines, recording the line each node starts on (0-based). */
function emit(
  text: string,
  node: JsonNode,
  level: number,
  key: string,
  comma: string,
  lines: string[],
  lineOf: Map<JsonNode, number>,
): void {
  const pad = '  '.repeat(level);
  lineOf.set(node, lines.length);

  if (node.kind === 'object') {
    if (node.members.length === 0) {
      lines.push(`${pad}${key}{}${comma}`);
      return;
    }

    lines.push(`${pad}${key}{`);
    const members = [...node.members].sort(byKey);

    members.forEach((member, index) => {
      emit(
        text,
        member.value,
        level + 1,
        `${text.slice(member.keySpan.start, member.keySpan.end)}: `,
        index < members.length - 1 ? ',' : '',
        lines,
        lineOf,
      );
    });
    lines.push(`${pad}}${comma}`);
    return;
  }

  if (node.kind === 'array') {
    if (node.items.length === 0) {
      lines.push(`${pad}${key}[]${comma}`);
      return;
    }

    lines.push(`${pad}${key}[`);
    node.items.forEach((item, index) => {
      emit(text, item, level + 1, '', index < node.items.length - 1 ? ',' : '', lines, lineOf);
    });
    lines.push(`${pad}]${comma}`);
    return;
  }

  lines.push(`${pad}${key}${text.slice(node.start, node.end)}${comma}`);
}

/** Members in UTF-16 code unit order, which is how the registry's canonical form orders them. */
function byKey(a: JsonMember, b: JsonMember): number {
  return a.key < b.key ? -1 : a.key > b.key ? 1 : 0;
}

/** The member of an object node with this key. */
function member(node: JsonNode, key: string): JsonNode | undefined {
  return node.kind === 'object' ? node.members.find((m) => m.key === key)?.value : undefined;
}

/** A path as its segments, with the leading `#` taken off and RFC 6901 escapes undone. */
function segmentsOf(path: string): string[] | null {
  const body = path.startsWith('#') ? path.slice(1) : path;

  if (body === '') {
    return [];
  }

  return body.startsWith('/')
    ? body
        .split('/')
        .slice(1)
        .map((raw) => raw.replaceAll('~1', '/').replaceAll('~0', '~'))
    : null;
}

/**
 * The line of a JSON Pointer, for a JSON Schema document.
 *
 * The registry's findings are `#/properties/id` and the like: a fragment pointer into the
 * document. The line is the one the member starts on, so `#/properties/id` is `"id": {`.
 */
function locateJson(root: JsonNode, lineOf: Map<JsonNode, number>, path: string): number | null {
  const segments = segmentsOf(path);

  if (segments === null) {
    return null;
  }

  let node: JsonNode | undefined = root;

  for (const segment of segments) {
    if (node.kind === 'object') {
      node = member(node, segment);
    } else if (node.kind === 'array' && /^(?:0|[1-9]\d*)$/.test(segment)) {
      node = node.items[Number(segment)];
    } else {
      return null;
    }

    if (node === undefined) {
      return null;
    }
  }

  const line = lineOf.get(node);
  return line === undefined ? null : line + 1;
}

/**
 * The line of an Avro finding.
 *
 * <b>Avro paths are names, not indexes</b> (`AvroSchemaResolver`): `#/fields/customer/fields/age`
 * is the field `age` of the record that is the type of the field `customer`. Following one is a
 * walk through the structure Avro puts between them — a field's `type`, which may be a record,
 * or a union of them, or an array whose `items` are one. The line returned is the one that names
 * the thing (`"name": "age"`), because that is the line a reader looks for.
 */
function locateAvro(root: JsonNode, lineOf: Map<JsonNode, number>, path: string): number | null {
  const segments = segmentsOf(path);

  if (segments === null) {
    return null;
  }

  let node: JsonNode | undefined = root;

  for (let i = 0; i < segments.length && node !== undefined;) {
    const segment = segments[i];

    if (segment === 'fields' && i + 1 < segments.length) {
      const name = segments[i + 1];
      node = within(node, (record) => {
        const fields = member(record, 'fields');

        return fields?.kind === 'array'
          ? fields.items.find(
              (f) => member(f, 'name')?.kind === 'string' && stringOf(member(f, 'name')) === name,
            )
          : undefined;
      });
      i += 2;
    } else if (segment === 'items' || segment === 'values') {
      node = within(node, (container) => member(container, segment));
      i += 1;
    } else {
      return null;
    }
  }

  if (node === undefined) {
    return null;
  }

  const named = member(node, 'name');
  const line = lineOf.get(named ?? node);

  return line === undefined ? null : line + 1;
}

function stringOf(node: JsonNode | undefined): string | undefined {
  return node?.kind === 'string' ? node.value : undefined;
}

/**
 * Applies `pick` to a node, and failing that to the node's `type` — an object, or each branch of
 * a union — because Avro nests a record inside the field that holds it.
 */
function within(
  node: JsonNode,
  pick: (object: JsonNode) => JsonNode | undefined,
): JsonNode | undefined {
  if (node.kind === 'array') {
    for (const branch of node.items) {
      const found = within(branch, pick);

      if (found !== undefined) {
        return found;
      }
    }

    return undefined;
  }

  if (node.kind !== 'object') {
    return undefined;
  }

  const direct = pick(node);

  if (direct !== undefined) {
    return direct;
  }

  const type = member(node, 'type');

  return type !== undefined && (type.kind === 'object' || type.kind === 'array')
    ? within(type, pick)
    : undefined;
}
