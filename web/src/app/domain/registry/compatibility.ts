// Compatibility as the registry models it: two orthogonal axes (ADR-016).

/** Who must keep working. */
export const COMPATIBILITY_MODES = [
  'NONE',
  'BACKWARD',
  'BACKWARD_TRANSITIVE',
  'FORWARD',
  'FORWARD_TRANSITIVE',
  'FULL',
  'FULL_TRANSITIVE',
] as const;

/**
 * A compatibility mode.
 *
 * These are the tokens `WireTokens.For(CompatibilityMode)` emits, pinned server-side by
 * `WireTokenTests`. Until M6.1 the API derived them from the C# member name, which produced
 * `BACKWARDTRANSITIVE` here and an underscored spelling elsewhere for the same value; this
 * build targets the corrected protocol and does not carry the workaround.
 */
export type CompatibilityMode = (typeof COMPATIBILITY_MODES)[number];

/** How much must keep working. */
export const COMPATIBILITY_SURFACES = ['WIRE', 'WIRE_JSON', 'SOURCE'] as const;

/**
 * A compatibility surface.
 *
 * One spelling everywhere in the API. That was not true before M6.1 — a policy said
 * `WIREJSON` and a breaking change said `WIRE_JSON` for the same value — which is the kind of
 * divergence a second client is very good at finding and a single implementation never
 * notices.
 */
export type CompatibilitySurface = (typeof COMPATIBILITY_SURFACES)[number];

/**
 * A subject's policy, or the inheriting state.
 *
 * Both members are null together when the subject inherits the environment default. That is
 * not the same as "the default": the API distinguishes them, and a UI that collapses them
 * would show an inherited policy as if it had been set deliberately, then silently pin it
 * the first time someone saved the form.
 */
export interface CompatibilityPolicy {
  readonly mode: CompatibilityMode | null;
  readonly surface: CompatibilitySurface | null;
}

/** Whether a policy is inherited rather than set on the subject. */
export function isInherited(policy: CompatibilityPolicy): boolean {
  return policy.mode === null && policy.surface === null;
}

/** Which direction of compatibility a divergence breaks. */
export type CompatibilityDirection = 'BACKWARD' | 'FORWARD';

/**
 * One way a schema diverges from a prior version.
 *
 * `kind` is deliberately a bare `string` rather than a union. The catalogue lives in
 * `BreakingChangeKinds` and grows per format — Avro and Protobuf add their own — so an
 * exhaustive union here would turn every registry release into a UI release. The rule is
 * the opposite of the one for `concordatCode`: the UI branches on `kind` only to choose an
 * icon or a hint, and must render `message` unchanged for a kind it has never seen.
 */
export interface Divergence {
  /** A JSON Pointer into the schema document. */
  readonly path: string;
  /** A stable token, for example `required_field_added`. */
  readonly kind: string;
  readonly direction: CompatibilityDirection;
  readonly surface: CompatibilitySurface;
  /** An actionable explanation, written by the registry. Always safe to show verbatim. */
  readonly message: string;
  /** The prior version this was compared against. */
  readonly conflictsWithVersion: number;
}
