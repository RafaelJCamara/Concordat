import { TestBed } from '@angular/core/testing';
import { beforeEach, describe, expect, it } from 'vitest';
import { SessionStore } from './session-store';

// `canWriteSchemas` hangs off `scopes`, so everything that follows depends on a dropped
// session being genuinely empty rather than merely missing its credential.

describe('SessionStore', () => {
  let session: InstanceType<typeof SessionStore>;

  beforeEach(() => {
    TestBed.configureTestingModule({});
    session = TestBed.inject(SessionStore);
  });

  it('is signed out and knows nothing about the instance on a cold start', () => {
    expect(session.credential()).toBeNull();
    expect(session.actor()).toBeNull();
    expect(session.scopes()).toEqual([]);

    // `claimed` is null, not false: "we have not asked yet" and "there are no accounts" are
    // different states, and treating the first as the second would render every write
    // affordance on a registry that is about to refuse them.
    expect(session.claimed()).toBeNull();
    expect(session.canWriteSchemas()).toBe(false);
    expect(session.needsSignIn()).toBe(false);
  });

  it('offers schema writes on an unclaimed instance that answers its caller as an owner', () => {
    // The opt-in (ADR-047): the API grants the unclaimed caller owner scopes, so hiding every
    // button on a registry that would accept the write is a worse first run than showing them.
    session.observeInstance({
      claimed: false,
      actor: 'unclaimed-instance',
      scopes: ['subject:write'],
    });

    expect(session.openInstance()).toBe(true);
    expect(session.canWriteSchemas()).toBe(true);
    expect(session.needsSignIn()).toBe(false);
  });

  it('asks for the setup screen on an unclaimed instance that is closed, which is how a fresh one starts', () => {
    // ADR-047: no accounts and no scopes granted. Nothing renders, so the visitor is sent to
    // sign-in, which is the setup screen while nobody has an account.
    session.observeInstance({ claimed: false, actor: null, scopes: [] });

    expect(session.openInstance()).toBe(false);
    expect(session.canWriteSchemas()).toBe(false);
    expect(session.needsSignIn()).toBe(true);
  });

  it('asks for a sign-in on a claimed instance with no session', () => {
    session.observeInstance({ claimed: true, actor: null, scopes: [] });

    expect(session.needsSignIn()).toBe(true);
    expect(session.canWriteSchemas()).toBe(false);
  });

  it('derives write permission from the scopes the API granted', () => {
    session.signIn({ credential: 't', actor: 'analyst', scopes: ['subject:read'] });
    expect(session.canWriteSchemas()).toBe(false);

    session.signIn({ credential: 't', actor: 'ops', scopes: ['subject:write'] });
    expect(session.canWriteSchemas()).toBe(true);
  });

  it('keeps knowing the instance is claimed after a session expires', () => {
    // Whether the instance has accounts is a fact about the server, not about this session.
    // Forgetting it would send the user back to a first-run experience they have left — and
    // worse, would re-enable the unclaimed write affordances.
    session.observeInstance({ claimed: true, actor: null, scopes: [] });
    session.signIn({ credential: 't', actor: 'ops', scopes: ['subject:admin'] });

    session.expire();

    expect(session.claimed()).toBe(true);
    expect(session.canWriteSchemas()).toBe(false);
    expect(session.needsSignIn()).toBe(true);
  });

  it('records the credential, the actor and the scopes the API granted', () => {
    // `actor` is what the API records in `registeredBy` and `decidedBy`, so it is audit
    // data rather than a display name.
    session.signIn({ credential: 'token-abc', actor: 'ci', scopes: ['subject:read'] });

    expect(session.credential()).toBe('token-abc');
    expect(session.actor()).toBe('ci');
    expect(session.scopes()).toEqual(['subject:read']);
  });

  it('drops the scopes as well as the credential when the session expires', () => {
    // The one that matters. `expire()` is called by the auth interceptor on a 401, and a
    // session that kept its scopes would keep rendering the write affordances gated on
    // them — offering buttons to somebody the registry has just stopped recognising.
    session.signIn({ credential: 'token-abc', actor: 'ci', scopes: ['subject:admin'] });

    session.expire();

    expect(session.credential()).toBeNull();
    expect(session.actor()).toBeNull();
    expect(session.scopes()).toEqual([]);
  });

  it('replaces the previous session rather than merging into it', () => {
    // Signing in again with fewer scopes must not leave the earlier ones behind.
    session.signIn({ credential: 'admin', actor: 'ops', scopes: ['subject:admin'] });

    session.signIn({ credential: 'reader', actor: 'analyst', scopes: ['subject:read'] });

    expect(session.scopes()).toEqual(['subject:read']);
    expect(session.actor()).toBe('analyst');
  });

  describe('a password an administrator reset (ADR-048)', () => {
    it('is not asked of anybody on a cold start, or of a session that was not told to', () => {
      expect(session.mustChangePassword()).toBe(false);

      session.signIn({ credential: 't', actor: 'ops', scopes: ['subject:admin'] });

      expect(session.mustChangePassword()).toBe(false);
    });

    it('is recorded from a sign-in that says so, and the session holds no scopes', () => {
      // The registry answers a limited session with no scopes. Nothing here grants any.
      session.signIn({ credential: 't', actor: 'ops', scopes: [], mustChangePassword: true });

      expect(session.mustChangePassword()).toBe(true);
      expect(session.isSignedIn()).toBe(true);
      expect(session.canWriteSchemas()).toBe(false);
    });

    it('is recorded from what the registry says about a session it resumed', () => {
      session.observeInstance({
        claimed: true,
        actor: 'ops',
        scopes: [],
        mustChangePassword: true,
      });

      expect(session.mustChangePassword()).toBe(true);
    });

    it('is cleared by the change, which gives the same credential the scopes the registry now grants', () => {
      session.signIn({ credential: 't', actor: 'ops', scopes: [], mustChangePassword: true });

      session.passwordReplaced(['subject:admin', 'org:admin']);

      expect(session.mustChangePassword()).toBe(false);
      expect(session.credential()).toBe('t');
      expect(session.scopes()).toEqual(['subject:admin', 'org:admin']);
      expect(session.canWriteSchemas()).toBe(true);
    });

    it('does not survive the session: signing out of one leaves the next one clean', () => {
      session.signIn({ credential: 't', actor: 'ops', scopes: [], mustChangePassword: true });

      session.expire();
      session.signIn({ credential: 'u', actor: 'ops', scopes: ['subject:read'] });

      expect(session.mustChangePassword()).toBe(false);
    });

    it('is dropped with the session when it expires', () => {
      session.signIn({ credential: 't', actor: 'ops', scopes: [], mustChangePassword: true });

      session.expire();

      expect(session.mustChangePassword()).toBe(false);
    });
  });

  it('can expire a session that was never signed in', () => {
    // The interceptor calls it on any 401, including one answered before a sign-in.
    expect(() => session.expire()).not.toThrow();
    expect(session.credential()).toBeNull();
  });
});
