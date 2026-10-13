/**
 * The characters `System.String.Trim()` strips and the nearest ones it does not, as measured
 * against the real registry — the table `domain/registry/registry-text.ts` implements.
 *
 * Shared by the two parity specs that hold it against an endpoint, `subject-name-parity.spec.ts`
 * (a subject name) and `subject-settings-parity.spec.ts` (an owner): both are trimmed by the
 * registry before they are judged, with the same set, and a character added to one table and not
 * the other is exactly the drift a parity test exists to catch. Kept as code points because a
 * literal in this file would be invisible.
 */

/** Characters .NET's `Trim()` strips, and which the client has to strip too. */
export const STRIPPED: readonly number[] = [
  0x09, 0x0a, 0x0b, 0x0c, 0x0d, 0x20, 0x85, 0xa0, 0x1680, 0x2000, 0x2001, 0x2002, 0x2003, 0x2004,
  0x2005, 0x2006, 0x2007, 0x2008, 0x2009, 0x200a, 0x2028, 0x2029, 0x202f, 0x205f, 0x3000,
];

/** And the ones that look as if they should be and are not: kept when they are on the end. */
export const NOT_STRIPPED: readonly number[] = [
  0xfeff, 0x180e, 0x200b, 0x200c, 0x200d, 0x2060, 0x001c, 0x001d, 0x001e, 0x001f, 0x0001, 0x007f,
  0x0080, 0x00ad,
];
