import { TestBed } from '@angular/core/testing';
import {
  Router,
  UrlTree,
  type ActivatedRouteSnapshot,
  type RouterStateSnapshot,
} from '@angular/router';
import { provideRouter } from '@angular/router';
import { beforeEach, describe, expect, it } from 'vitest';
import type { Scope } from '../../domain/identity/scope';
import { scopeGuard } from './scope-guard';
import { SessionStore } from './session-store';

/**
 * The guard keeps a screen from rendering for someone who cannot use it. It is deliberately
 * *not* the security boundary — the server refuses the same requests with 403 — so what these
 * assert is that the UI agrees with the server rather than that it enforces anything.
 */
describe('scopeGuard', () => {
  let session: InstanceType<typeof SessionStore>;

  const run = (...required: readonly Scope[]) =>
    TestBed.runInInjectionContext(() =>
      scopeGuard(...required)(
        {} as ActivatedRouteSnapshot,
        { url: '/subjects/new' } as RouterStateSnapshot,
      ),
    );

  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideRouter([])] });
    session = TestBed.inject(SessionStore);
  });

  it('lets a caller through when they hold the scope', () => {
    session.signIn({ credential: 't', actor: 'ops', scopes: ['subject:write'] });

    expect(run('subject:write')).toBe(true);
  });

  it('lets a caller through when they hold any one of several', () => {
    session.signIn({ credential: 't', actor: 'ops', scopes: ['subject:admin'] });

    expect(run('subject:write', 'subject:admin')).toBe(true);
  });

  it('refuses a caller who holds a different scope entirely', () => {
    // The case that matters: an admin who can write schemas must not reach an org:admin
    // screen just because they can write something.
    session.signIn({ credential: 't', actor: 'ops', scopes: ['subject:admin'] });

    expect(run('org:admin')).toBeInstanceOf(UrlTree);
  });

  it('sends a signed-out caller to sign in, with somewhere to come back to', () => {
    // A locked door with a key slot, rather than a locked door.
    session.observeInstance({ claimed: true, actor: null, scopes: [] });

    const result = run('subject:write') as UrlTree;
    const router = TestBed.inject(Router);

    expect(router.serializeUrl(result)).toBe('/sign-in?returnTo=%2Fsubjects%2Fnew');
  });

  it('sends a signed-in caller who simply lacks the scope to the read surface', () => {
    // Not to sign-in: they have already said who they are, and offering the sign-in form
    // again suggests the credential is the problem when it is not.
    session.signIn({ credential: 't', actor: 'analyst', scopes: ['subject:read'] });

    const result = run('subject:write') as UrlTree;

    expect(TestBed.inject(Router).serializeUrl(result)).toBe('/subjects');
  });

  it('lets everyone through on an unclaimed instance', () => {
    // The API answers an unauthenticated caller as an owner until somebody creates an
    // account, so gating the UI harder than the server would hide a screen that works.
    session.observeInstance({ claimed: false, actor: null, scopes: [] });

    expect(run('org:admin')).toBe(true);
  });

  it('refuses before the API has answered', () => {
    // `claimed` is null on a cold start whose probe failed. Refusing is the safe direction:
    // a screen that renders and then 403s on submit reads as broken.
    expect(run('subject:write')).toBeInstanceOf(UrlTree);
  });
});
