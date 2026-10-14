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

/** The directions a divergence can break. */
export const COMPATIBILITY_DIRECTIONS = ['BACKWARD', 'FORWARD'] as const;

/** Which direction of compatibility a divergence breaks. */
export type CompatibilityDirection = (typeof COMPATIBILITY_DIRECTIONS)[number];

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

/**
 * What each mode asks, in one sentence, for somebody choosing or reading one.
 *
 * <b>One source of truth for the wording.</b> Every screen that shows a mode, or lets someone choose
 * one, reads these; `compatibility.spec.ts` pins that this has exactly the tokens in
 * {@link COMPATIBILITY_MODES} so a mode added there cannot appear without words.
 *
 * Each sentence restates the engine, not the prototype's memory of it:
 *
 * - The direction comes from the XML docs on `CompatibilityMode` in
 *   `Concordat.Domain/Registry/Compatibility.cs` and `JsonSchemaDiff`'s remarks: backward is the
 *   new schema reading data written under the old one; forward is the old reading the new.
 * - "Only the most recent earlier version" against "every earlier version" is
 *   `JsonSchemaCompatibilityChecker.SelectPriors`, pinned by `NonTransitive_ComparesOnlyAgainst
 *   TheHighestOrdinal` and `Transitive_ComparesAgainstEveryPriorVersion` in
 *   `Concordat.Formats.Json.Tests/CompatibilityTests.cs`. The transitive form is the one that
 *   catches a chain of individually compatible changes.
 * - `FULL` is both directions, pinned by `Full_ReportsBothDirections`; `NONE` skips checking
 *   entirely, pinned by `ModeNone_SkipsCheckingEntirely`.
 */
export const COMPATIBILITY_MODE_MEANING: Readonly<Record<CompatibilityMode, string>> = {
  NONE: 'No compatibility checking: no change is reported as breaking.',
  BACKWARD:
    'The new version must read data written under the previous one. Compared against the most recent earlier version only.',
  BACKWARD_TRANSITIVE:
    'As BACKWARD, checked against every earlier version instead of the most recent one, so a chain of changes that are each compatible cannot add up to one that is not.',
  FORWARD:
    'The most recent earlier version must read data written under the new one. Compared against that version only.',
  FORWARD_TRANSITIVE:
    'As FORWARD, checked against every earlier version instead of the most recent one, so a chain of changes that are each compatible cannot add up to one that is not.',
  FULL: 'Both BACKWARD and FORWARD: the new version and the most recent earlier version must each read what the other writes.',
  FULL_TRANSITIVE: 'As FULL, checked against every earlier version instead of the most recent one.',
};

/** What each surface counts as a break, in one sentence. See {@link COMPATIBILITY_MODE_MEANING}. */
export const COMPATIBILITY_SURFACE_MEANING: Readonly<Record<CompatibilitySurface, string>> = {
  WIRE: 'Blocks only changes that stop existing bytes decoding. JSON Schema has no such change, so for a JSON subject this blocks nothing; differences are still listed.',
  WIRE_JSON:
    'Blocks what WIRE blocks, and changes to which JSON documents are valid. The default for a new environment.',
  SOURCE:
    'The strictest. Blocks what WIRE_JSON blocks, and changes that only alter generated code, such as an integer widened to a number or a changed format.',
};

/** What each direction of a finding means. See {@link COMPATIBILITY_MODE_MEANING}. */
export const COMPATIBILITY_DIRECTION_MEANING: Readonly<Record<CompatibilityDirection, string>> = {
  BACKWARD: 'Breaks the new version’s ability to read data written under an earlier one.',
  FORWARD: 'Breaks an earlier version’s ability to read data written under the new one.',
};

/**
 * How to read the surface on a finding, as opposed to on a policy.
 *
 * A policy's surface is how much must keep working; a finding's is the narrowest surface it
 * breaks, and `CompatibilityPolicy.IsViolatedBy` is `finding <= policy surface`: a `WIRE`
 * finding violates every policy, a `SOURCE` one only a `SOURCE` policy. Without this sentence the
 * same token means two things on one screen.
 */
export const COMPATIBILITY_FINDING_SURFACE_NOTE =
  'Each finding carries the narrowest surface it breaks. A policy at that surface, or a wider one, blocks it.';
