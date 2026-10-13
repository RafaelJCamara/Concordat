// People and credentials: who belongs to the organisation, and what keys exist for it.

import type { Scope } from './scope';

/**
 * What a member may do.
 *
 * A role is a bundle of scopes and nothing else (ADR-018): `READER` reads everything, `ADMIN`
 * adds every schema, contract and broker write, and `OWNER` adds organisation administration —
 * members and API keys. Three rather than a permission matrix, so "can this person change a
 * contract?" stays a short question.
 */
export const ROLES = ['READER', 'ADMIN', 'OWNER'] as const;

/** A role, spelled as the API spells it. */
export type Role = (typeof ROLES)[number];

/** What each role grants, for a person choosing between them. */
export const ROLE_MEANING: Readonly<Record<Role, string>> = {
  READER: 'Reads everything and changes nothing. The default, and the majority.',
  ADMIN: 'Everything a reader may, plus every schema, contract and broker write.',
  OWNER: 'Everything an admin may, plus members and API keys.',
};

/** A person with an account in the organisation. */
export interface Member {
  readonly userId: string;
  readonly email: string;
  readonly displayName: string;
  readonly role: Role;
  /** Whether they have been stopped from signing in. */
  readonly disabled: boolean;
  readonly createdAt: Date;
  readonly lastSignedInAt: Date | null;
}

/**
 * An API key, as listed.
 *
 * <b>There is no secret here, and there never will be.</b> The registry stores only a hash and
 * cannot show a key again; the one time a secret exists is the response to issuing it, and it
 * lives in {@link IssuedApiKey}, not in anything a list can hold.
 */
export interface ApiKey {
  readonly id: string;
  /** The public half, which appears in the credential and in logs. */
  readonly keyId: string;
  readonly label: string;
  readonly scopes: readonly Scope[];
  readonly createdAt: Date;
  readonly expiresAt: Date | null;
  readonly lastUsedAt: Date | null;
  readonly revokedAt: Date | null;
}

/** A newly issued key and its one-time secret. */
export interface IssuedApiKey {
  readonly key: ApiKey;
  /** The full credential. The only time it is ever returned. */
  readonly secret: string;
}
