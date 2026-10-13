// The audit endpoint's wire shape, and the mapping onto the domain.
//
// Mirrors `AuditResponse` in `Concordat.Api/GovernanceEndpoints.cs` field for field.

import type { AuditEntry } from '../../../domain/governance/audit';
import { wireTimestamp } from '../../../core/http/wire-token';

/** `AuditResponse`. */
export interface AuditEntryDto {
  /** ISO 8601, UTC. */
  readonly at: string;
  readonly action: string;
  readonly actor: string;
  readonly target: string;
  readonly detail: string | null;
}

export function toAuditEntry(dto: AuditEntryDto): AuditEntry {
  return {
    // Strict about the timestamp and lenient about the action, and for the same reason in
    // opposite directions. A date that does not parse would sort and render as "Invalid Date" in
    // the one list whose order is the entire point; an action this build has not heard of is
    // still a perfectly good label (see `AUDIT_ACTIONS`).
    at: wireTimestamp(dto.at),
    action: dto.action,
    actor: dto.actor,
    target: dto.target,
    detail: dto.detail,
  };
}
