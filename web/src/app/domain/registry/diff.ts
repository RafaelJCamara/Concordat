import type { CompatibilityPolicy, Divergence } from './compatibility';

/**
 * Two versions of a subject, compared.
 *
 * <b>`divergences` is every difference the engine found, not only the ones the policy
 * forbids.</b> A diff answers "what changed", and filtering it by the policy would hide
 * changes that matter to a consumer generating code from the schema. Whether a given
 * divergence is *tolerated* is a separate question this type does not answer — the same
 * list is what registration turns into a verdict.
 *
 * <b>`identical` and an empty `divergences` are different facts.</b> Under an open content
 * model, adding or removing a property cannot affect compatibility, so two schemas with
 * different ids can diff to an empty list (decisions-pending #12, left as is for v1). A
 * screen that read an empty list as "nothing changed" would say so about two documents the
 * registry knows differ; `identical` is what says the documents are the same one.
 */
export interface VersionDiff {
  readonly from: number;
  readonly to: number;
  readonly fromSchemaId: string;
  readonly toSchemaId: string;
  /** Whether both versions point at the same schema document. */
  readonly identical: boolean;
  /** The subject's effective policy, which is what the verdict would be read against. */
  readonly policy: CompatibilityPolicy;
  readonly divergences: readonly Divergence[];
}
