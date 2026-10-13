// The impact endpoint's wire shapes, and the mapping onto the domain.
//
// They mirror `ImpactResponse` and `ConsumerImpactResponse` in
// `Concordat.Api/GovernanceEndpoints.cs` field for field.

import type { ConsumerImpact, ImpactReport } from '../../../domain/registry/impact';
import { IMPACT_CERTAINTIES, type ImpactCertainty } from '../../../domain/registry/wire-tokens';
import { wireTimestamp, wireToken } from '../../../core/http/wire-token';

/** `ConsumerImpactResponse`. */
export interface ConsumerImpactDto {
  readonly service: string;
  readonly selector: string;
  readonly readerOrdinal: number | null;
  readonly breaks: boolean;
  readonly certainty: string;
  /** ISO 8601, UTC. */
  readonly lastSeenAt: string;
  readonly stale: boolean;
  readonly reasons: readonly string[];
}

/** `ImpactResponse`. */
export interface ImpactDto {
  readonly subject: string;
  readonly candidateOrdinal: number | null;
  readonly candidateSchemaId: string | null;
  readonly breakingCount: number;
  readonly consumers: readonly ConsumerImpactDto[];
  readonly contracts: readonly string[];
}

export function toConsumerImpact(dto: ConsumerImpactDto): ConsumerImpact {
  return {
    service: dto.service,
    selector: dto.selector,
    readerOrdinal: dto.readerOrdinal,
    breaks: dto.breaks,
    certainty: wireToken('certainty', dto.certainty, IMPACT_CERTAINTIES) as ImpactCertainty,
    lastSeenAt: wireTimestamp(dto.lastSeenAt),
    stale: dto.stale,
    reasons: dto.reasons,
  };
}

export function toImpactReport(dto: ImpactDto): ImpactReport {
  return {
    subject: dto.subject,
    candidateOrdinal: dto.candidateOrdinal,
    candidateSchemaId: dto.candidateSchemaId,
    breakingCount: dto.breakingCount,
    consumers: dto.consumers.map(toConsumerImpact),
    contracts: dto.contracts,
  };
}
