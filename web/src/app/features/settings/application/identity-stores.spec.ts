import { TestBed } from '@angular/core/testing';
import { Subject as ResponseStream } from 'rxjs';
import { beforeEach, describe, expect, it } from 'vitest';
import { provideConcordatConfig } from '../../../core/config/app-config';
import { ConcordatError } from '../../../core/http/problem-details';
import type { ApiKey, IssuedApiKey, Member, Role } from '../../../domain/identity/member';
import {
  IdentityApi,
  type MemberDisabled,
  type MemberPasswordReset,
  type NewApiKey,
  type NewMember,
} from '../data-access/identity-api';
import { ApiKeysStore } from './api-keys-store';
import { MembersStore } from './members-store';

function member(email: string, role: Role = 'READER', id = email): Member {
  return {
    userId: id,
    email,
    displayName: email,
    role,
    disabled: false,
    mustChangePassword: false,
    createdAt: new Date('2026-08-01T09:00:00Z'),
    lastSignedInAt: null,
  };
}

function key(id: string, createdAt: string, overrides: Partial<ApiKey> = {}): ApiKey {
  return {
    id,
    keyId: `k-${id}`,
    label: `label ${id}`,
    scopes: ['subject:write'],
    createdAt: new Date(createdAt),
    expiresAt: null,
    lastUsedAt: null,
    revokedAt: null,
    ...overrides,
  };
}

function fakeApi() {
  const calls: { kind: string; args: unknown[]; stream: ResponseStream<unknown> }[] = [];

  function call<T>(kind: string, ...args: unknown[]) {
    const stream = new ResponseStream<T>();
    calls.push({ kind, args, stream: stream as ResponseStream<unknown> });
    return stream.asObservable();
  }

  return {
    calls,
    ofKind: (kind: string) => calls.filter((c) => c.kind === kind),
    listMembers: () => call<readonly Member[]>('listMembers'),
    addMember: (m: NewMember) => call<Member>('addMember', m),
    changeRole: (id: string, role: Role) =>
      call<{ userId: string; role: Role }>('changeRole', id, role),
    resetPassword: (id: string, password: string) =>
      call<MemberPasswordReset>('resetPassword', id, password),
    setDisabled: (id: string, disabled: boolean) =>
      call<MemberDisabled>('setDisabled', id, disabled),
    listApiKeys: () => call<readonly ApiKey[]>('listApiKeys'),
    issueApiKey: (k: NewApiKey) => call<IssuedApiKey>('issueApiKey', k),
    revokeApiKey: (id: string) => call<ApiKey>('revokeApiKey', id),
  };
}

function setUp() {
  const api = fakeApi();

  TestBed.configureTestingModule({
    providers: [
      provideConcordatConfig({ defaultEnvironment: 'dev' }),
      { provide: IdentityApi, useValue: api },
      MembersStore,
      ApiKeysStore,
    ],
  });

  return api;
}

describe('MembersStore', () => {
  let api: ReturnType<typeof fakeApi>;
  let store: InstanceType<typeof MembersStore>;

  beforeEach(() => {
    api = setUp();
    store = TestBed.inject(MembersStore);
  });

  const newMember: NewMember = {
    email: 'new@example.com',
    password: 'correct horse battery',
    role: 'READER',
    displayName: '',
  };

  it('lists members by email, whatever order the registry used', () => {
    store.load();
    api.ofKind('listMembers')[0]!.stream.next([member('zed@x.y'), member('amy@x.y')]);

    expect(store.members().map((m) => m.email)).toEqual(['amy@x.y', 'zed@x.y']);
  });

  it('is empty only after a load that found nothing', () => {
    expect(store.isEmpty()).toBe(false);
    store.load();
    expect(store.isEmpty()).toBe(false);
    api.ofKind('listMembers')[0]!.stream.next([]);

    expect(store.isEmpty()).toBe(true);
  });

  it('reports a failed load without claiming there are no members', () => {
    store.load();
    api
      .ofKind('listMembers')[0]!
      .stream.error(
        new ConcordatError({ status: 403, code: 'insufficient_scope', detail: 'org:admin' }),
      );

    expect(store.error()?.code).toBe('insufficient_scope');
  });

  describe('adding', () => {
    it('inserts the member the registry returned, in email order', () => {
      store.load();
      api.ofKind('listMembers')[0]!.stream.next([member('amy@x.y'), member('zed@x.y')]);

      store.add(newMember);
      api.ofKind('addMember')[0]!.stream.next(member('new@example.com'));

      expect(store.members().map((m) => m.email)).toEqual([
        'amy@x.y',
        'new@example.com',
        'zed@x.y',
      ]);
      expect(store.added()?.email).toBe('new@example.com');
      expect(store.adding()).toBe(false);
    });

    it('never holds the initial password', () => {
      store.add(newMember);
      api.ofKind('addMember')[0]!.stream.next(member('new@example.com'));

      expect(JSON.stringify(store.members())).not.toContain('correct horse battery');
      expect(JSON.stringify(store.added())).not.toContain('correct horse battery');
    });

    it('ignores a second click while the first is in flight', () => {
      store.add(newMember);
      store.add(newMember);

      expect(api.ofKind('addMember')).toHaveLength(1);
    });

    it('keeps a refusal apart from the list', () => {
      store.load();
      api.ofKind('listMembers')[0]!.stream.next([member('amy@x.y')]);

      store.add(newMember);
      api
        .ofKind('addMember')[0]!
        .stream.error(
          new ConcordatError({ status: 409, code: 'user_already_exists', detail: 'taken' }),
        );

      expect(store.addError()?.code).toBe('user_already_exists');
      expect(store.error()).toBeNull();
      expect(store.members()).toHaveLength(1);
    });

    it('clears the earlier outcome when asked again', () => {
      store.add(newMember);
      api.ofKind('addMember')[0]!.stream.error(new Error('offline'));
      store.add(newMember);

      expect(store.addError()).toBeNull();
      expect(store.added()).toBeNull();
    });
  });

  describe('changing a role', () => {
    beforeEach(() => {
      store.load();
      api
        .ofKind('listMembers')[0]!
        .stream.next([member('amy@x.y', 'READER', 'u1'), member('bo@x.y', 'ADMIN', 'u2')]);
    });

    it('applies the role from the registry’s answer, not ahead of it', () => {
      store.changeRole({ userId: 'u1', role: 'ADMIN' });

      expect(store.members()[0]?.role).toBe('READER');
      expect(store.changing()).toEqual(['u1']);

      api.ofKind('changeRole')[0]!.stream.next({ userId: 'u1', role: 'ADMIN' });

      expect(store.members()[0]?.role).toBe('ADMIN');
      expect(store.changing()).toEqual([]);
    });

    it('ignores a second change for the same member while one is in flight', () => {
      store.changeRole({ userId: 'u1', role: 'ADMIN' });
      store.changeRole({ userId: 'u1', role: 'OWNER' });

      expect(api.ofKind('changeRole')).toHaveLength(1);
    });

    it('lets different members change at once', () => {
      store.changeRole({ userId: 'u1', role: 'ADMIN' });
      store.changeRole({ userId: 'u2', role: 'READER' });

      expect(api.ofKind('changeRole')).toHaveLength(2);
    });

    it('keeps a refusal on the member it concerns, and leaves their role alone', () => {
      store.changeRole({ userId: 'u2', role: 'READER' });
      api
        .ofKind('changeRole')[0]!
        .stream.error(
          new ConcordatError({ status: 400, code: 'cannot_demote_self', detail: 'last owner' }),
        );

      expect(store.roleErrors()['u2']?.detail).toBe('last owner');
      expect(store.members()[1]?.role).toBe('ADMIN');
      expect(store.changing()).toEqual([]);
    });

    it('clears that member’s refusal when they are tried again', () => {
      store.changeRole({ userId: 'u2', role: 'READER' });
      api.ofKind('changeRole')[0]!.stream.error(new Error('offline'));
      store.changeRole({ userId: 'u2', role: 'READER' });

      expect(store.roleErrors()['u2']).toBeUndefined();
    });
  });

  describe('resetting a password (ADR-048)', () => {
    beforeEach(() => {
      store.load();
      api
        .ofKind('listMembers')[0]!
        .stream.next([member('amy@x.y', 'READER', 'u1'), member('bo@x.y', 'ADMIN', 'u2')]);
    });

    it('marks the member from the registry’s answer, not ahead of it', () => {
      store.resetPassword({ userId: 'u1', password: 'a temporary one' });

      expect(store.members()[0]?.mustChangePassword).toBe(false);
      expect(store.acting()).toEqual(['u1']);

      api
        .ofKind('resetPassword')[0]!
        .stream.next({ userId: 'u1', mustChangePassword: true, sessionsSignedOut: 2 });

      expect(store.members()[0]?.mustChangePassword).toBe(true);
      expect(store.members()[1]?.mustChangePassword).toBe(false);
      expect(store.acting()).toEqual([]);
    });

    it('says what it did, from the registry’s count of the sessions it ended', () => {
      store.resetPassword({ userId: 'u1', password: 'a temporary one' });
      api
        .ofKind('resetPassword')[0]!
        .stream.next({ userId: 'u1', mustChangePassword: true, sessionsSignedOut: 2 });

      expect(store.notice()).toEqual({ kind: 'reset', email: 'amy@x.y', count: 2 });
    });

    it('sends the password to the registry and keeps it nowhere', () => {
      store.resetPassword({ userId: 'u1', password: 'a temporary one' });
      api
        .ofKind('resetPassword')[0]!
        .stream.next({ userId: 'u1', mustChangePassword: true, sessionsSignedOut: 0 });

      expect(api.ofKind('resetPassword')[0]?.args).toEqual(['u1', 'a temporary one']);
      expect(JSON.stringify(store.members())).not.toContain('a temporary one');
      expect(JSON.stringify(store.notice())).not.toContain('a temporary one');
      expect(JSON.stringify(store.accountErrors())).not.toContain('a temporary one');
    });

    it('ignores a second request for the same member while one is in flight', () => {
      store.resetPassword({ userId: 'u1', password: 'a temporary one' });
      store.resetPassword({ userId: 'u1', password: 'another temporary' });

      expect(api.ofKind('resetPassword')).toHaveLength(1);
    });

    it('keeps a refusal on the member it concerns, and leaves the member alone', () => {
      store.resetPassword({ userId: 'u2', password: 'a temporary one' });
      api
        .ofKind('resetPassword')[0]!
        .stream.error(
          new ConcordatError({ status: 409, code: 'own_account', detail: 'That is you.' }),
        );

      expect(store.accountErrors()['u2']?.detail).toBe('That is you.');
      expect(store.members()[1]?.mustChangePassword).toBe(false);
      expect(store.acting()).toEqual([]);
      expect(store.notice()).toBeNull();
    });

    it('clears the member’s refusal and the last notice when asked again', () => {
      store.resetPassword({ userId: 'u2', password: 'a temporary one' });
      api.ofKind('resetPassword')[0]!.stream.error(new Error('offline'));
      store.resetPassword({ userId: 'u2', password: 'a temporary one' });

      expect(store.accountErrors()['u2']).toBeUndefined();
    });
  });

  describe('disabling and enabling (ADR-048)', () => {
    beforeEach(() => {
      store.load();
      api
        .ofKind('listMembers')[0]!
        .stream.next([member('amy@x.y', 'READER', 'u1'), member('bo@x.y', 'ADMIN', 'u2')]);
    });

    it('applies the flag from the registry’s answer, not ahead of it', () => {
      store.setDisabled({ userId: 'u1', disabled: true });

      expect(store.members()[0]?.disabled).toBe(false);
      expect(store.acting()).toEqual(['u1']);

      api
        .ofKind('setDisabled')[0]!
        .stream.next({ userId: 'u1', disabled: true, credentialsRevoked: 3 });

      expect(store.members()[0]?.disabled).toBe(true);
      expect(store.members()[1]?.disabled).toBe(false);
      expect(store.acting()).toEqual([]);
    });

    it('asks for what was clicked, and says what the registry answered', () => {
      store.setDisabled({ userId: 'u1', disabled: true });
      api
        .ofKind('setDisabled')[0]!
        .stream.next({ userId: 'u1', disabled: true, credentialsRevoked: 3 });

      expect(api.ofKind('setDisabled')[0]?.args).toEqual(['u1', true]);
      expect(store.notice()).toEqual({ kind: 'disabled', email: 'amy@x.y', count: 3 });
    });

    it('says an enabling restored nothing, since the registry revokes nothing and brings nothing back', () => {
      store.setDisabled({ userId: 'u1', disabled: false });
      api
        .ofKind('setDisabled')[0]!
        .stream.next({ userId: 'u1', disabled: false, credentialsRevoked: 0 });

      expect(store.notice()).toEqual({ kind: 'enabled', email: 'amy@x.y', count: 0 });
    });

    it('shows what the registry says even when it differs from what was asked', () => {
      // A repeat of a request that already went through answers the state as it is.
      store.setDisabled({ userId: 'u1', disabled: true });
      api
        .ofKind('setDisabled')[0]!
        .stream.next({ userId: 'u1', disabled: true, credentialsRevoked: 0 });
      store.setDisabled({ userId: 'u1', disabled: true });
      api
        .ofKind('setDisabled')[1]!
        .stream.next({ userId: 'u1', disabled: true, credentialsRevoked: 0 });

      expect(store.members()[0]?.disabled).toBe(true);
    });

    it('keeps a refusal on the member it concerns, and leaves them as they were', () => {
      store.setDisabled({ userId: 'u2', disabled: true });
      api
        .ofKind('setDisabled')[0]!
        .stream.error(
          new ConcordatError({ status: 409, code: 'last_owner', detail: 'The last owner.' }),
        );

      expect(store.accountErrors()['u2']?.code).toBe('last_owner');
      expect(store.members()[1]?.disabled).toBe(false);
      expect(store.acting()).toEqual([]);
    });

    it('applies what the registry answered, which is what it now says about the member', () => {
      // Asked to disable; the answer says they are not (somebody enabled them in between). The row
      // shows the answer, never the ask.
      store.setDisabled({ userId: 'u1', disabled: true });
      api
        .ofKind('setDisabled')[0]!
        .stream.next({ userId: 'u1', disabled: false, credentialsRevoked: 0 });

      expect(store.members()[0]?.disabled).toBe(false);
      expect(store.notice()?.kind).toBe('enabled');
    });

    it('ignores a second request for the same member while one is in flight', () => {
      store.setDisabled({ userId: 'u1', disabled: true });
      store.setDisabled({ userId: 'u1', disabled: false });

      expect(api.ofKind('setDisabled')).toHaveLength(1);
    });

    it('lets different members be acted on at once, and one at a time each', () => {
      store.setDisabled({ userId: 'u1', disabled: true });
      store.resetPassword({ userId: 'u1', password: 'a temporary one' });
      store.setDisabled({ userId: 'u2', disabled: true });

      expect(api.ofKind('setDisabled')).toHaveLength(2);
      expect(api.ofKind('resetPassword')).toHaveLength(0);
    });
  });
});

describe('ApiKeysStore', () => {
  let api: ReturnType<typeof fakeApi>;
  let store: InstanceType<typeof ApiKeysStore>;

  beforeEach(() => {
    api = setUp();
    store = TestBed.inject(ApiKeysStore);
  });

  const request: NewApiKey = { label: 'ci', scopes: ['subject:write'], expiresAt: null };

  it('lists the newest key first, which is nearly always the one somebody wants', () => {
    store.load();
    api
      .ofKind('listApiKeys')[0]!
      .stream.next([key('old', '2026-01-01T00:00:00Z'), key('new', '2026-08-01T00:00:00Z')]);

    expect(store.keys().map((k) => k.id)).toEqual(['new', 'old']);
  });

  describe('issuing', () => {
    it('holds the secret until dismissed, and never writes it into the list', () => {
      store.issue(request);
      api.ofKind('issueApiKey')[0]!.stream.next({
        key: key('k1', '2026-08-13T00:00:00Z'),
        secret: 'cdt_k-k1_SECRET',
      });

      expect(store.issued()?.secret).toBe('cdt_k-k1_SECRET');
      expect(JSON.stringify(store.keys())).not.toContain('SECRET');
      expect(store.keys()).toHaveLength(1);
    });

    it('forgets the secret when dismissed', () => {
      store.issue(request);
      api.ofKind('issueApiKey')[0]!.stream.next({
        key: key('k1', '2026-08-13T00:00:00Z'),
        secret: 'cdt_k-k1_SECRET',
      });

      store.dismiss();

      expect(store.issued()).toBeNull();
      expect(store.keys()).toHaveLength(1);
    });

    it('clears the previous secret the moment a new request starts', () => {
      // Two secrets are never on screen, and the old one is not left lying around while the new
      // one is awaited.
      store.issue(request);
      api.ofKind('issueApiKey')[0]!.stream.next({
        key: key('k1', '2026-08-13T00:00:00Z'),
        secret: 'FIRST',
      });

      store.issue(request);

      expect(store.issued()).toBeNull();
    });

    it('ignores a second click while the first is in flight, which would be a second secret', () => {
      store.issue(request);
      store.issue(request);

      expect(api.ofKind('issueApiKey')).toHaveLength(1);
    });

    it('keeps a refusal apart from the list', () => {
      store.load();
      api.ofKind('listApiKeys')[0]!.stream.next([key('k1', '2026-08-13T00:00:00Z')]);

      store.issue(request);
      api.ofKind('issueApiKey')[0]!.stream.error(
        new ConcordatError({
          status: 403,
          code: 'scope_escalation',
          detail: 'more than you hold',
        }),
      );

      expect(store.issueError()?.detail).toBe('more than you hold');
      expect(store.issued()).toBeNull();
      expect(store.keys()).toHaveLength(1);
    });
  });

  describe('revoking', () => {
    beforeEach(() => {
      store.load();
      api.ofKind('listApiKeys')[0]!.stream.next([key('k1', '2026-08-13T00:00:00Z')]);
    });

    it('applies the registry’s revoked record, so the timestamp is the registry’s', () => {
      store.revoke('k1');
      api
        .ofKind('revokeApiKey')[0]!
        .stream.next(
          key('k1', '2026-08-13T00:00:00Z', { revokedAt: new Date('2026-08-14T10:00:00Z') }),
        );

      expect(store.keys()[0]?.revokedAt?.toISOString()).toBe('2026-08-14T10:00:00.000Z');
      expect(store.revoking()).toEqual([]);
    });

    it('keeps the key in the list, revoked, since a key that stopped working is evidence', () => {
      store.revoke('k1');
      api
        .ofKind('revokeApiKey')[0]!
        .stream.next(key('k1', '2026-08-13T00:00:00Z', { revokedAt: new Date() }));

      expect(store.keys()).toHaveLength(1);
    });

    it('ignores a second revoke for a key already being revoked', () => {
      store.revoke('k1');
      store.revoke('k1');

      expect(api.ofKind('revokeApiKey')).toHaveLength(1);
    });

    it('keeps a refusal on the key it concerns', () => {
      store.revoke('k1');
      api
        .ofKind('revokeApiKey')[0]!
        .stream.error(
          new ConcordatError({ status: 404, code: 'api_key_not_found', detail: 'gone' }),
        );

      expect(store.revokeErrors()['k1']?.detail).toBe('gone');
      expect(store.keys()[0]?.revokedAt).toBeNull();
    });
  });
});
