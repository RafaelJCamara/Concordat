import { TestBed } from '@angular/core/testing';
import { beforeEach, describe, expect, it } from 'vitest';
import { SessionStore } from './session-store';

// The store is three fields and two methods. What is worth testing is the shape of the
// signed-out state, because M4.2 will hang `canWriteSchemas` off `scopes` and everything
// that follows depends on a dropped session being genuinely empty rather than merely
// missing its credential.

describe('SessionStore', () => {
  let session: InstanceType<typeof SessionStore>;

  beforeEach(() => {
    TestBed.configureTestingModule({});
    session = TestBed.inject(SessionStore);
  });

  it('is signed out on a cold start', () => {
    // There is no sign-in flow yet and the self-hosted API does not require a credential,
    // so the interceptor sends nothing and the registry serves the request. That is the
    // correct single-user behaviour, not an oversight.
    expect(session.credential()).toBeNull();
    expect(session.actor()).toBeNull();
    expect(session.scopes()).toEqual([]);
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
    // session that kept its scopes would keep rendering the write affordances M4.2 gates on
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

  it('can expire a session that was never signed in', () => {
    // The interceptor calls it on any 401, including one answered before a sign-in.
    expect(() => session.expire()).not.toThrow();
    expect(session.credential()).toBeNull();
  });
});
