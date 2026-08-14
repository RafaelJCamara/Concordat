import { computed, inject } from '@angular/core';
import { patchState, signalStore, withComputed, withMethods, withState } from '@ngrx/signals';
import { rxMethod } from '@ngrx/signals/rxjs-interop';
import { catchError, of, pipe, switchMap, tap } from 'rxjs';
import { SessionStore } from '../../../core/auth/session-store';
import { toConcordatError, type ConcordatError } from '../../../core/http/problem-details';
import { AuthApi } from '../data-access/auth-api';

interface SignInState {
  readonly submitting: boolean;
  readonly error: ConcordatError | null;
  /** True once a sign-in or a bootstrap has succeeded, so the page can navigate away. */
  readonly succeeded: boolean;
}

const INITIAL: SignInState = { submitting: false, error: null, succeeded: false };

/**
 * The sign-in screen's state.
 *
 * It writes the credential into {@link SessionStore} and nothing else — the page navigates,
 * the interceptor attaches, and neither of them needs to know how the credential was
 * obtained.
 */
export const SignInStore = signalStore(
  withState<SignInState>(INITIAL),

  withComputed(({ error }) => ({
    /**
     * What to put in front of the user.
     *
     * The API answers one message for a wrong address, a wrong password, a disabled account
     * and a missing membership, and this passes it through unchanged. Helpfully expanding it
     * client-side — "no such account" — would hand back exactly the account enumeration the
     * server went to trouble to withhold.
     */
    message: computed(() => error()?.detail ?? null),
  })),

  withMethods((store) => {
    const api = inject(AuthApi);
    const session = inject(SessionStore);

    return {
      /** Exchanges an email and password for a credential. */
      signIn: rxMethod<{ email: string; password: string }>(
        pipe(
          tap(() => patchState(store, { submitting: true, error: null })),
          switchMap(({ email, password }) =>
            api.signIn(email, password).pipe(
              tap((result) => {
                session.signIn({
                  credential: result.credential,
                  actor: result.actor,
                  scopes: result.scopes,
                });

                patchState(store, { submitting: false, succeeded: true });
              }),
              catchError((error: unknown) => {
                patchState(store, { submitting: false, error: toConcordatError(error) });
                return of(null);
              }),
            ),
          ),
        ),
      ),

      /**
       * Claims an unclaimed instance and signs the new owner straight in.
       *
       * Two calls rather than one because bootstrap deliberately returns no credential — it
       * creates an account, and creating an account is not the same act as authenticating as
       * one. Chaining them here spares the user typing the password twice.
       */
      bootstrap: rxMethod<{ email: string; password: string; displayName?: string }>(
        pipe(
          tap(() => patchState(store, { submitting: true, error: null })),
          switchMap(({ email, password, displayName }) =>
            api.bootstrap(email, password, displayName).pipe(
              switchMap(() => api.signIn(email, password)),
              tap((result) => {
                session.signIn({
                  credential: result.credential,
                  actor: result.actor,
                  scopes: result.scopes,
                });

                patchState(store, { submitting: false, succeeded: true });
              }),
              catchError((error: unknown) => {
                patchState(store, { submitting: false, error: toConcordatError(error) });
                return of(null);
              }),
            ),
          ),
        ),
      ),
    };
  }),
);
