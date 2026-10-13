import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { map, type Observable } from 'rxjs';
import { CONCORDAT_CONFIG } from '../../../core/config/app-config';
import { apiRoot } from '../../../core/http/api-url';
import { wireToken } from '../../../core/http/wire-token';
import {
  ROLES,
  type ApiKey,
  type IssuedApiKey,
  type Member,
  type Role,
} from '../../../domain/identity/member';
import type { Scope } from '../../../domain/identity/scope';
import {
  toApiKey,
  toIssuedApiKey,
  toMember,
  type ApiKeyDto,
  type IssuedApiKeyDto,
  type MemberDto,
} from './identity-dtos';

/** A member as a person describes one. */
export interface NewMember {
  readonly email: string;
  readonly password: string;
  readonly role: Role;
  readonly displayName: string;
}

/** An API key as a person asks for one. */
export interface NewApiKey {
  readonly label: string;
  readonly scopes: readonly Scope[];
  /** When it should stop working, or null for a key that never expires. */
  readonly expiresAt: Date | null;
}

/**
 * The organisation's members and API keys.
 *
 * <b>Both groups require `org:admin` on the server, reads included</b> — which is why nothing
 * here is shown to anyone else. They are tenant-wide rather than environment-scoped, so the
 * paths have no environment segment.
 *
 * <b>Nothing returned here can carry a password or a stored secret.</b> A member's response has
 * no hash, a key's list has no secret, and the one secret that exists is the answer to issuing
 * it. The password this file *sends* when adding a member goes out in the request body and is
 * not held on this side of the call.
 */
@Injectable({ providedIn: 'root' })
export class IdentityApi {
  private readonly http = inject(HttpClient);
  private readonly config = inject(CONCORDAT_CONFIG);

  listMembers(): Observable<readonly Member[]> {
    return this.http
      .get<readonly MemberDto[]>(`${apiRoot(this.config)}/members`)
      .pipe(map((dtos) => dtos.map(toMember)));
  }

  addMember(member: NewMember): Observable<Member> {
    return this.http
      .post<MemberDto>(`${apiRoot(this.config)}/members`, {
        email: member.email,
        password: member.password,
        role: member.role,
        // Left off when blank: the registry derives a display name, and an empty one is a
        // person who appears in every list as nothing.
        ...(member.displayName.trim() === '' ? {} : { displayName: member.displayName.trim() }),
      })
      .pipe(map(toMember));
  }

  /** Answers with the member's new role only, so the caller applies it to the copy it holds. */
  changeRole(userId: string, role: Role): Observable<{ userId: string; role: Role }> {
    return this.http
      .put<{ userId: string; role: string }>(
        `${apiRoot(this.config)}/members/${encodeURIComponent(userId)}/role`,
        { role },
      )
      .pipe(
        map((response) => ({
          userId: response.userId,
          role: wireToken('role', response.role, ROLES) as Role,
        })),
      );
  }

  listApiKeys(): Observable<readonly ApiKey[]> {
    return this.http
      .get<readonly ApiKeyDto[]>(`${apiRoot(this.config)}/api-keys`)
      .pipe(map((dtos) => dtos.map(toApiKey)));
  }

  issueApiKey(key: NewApiKey): Observable<IssuedApiKey> {
    return this.http
      .post<IssuedApiKeyDto>(`${apiRoot(this.config)}/api-keys`, {
        label: key.label,
        scopes: key.scopes,
        ...(key.expiresAt === null ? {} : { expiresAt: key.expiresAt.toISOString() }),
      })
      .pipe(map(toIssuedApiKey));
  }

  revokeApiKey(id: string): Observable<ApiKey> {
    return this.http
      .delete<ApiKeyDto>(`${apiRoot(this.config)}/api-keys/${encodeURIComponent(id)}`)
      .pipe(map(toApiKey));
  }
}
