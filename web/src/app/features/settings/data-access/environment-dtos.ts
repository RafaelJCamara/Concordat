// The environment endpoints' wire shapes, and the mapping onto the domain.
//
// They mirror `EnvironmentResponse` and `BrokerResponse` in
// `Concordat.Api/EnvironmentEndpoints.cs` field for field.

import { wireTimestamp, wireToken } from '../../../core/http/wire-token';
import {
  BROKER_STATUSES,
  REGISTRATION_POLICIES,
  type Broker,
  type BrokerStatus,
  type Environment,
  type RegistrationPolicy,
} from '../../../domain/environments/environment';
import {
  COMPATIBILITY_MODES,
  COMPATIBILITY_SURFACES,
  type CompatibilityMode,
  type CompatibilityPolicy,
  type CompatibilitySurface,
} from '../../../domain/registry/compatibility';

/** `BrokerResponse`. */
export interface BrokerDto {
  readonly brokerId: string;
  readonly displayName: string;
  readonly uri: string;
  readonly virtualHost: string;
  readonly useTls: boolean;
  readonly hasCredentials: boolean;
  readonly status: string;
  /** ISO 8601, UTC. */
  readonly lastCheckedAt: string | null;
  readonly lastError: string | null;
}

/** `EnvironmentResponse`. */
export interface EnvironmentDto {
  readonly name: string;
  readonly description: string | null;
  readonly defaultCompatibilityPolicy: {
    readonly mode: string | null;
    readonly surface: string | null;
  };
  readonly registrationPolicy: string;
  readonly allowPreReleaseVersions: boolean;
  readonly createdAt: string;
  readonly brokers: readonly BrokerDto[];
}

/** `UpdateEnvironmentRequest`. Omitted members are left alone by the registry. */
export interface UpdateEnvironmentDto {
  readonly description?: string;
  readonly compatibilityMode?: string;
  readonly compatibilitySurface?: string;
  readonly registrationPolicy?: string;
  readonly allowPreReleaseVersions?: boolean;
}

export function toEnvironment(dto: EnvironmentDto): Environment {
  return {
    name: dto.name,
    description: dto.description,
    defaultCompatibility: toPolicy(dto.defaultCompatibilityPolicy),
    registrationPolicy: wireToken(
      'registrationPolicy',
      dto.registrationPolicy,
      REGISTRATION_POLICIES,
    ) as RegistrationPolicy,
    allowPreReleaseVersions: dto.allowPreReleaseVersions,
    createdAt: wireTimestamp(dto.createdAt),
    brokers: dto.brokers.map(toBroker),
  };
}

export function toBroker(dto: BrokerDto): Broker {
  return {
    brokerId: dto.brokerId,
    displayName: dto.displayName,
    uri: dto.uri,
    virtualHost: dto.virtualHost,
    useTls: dto.useTls,
    hasCredentials: dto.hasCredentials,
    status: wireToken('broker status', dto.status, BROKER_STATUSES) as BrokerStatus,
    lastCheckedAt: dto.lastCheckedAt === null ? null : wireTimestamp(dto.lastCheckedAt),
    lastError: dto.lastError,
  };
}

function toPolicy(dto: EnvironmentDto['defaultCompatibilityPolicy']): CompatibilityPolicy {
  // Nulls together mean nothing is set at this level, which is not the same as a default the
  // registry chose: the type keeps the two apart so the form can say which it is showing.
  if (dto.mode === null && dto.surface === null) {
    return { mode: null, surface: null };
  }

  return {
    mode: wireToken(
      'defaultCompatibilityPolicy.mode',
      dto.mode ?? '',
      COMPATIBILITY_MODES,
    ) as CompatibilityMode,
    surface: wireToken(
      'defaultCompatibilityPolicy.surface',
      dto.surface ?? '',
      COMPATIBILITY_SURFACES,
    ) as CompatibilitySurface,
  };
}
