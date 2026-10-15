import { TestBed } from '@angular/core/testing';
import { of } from 'rxjs';
import { beforeEach, describe, expect, it } from 'vitest';
import { SessionStore } from '../../../core/auth/session-store';
import { AuthApi, type SignedIn } from '../data-access/auth-api';
import { SignInStore } from './sign-in-store';

// What the store hands the session: the credential, who it is and what it holds, and, since an
// administrator can reset a password (ADR-048), whether the credential can do nothing yet but replace
// it. Losing that last fact here would let a limited session render the whole app and fail every read.

function signedIn(overrides: Partial<SignedIn> = {}): SignedIn {
  return {
    credential: 'cdt_x',
    actor: 'alice@example.com',
    scopes: ['subject:read'],
    expiresAt: new Date('2026-10-16T00:00:00Z'),
    mustChangePassword: false,
    ...overrides,
  };
}

function setUp(answer: SignedIn) {
  TestBed.configureTestingModule({
    providers: [
      SignInStore,
      {
        provide: AuthApi,
        useValue: { signIn: () => of(answer), bootstrap: () => of(undefined) },
      },
    ],
  });

  return { store: TestBed.inject(SignInStore), session: TestBed.inject(SessionStore) };
}

describe('SignInStore', () => {
  let ctx: ReturnType<typeof setUp>;

  describe('a sign-in', () => {
    beforeEach(() => {
      ctx = setUp(signedIn());
    });

    it('records the credential, who it is and what it holds', () => {
      ctx.store.signIn({ email: 'alice@example.com', password: 'a long enough one' });

      expect(ctx.session.credential()).toBe('cdt_x');
      expect(ctx.session.actor()).toBe('alice@example.com');
      expect(ctx.session.scopes()).toEqual(['subject:read']);
      expect(ctx.session.mustChangePassword()).toBe(false);
      expect(ctx.store.succeeded()).toBe(true);
    });
  });

  describe('a sign-in with a password an administrator set (ADR-048)', () => {
    beforeEach(() => {
      ctx = setUp(signedIn({ scopes: [], mustChangePassword: true }));
    });

    it('tells the session, which holds nothing, that it must change it', () => {
      ctx.store.signIn({ email: 'alice@example.com', password: 'a temporary one!' });

      expect(ctx.session.mustChangePassword()).toBe(true);
      expect(ctx.session.scopes()).toEqual([]);
      expect(ctx.session.isSignedIn()).toBe(true);
    });

    it('does the same when the sign-in follows claiming an instance', () => {
      ctx.store.bootstrap({ email: 'alice@example.com', password: 'a temporary one!' });

      expect(ctx.session.mustChangePassword()).toBe(true);
    });
  });
});
