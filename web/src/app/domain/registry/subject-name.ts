import { trimRegistryText } from './registry-text';

/** `SubjectName.MaxLength`, in UTF-16 code units, counted after the registry has trimmed. */
export const SUBJECT_NAME_MAX_LENGTH = 512;

/**
 * The grammar of a subject name (ADR-011): dot-separated segments of ASCII letters, digits and
 * underscores, with no leading, trailing or repeated dot.
 *
 * The ranges are spelled out rather than written `\w`, because they are the registry's own and
 * JavaScript's `\w` happens to agree only while nobody adds the `u` and `i` flags together.
 * JavaScript's `$` without the `m` flag matches only at the very end, which is what the
 * registry's `\z` means; .NET's `$` would also match before a trailing newline, and that is
 * the reason the registry does not use it.
 */
const NAME_PATTERN = /^[A-Za-z0-9_]+(?:\.[A-Za-z0-9_]+)*$/;

/** The code the registry answers with for any of the refusals below. */
export const SUBJECT_NAME_INVALID = 'subject_name_invalid';

/** What the registry would make of a name. */
export type SubjectNameCheck =
  | {
      readonly ok: true;
      /** The name as the registry will store it: trimmed. */
      readonly name: string;
    }
  | {
      readonly ok: false;
      readonly code: typeof SUBJECT_NAME_INVALID;
      readonly message: string;
    };

/**
 * Whether the registry would accept this as a subject name, and why not when it would not.
 *
 * <b>A port of `SubjectName.Create`, and every verdict here is one the registry certainly
 * reaches</b> (ADR-028's rule: a check that can block a submit is never stricter than the
 * registry, and one that is looser only costs a round trip). The order is the registry's — trim,
 * then empty, then length, then grammar — and so are the verdicts, which is not something to
 * take on trust: `e2e/subject-name-parity.spec.ts` sends every row of its table to the real
 * create endpoint and compares. It found nothing to correct in the grammar and one thing in the
 * trim, which is why `registry-text.ts` exists.
 *
 * The *messages* are this client's own. The registry says only that a name is not valid and what
 * the grammar is; saying which character is the problem is the part somebody editing a name
 * actually needs, and it changes no verdict.
 */
export function checkSubjectName(text: string): SubjectNameCheck {
  const name = trimRegistryText(text);

  if (name === '') {
    return refuse('A subject name is required.');
  }

  if (name.length > SUBJECT_NAME_MAX_LENGTH) {
    return refuse(
      `A subject name may be at most ${SUBJECT_NAME_MAX_LENGTH} characters; this one is ${name.length}.`,
    );
  }

  if (!NAME_PATTERN.test(name)) {
    return refuse(explainInvalidName(name));
  }

  return { ok: true, name };
}

function refuse(message: string): SubjectNameCheck {
  return { ok: false, code: SUBJECT_NAME_INVALID, message };
}

const GRAMMAR =
  'Use dot-separated segments of letters, digits and underscores, such as acme.orders.OrderCreated.';

/**
 * Names the first thing wrong with a name the grammar rejects.
 *
 * Characters are walked by code point and only ever *described*, never echoed raw when they are
 * not printable or not a character at all: a lone surrogate quoted into this message would be
 * the same defect the registry used to have, one layer up, and a control character quoted into
 * a sentence is invisible. The offender is named by its code point as well.
 */
function explainInvalidName(name: string): string {
  for (let i = 0; i < name.length;) {
    const point = name.codePointAt(i) as number;
    const length = point > 0xffff ? 2 : 1;
    const char = name.slice(i, i + length);

    if (!/^[A-Za-z0-9_.]$/.test(char)) {
      return `${describeCharacter(point, char)} is not allowed in a subject name. ${GRAMMAR}`;
    }

    i += length;
  }

  // Every character is allowed, so the fault is in how the dots sit.
  if (name.startsWith('.')) {
    return `A subject name cannot start with a dot. ${GRAMMAR}`;
  }

  if (name.endsWith('.')) {
    return `A subject name cannot end with a dot. ${GRAMMAR}`;
  }

  return `A subject name cannot have two dots in a row. ${GRAMMAR}`;
}

function describeCharacter(point: number, char: string): string {
  const hex = `U+${point.toString(16).toUpperCase().padStart(4, '0')}`;
  const isSurrogate = point >= 0xd800 && point <= 0xdfff;
  const isControlOrBlank = point < 0x20 || (point >= 0x7f && point <= 0xa0) || /^\s$/u.test(char);

  if (isSurrogate) {
    return `The character ${hex}`;
  }

  if (char === ' ') {
    return 'A space';
  }

  return isControlOrBlank ? `The character ${hex}` : `'${char}' (${hex})`;
}

/**
 * What may be separators in a name's source text, and the rewrite the SDKs already apply.
 *
 * `+` (the nested-type separator in .NET) and `:` (the scope separator several brokers use) are
 * `SubjectNormalizer`'s rewrites to `.`, pinned for every SDK by the conformance corpus
 * (`subject-resolution/`). Applying them here means a pasted `Acme.Orders+Created` lands on the
 * name a .NET publisher will compute, instead of on a second subject that differs by one character
 * and is found out when nothing validates. Its other two rules are deliberately **not** applied:
 * cutting at the first comma is right for an assembly-qualified type name and wrong for a title
 * with a comma in it, and generics need a bracket grammar this screen has no business owning —
 * names the SDK derives are the SDK's to derive.
 */
const SDK_SEPARATORS = /[+:]/g;

/**
 * Marks a run of characters that were replaced, until it is known whether it sits between two
 * words (and becomes `_`) or at the edge of a segment (and is dropped). Distinct from `_` so an
 * underscore the author typed at the edge of a segment is never mistaken for one this made. It
 * cannot collide with the text: U+0001 is itself outside the grammar, so any that was typed has
 * been replaced by the time the marker is used.
 */
const BREAK = String.fromCharCode(1);
const BREAK_AT_EDGE = new RegExp(`^${BREAK}+|${BREAK}+$`, 'g');

/**
 * Suggests a subject name for some words.
 *
 * <b>It is a suggestion and a typing aid, and the registry's grammar is the only judge of it.</b>
 * The screen follows the title until somebody edits the name by hand (the prototype's "touched"
 * flag), and what it follows is this. The rule is the least that makes text acceptable, derived
 * from what the registry refuses rather than from a house style:
 *
 * 1. Trim as the registry does, and if what is left is already a valid name, return it
 *    unchanged — so typing the real name into the title just works, and the function is
 *    idempotent.
 * 2. Apply `SubjectNormalizer`'s two single-character rewrites (see {@link SDK_SEPARATORS}).
 * 3. Fold Latin accents (`é` becomes `e`): the grammar is ASCII, and `Café` should not become
 *    `Caf_`. A letter with no ASCII decomposition (`ß`, `中`) is not folded, and is treated as any
 *    other character below.
 * 4. Every run of characters outside letters, digits, underscores and dots becomes one `_`
 *    between two words, and is dropped at the edge of a segment.
 * 5. Empty segments are dropped, so no dot leads, trails or doubles.
 *
 * <b>Case is never changed and words are never joined into a style.</b> `Order Created` becomes
 * `Order_Created`, not `OrderCreated` or `order_created`: choosing a casing is a convention the
 * registry does not have, and a name is case-sensitive (`orders.Created` and `orders.created`
 * are two subjects), so guessing one would be guessing which subject somebody meant.
 *
 * <b>Length is not its business.</b> A result over {@link SUBJECT_NAME_MAX_LENGTH} is returned
 * as it is and {@link checkSubjectName} says so, because a silently shortened name is a
 * different subject from the one typed.
 *
 * Returns an empty string when nothing in the text can be part of a name.
 */
export function slugSubjectName(text: string): string {
  const trimmed = trimRegistryText(text);

  if (NAME_PATTERN.test(trimmed)) {
    return trimmed;
  }

  const folded = trimmed
    .replace(SDK_SEPARATORS, '.')
    .normalize('NFKD')
    .replace(/\p{M}+/gu, '')
    .replace(/[^A-Za-z0-9_.]+/g, BREAK);

  return folded
    .split('.')
    .map((segment) => segment.replace(BREAK_AT_EDGE, '').replaceAll(BREAK, '_'))
    .filter((segment) => segment !== '')
    .join('.');
}
