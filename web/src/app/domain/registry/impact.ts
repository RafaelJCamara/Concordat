import type { ImpactCertainty } from './wire-tokens';

/**
 * What a change does to one registered consumer.
 *
 * <b>`breaks` is only meaningful when `certainty` is `CHECKED`.</b> A consumer that follows
 * `latest` reports `breaks: false`, which reads as "safe" and means nothing of the kind: the
 * registry does not know what that consumer's code was built against, only what it fetches.
 * Every place that shows a verdict has to read the two together, which is why they travel
 * as one type and the screen never branches on `breaks` alone.
 */
export interface ConsumerImpact {
  readonly service: string;
  /** What the consumer declared: `latest`, an ordinal, or `>=N`. */
  readonly selector: string;
  /** The version the verdict was computed against — the pinned ordinal, or a range's floor. */
  readonly readerOrdinal: number | null;
  readonly breaks: boolean;
  readonly certainty: ImpactCertainty;
  readonly lastSeenAt: Date;
  /** Nothing has been heard from it in a long time. A hint, never a reason to drop it. */
  readonly stale: boolean;
  /** The divergences that break it, in the engine's own words. Safe to show verbatim. */
  readonly reasons: readonly string[];
}

/** Who breaks if a version of a subject is the one consumers meet. */
export interface ImpactReport {
  readonly subject: string;
  /** The version analysed, or null for an unregistered candidate. */
  readonly candidateOrdinal: number | null;
  readonly candidateSchemaId: string | null;
  readonly breakingCount: number;
  /** Every registered consumer, breaking ones first. */
  readonly consumers: readonly ConsumerImpact[];
  /** The contracts whose bindings carry this subject. */
  readonly contracts: readonly string[];
}
