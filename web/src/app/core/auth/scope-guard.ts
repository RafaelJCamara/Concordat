import { inject } from '@angular/core';
import { Router, type CanActivateFn } from '@angular/router';
import {
  grants,
  ORG_ADMIN_SCOPES,
  SCHEMA_WRITE_SCOPES,
  type Scope,
} from '../../domain/identity/scope';
import { SessionStore } from './session-store';

/** Where a session goes while it must replace a password an administrator set (ADR-048). */
const ACCOUNT_ROUTE = '/settings/account';

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

    // A session that must replace a temporary password holds no scopes whatever its role, and the
    // fall-through below would send it to `/subjects`, which sends it here a hop later. Said once, at
    // the place that knows why (ADR-048).
    if (session.mustChangePassword()) {
      return router.createUrlTree([ACCOUNT_ROUTE]);
    }

    // An instance that answers an unclaimed caller as an owner (the opt-in, ADR-047) grants those
    // scopes in `/v1/auth/status`, so this needs no special case for it: holding the scopes is
    // the whole question. A closed unclaimed instance grants none and falls to the sign-in below.
    if (grants(session.scopes(), required)) {
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

  // Every read this person's session makes would answer 403 `password_change_required`, so a screen
  // that rendered would fail everything it asked for and look broken. The account screen is the one
  // place that works (ADR-048).
  if (session.mustChangePassword()) {
    return router.createUrlTree([ACCOUNT_ROUTE]);
  }

  // Nothing to gate on an instance that answers an unclaimed caller as an owner (the opt-in,
  // ADR-047). A closed one, claimed or not, sends a visitor to sign-in, which is the setup screen
  // while nobody has an account.
  if (session.openInstance() || session.isSignedIn()) {
    return true;
  }

  return router.createUrlTree(['/sign-in'], { queryParams: { returnTo: state.url } });
};

/**
 * The guard for the account screen: anybody who is signed in, and nobody else.
 *
 * <b>It is the one guard that lets a session through that must change its password</b>, because that
 * session is exactly who the screen is for, and it asks for no scope, because everybody owns their
 * own password: a reader changes theirs. An instance that answers a caller with no credential as an
 * owner (the opt-in, ADR-047) has nobody signed in and so no password to change, and sends them to
 * sign-in like any other signed-out visitor.
 */
export const accountGuard: CanActivateFn = (_route, state) => {
  const session = inject(SessionStore);
  const router = inject(Router);

  return session.isSignedIn()
    ? true
    : router.createUrlTree(['/sign-in'], { queryParams: { returnTo: state.url } });
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

/**
 * The guard for the members and API-key screens.
 *
 * Unlike the schema write guard this gates *reading*: the whole endpoint group requires
 * `org:admin`, so there is no read-only version of these screens to show anyone else, and a
 * screen that rendered would fail every request it made. Named here for the same reason
 * `schemaWriteGuard` is — the route table may not import from `domain/`, and the tab that
 * points at the route reads the same constant, so the two cannot drift.
 */
export const orgAdminGuard: CanActivateFn = scopeGuard(...ORG_ADMIN_SCOPES);
