// The registry's wire shapes, and the mapping onto the domain.
//
// These mirror `Concordat.Api/Contracts.cs` field for field. They are deliberately separate
// types from `domain/registry/` even where the two currently agree: a DTO is what the
// server sent, a domain type is what the app believes, and collapsing them means the first
// wire change that needs a translation has nowhere to put one.

import type {
  CompatibilityDirection,
  CompatibilityMode,
  CompatibilityPolicy,
  CompatibilitySurface,
  Divergence,
} from '../../../domain/registry/compatibility';
import {
  COMPATIBILITY_MODES,
  COMPATIBILITY_SURFACES,
} from '../../../domain/registry/compatibility';
import type { SchemaVersion, Subject } from '../../../domain/registry/subject';
import {
  CONTENT_MODELS,
  SCHEMA_FORMATS,
  SUBJECT_LIFECYCLES,
  VERSION_STATUSES,
  type ContentModel,
  type SchemaFormat,
  type SubjectLifecycle,
  type VersionStatus,
} from '../../../domain/registry/wire-tokens';
import { wireTimestamp, wireToken } from './wire-token';

/** `PolicyResponse`. Both members are null together when the subject inherits. */
export interface PolicyDto {
  readonly mode: string | null;
  readonly surface: string | null;
}

/** `VersionResponse`. */
export interface VersionDto {
  readonly ordinal: number;
  readonly schemaId: string;
  readonly semanticVersion: string | null;
  readonly status: string;
  readonly changelog: string | null;
  /** ISO 8601, UTC. */
  readonly registeredAt: string;
  readonly registeredBy: string;
  readonly deprecated: boolean;
}

/** `SubjectResponse`. */
export interface SubjectDto {
  readonly name: string;
  readonly format: string;
  readonly owner: string;
  readonly lifecycle: string;
  readonly contentModel: string;
  readonly compatibilityPolicy: PolicyDto;
  readonly latest: number | null;
  readonly versions: readonly VersionDto[];
}

/** `BreakingChangeResponse`. */
export interface DivergenceDto {
  readonly path: string;
  readonly kind: string;
  readonly direction: string;
  readonly surface: string;
  readonly message: string;
  readonly conflictsWithVersion: number;
}

export function toSubject(dto: SubjectDto): Subject {
  return {
    name: dto.name,
    format: wireToken('format', dto.format, SCHEMA_FORMATS) as SchemaFormat,
    owner: dto.owner,
    lifecycle: wireToken('lifecycle', dto.lifecycle, SUBJECT_LIFECYCLES) as SubjectLifecycle,
    contentModel: wireToken('contentModel', dto.contentModel, CONTENT_MODELS) as ContentModel,
    compatibilityPolicy: toPolicy(dto.compatibilityPolicy),
    latest: dto.latest,
    versions: dto.versions.map(toVersion),
  };
}

export function toVersion(dto: VersionDto): SchemaVersion {
  return {
    ordinal: dto.ordinal,
    schemaId: dto.schemaId,
    semanticVersion: dto.semanticVersion,
    status: wireToken('status', dto.status, VERSION_STATUSES) as VersionStatus,
    changelog: dto.changelog,
    registeredAt: wireTimestamp(dto.registeredAt),
    registeredBy: dto.registeredBy,
    deprecated: dto.deprecated,
  };
}

export function toPolicy(dto: PolicyDto): CompatibilityPolicy {
  // Nulls together mean "inherit the environment default", which the domain type preserves.
  if (dto.mode === null && dto.surface === null) {
    return { mode: null, surface: null };
  }

  return {
    mode: wireToken(
      'compatibilityPolicy.mode',
      dto.mode ?? '',
      COMPATIBILITY_MODES,
    ) as CompatibilityMode,
    surface: toSurface('compatibilityPolicy.surface', dto.surface ?? ''),
  };
}

export function toDivergence(dto: DivergenceDto): Divergence {
  return {
    path: dto.path,
    kind: dto.kind,
    direction: wireToken('direction', dto.direction, [
      'BACKWARD',
      'FORWARD',
    ]) as CompatibilityDirection,
    surface: toSurface('surface', dto.surface),
    message: dto.message,
    conflictsWithVersion: dto.conflictsWithVersion,
  };
}

/**
 * Reads a compatibility surface.
 *
 * Deliberately strict, with no underscore-stripping fallback. An earlier build normalised two
 * spellings because the API emitted both; that was fixed server-side in M6.1, and keeping the
 * leniency would mean a future divergence passed silently instead of failing here.
 */
function toSurface(field: string, value: string): CompatibilitySurface {
  return wireToken(field, value, COMPATIBILITY_SURFACES) as CompatibilitySurface;
}
