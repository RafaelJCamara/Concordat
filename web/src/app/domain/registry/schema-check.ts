import type { EditorFinding } from './editor-finding';

/**
 * What is asked of the client-side checker.
 *
 * Plain data, because it crosses a `postMessage` boundary: it is structured-cloned into a
 * worker and the answer is cloned back, so nothing here may be a class instance, a function or
 * a signal.
 */
export interface SchemaCheckRequest {
  /** The schema text, as it would be sent. */
  readonly schema: string;
  /** A sample message to check against the schema, or null to check the schema alone. */
  readonly payload: string | null;
}

/** One way a sample message does not conform. */
export interface PayloadError {
  /** A JSON Pointer into the message; empty for the message as a whole. */
  readonly path: string;
  readonly message: string;
}

/**
 * What came of checking a sample message.
 *
 * Four outcomes, because "no errors" is not one answer: the message conforms; it does not; it
 * is not JSON at all; or it could not be judged — a reference to something only the registry
 * can resolve, a schema that does not compile — and saying "valid" for that would be a lie.
 */
export type PayloadCheck =
  | { readonly kind: 'valid' }
  | {
      readonly kind: 'invalid';
      readonly errors: readonly PayloadError[];
      /** How many more there were beyond the ones listed. */
      readonly omitted: number;
    }
  | { readonly kind: 'not_json'; readonly message: string }
  | { readonly kind: 'unavailable'; readonly reason: string };

/**
 * What the checker found.
 *
 * <b>Advice, never a verdict.</b> Everything here comes from `ajv`, which is a different
 * engine from the registry's: it can disagree about `format`, about an unusual keyword, about
 * a corner of the metaschema. The registry registers `{"type":"bogus"}` without complaint, so
 * a client that treated the metaschema as a gate would be stricter than the thing it fronts.
 * These findings are therefore `advice` and nothing in the page may block on them.
 */
export interface SchemaCheckResult {
  /** The text that was checked, so the page can tell when the result has gone stale. */
  readonly checked: string;
  /**
   * Why the schema itself was not checked, or null when it was.
   *
   * Kept apart from an empty `advice` list, because "nothing to flag" and "could not look" are
   * different statements and an empty list reads as the first whichever it is.
   */
  readonly skipped: string | null;
  /** Problems with the schema itself, as advice; each knows a JSON Pointer to point at. */
  readonly advice: readonly AdvisoryFinding[];
  /** The sample message's outcome, or null when none was supplied. */
  readonly payload: PayloadCheck | null;
}

/** A piece of advice about the schema, located by JSON Pointer rather than by text offset. */
export interface AdvisoryFinding extends Pick<EditorFinding, 'code' | 'message'> {
  /** Where in the schema, as a JSON Pointer; empty for the document as a whole. */
  readonly pointer: string;
}
