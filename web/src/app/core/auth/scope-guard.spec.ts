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
import { accountGuard, scopeGuard, signedInGuard } from './scope-guard';
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

  it('lets a caller through on an unclaimed instance that answers them as an owner', () => {
    // The opt-in (ADR-047): the scopes it granted are the whole question, as on any instance.
    session.observeInstance({
      claimed: false,
      actor: 'unclaimed-instance',
      scopes: ['subject:write', 'org:admin'],
    });

    expect(run('org:admin')).toBe(true);
  });

  it('sends a visitor to the setup screen on an unclaimed instance that is closed', () => {
    session.observeInstance({ claimed: false, actor: null, scopes: [] });

    const result = run('subject:write') as UrlTree;

    expect(TestBed.inject(Router).serializeUrl(result)).toBe('/sign-in?returnTo=%2Fsubjects%2Fnew');
  });

  it('refuses before the API has answered', () => {
    // `claimed` is null on a cold start whose probe failed. Refusing is the safe direction:
    // a screen that renders and then 403s on submit reads as broken.
    expect(run('subject:write')).toBeInstanceOf(UrlTree);
  });

  it('sends a session that must change its password to the one screen that works (ADR-048)', () => {
    // Not to `/subjects`, which would send it on to the same place a hop later, and not to
    // sign-in, which would suggest the credential is the problem. Whatever scopes it holds.
    session.signIn({
      credential: 't',
      actor: 'ops',
      scopes: ['org:admin'],
      mustChangePassword: true,
    });

    const result = run('org:admin') as UrlTree;

    expect(TestBed.inject(Router).serializeUrl(result)).toBe('/settings/account');
  });
});

describe('signedInGuard', () => {
  let session: InstanceType<typeof SessionStore>;

  const run = () =>
    TestBed.runInInjectionContext(() =>
      signedInGuard({} as ActivatedRouteSnapshot, { url: '/subjects' } as RouterStateSnapshot),
    );

  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideRouter([])] });
    session = TestBed.inject(SessionStore);
  });

  it('lets a signed-in caller through, no matter what scopes they hold', () => {
    // Reading needs no particular scope (scope.ts) -- holding none at all is the case that
    // matters here, since a reader's whole point is not writing anything.
    session.signIn({ credential: 't', actor: 'reader', scopes: [] });

    expect(run()).toBe(true);
  });

  it('lets everyone through on an unclaimed instance that answers its caller as an owner', () => {
    session.observeInstance({
      claimed: false,
      actor: 'unclaimed-instance',
      scopes: ['subject:write'],
    });

    expect(run()).toBe(true);
  });

  it('sends a visitor to the setup screen on an unclaimed instance that is closed', () => {
    // A fresh install (ADR-047): there is nothing to read until somebody sets it up.
    session.observeInstance({ claimed: false, actor: null, scopes: [] });

    const result = run() as UrlTree;

    expect(TestBed.inject(Router).serializeUrl(result)).toBe('/sign-in?returnTo=%2Fsubjects');
  });

  it('sends a signed-out caller to sign in, with somewhere to come back to', () => {
    session.observeInstance({ claimed: true, actor: null, scopes: [] });

    const result = run() as UrlTree;

    expect(TestBed.inject(Router).serializeUrl(result)).toBe('/sign-in?returnTo=%2Fsubjects');
  });

  it('refuses before the API has answered, rather than flashing the read surface first', () => {
    // `claimed` is null on a cold start. This guard protects the read surface itself, so
    // unlike scopeGuard it has no `/subjects` to fall back to -- refusing is the only safe
    // direction, not just the preferred one.
    expect(run()).toBeInstanceOf(UrlTree);
  });

  it('sends a session that must change its password to the account screen (ADR-048)', () => {
    // Every read would answer 403 `password_change_required`, so a screen that rendered would
    // fail everything it asked for and look broken.
    session.signIn({ credential: 't', actor: 'ops', scopes: [], mustChangePassword: true });

    const result = run() as UrlTree;

    expect(TestBed.inject(Router).serializeUrl(result)).toBe('/settings/account');
  });
});

describe('accountGuard', () => {
  let session: InstanceType<typeof SessionStore>;

  const run = () =>
    TestBed.runInInjectionContext(() =>
      accountGuard(
        {} as ActivatedRouteSnapshot,
        { url: '/settings/account' } as RouterStateSnapshot,
      ),
    );

  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideRouter([])] });
    session = TestBed.inject(SessionStore);
  });

  it('lets any signed-in person through, holding no scope at all', () => {
    // Everyone owns their own password: a Reader changes it with no scope.
    session.signIn({ credential: 't', actor: 'reader', scopes: [] });

    expect(run()).toBe(true);
  });

  it('lets through the session that must change its password, which is the one it exists for', () => {
    session.signIn({ credential: 't', actor: 'ops', scopes: [], mustChangePassword: true });

    expect(run()).toBe(true);
  });

  it('sends a signed-out visitor to sign in, with somewhere to come back to', () => {
    session.observeInstance({ claimed: true, actor: null, scopes: [] });

    const result = run() as UrlTree;

    expect(TestBed.inject(Router).serializeUrl(result)).toBe(
      '/sign-in?returnTo=%2Fsettings%2Faccount',
    );
  });

  it('has no account to offer a visitor to an instance that answers everyone as its owner', () => {
    // The opt-in (ADR-047): nobody is signed in, so there is no password to change.
    session.observeInstance({
      claimed: false,
      actor: 'unclaimed-instance',
      scopes: ['org:admin'],
    });

    expect(run()).toBeInstanceOf(UrlTree);
  });
});
