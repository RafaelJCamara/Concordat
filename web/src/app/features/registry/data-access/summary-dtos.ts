// The overview's wire shape, and the mapping onto the domain.
//
// Mirrors `Concordat.Api/SummaryEndpoints.cs` field for field, and separate from
// `domain/registry/environment-summary.ts` for the reason `subject-dtos.ts` is: a DTO is what the
// server sent, a domain type is what the app believes.

import { wireTimestamp, wireToken } from '../../../core/http/wire-token';
import { ENFORCEMENT_MODES, type EnforcementMode } from '../../../domain/contracts/contract';
import type {
  EnforcementTier,
  EnvironmentSummary,
  HeldStatus,
  HeldVersion,
  NewestSubject,
} from '../../../domain/registry/environment-summary';
import {
  SCHEMA_FORMATS,
  SUBJECT_LIFECYCLES,
  type SchemaFormat,
  type SubjectLifecycle,
} from '../../../domain/registry/wire-tokens';

/** `NewestSubjectResponse`. */
export interface NewestSubjectDto {
  readonly name: string;
  readonly format: string;
  readonly owner: string;
  readonly lifecycle: string;
  readonly latest: number | null;
  readonly versions: number;
  readonly awaitingApproval: number;
  /** ISO 8601, UTC. */
  readonly createdAt: string;
}

/** `HeldVersionResponse`. */
export interface HeldVersionDto {
  readonly subject: string;
  readonly ordinal: number;
  readonly status: string;
  readonly semanticVersion: string | null;
  readonly registeredAt: string;
  readonly registeredBy: string;
  readonly decidedAt: string | null;
  readonly decidedBy: string | null;
}

/** `EnforcementTierResponse`. */
export interface EnforcementTierDto {
  readonly mode: string;
  readonly contracts: number;
  readonly publishBindings: number;
  readonly consumeBindings: number;
}

/** `EnvironmentSummaryResponse`. */
export interface EnvironmentSummaryDto {
  readonly subjects: {
    readonly total: number;
    readonly active: number;
    readonly deprecated: number;
    readonly retired: number;
  };
  readonly versions: {
    readonly total: number;
    readonly active: number;
    readonly awaitingApproval: number;
    readonly rejected: number;
    readonly dismissed: number;
  };
  readonly newestSubjects: readonly NewestSubjectDto[];
  readonly breakingChanges: readonly HeldVersionDto[];
  readonly enforcement: readonly EnforcementTierDto[];
  readonly violations: {
    readonly distinct: number;
    readonly recent: number;
    readonly windowHours: number;
  };
}

/** The only two statuses the registry puts in the list. Anything else is a build that is behind. */
const HELD_STATUSES: readonly HeldStatus[] = ['AWAITING_APPROVAL', 'REJECTED'];

export function toNewestSubject(dto: NewestSubjectDto): NewestSubject {
  return {
    name: dto.name,
    format: wireToken('format', dto.format, SCHEMA_FORMATS) as SchemaFormat,
    owner: dto.owner,
    lifecycle: wireToken('lifecycle', dto.lifecycle, SUBJECT_LIFECYCLES) as SubjectLifecycle,
    latest: dto.latest,
    versions: dto.versions,
    awaitingApproval: dto.awaitingApproval,
    createdAt: wireTimestamp(dto.createdAt),
  };
}

export function toHeldVersion(dto: HeldVersionDto): HeldVersion {
  return {
    subject: dto.subject,
    ordinal: dto.ordinal,
    status: wireToken('breakingChanges.status', dto.status, HELD_STATUSES) as HeldStatus,
    semanticVersion: dto.semanticVersion,
    registeredAt: wireTimestamp(dto.registeredAt),
    registeredBy: dto.registeredBy,
    decidedAt: dto.decidedAt === null ? null : wireTimestamp(dto.decidedAt),
    decidedBy: dto.decidedBy,
  };
}

export function toEnforcementTier(dto: EnforcementTierDto): EnforcementTier {
  return {
    mode: wireToken('enforcement.mode', dto.mode, ENFORCEMENT_MODES) as EnforcementMode,
    contracts: dto.contracts,
    publishBindings: dto.publishBindings,
    consumeBindings: dto.consumeBindings,
  };
}

export function toEnvironmentSummary(dto: EnvironmentSummaryDto): EnvironmentSummary {
  return {
    subjects: { ...dto.subjects },
    versions: { ...dto.versions },
    newestSubjects: dto.newestSubjects.map(toNewestSubject),
    breakingChanges: dto.breakingChanges.map(toHeldVersion),
    enforcement: dto.enforcement.map(toEnforcementTier),
    violations: { ...dto.violations },
  };
}
