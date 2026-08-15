import type { Divergence } from './compatibility';
import type { VersionStatus } from './wire-tokens';

/**
 * One way a schema may not behave identically in every SDK (M6.1).
 *
 * Always a warning here: a portability finding severe enough to be an error refuses the
 * registration outright and arrives as a Problem Details body instead. So anything in this
 * list registered successfully, and is advice rather than a failure.
 *
 * `kind` is a bare `string` for the same reason `Divergence.kind` is — the catalogue lives in
 * `PortabilityKinds` server-side and grows per format, and an exhaustive union here would
 * turn every registry release into a UI release.
 */
export interface PortabilityFinding {
  /** A JSON Pointer into the schema document. */
  readonly path: string;
  /** A stable token from `PortabilityKinds`. */
  readonly kind: string;
  /** What will differ between SDKs, and what it costs. Safe to show verbatim. */
  readonly message: string;
}

/**
 * What the registry did with a submitted schema.
 *
 * <b>Three outcomes, and conflating any two of them is a real defect.</b>
 *
 * - *Registered.* A new ordinal was allocated and `latest` moved.
 * - *Held.* The change is breaking, so it registered with `AWAITING_APPROVAL` and `latest`
 *   did **not** move (ADR-017). This is a success, not a failure: CI never wedges, and the
 *   proposal stays reviewable. A UI that reported it as an error would have people
 *   re-submitting a change the registry already accepted.
 * - *Unchanged.* The submitted document is byte-identical to the tip after canonicalisation,
 *   so no ordinal was allocated. The API says so with 200 rather than 201, and `created` is
 *   what carries it. A client that treats the two statuses alike double-counts versions.
 */
export interface RegistrationOutcome {
  readonly subject: string;
  readonly ordinal: number;
  readonly schemaId: string;
  readonly status: VersionStatus;
  /** Whether an ordinal was allocated. False when re-registering the current tip. */
  readonly created: boolean;
  /** Every difference from the prior version, whether or not the policy tolerates it. */
  readonly divergences: readonly Divergence[];
  readonly portability: readonly PortabilityFinding[];
}

/** Whether the registration is waiting on a human before it becomes current. */
export function isHeldForApproval(outcome: RegistrationOutcome): boolean {
  return outcome.status === 'AWAITING_APPROVAL';
}
