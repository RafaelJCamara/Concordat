import { inject } from '@angular/core';
import { patchState, signalStore, withMethods, withState } from '@ngrx/signals';
import { rxMethod } from '@ngrx/signals/rxjs-interop';
import { catchError, exhaustMap, map, of, pipe, switchMap, tap, type Observable } from 'rxjs';
import { SessionApi } from '../../../core/auth/session-api';
import { SessionStore } from '../../../core/auth/session-store';
import { ConcordatError, toConcordatError } from '../../../core/http/problem-details';
import { IdentityApi, type PasswordChanged } from '../data-access/identity-api';

interface AccountState {
  readonly submitting: boolean;
  readonly error: ConcordatError | null;
  /** What the last change did, from the registry's answer, or null. */
  readonly changed: PasswordChanged | null;
}

const INITIAL: AccountState = { submitting: false, error: null, changed: null };

/**
 * Changing the signed-in person's own password (ADR-048).
 *
 * <b>Both passwords are arguments to the call and nothing here holds either.</b> State is whether a
 * request is in flight, what the registry refused with, and what it said it did; nothing in it could
 * show a password back.
 *
 * <b>A wrong current password is shown, and does not sign anybody out.</b> The registry answers it as
 * it answers a wrong sign-in, `401 unauthenticated`, on a request made with a credential that is
 * perfectly good, and the auth interceptor is told not to read that as an expiry
 * (`REFUSAL_IS_NOT_EXPIRY`). That leaves one question the interceptor would have answered for free:
 * whether the credential is in fact dead. So a 401 is followed by asking the registry who it thinks
 * the caller is, and only "nobody" ends the session.
 *
 * <b>Replacing a temporary password is not finished until the registry has said what the credential
 * holds.</b> The same credential was limited to this one action on every request; it holds its scopes
 * again the moment the password changes, and which ones is the registry's to say. The store reads them
 * and only then lets the person on, so a change whose aftermath could not be read is reported as
 * exactly that and not as a success the app cannot act on.
 */
export const AccountStore = signalStore(
  withState<AccountState>(INITIAL),

  withMethods((store) => {
    const identity = inject(IdentityApi);
    const sessions = inject(SessionApi);
    const session = inject(SessionStore);

    /** Asks whether the credential is still good, and ends the session if it is not. */
    const probe = (): Observable<null> =>
      sessions.status().pipe(
        tap((status) => {
          if (!status.authenticated) {
            session.expire();
          }
        }),
        map(() => null),
        // Offline is not the registry saying no: the session stays, as the interceptor keeps it.
        catchError(() => of(null)),
      );

    const finishedWithoutAccess = (reason: ConcordatError) =>
      patchState(store, { submitting: false, error: reason });

    return {
      change: rxMethod<{ current: string; next: string }>(
        pipe(
          tap(() => patchState(store, { submitting: true, error: null, changed: null })),
          exhaustMap(({ current, next }) =>
            identity.changePassword(current, next).pipe(
              switchMap((changed) => {
                if (!session.mustChangePassword()) {
                  patchState(store, { submitting: false, changed });
                  return of(null);
                }

                return sessions.status().pipe(
                  tap((status) => {
                    if (status.mustChangePassword) {
                      finishedWithoutAccess(
                        new ConcordatError({
                          status: 0,
                          code: 'registry_refused',
                          detail:
                            'Your password was changed, but the registry still says it must be ' +
                            'replaced. Sign out and sign in again.',
                        }),
                      );
                      return;
                    }

                    session.passwordReplaced(status.scopes);
                    patchState(store, { submitting: false, changed });
                  }),
                  map(() => null),
                  catchError((error: unknown) => {
                    const failure = toConcordatError(error);

                    finishedWithoutAccess(
                      new ConcordatError({
                        status: failure.status,
                        code: failure.code,
                        detail:
                          'Your password was changed, but this page could not read what you may ' +
                          'do now. Reload it to continue.',
                        cause: error,
                      }),
                    );

                    return of(null);
                  }),
                );
              }),
              catchError((error: unknown) => {
                const failure = toConcordatError(error);

                patchState(store, { submitting: false, error: failure });

                return failure.status === 401 ? probe() : of(null);
              }),
            ),
          ),
        ),
      ),
    };
  }),
);
