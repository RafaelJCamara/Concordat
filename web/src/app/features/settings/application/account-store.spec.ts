import { TestBed } from '@angular/core/testing';
import { Subject as ResponseStream, type Observable } from 'rxjs';
import { beforeEach, describe, expect, it } from 'vitest';
import { SessionApi, type CredentialStatus } from '../../../core/auth/session-api';
import { SessionStore } from '../../../core/auth/session-store';
import { provideConcordatConfig } from '../../../core/config/app-config';
import { ConcordatError } from '../../../core/http/problem-details';
import { IdentityApi, type PasswordChanged } from '../data-access/identity-api';
import { AccountStore } from './account-store';

// The account screen's one action. What matters: the passwords are arguments and never state, a wrong
// current password is shown without signing anybody out, and replacing a temporary password learns
// what the credential holds from the registry before the app lets the person on.

function fakes() {
  const changes: { args: [string, string]; stream: ResponseStream<PasswordChanged> }[] = [];
  const statuses: ResponseStream<CredentialStatus>[] = [];

  return {
    changes,
    statuses,
    identity: {
      changePassword: (current: string, next: string): Observable<PasswordChanged> => {
        const stream = new ResponseStream<PasswordChanged>();
        changes.push({ args: [current, next], stream });
        return stream.asObservable();
      },
    },
    sessions: {
      status: (): Observable<CredentialStatus> => {
        const stream = new ResponseStream<CredentialStatus>();
        statuses.push(stream);
        return stream.asObservable();
      },
    },
  };
}

function setUp() {
  const f = fakes();

  TestBed.configureTestingModule({
    providers: [
      provideConcordatConfig({ defaultEnvironment: 'dev' }),
      { provide: IdentityApi, useValue: f.identity },
      { provide: SessionApi, useValue: f.sessions },
      AccountStore,
    ],
  });

  return {
    f,
    store: TestBed.inject(AccountStore),
    session: TestBed.inject(SessionStore),
  };
}

const status = (overrides: Partial<CredentialStatus> = {}): CredentialStatus => ({
  claimed: true,
  authenticated: true,
  actor: 'ops@example.com',
  scopes: ['subject:admin', 'org:admin'],
  mustChangePassword: false,
  ...overrides,
});

describe('AccountStore', () => {
  let ctx: ReturnType<typeof setUp>;

  beforeEach(() => {
    ctx = setUp();
    ctx.session.signIn({ credential: 'cred', actor: 'ops@example.com', scopes: ['subject:read'] });
  });

  it('sends both passwords and holds neither', () => {
    ctx.store.change({ current: 'the old one', next: 'a brand new one' });
    ctx.f.changes[0]!.stream.next({ otherSessionsSignedOut: 0 });

    expect(ctx.f.changes[0]?.args).toEqual(['the old one', 'a brand new one']);

    const everything = JSON.stringify([ctx.store.changed(), ctx.store.error()]);
    expect(everything).not.toContain('the old one');
    expect(everything).not.toContain('a brand new one');
  });

  it('says how many other sessions the registry signed out, from its answer', () => {
    ctx.store.change({ current: 'the old one', next: 'a brand new one' });

    expect(ctx.store.submitting()).toBe(true);
    expect(ctx.store.changed()).toBeNull();

    ctx.f.changes[0]!.stream.next({ otherSessionsSignedOut: 2 });

    expect(ctx.store.submitting()).toBe(false);
    expect(ctx.store.changed()).toEqual({ otherSessionsSignedOut: 2 });
  });

  it('asks the registry nothing more when the password was the person’s own', () => {
    ctx.store.change({ current: 'the old one', next: 'a brand new one' });
    ctx.f.changes[0]!.stream.next({ otherSessionsSignedOut: 0 });

    expect(ctx.f.statuses).toHaveLength(0);
    expect(ctx.session.scopes()).toEqual(['subject:read']);
  });

  it('ignores a second click while the first is in flight', () => {
    ctx.store.change({ current: 'the old one', next: 'a brand new one' });
    ctx.store.change({ current: 'the old one', next: 'a brand new one' });

    expect(ctx.f.changes).toHaveLength(1);
  });

  describe('a wrong current password', () => {
    const refusal = () =>
      new ConcordatError({
        status: 401,
        code: 'unauthenticated',
        detail: 'That is not your current password.',
      });

    it('is shown as the registry said it', () => {
      ctx.store.change({ current: 'wrong', next: 'a brand new one' });
      ctx.f.changes[0]!.stream.error(refusal());

      expect(ctx.store.error()?.detail).toBe('That is not your current password.');
      expect(ctx.store.changed()).toBeNull();
      expect(ctx.store.submitting()).toBe(false);
    });

    it('is checked against the registry before it is taken for a dead session', () => {
      // The interceptor was told not to treat this 401 as an expiry, so something has to: a session
      // that really died would otherwise sit on this form being told it typed a password wrong.
      ctx.store.change({ current: 'wrong', next: 'a brand new one' });
      ctx.f.changes[0]!.stream.error(refusal());

      expect(ctx.f.statuses).toHaveLength(1);

      ctx.f.statuses[0]!.next(status({ authenticated: true }));

      expect(ctx.session.credential()).toBe('cred');
    });

    it('signs the person out when the registry says the credential is the problem', () => {
      ctx.store.change({ current: 'wrong', next: 'a brand new one' });
      ctx.f.changes[0]!.stream.error(refusal());

      ctx.f.statuses[0]!.next(status({ authenticated: false, scopes: [] }));

      expect(ctx.session.credential()).toBeNull();
    });

    it('keeps the session when the registry cannot be asked', () => {
      ctx.store.change({ current: 'wrong', next: 'a brand new one' });
      ctx.f.changes[0]!.stream.error(refusal());

      ctx.f.statuses[0]!.error(new Error('offline'));

      expect(ctx.session.credential()).toBe('cred');
    });

    it('does not probe for a refusal that is not a 401', () => {
      ctx.store.change({ current: 'the old one', next: 'a brand new one' });
      ctx.f.changes[0]!.stream.error(
        new ConcordatError({ status: 400, code: 'password_invalid', detail: 'Too short.' }),
      );

      expect(ctx.f.statuses).toHaveLength(0);
      expect(ctx.store.error()?.code).toBe('password_invalid');
    });

    it('clears the earlier outcome when asked again', () => {
      ctx.store.change({ current: 'wrong', next: 'a brand new one' });
      ctx.f.changes[0]!.stream.error(refusal());
      ctx.store.change({ current: 'right', next: 'a brand new one' });

      expect(ctx.store.error()).toBeNull();
    });
  });

  describe('replacing a temporary password (ADR-048)', () => {
    beforeEach(() => {
      ctx.session.signIn({
        credential: 'cred',
        actor: 'ops@example.com',
        scopes: [],
        mustChangePassword: true,
      });
    });

    it('reads what the credential holds now before letting the person on', () => {
      ctx.store.change({ current: 'temporary', next: 'a brand new one' });
      ctx.f.changes[0]!.stream.next({ otherSessionsSignedOut: 0 });

      // Still limited, and not yet done: the change went through but the app does not know what the
      // same credential may do until the registry says.
      expect(ctx.session.mustChangePassword()).toBe(true);
      expect(ctx.store.changed()).toBeNull();
      expect(ctx.f.statuses).toHaveLength(1);

      ctx.f.statuses[0]!.next(status());

      expect(ctx.session.mustChangePassword()).toBe(false);
      expect(ctx.session.scopes()).toEqual(['subject:admin', 'org:admin']);
      expect(ctx.store.changed()).toEqual({ otherSessionsSignedOut: 0 });
    });

    it('does not let the person on when the registry still says the password must change', () => {
      ctx.store.change({ current: 'temporary', next: 'a brand new one' });
      ctx.f.changes[0]!.stream.next({ otherSessionsSignedOut: 0 });

      ctx.f.statuses[0]!.next(status({ scopes: [], mustChangePassword: true }));

      expect(ctx.session.mustChangePassword()).toBe(true);
      expect(ctx.store.error()?.code).toBe('registry_refused');
    });

    it('says plainly when the change went through and the access could not be read', () => {
      ctx.store.change({ current: 'temporary', next: 'a brand new one' });
      ctx.f.changes[0]!.stream.next({ otherSessionsSignedOut: 0 });

      ctx.f.statuses[0]!.error(new Error('offline'));

      expect(ctx.store.error()?.detail).toContain('password was changed');
      expect(ctx.session.mustChangePassword()).toBe(true);
    });
  });
});
