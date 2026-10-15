// The approval queue's wire shape, and the mapping onto the domain.
//
// Mirrors `Concordat.Api/ApprovalQueueEndpoints.cs` field for field, and separate from
// `domain/registry/approval-queue.ts` for the reason `subject-dtos.ts` is: a DTO is what the
// server sent, a domain type is what the app believes.

import { wireTimestamp } from '../../../core/http/wire-token';
import type { ApprovalQueue, PendingApproval } from '../../../domain/registry/approval-queue';

/** `PendingApprovalResponse`. */
export interface PendingApprovalDto {
  readonly subject: string;
  readonly ordinal: number;
  readonly semanticVersion: string | null;
  readonly changelog: string | null;
  /** ISO 8601, UTC. */
  readonly registeredAt: string;
  readonly registeredBy: string;
  readonly baseline: number | null;
  readonly reviewers: readonly string[];
  readonly mayDecide: boolean;
}

/** `PendingApprovalsResponse`. */
export interface ApprovalQueueDto {
  readonly total: number;
  readonly items: readonly PendingApprovalDto[];
}

export function toPendingApproval(dto: PendingApprovalDto): PendingApproval {
  return {
    subject: dto.subject,
    ordinal: dto.ordinal,
    semanticVersion: dto.semanticVersion,
    changelog: dto.changelog,
    registeredAt: wireTimestamp(dto.registeredAt),
    registeredBy: dto.registeredBy,
    baseline: dto.baseline,
    reviewers: dto.reviewers,
    mayDecide: dto.mayDecide,
  };
}

export function toApprovalQueue(dto: ApprovalQueueDto): ApprovalQueue {
  return { total: dto.total, items: dto.items.map(toPendingApproval) };
}
