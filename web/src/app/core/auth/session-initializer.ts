import { inject, provideAppInitializer } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { catchError, of, tap } from 'rxjs';
import { CONCORDAT_CONFIG } from '../config/app-config';
import { apiRoot } from '../http/api-url';
import { SCOPES, type Scope } from '../../domain/identity/scope';
import { SessionStore } from './session-store';

interface AuthStatusDto {
  readonly claimed: boolean;
  readonly authenticated: boolean;
  readonly actor: string | null;
  readonly scopes: readonly string[];
}

/**
 * Asks the API who we are before the first screen renders.
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

    return http.get<AuthStatusDto>(`${apiRoot(config)}/auth/status`).pipe(
      tap((status) =>
        session.observeInstance({
          claimed: status.claimed,
          actor: status.actor,
          scopes: status.scopes.filter((scope): scope is Scope =>
            (SCOPES as readonly string[]).includes(scope),
          ),
        }),
      ),
      catchError(() => of(null)),
    );
  });
}
