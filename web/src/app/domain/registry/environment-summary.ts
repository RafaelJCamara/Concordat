// The overview of one environment, as the registry computes it (ADR-037).
//
// The dashboard used to derive every one of these numbers in the browser from the subject list.
// That was honest while the list was one small request and wrong the day it was paginated, so the
// registry now answers them; this file is what the app believes about the answer.

import type { EnforcementMode } from '../contracts/contract';
import type { SchemaFormat, SubjectLifecycle, VersionStatus } from './wire-tokens';

/**
 * The two statuses a version can have and still be a "breaking change" the overview lists.
 *
 * A version held at the approval gate, or one a reviewer rejected. Not `DISMISSED` (reverted before
 * anyone judged it) and not `ACTIVE` (including one that was breaking and has been approved: that
 * is live, and the audit trail is where its approval is recorded).
 */
export type HeldStatus = Extract<VersionStatus, 'AWAITING_APPROVAL' | 'REJECTED'>;

/** Subjects by lifecycle. `total` counts retired ones, as the subject list lists them. */
export interface SubjectTotals {
  readonly total: number;
  readonly active: number;
  readonly deprecated: number;
  readonly retired: number;
}

/** Versions by where each sits in the approval gate (ADR-017). */
export interface VersionTotals {
  readonly total: number;
  readonly active: number;
  readonly awaitingApproval: number;
  readonly rejected: number;
  readonly dismissed: number;
}

/** One of the most recently created subjects, with what a card shows. */
export interface NewestSubject {
  readonly name: string;
  readonly format: SchemaFormat;
  readonly owner: string;
  readonly lifecycle: SubjectLifecycle;
  /** What the gated `latest` pointer targets, or null when no version is active. */
  readonly latest: number | null;
  readonly versions: number;
  readonly awaitingApproval: number;
  readonly createdAt: Date;
}

/** A version the approval gate is holding or turned away. */
export interface HeldVersion {
  readonly subject: string;
  readonly ordinal: number;
  readonly status: HeldStatus;
  readonly semanticVersion: string | null;
  readonly registeredAt: Date;
  readonly registeredBy: string;
  /** When it was rejected. Null while it is still awaiting a decision. */
  readonly decidedAt: Date | null;
  /** Who rejected it. Null while it is still awaiting a decision. */
  readonly decidedBy: string | null;
}

/**
 * What contracts declare under one enforcement mode.
 *
 * **Declared, not observed.** The registry never sees message traffic, so a count here says how
 * many contracts and bindings are set to this mode and nothing about whether any service is
 * running a client that honours it.
 */
export interface EnforcementTier {
  readonly mode: EnforcementMode;
  readonly contracts: number;
  readonly publishBindings: number;
  readonly consumeBindings: number;
}

/**
 * Violations SDKs have reported.
 *
 * A violation is one distinct problem (a side, a route, a subject and a code), however many
 * messages hit it. **Zero means none was reported, not that none happened**: only an SDK can see a
 * violation, and only one that reports.
 */
export interface ViolationTotals {
  readonly distinct: number;
  /** How many of them were last seen inside the window. */
  readonly recent: number;
  readonly windowHours: number;
}

/** The overview of one environment. */
export interface EnvironmentSummary {
  readonly subjects: SubjectTotals;
  readonly versions: VersionTotals;
  /** The most recently created subjects, newest first. */
  readonly newestSubjects: readonly NewestSubject[];
  /** The most recently proposed versions held at the gate or rejected, newest first. */
  readonly breakingChanges: readonly HeldVersion[];
  /** One entry per mode, in the order `OFF`, `MONITOR`, `ENFORCE`. */
  readonly enforcement: readonly EnforcementTier[];
  readonly violations: ViolationTotals;
}

/** How many contracts the environment has, whatever their mode. */
export function contractCount(summary: EnvironmentSummary): number {
  return summary.enforcement.reduce((total, tier) => total + tier.contracts, 0);
}

/** How many bindings (publish and consume) the environment's contracts carry. */
export function bindingCount(summary: EnvironmentSummary): number {
  return summary.enforcement.reduce(
    (total, tier) => total + tier.publishBindings + tier.consumeBindings,
    0,
  );
}
