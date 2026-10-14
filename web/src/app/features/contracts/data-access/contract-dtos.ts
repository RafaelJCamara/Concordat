// The contract endpoints' wire shapes, and the mapping onto the domain.
//
// They mirror `ContractResponse` and its members in `Concordat.Api/ContractEndpoints.cs` field
// for field.

import {
  ENFORCEMENT_MODES,
  type ConsumeBinding,
  type Contract,
  type EnforcementMode,
  type PublishBinding,
  type SubjectRef,
} from '../../../domain/contracts/contract';
import { wireTimestamp, wireToken } from '../../../core/http/wire-token';

/** `SubjectRefResponse`, and `SubjectRefInput` going the other way. */
export interface SubjectRefDto {
  readonly subject: string;
  readonly selector: string;
}

/** `PublishBindingResponse`. */
export interface PublishBindingDto {
  readonly id: string;
  readonly exchange: string;
  readonly routingKeyPattern: string;
  readonly brokerId: string | null;
  readonly virtualHost: string;
  readonly precedence: number | null;
  readonly subjects: readonly SubjectRefDto[];
}

/** `ConsumeBindingResponse`. */
export interface ConsumeBindingDto {
  readonly id: string;
  readonly queue: string;
  readonly brokerId: string | null;
  readonly virtualHost: string;
  readonly subjects: readonly SubjectRefDto[];
}

/** `ContractResponse`. */
export interface ContractDto {
  readonly name: string;
  readonly enforcement: string;
  /** ISO 8601, UTC. */
  readonly createdAt: string;
  readonly publishes: readonly PublishBindingDto[];
  readonly consumes: readonly ConsumeBindingDto[];
}

/** `AddPublishBindingRequest`. */
export interface AddPublishBindingDto {
  readonly exchange: string;
  readonly routingKeyPattern: string;
  readonly subjects: readonly SubjectRefDto[];
  readonly virtualHost?: string;
  readonly precedence?: number;
}

/** `ReplacePublishBindingRequest`: the precedence is left off to clear it. */
export interface ReplacePublishBindingDto {
  readonly subjects: readonly SubjectRefDto[];
  readonly precedence?: number;
}

/** `ReplaceConsumeBindingRequest`. */
export interface ReplaceConsumeBindingDto {
  readonly subjects: readonly SubjectRefDto[];
}

/** `AddConsumeBindingRequest`. */
export interface AddConsumeBindingDto {
  readonly queue: string;
  readonly subjects: readonly SubjectRefDto[];
  readonly virtualHost?: string;
}

export function toContract(dto: ContractDto): Contract {
  return {
    name: dto.name,
    enforcement: wireToken('enforcement', dto.enforcement, ENFORCEMENT_MODES) as EnforcementMode,
    createdAt: wireTimestamp(dto.createdAt),
    publishes: dto.publishes.map(toPublishBinding),
    consumes: dto.consumes.map(toConsumeBinding),
  };
}

function toPublishBinding(dto: PublishBindingDto): PublishBinding {
  return {
    id: dto.id,
    exchange: dto.exchange,
    routingKeyPattern: dto.routingKeyPattern,
    brokerId: dto.brokerId,
    virtualHost: dto.virtualHost,
    precedence: dto.precedence,
    subjects: dto.subjects.map(toSubjectRef),
  };
}

function toConsumeBinding(dto: ConsumeBindingDto): ConsumeBinding {
  return {
    id: dto.id,
    queue: dto.queue,
    brokerId: dto.brokerId,
    virtualHost: dto.virtualHost,
    subjects: dto.subjects.map(toSubjectRef),
  };
}

function toSubjectRef(dto: SubjectRefDto): SubjectRef {
  return { subject: dto.subject, selector: dto.selector };
}
