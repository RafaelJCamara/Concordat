import type { Route } from '@angular/router';
import { describe, expect, it } from 'vitest';
import { routes } from './app.routes';

/**
 * Structural tests over the route table.
 *
 * The same shape as `EveryMutatingRouteDeclaresAScope` on the API side, and for the same
 * reason: both of these are properties nobody would think to re-check when adding the
 * fifteenth route, and both fail in ways that look like something else. A shadowed route
 * reads as a missing page; an unguarded write route reads as a working screen until submit.
 */

/** Route paths that write, and must therefore be guarded. */
const WRITE_ROUTE_MARKERS = ['/new', '/edit'];

function paths(table: readonly Route[]): readonly string[] {
  return table.map((route) => route.path ?? '');
}

describe('the route table', () => {
  it('matches `versions/new` before `versions/:ordinal`', () => {
    /*
     * The router matches in order, so with these two swapped, `/subjects/x/versions/new`
     * matches the *detail* screen with `ordinal: 'new'` — which resolves to no version and
     * renders "no version 'new' on subject x". A 404 for the one route that is not missing,
     * and it would be reported as the register button being broken.
     */
    const table = paths(routes);
    const literal = table.indexOf('subjects/:name/versions/new');
    const parameterised = table.indexOf('subjects/:name/versions/:ordinal');

    expect(literal).toBeGreaterThanOrEqual(0);
    expect(parameterised).toBeGreaterThanOrEqual(0);
    expect(literal).toBeLessThan(parameterised);
  });

  it('guards every write route', () => {
    // ADR-018. The server refuses regardless, so this is not the security boundary — what it
    // prevents is a screen that renders, lets someone fill in a form and then fails on
    // submit, which reads as a broken product rather than as a permission they do not have.
    const unguarded = routes.filter(
      (route) =>
        WRITE_ROUTE_MARKERS.some((marker) => (route.path ?? '').endsWith(marker)) &&
        (route.canActivate ?? []).length === 0,
    );

    expect(unguarded.map((route) => route.path)).toEqual([]);
  });

  it('keeps the wildcard last', () => {
    // `**` matches everything, so a route after it is unreachable — and unreachable in a way
    // that looks exactly like the route being broken.
    const table = paths(routes);

    expect(table.indexOf('**')).toBe(table.length - 1);
  });

  it('lazy-loads every screen', () => {
    // A screen nobody opened is a chunk nobody downloaded, which is what keeps Monaco (M4.4)
    // off the critical path for someone who only came to read a subject list.
    const eager = routes.filter(
      (route) => route.path !== '**' && route.loadComponent === undefined && !route.redirectTo,
    );

    expect(eager.map((route) => route.path)).toEqual([]);
  });

  it('titles every screen, so a browser tab and a history entry are readable', () => {
    const untitled = routes.filter(
      (route) => route.loadComponent !== undefined && route.title === undefined,
    );

    expect(untitled.map((route) => route.path)).toEqual([]);
  });
});
