import { inject } from '@angular/core';
import { Router, type CanActivateFn } from '@angular/router';
import { grants, SCHEMA_WRITE_SCOPES, type Scope } from '../../domain/identity/scope';
import { SessionStore } from './session-store';

/**
 * Keeps a route from rendering for someone who lacks the scopes it needs (ADR-018).
 *
 * **This is not authorization.** The server checks the same scopes on every mutating
 * endpoint and answers 403 regardless of caller. What this prevents is a screen that
 * renders, lets someone fill in a form, and then fails on submit — which reads as a broken
 * product rather than as a permission they do not have.
 *
 * Redirects rather than showing a 403 page. Somebody who followed a link from an incident
 * channel to a route they cannot use is better served by the read surface they *can* use
 * than by a dead end, and the `returnTo` parameter means a sign-in brings them back.
 */
export function scopeGuard(...required: readonly Scope[]): CanActivateFn {
  return (_route, state) => {
    const session = inject(SessionStore);
    const router = inject(Router);

    // An unclaimed instance grants owner scopes to an unauthenticated caller server-side
    // (M8.2), so gating the UI harder than the API would hide a screen that works. Checked
    // against `claimed` rather than `canWriteSchemas`, which answers a different question and
    // would let a schema writer through a route that wanted org:admin.
    if (session.claimed() === false || grants(session.scopes(), required)) {
      return true;
    }

    // Signed out on a claimed instance: they may well be entitled to this, they simply have
    // not said who they are. Sending them to sign-in with somewhere to come back to is the
    // difference between a locked door and a locked door with a key slot.
    if (session.needsSignIn()) {
      return router.createUrlTree(['/sign-in'], { queryParams: { returnTo: state.url } });
    }

    return router.createUrlTree(['/subjects']);
  };
}

/**
 * Keeps a read route from rendering for someone who has not signed in.
 *
 * Reading needs no particular scope -- every authenticated caller may read (`scope.ts`) --
 * but it does need a caller: the API refuses an anonymous GET by default (H1). A route with
 * no guard at all would still render here, then fail every request the page makes and report
 * it as a broken screen instead of the sign-in prompt it actually needs.
 *
 * Checked against `isSignedIn` rather than `!needsSignIn`, which agree everywhere except the
 * moment `claimed` has not resolved yet (`null`, before `/v1/auth/status` answers):
 * `needsSignIn` reads `null` as "no" there, `isSignedIn` reads it as "not proven yet" — and
 * this guard protects the read surface itself, so it has nowhere safe to fall back to the way
 * `scopeGuard` falls back to `/subjects`. Refusing until proven otherwise is the same
 * direction `scopeGuard`'s own "refuses before the API has answered" case takes.
 */
export const signedInGuard: CanActivateFn = (_route, state) => {
  const session = inject(SessionStore);
  const router = inject(Router);

  // An unclaimed instance answers every request as owner server-side (M8.2), so there is
  // nothing to gate here either.
  if (session.claimed() === false || session.isSignedIn()) {
    return true;
  }

  return router.createUrlTree(['/sign-in'], { queryParams: { returnTo: state.url } });
};

/**
 * The guard for routes that change a contract (ADR-018).
 *
 * Named here rather than spelled `scopeGuard(...SCHEMA_WRITE_SCOPES)` at each route, for two
 * reasons. The route table is the composition root and may not import from `domain/` — the
 * boundaries rule says so, and the point is that the wiring layer holds no policy of its own.
 * And a scope list repeated per route is a list that eventually disagrees with itself: this
 * is the same constant `*cdIfScope` reads on the affordance, so the button and the route it
 * points at cannot drift apart.
 */
export const schemaWriteGuard: CanActivateFn = scopeGuard(...SCHEMA_WRITE_SCOPES);
