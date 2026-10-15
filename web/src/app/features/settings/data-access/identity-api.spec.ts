import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { afterEach, describe, expect, it } from 'vitest';
import { provideConcordatConfig } from '../../../core/config/app-config';
import { REFUSAL_IS_NOT_EXPIRY } from '../../../core/http/auth-interceptor';
import type { ApiKey, IssuedApiKey, Member } from '../../../domain/identity/member';
import { IdentityApi } from './identity-api';
import { toApiKey, toMember, type ApiKeyDto, type MemberDto } from './identity-dtos';

// The URLs and bodies, and the one property that matters most here: nothing a list returns can
// carry a secret, and the one secret that exists is the answer to issuing it.

const member: MemberDto = {
  userId: '11111111-1111-1111-1111-111111111111',
  email: 'alice@example.com',
  displayName: 'Alice',
  role: 'ADMIN',
  disabled: false,
  mustChangePassword: false,
  createdAt: '2026-08-01T09:00:00+00:00',
  lastSignedInAt: '2026-08-13T09:00:00+00:00',
};

const key: ApiKeyDto = {
  id: '22222222-2222-2222-2222-222222222222',
  keyId: 'k3y1d',
  label: 'ci-pipeline',
  scopes: ['subject:write', 'ci'],
  createdAt: '2026-08-01T09:00:00+00:00',
  expiresAt: null,
  lastUsedAt: null,
  revokedAt: null,
};

function setUp() {
  TestBed.configureTestingModule({
    providers: [
      provideConcordatConfig({ defaultEnvironment: 'dev' }),
      provideHttpClient(),
      provideHttpClientTesting(),
    ],
  });

  return { api: TestBed.inject(IdentityApi), backend: TestBed.inject(HttpTestingController) };
}

describe('IdentityApi', () => {
  afterEach(() => TestBed.inject(HttpTestingController).verify());

  describe('members', () => {
    it('lists them from the tenant-wide route, which has no environment in it', () => {
      let members: readonly Member[] = [];
      const { api, backend } = setUp();
      api.listMembers().subscribe((value) => (members = value));

      backend.expectOne('/v1/members').flush([member]);

      expect(members[0]?.lastSignedInAt).toBeInstanceOf(Date);
    });

    it('adds one, leaving a blank display name off the body', () => {
      // The registry derives one; an empty name is a person who appears in every list as nothing.
      const { api, backend } = setUp();
      api
        .addMember({ email: 'a@b.c', password: 'p'.repeat(12), role: 'READER', displayName: ' ' })
        .subscribe();

      const request = backend.expectOne('/v1/members');

      expect(request.request.method).toBe('POST');
      expect(request.request.body).toEqual({
        email: 'a@b.c',
        password: 'p'.repeat(12),
        role: 'READER',
      });
    });

    it('sends a display name when given, trimmed', () => {
      const { api, backend } = setUp();
      api
        .addMember({ email: 'a@b.c', password: 'x', role: 'OWNER', displayName: ' Ada ' })
        .subscribe();

      expect(backend.expectOne('/v1/members').request.body.displayName).toBe('Ada');
    });

    it('puts a role change to the member’s role route', () => {
      let changed: { userId: string; role: string } | null = null;
      const { api, backend } = setUp();
      api.changeRole(member.userId, 'READER').subscribe((value) => (changed = value));

      const request = backend.expectOne(`/v1/members/${member.userId}/role`);

      expect(request.request.method).toBe('PUT');
      expect(request.request.body).toEqual({ role: 'READER' });

      request.flush({ userId: member.userId, role: 'READER' });

      expect(changed).toEqual({ userId: member.userId, role: 'READER' });
    });

    it('refuses a role in the answer that it does not recognise', () => {
      let failed: unknown = null;
      const { api, backend } = setUp();
      api.changeRole(member.userId, 'READER').subscribe({ error: (e) => (failed = e) });

      backend
        .expectOne(`/v1/members/${member.userId}/role`)
        .flush({ userId: member.userId, role: 'SUPERUSER' });

      expect(String((failed as Error).message ?? failed)).toContain('SUPERUSER');
    });

    it('reads whether a member still has to change a password an administrator set', () => {
      let members: readonly Member[] = [];
      const { api, backend } = setUp();
      api.listMembers().subscribe((value) => (members = value));

      backend.expectOne('/v1/members').flush([{ ...member, mustChangePassword: true }]);

      expect(members[0]?.mustChangePassword).toBe(true);
    });

    it('resets a password with a put to the member’s password route, sending only the password', () => {
      let reset: { userId: string; mustChangePassword: boolean; sessionsSignedOut: number } | null =
        null;
      const { api, backend } = setUp();
      api.resetPassword(member.userId, 'temporary 12345').subscribe((value) => (reset = value));

      const request = backend.expectOne(`/v1/members/${member.userId}/password`);

      expect(request.request.method).toBe('PUT');
      expect(request.request.body).toEqual({ password: 'temporary 12345' });

      request.flush({ userId: member.userId, mustChangePassword: true, sessionsSignedOut: 2 });

      expect(reset).toEqual({
        userId: member.userId,
        mustChangePassword: true,
        sessionsSignedOut: 2,
      });
    });

    it('disables with a put to the member’s disabled route, and says which way it was asked', () => {
      let result: { userId: string; disabled: boolean; credentialsRevoked: number } | null = null;
      const { api, backend } = setUp();
      api.setDisabled(member.userId, true).subscribe((value) => (result = value));

      const request = backend.expectOne(`/v1/members/${member.userId}/disabled`);

      expect(request.request.method).toBe('PUT');
      expect(request.request.body).toEqual({ disabled: true });

      request.flush({ userId: member.userId, disabled: true, credentialsRevoked: 3 });

      expect(result).toEqual({ userId: member.userId, disabled: true, credentialsRevoked: 3 });
    });

    it('enables with the same route and a false, which is never left off the body', () => {
      // A body without it would be refused, and one that read as "enable" by default would enable
      // the member a misspelt field was meant to disable. Sent explicitly, both ways.
      const { api, backend } = setUp();
      api.setDisabled(member.userId, false).subscribe();

      const request = backend.expectOne(`/v1/members/${member.userId}/disabled`);

      expect(request.request.body).toEqual({ disabled: false });
    });
  });

  describe('your own password', () => {
    it('is changed with a post to the password route, carrying both passwords and nothing else', () => {
      let changed: { otherSessionsSignedOut: number } | null = null;
      const { api, backend } = setUp();
      api.changePassword('the old one', 'a brand new one').subscribe((value) => (changed = value));

      const request = backend.expectOne('/v1/auth/password');

      expect(request.request.method).toBe('POST');
      expect(request.request.body).toEqual({
        currentPassword: 'the old one',
        newPassword: 'a brand new one',
      });

      request.flush({ otherSessionsSignedOut: 1 });

      expect(changed).toEqual({ otherSessionsSignedOut: 1 });
    });

    it('is marked so that a wrong current password is not read as an expired session', () => {
      // The registry answers it 401, as it answers sign-in. See `REFUSAL_IS_NOT_EXPIRY`.
      const { api, backend } = setUp();
      api.changePassword('the old one', 'a brand new one').subscribe();

      const request = backend.expectOne('/v1/auth/password').request;

      expect(request.context.get(REFUSAL_IS_NOT_EXPIRY)).toBe(true);
    });

    it('does not mark any other request so', () => {
      const { api, backend } = setUp();
      api.setDisabled(member.userId, true).subscribe();

      expect(
        backend
          .expectOne(`/v1/members/${member.userId}/disabled`)
          .request.context.get(REFUSAL_IS_NOT_EXPIRY),
      ).toBe(false);
    });

    it('never answers with a password, and the request is the only place one goes', () => {
      let answer = '';
      const { api, backend } = setUp();
      api
        .changePassword('the old one', 'a brand new one')
        .subscribe((value) => (answer = JSON.stringify(value)));

      backend.expectOne('/v1/auth/password').flush({ otherSessionsSignedOut: 0 });

      expect(answer).not.toContain('the old one');
      expect(answer).not.toContain('a brand new one');
    });
  });

  describe('API keys', () => {
    it('lists them, and a listed key has no secret to read', () => {
      let keys: readonly ApiKey[] = [];
      const { api, backend } = setUp();
      api.listApiKeys().subscribe((value) => (keys = value));

      backend.expectOne('/v1/api-keys').flush([key]);

      expect(Object.keys(keys[0]!)).not.toContain('secret');
    });

    it('issues one with the scopes asked for, and an expiry only when there is one', () => {
      const { api, backend } = setUp();
      api.issueApiKey({ label: 'ci', scopes: ['subject:write'], expiresAt: null }).subscribe();

      const request = backend.expectOne('/v1/api-keys');

      expect(request.request.method).toBe('POST');
      expect(request.request.body).toEqual({ label: 'ci', scopes: ['subject:write'] });
    });

    it('sends the expiry as a UTC instant', () => {
      const { api, backend } = setUp();
      api
        .issueApiKey({
          label: 'ci',
          scopes: ['subject:write'],
          expiresAt: new Date('2026-12-31T23:59:59.999Z'),
        })
        .subscribe();

      expect(backend.expectOne('/v1/api-keys').request.body.expiresAt).toBe(
        '2026-12-31T23:59:59.999Z',
      );
    });

    it('returns the secret from the issue response, and only from there', () => {
      let issued: IssuedApiKey | null = null;
      const { api, backend } = setUp();
      api
        .issueApiKey({ label: 'ci', scopes: ['subject:write'], expiresAt: null })
        .subscribe((value) => (issued = value));

      backend.expectOne('/v1/api-keys').flush({ key, secret: 'cdt_k3y1d_abcdef' });

      expect(issued!.secret).toBe('cdt_k3y1d_abcdef');
      expect(issued!.key.label).toBe('ci-pipeline');
    });

    it('revokes with a DELETE to the key’s own route', () => {
      const { api, backend } = setUp();
      api.revokeApiKey(key.id).subscribe();

      expect(backend.expectOne(`/v1/api-keys/${key.id}`).request.method).toBe('DELETE');
    });
  });
});

describe('toMember', () => {
  it.each(['READER', 'ADMIN', 'OWNER'])('accepts the role %s', (role) => {
    expect(toMember({ ...member, role }).role).toBe(role);
  });

  it('refuses a role it does not recognise, rather than showing it as something it is not', () => {
    expect(() => toMember({ ...member, role: 'ROOT' })).toThrow(/ROOT/);
  });

  it('keeps a member who has never signed in as null', () => {
    expect(toMember({ ...member, lastSignedInAt: null }).lastSignedInAt).toBeNull();
  });
});

describe('toApiKey', () => {
  it('maps a key and its scopes', () => {
    expect(toApiKey(key).scopes).toEqual(['subject:write', 'ci']);
  });

  it('refuses a scope it cannot name', () => {
    // A key shown with a scope this build cannot name is a key somebody cannot reason about.
    expect(() => toApiKey({ ...key, scopes: ['subject:write', 'root:everything'] })).toThrow(
      /root:everything/,
    );
  });

  it('keeps a revoked key’s timestamp, so it can be shown as revoked and when', () => {
    expect(
      toApiKey({ ...key, revokedAt: '2026-08-14T09:00:00+00:00' }).revokedAt?.toISOString(),
    ).toBe('2026-08-14T09:00:00.000Z');
  });

  it('leaves the optional timestamps null when absent', () => {
    const mapped = toApiKey(key);

    expect(mapped.expiresAt).toBeNull();
    expect(mapped.lastUsedAt).toBeNull();
    expect(mapped.revokedAt).toBeNull();
  });
});
