// Environments and the brokers registered in them.

import type { CompatibilityMode, CompatibilitySurface } from '../registry/compatibility';

/**
 * Who may register schemas into an environment.
 *
 * `OPEN` admits any caller with `subject:write`. `CI_ONLY` additionally requires the `ci`
 * marker scope, which is what separates a build pipeline from a running producer — both
 * otherwise arrive with an API key and write access. `CLOSED` admits nobody: schemas reach the
 * environment only by promotion.
 */
export const REGISTRATION_POLICIES = ['OPEN', 'CI_ONLY', 'CLOSED'] as const;

/** A registration policy, spelled as the API spells it. */
export type RegistrationPolicy = (typeof REGISTRATION_POLICIES)[number];

/** What a policy means, for a person choosing between them. */
export const REGISTRATION_POLICY_MEANING: Readonly<Record<RegistrationPolicy, string>> = {
  OPEN: 'Any caller with subject:write may register.',
  CI_ONLY:
    'Only a credential carrying the ci scope — a build pipeline — may register. A person cannot, however senior.',
  CLOSED: 'Nobody may register. Schemas reach this environment only by promotion.',
};

/** What the last connection check found. */
export const BROKER_STATUSES = ['UNKNOWN', 'REACHABLE', 'UNREACHABLE'] as const;

/** A broker's last-known reachability. */
export type BrokerStatus = (typeof BROKER_STATUSES)[number];

/**
 * A broker registered in an environment.
 *
 * <b>There is no credential here, and there never will be.</b> Credentials are write-only over
 * the API (ADR-012): reads report only whether one exists, and replacing one rotates it in
 * place. Nothing on this type could carry a secret, which is the property that makes it safe to
 * log, to put in a list, and to pass to a component that renders it.
 */
export interface Broker {
  readonly brokerId: string;
  readonly displayName: string;
  readonly uri: string;
  readonly virtualHost: string;
  readonly useTls: boolean;
  readonly hasCredentials: boolean;
  readonly status: BrokerStatus;
  readonly lastCheckedAt: Date | null;
  /** Why the last check failed, in the broker's own words. */
  readonly lastError: string | null;
}

/**
 * The compatibility policy an environment hands to every subject that sets none of its own.
 *
 * <b>Always a whole pair, unlike a subject's {@link CompatibilityPolicy}.</b> A subject can
 * inherit, so its members are null together; an environment cannot. Creating one without a
 * default stores the built-in `BACKWARD · WIRE_JSON` and an update with neither member leaves it
 * alone (ADR-033), so the registry never answers "not set" and this type has no way to say it.
 */
export interface EnvironmentDefaultPolicy {
  readonly mode: CompatibilityMode;
  readonly surface: CompatibilitySurface;
}

/** An environment: a place schemas live, with the rules for registering them. */
export interface Environment {
  readonly name: string;
  readonly description: string | null;
  /** What a subject that sets none of its own inherits. */
  readonly defaultCompatibility: EnvironmentDefaultPolicy;
  readonly registrationPolicy: RegistrationPolicy;
  readonly allowPreReleaseVersions: boolean;
  readonly createdAt: Date;
  readonly brokers: readonly Broker[];
}

/**
 * What can be changed about an environment. A member left out is left alone by the registry, so
 * saving one field never restates the others.
 *
 * <b>The two compatibility members go together or not at all.</b> The registry refuses half a
 * policy (`400`, "needs both a mode and a surface", ADR-016) however much of the other half is
 * already stored, so a change to either is sent as the pair.
 */
export interface EnvironmentChanges {
  readonly description?: string;
  readonly compatibilityMode?: CompatibilityMode;
  readonly compatibilitySurface?: CompatibilitySurface;
  readonly registrationPolicy?: RegistrationPolicy;
  readonly allowPreReleaseVersions?: boolean;
}
