import { inject, provideAppInitializer } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { catchError, of, switchMap, tap } from 'rxjs';
import { CONCORDAT_CONFIG } from '../config/app-config';
import { apiRoot } from '../http/api-url';
import { SCOPES, type Scope } from '../../domain/identity/scope';
import { SessionApi } from './session-api';
import { SessionStore } from './session-store';

interface AuthStatusDto {
  readonly claimed: boolean;
  readonly authenticated: boolean;
  readonly actor: string | null;
  readonly scopes: readonly string[];
}

function known(scopes: readonly string[]): Scope[] {
  return scopes.filter((scope): scope is Scope => (SCOPES as readonly string[]).includes(scope));
}

/**
 * Recovers the session, then asks the API who we are, before the first screen renders.
 *
 * <b>`/auth/resume` first (decision 26).</b> The credential is held in memory only, so a reload
 * has nothing left — but the browser still holds an httpOnly cookie that no script can read, and
 * this is the one route that trades it back for a credential. Without this step F5 signs you
 * out, which is irritating enough that somebody eventually "fixes" it by putting the token in
 * `localStorage`, which is the XSS hole ADR-006 refused.
 *
 * <b>Without this, the app cannot tell "signed out" from "nobody has signed up".</b> An
 * unclaimed instance answers every request as an owner (M8.2), so a cold start with no probe
 * would either hide every write affordance on a registry that would accept the write, or show
 * every affordance on one that will refuse it. Both read as a broken product.
 *
 * <b>A failure is not fatal.</b> The registry being unreachable at boot is a normal condition
 * — a container starting alongside its database — and refusing to render leaves the user with
 * nothing at all. The app starts signed out, which shows the read surface and no write
 * affordances, and the first successful request corrects it.
 *
 * It calls `HttpClient` directly rather than through the identity feature's `AuthApi`: this is
 * composition-root wiring in `core/`, and `core` may not import a feature. Duplicating four
 * field names is cheaper than inverting that boundary.
 */
export function provideSessionInitializer() {
  return provideAppInitializer(() => {
    const http = inject(HttpClient);
    const config = inject(CONCORDAT_CONFIG);
    const session = inject(SessionStore);
    const sessions = inject(SessionApi);

    const root = apiRoot(config);

    const status$ = http.get<AuthStatusDto>(`${root}/auth/status`).pipe(
      tap((status) =>
        session.observeInstance({
          claimed: status.claimed,
          actor: status.actor,
          scopes: known(status.scopes),
        }),
      ),
      catchError(() => of(null)),
    );

    return sessions.resume().pipe(
      tap((resumed) => {
        // null is "nothing to resume", the ordinary signed-out answer. Only a genuine
        // failure reaches catchError below.
        if (resumed) {
          session.signIn(resumed);
        }
      }),
      // Still asked, because signing in does not answer whether the INSTANCE is claimed --
      // and that is what the unclaimed banner and the scope guard read.
      switchMap(() => status$),
      // No cookie, an expired one, or a registry that cannot be reached. All three mean the
      // same thing here: carry on and let /auth/status say what it can.
      catchError(() => status$),
    );
  });
}
