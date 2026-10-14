// The audit trail, as the registry reports it.

/**
 * Every action the audit trail can record, as the API spells it.
 *
 * <b>This list drives the filter and nothing else.</b> A row's `action` is deliberately a bare
 * `string` rather than a member of this union, so a newer registry's new kind of event renders
 * as itself instead of failing the whole page. The other wire vocabularies are strict because a
 * wrong guess mislabels a state; here the action is only a label on a record, and a log that
 * cannot display the newest kind of entry hides exactly the thing somebody opened it to find.
 *
 * Kept in step with `AuditTokens` by `WireTokenTests.AuditActionsMatchWhatTheFrontendPublishes`.
 */
export const AUDIT_ACTIONS = [
  'SUBJECT_CREATED',
  'SUBJECT_UPDATED',
  'SUBJECT_POLICY_CHANGED',
  'SUBJECT_RETIRED',
  'VERSION_REGISTERED',
  'VERSION_SUBMITTED',
  'VERSION_APPROVED',
  'VERSION_REJECTED',
  'VERSION_DISMISSED',
  'VERSION_PROMOTED',
  'ENVIRONMENT_CREATED',
  'ENVIRONMENT_UPDATED',
  'BROKER_ADDED',
  'BROKER_REMOVED',
  'BROKER_CREDENTIAL_SET',
  'BROKER_CREDENTIAL_REMOVED',
  'CONTRACT_CREATED',
  'CONTRACT_BINDING_ADDED',
  'CONTRACT_BINDING_CHANGED',
  'CONTRACT_BINDING_REMOVED',
  'CONTRACT_ENFORCEMENT_CHANGED',
  'SERVICE_REGISTERED',
  'MEMBER_ADDED',
  'MEMBER_ROLE_CHANGED',
  'API_KEY_ISSUED',
  'API_KEY_REVOKED',
] as const;

/** An audit action the filter offers. */
export type AuditAction = (typeof AUDIT_ACTIONS)[number];

/**
 * One line of the trail: who did what to which thing, and when.
 *
 * <b>It records what happened, not what was attempted.</b> A refused request never reaches a
 * commit, so it leaves no entry (decisions-pending #24): a 403 will not be here, and an empty
 * result for "who tried to approve this" is not evidence that nobody did.
 */
export interface AuditEntry {
  readonly at: Date;
  /** A stable token. Rendered as received; see {@link AUDIT_ACTIONS}. */
  readonly action: string;
  readonly actor: string;
  /** A subject, contract or broker name. */
  readonly target: string;
  readonly detail: string | null;
}

/** What to ask the trail for. */
export interface AuditQuery {
  /** Whether to look in the active environment or across every environment in the tenant. */
  readonly scope: 'environment' | 'all';
  readonly action: AuditAction | null;
  /** An exact subject, contract or broker name; empty for no filter. */
  readonly target: string;
  readonly since: Date | null;
  readonly until: Date | null;
  readonly limit: number;
}

/** The most entries the registry will return in one answer. */
export const AUDIT_MAX_LIMIT = 500;

/** How many it returns when not told. */
export const AUDIT_DEFAULT_LIMIT = 100;
