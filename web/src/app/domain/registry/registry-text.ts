/**
 * Which characters the registry strips from the ends of a value before it judges it.
 *
 * <b>This is `System.String.Trim()`, and it is not `String.prototype.trim()`.</b> A subject name
 * and an owner are both trimmed by the registry *before* validation (`SubjectName.Create`,
 * `ActorId.Create`), so `" acme.orders "` is a valid name that is stored as `acme.orders`. A
 * client that trims with the JavaScript function gets the set wrong in both directions, and each
 * was found by sending the character to the real create endpoint, not by reading either
 * specification:
 *
 * - it strips U+FEFF (the byte-order mark), which the registry does not — so a name with U+FEFF in front of it
 *   would be called valid here and refused there (*looser*);
 * - it does not strip U+0085 (next line), which the registry does — so a name padded with it
 *   would be refused here and accepted there (*stricter*, the worse direction: it blocks a
 *   submit the registry would have taken).
 *
 * Neither the registry nor this strips U+180E, U+200B or the information separators
 * U+001C to U+001F; they are the characters nearest the line, so the parity spec sends them too.
 * `subject-name.spec.ts` pins the table and `e2e/subject-name-parity.spec.ts` holds it against
 * the registry, one padded name per row.
 */
const WHITESPACE_RANGES: readonly (readonly [first: number, last: number])[] = [
  // Tab, line feed, vertical tab, form feed, carriage return.
  [0x0009, 0x000d],
  // Space.
  [0x0020, 0x0020],
  // Next line (a C1 control, which .NET counts as white space).
  [0x0085, 0x0085],
  // No-break space.
  [0x00a0, 0x00a0],
  // Ogham space mark.
  [0x1680, 0x1680],
  // En quad to hair space.
  [0x2000, 0x200a],
  // Line separator and paragraph separator.
  [0x2028, 0x2029],
  // Narrow no-break space.
  [0x202f, 0x202f],
  // Medium mathematical space.
  [0x205f, 0x205f],
  // Ideographic space.
  [0x3000, 0x3000],
];

/** Whether the registry strips this UTF-16 code unit from the ends of a value. */
export function isRegistryWhitespace(codeUnit: number): boolean {
  return WHITESPACE_RANGES.some(([first, last]) => codeUnit >= first && codeUnit <= last);
}

/**
 * Trims the ends of a value exactly as the registry does before validating it.
 *
 * Only the ends: whitespace inside a value is part of it, and an invalid part.
 */
export function trimRegistryText(text: string): string {
  let start = 0;
  let end = text.length;

  while (start < end && isRegistryWhitespace(text.charCodeAt(start))) {
    start++;
  }

  while (end > start && isRegistryWhitespace(text.charCodeAt(end - 1))) {
    end--;
  }

  return text.slice(start, end);
}
