import type { JsonPosition } from './json-document';

/**
 * How much weight a finding carries, which decides whether it can stop a submit.
 *
 * <b>Two grades, and the line between them is the registry's.</b>
 *
 * - `refusal`: the registry will certainly refuse this text, for this reason. These may block
 *   the Register button, because a block that is wrong in the strict direction would stop
 *   someone registering a document the registry takes.
 * - `advice`: this client thinks something is off — a keyword that is not valid under the
 *   2020-12 metaschema, a sample message that does not conform. Never blocks anything: the
 *   registry is the authority, `ajv` and the registry's validator are different engines, and
 *   advice that could veto a submit would eventually be wrong in exactly that direction.
 */
export type FindingSeverity = 'refusal' | 'advice';

/** One thing worth telling the author about the document they are editing. */
export interface EditorFinding {
  readonly severity: FindingSeverity;
  /**
   * A stable token. The registry's own for a refusal (`schema_malformed`), and this
   * client's for advice — never shown as though the registry had said it.
   */
  readonly code: string;
  readonly message: string;
  /** Where to point, or null when the finding is about the document as a whole. */
  readonly position: JsonPosition | null;
  /** How many characters to underline from `position`. */
  readonly length: number;
}
