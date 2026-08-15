// The strict boundary guards, shared by every DTO mapping in this feature.
//
// Extracted from `subject-dtos.ts` when schemas gained their own mapping. Two copies of a
// guard whose whole value is that it never gets more lenient is exactly the thing that
// drifts: the second copy grows a fallback because someone hit a token in a hurry, and the
// strictness the first one documents quietly stops being true of the app.

import { ConcordatError } from '../../../core/http/problem-details';

/**
 * Checks a wire token against the set this build knows.
 *
 * Throws rather than coercing. The tempting alternative — fall back to the first member —
 * would render a version awaiting approval as `ACTIVE` the day the registry gains a fourth
 * status, and a UI that quietly mislabels the approval gate is worse than one that refuses
 * to draw the row. The error names the version mismatch so the reader knows to upgrade
 * rather than to go looking for a bug.
 *
 * This is not theoretical: `DISMISSED` shipped with M7, this list did not learn it, and one
 * dismissed version failed the entire subject list on the first browser run. The fix was to
 * teach the list, not to loosen the guard — `WireTokenTests` now compares the vocabularies
 * against the .NET enums so the next addition fails in CI instead of in a browser.
 *
 * @param field The field being read, named as the server spells it, for the error message.
 * @param value What arrived on the wire.
 * @param permitted The tokens this build understands.
 */
export function wireToken(field: string, value: string, permitted: readonly string[]): string {
  if (permitted.includes(value)) {
    return value;
  }

  throw new ConcordatError({
    status: 0,
    code: 'registry_refused',
    detail:
      `The registry sent '${value}' for '${field}', which this build does not recognise. ` +
      'It is probably newer than this web app; upgrade the web app to match.',
  });
}

/**
 * Parses an ISO 8601 timestamp.
 *
 * Also strict, and for the same reason: `new Date('nonsense')` is an `Invalid Date` rather
 * than a throw, and it propagates silently — sorting by it puts rows in arbitrary order and
 * formatting it prints "Invalid Date" into the interface.
 */
export function wireTimestamp(value: string): Date {
  const parsed = new Date(value);

  if (Number.isNaN(parsed.getTime())) {
    throw new ConcordatError({
      status: 0,
      code: 'registry_refused',
      detail: `The registry sent '${value}' as a timestamp, which is not a date.`,
    });
  }

  return parsed;
}
