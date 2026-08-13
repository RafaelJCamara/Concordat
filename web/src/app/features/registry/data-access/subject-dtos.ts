// The registry's wire shapes, and the mapping onto the domain.
//
// These mirror `Concordat.Api/Contracts.cs` field for field. They are deliberately separate
// types from `domain/registry/` even where the two currently agree: a DTO is what the
// server sent, a domain type is what the app believes, and collapsing them means the first
// wire change that needs a translation has nowhere to put one.

import { ConcordatError } from '../../../core/http/problem-details';
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
    format: token('format', dto.format, SCHEMA_FORMATS) as SchemaFormat,
    owner: dto.owner,
    lifecycle: token('lifecycle', dto.lifecycle, SUBJECT_LIFECYCLES) as SubjectLifecycle,
    contentModel: token('contentModel', dto.contentModel, CONTENT_MODELS) as ContentModel,
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
    status: token('status', dto.status, VERSION_STATUSES) as VersionStatus,
    changelog: dto.changelog,
    registeredAt: timestamp(dto.registeredAt),
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
    mode: token(
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
    direction: token('direction', dto.direction, ['BACKWARD', 'FORWARD']) as CompatibilityDirection,
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
  return token(field, value, COMPATIBILITY_SURFACES) as CompatibilitySurface;
}

/**
 * Checks a wire token against the set this build knows.
 *
 * Throws rather than coercing. The tempting alternative — fall back to the first member —
 * would render a version awaiting approval as `ACTIVE` the day the registry gains a fourth
 * status, and a UI that quietly mislabels the approval gate is worse than one that refuses
 * to draw the row. The error names the version mismatch so the reader knows to upgrade
 * rather than to go looking for a bug.
 */
function token(field: string, value: string, permitted: readonly string[]): string {
  if (permitted.includes(value)) {
    return value;
  }

  throw new ConcordatError({
    status: 0,
    code: 'registry_refused',
    detail:
      `The registry sent '${value}' for '${field}', which this build does not recognise. ` +
      'It is probably newer than this web app; upgrade the web app to match.',
  });
}

function timestamp(value: string): Date {
  const parsed = new Date(value);

  if (Number.isNaN(parsed.getTime())) {
    throw new ConcordatError({
      status: 0,
      code: 'registry_refused',
      detail: `The registry sent '${value}' as a timestamp, which is not a date.`,
    });
  }

  return parsed;
}
