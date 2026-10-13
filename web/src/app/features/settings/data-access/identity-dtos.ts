// The member and API-key endpoints' wire shapes, and the mapping onto the domain.
//
// They mirror `MemberResponse`, `ApiKeyResponse` and `IssuedApiKeyResponse` in
// `Concordat.Api/IdentityEndpoints.cs` field for field.

import { wireTimestamp, wireToken } from '../../../core/http/wire-token';
import {
  ROLES,
  type ApiKey,
  type IssuedApiKey,
  type Member,
  type Role,
} from '../../../domain/identity/member';
import { SCOPES, type Scope } from '../../../domain/identity/scope';

/** `MemberResponse`. */
export interface MemberDto {
  readonly userId: string;
  readonly email: string;
  readonly displayName: string;
  readonly role: string;
  readonly disabled: boolean;
  readonly createdAt: string;
  readonly lastSignedInAt: string | null;
}

/** `ApiKeyResponse`. */
export interface ApiKeyDto {
  readonly id: string;
  readonly keyId: string;
  readonly label: string;
  readonly scopes: readonly string[];
  readonly createdAt: string;
  readonly expiresAt: string | null;
  readonly lastUsedAt: string | null;
  readonly revokedAt: string | null;
}

/** `IssuedApiKeyResponse`. */
export interface IssuedApiKeyDto {
  readonly key: ApiKeyDto;
  readonly secret: string;
}

export function toMember(dto: MemberDto): Member {
  return {
    userId: dto.userId,
    email: dto.email,
    displayName: dto.displayName,
    role: wireToken('role', dto.role, ROLES) as Role,
    disabled: dto.disabled,
    createdAt: wireTimestamp(dto.createdAt),
    lastSignedInAt: optionalTimestamp(dto.lastSignedInAt),
  };
}

export function toApiKey(dto: ApiKeyDto): ApiKey {
  return {
    id: dto.id,
    keyId: dto.keyId,
    label: dto.label,
    // Strict, like the scopes a sign-in returns: a key shown with a scope this build cannot name
    // is a key somebody cannot reason about, and guessing at what it grants is worse than
    // refusing to draw the row.
    scopes: dto.scopes.map((scope) => wireToken('scopes', scope, SCOPES) as Scope),
    createdAt: wireTimestamp(dto.createdAt),
    expiresAt: optionalTimestamp(dto.expiresAt),
    lastUsedAt: optionalTimestamp(dto.lastUsedAt),
    revokedAt: optionalTimestamp(dto.revokedAt),
  };
}

export function toIssuedApiKey(dto: IssuedApiKeyDto): IssuedApiKey {
  return { key: toApiKey(dto.key), secret: dto.secret };
}

function optionalTimestamp(value: string | null): Date | null {
  return value === null ? null : wireTimestamp(value);
}
