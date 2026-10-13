import { computed, inject } from '@angular/core';
import { patchState, signalStore, withComputed, withMethods, withState } from '@ngrx/signals';
import { rxMethod } from '@ngrx/signals/rxjs-interop';
import { catchError, exhaustMap, mergeMap, of, pipe, switchMap, tap } from 'rxjs';
import { toConcordatError, type ConcordatError } from '../../../core/http/problem-details';
import type { ApiKey, IssuedApiKey } from '../../../domain/identity/member';
import { IdentityApi, type NewApiKey } from '../data-access/identity-api';

interface ApiKeysState {
  readonly keys: readonly ApiKey[];
  readonly loading: boolean;
  readonly loaded: boolean;
  readonly error: ConcordatError | null;
  readonly issuing: boolean;
  readonly issueError: ConcordatError | null;
  /**
   * The key just issued, with its one-time secret, until the reader dismisses it.
   *
   * <b>The only place a secret is ever held.</b> It is in memory, it is cleared by `dismiss()`
   * and by anything that replaces it, and it is never written into the list: the registry
   * stores only a hash and cannot show it again, so this is the single copy a person will ever
   * be handed.
   */
  readonly issued: IssuedApiKey | null;
  /** Which keys have a revocation in flight. */
  readonly revoking: readonly string[];
  readonly revokeErrors: Readonly<Record<string, ConcordatError>>;
}

const INITIAL: ApiKeysState = {
  keys: [],
  loading: false,
  loaded: false,
  error: null,
  issuing: false,
  issueError: null,
  issued: null,
  revoking: [],
  revokeErrors: {},
};

/**
 * The organisation's API keys, issuing one and revoking one.
 *
 * <b>Newest first</b>, because the key somebody is looking for is nearly always the one they
 * just made or one of the last few. Revoked keys stay in the list, marked, rather than
 * vanishing: a key that stopped working is evidence about why a pipeline did.
 *
 * <b>Issuing uses `exhaustMap`</b>, for the reason adding a member does — and with a sharper
 * edge: a second issue request is a second secret, and an issuer who clicked twice would end up
 * holding one key they did not mean to create and cannot find the secret for.
 *
 * <b>Revocation applies the registry's answer</b>, so the row shows the timestamp the registry
 * recorded rather than the browser's clock.
 */
export const ApiKeysStore = signalStore(
  withState<ApiKeysState>(INITIAL),
  withComputed(({ loaded, loading, keys }) => ({
    isEmpty: computed(() => loaded() && !loading() && keys().length === 0),
  })),
  withMethods((store) => {
    const api = inject(IdentityApi);

    return {
      load: rxMethod<void>(
        pipe(
          tap(() => patchState(store, { loading: true, error: null })),
          switchMap(() =>
            api.listApiKeys().pipe(
              tap((keys) =>
                patchState(store, { keys: newestFirst(keys), loading: false, loaded: true }),
              ),
              catchError((error: unknown) => {
                patchState(store, {
                  keys: [],
                  loading: false,
                  loaded: true,
                  error: toConcordatError(error),
                });
                return of(null);
              }),
            ),
          ),
        ),
      ),

      issue: rxMethod<NewApiKey>(
        pipe(
          // Clears the previous secret the moment a new request starts, so two are never on
          // screen and the old one is not left lying around while the new one is awaited.
          tap(() => patchState(store, { issuing: true, issueError: null, issued: null })),
          exhaustMap((request) =>
            api.issueApiKey(request).pipe(
              tap((issued) =>
                patchState(store, {
                  keys: newestFirst([...store.keys(), issued.key]),
                  issuing: false,
                  issued,
                }),
              ),
              catchError((error: unknown) => {
                patchState(store, { issuing: false, issueError: toConcordatError(error) });
                return of(null);
              }),
            ),
          ),
        ),
      ),

      /** Forgets the secret. After this the only record of it is wherever the person put it. */
      dismiss(): void {
        patchState(store, { issued: null });
      },

      revoke: rxMethod<string>(
        pipe(
          mergeMap((id) => {
            if (store.revoking().includes(id)) {
              return of(null);
            }

            const { [id]: _cleared, ...remaining } = store.revokeErrors();
            patchState(store, { revoking: [...store.revoking(), id], revokeErrors: remaining });

            return api.revokeApiKey(id).pipe(
              tap((revoked) =>
                patchState(store, {
                  keys: store.keys().map((k) => (k.id === revoked.id ? revoked : k)),
                  revoking: store.revoking().filter((x) => x !== id),
                }),
              ),
              catchError((error: unknown) => {
                patchState(store, {
                  revoking: store.revoking().filter((x) => x !== id),
                  revokeErrors: { ...store.revokeErrors(), [id]: toConcordatError(error) },
                });
                return of(null);
              }),
            );
          }),
        ),
      ),
    };
  }),
);

function newestFirst(keys: readonly ApiKey[]): readonly ApiKey[] {
  return [...keys].sort((a, b) => b.createdAt.getTime() - a.createdAt.getTime());
}
