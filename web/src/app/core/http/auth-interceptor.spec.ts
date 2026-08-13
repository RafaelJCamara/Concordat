import {
  HttpClient,
  type HttpInterceptorFn,
  provideHttpClient,
  withInterceptors,
} from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { afterEach, describe, expect, it } from 'vitest';
import { SessionStore } from '../auth/session-store';
import { provideConcordatConfig } from '../config/app-config';
import { authInterceptor } from './auth-interceptor';
import { problemDetailsInterceptor } from './problem-details-interceptor';

// Two separate concerns live in this interceptor and both are security-shaped: which
// requests receive the credential, and what happens to the session when one is rejected.

const API = '/v1/environments/dev/subjects';
const ELSEWHERE = 'https://cdn.example.com/avatars/ci.png';

/**
 * Builds the chain in the order `app.config.ts` uses.
 *
 * `problemDetailsInterceptor` is registered last on purpose: Angular runs the response
 * chain in reverse, so registering it last makes it the first to see an error, and the auth
 * interceptor therefore sees a `ConcordatError`. The `interceptors` parameter exists so one
 * test below can register them the other way round and show that the order is load-bearing.
 */
function setUp(interceptors: HttpInterceptorFn[] = [authInterceptor, problemDetailsInterceptor]) {
  TestBed.configureTestingModule({
    providers: [
      provideConcordatConfig(),
      provideHttpClient(withInterceptors(interceptors)),
      provideHttpClientTesting(),
    ],
  });

  return {
    http: TestBed.inject(HttpClient),
    backend: TestBed.inject(HttpTestingController),
    session: TestBed.inject(SessionStore),
  };
}

function signedIn() {
  const context = setUp();
  context.session.signIn({ credential: 'token-abc', actor: 'ci', scopes: ['subject:read'] });
  return context;
}

describe('authInterceptor', () => {
  afterEach(() => TestBed.inject(HttpTestingController).verify());

  describe('attaching the credential', () => {
    it('sends the bearer token to the registry', () => {
      const { http, backend } = signedIn();
      http.get(API).subscribe();

      const request = backend.expectOne(API).request;

      expect(request.headers.get('Authorization')).toBe('Bearer token-abc');
    });

    it('sends no Authorization header at all when signed out', () => {
      // Not an empty `Bearer`. The single-user self-hosted profile has no sign-in, and an
      // empty credential reads to a server as a malformed one rather than an anonymous call.
      const { http, backend } = setUp();
      http.get(API).subscribe();

      const request = backend.expectOne(API).request;

      expect(request.headers.has('Authorization')).toBe(false);
    });

    it('does not send the credential anywhere but the registry', () => {
      // The reason the interceptor gates on `isApiUrl` rather than stamping every request.
      // Without it, one avatar, doc link or map tile going through `HttpClient` hands a
      // third-party host a working registry token.
      const { http, backend } = signedIn();
      http.get(ELSEWHERE).subscribe();

      const request = backend.expectOne(ELSEWHERE).request;

      expect(request.headers.has('Authorization')).toBe(false);
    });
  });

  describe('when the registry rejects the credential', () => {
    it('drops the session on 401', () => {
      // Keeping a credential the registry has already refused replays it on every open
      // panel's next poll, turning one expiry into a burst of failures.
      const { http, backend, session } = signedIn();
      http.get(API).subscribe({ error: () => undefined });

      backend.expectOne(API).flush(null, { status: 401, statusText: 'Unauthorized' });

      expect(session.credential()).toBeNull();
      expect(session.scopes()).toEqual([]);
    });

    it('keeps the session on 403', () => {
      // A 403 means the credential is fine and the scope is not (ADR-018). Signing the user
      // out would hide the read surface they are entitled to, and would look to them like a
      // random logout on the one click that was refused.
      const { http, backend, session } = signedIn();
      http.get(API).subscribe({ error: () => undefined });

      backend
        .expectOne(API)
        .flush(
          { detail: 'This action needs subject:write.', concordatCode: 'insufficient_scope' },
          { status: 403, statusText: 'Forbidden' },
        );

      expect(session.credential()).toBe('token-abc');
    });

    it('keeps the session when the 401 came from somewhere else', () => {
      // A third-party host refusing a request it was never given a credential for says
      // nothing about the registry session. The `isApiUrl` gate returns before the error is
      // ever inspected, which is what makes this true.
      const { http, backend, session } = signedIn();
      http.get(ELSEWHERE).subscribe({ error: () => undefined });

      backend.expectOne(ELSEWHERE).flush(null, { status: 401, statusText: 'Unauthorized' });

      expect(session.credential()).toBe('token-abc');
    });

    it('keeps the session when the registry simply could not be reached', () => {
      // Being offline is not the registry saying no.
      const { http, backend, session } = signedIn();
      http.get(API).subscribe({ error: () => undefined });

      backend.expectOne(API).error(new ProgressEvent('error'));

      expect(session.credential()).toBe('token-abc');
    });
  });

  it('stops recognising a 401 if the interceptors are registered the other way round', () => {
    // Not a test of desired behaviour — a test of why the order in `app.config.ts` is
    // commented. Registered this way, `problemDetailsInterceptor` converts the failure
    // *after* the auth interceptor has already looked at it, so `isConcordatError` is false
    // and the sign-out silently never happens. Nothing else in the app would fail.
    const { http, backend, session } = (() => {
      const context = setUp([problemDetailsInterceptor, authInterceptor]);
      context.session.signIn({ credential: 'token-abc', actor: 'ci', scopes: [] });
      return context;
    })();

    http.get(API).subscribe({ error: () => undefined });
    backend.expectOne(API).flush(null, { status: 401, statusText: 'Unauthorized' });

    expect(session.credential()).toBe('token-abc');
  });
});
