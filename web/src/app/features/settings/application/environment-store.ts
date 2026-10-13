import { inject } from '@angular/core';
import { patchState, signalStore, withMethods, withState } from '@ngrx/signals';
import { rxMethod } from '@ngrx/signals/rxjs-interop';
import { catchError, mergeMap, of, pipe, switchMap, tap, type Observable } from 'rxjs';
import { ActiveEnvironmentStore } from '../../../core/config/active-environment-store';
import { toConcordatError, type ConcordatError } from '../../../core/http/problem-details';
import type { Environment, EnvironmentChanges } from '../../../domain/environments/environment';
import { EnvironmentsApi } from '../data-access/environments-api';

/**
 * What the screens can ask of an environment.
 *
 * A change to the environment's own settings, adding a broker, and four things done to one
 * broker — each addressed by a key so a refusal can be kept next to the control that caused it.
 */
export type EnvironmentAction =
  | 'settings'
  | 'add-broker'
  | `check:${string}`
  | `credentials:${string}`
  | `remove-credentials:${string}`
  | `remove:${string}`;

interface EnvironmentState {
  readonly environment: Environment | null;
  readonly loading: boolean;
  readonly error: ConcordatError | null;
  /** Which change is in flight, or null. One at a time: every one rewrites the same environment. */
  readonly saving: EnvironmentAction | null;
  /** Why the last attempt at each action was refused, apart from `error` so the environment stays on screen. */
  readonly actionErrors: Readonly<Record<string, ConcordatError>>;
  /** What last succeeded, so the screen can say so and a form knows when to clear itself. */
  readonly lastSaved: EnvironmentAction | null;
}

const INITIAL: EnvironmentState = {
  environment: null,
  loading: false,
  error: null,
  saving: null,
  actionErrors: {},
  lastSaved: null,
};

/**
 * The active environment's settings and its brokers.
 *
 * <b>One store for two screens, because they are two views of one document.</b> The registry
 * returns brokers inside the environment and every broker call answers with the whole thing, so
 * the settings page and the brokers page are reading and replacing the same value.
 *
 * <b>Every change replaces the environment with the one the registry returned</b>, and nothing
 * is added or removed locally. A credential set changes `hasCredentials` and a check changes
 * `status`, `lastCheckedAt` and `lastError`, none of which a client could have predicted.
 *
 * <b>One write at a time</b>, because every one rewrites the same environment and a second,
 * concurrent change would be computed against a document the first had already altered. A
 * connection check is the slow one — a real AMQP handshake — and blocking the rest while it
 * runs is the cheaper way to be right.
 *
 * <b>A refusal is kept with the action that caused it.</b> A broker that cannot be added
 * because its display name is taken is an ordinary answer, and it must leave the environment and
 * every other control exactly where they were.
 *
 * <b>Secrets are never held.</b> A broker password passes through as an argument to the call
 * and is not written to state, so nothing here could show it, log it, or hand it to a template.
 */
export const EnvironmentStore = signalStore(
  withState<EnvironmentState>(INITIAL),
  withMethods((store) => {
    const api = inject(EnvironmentsApi);
    const environments = inject(ActiveEnvironmentStore);

    /** Runs one change, and settles the state whichever way it goes. */
    function change(
      action: EnvironmentAction,
      run: (name: string) => Observable<Environment>,
    ): Observable<unknown> {
      if (store.environment() === null || store.saving() !== null) {
        return of(null);
      }

      const { [action]: _cleared, ...remaining } = store.actionErrors();
      patchState(store, { saving: action, lastSaved: null, actionErrors: remaining });

      return run(environments.name()).pipe(
        tap((environment) => patchState(store, { environment, saving: null, lastSaved: action })),
        catchError((error: unknown) => {
          patchState(store, {
            saving: null,
            actionErrors: { ...store.actionErrors(), [action]: toConcordatError(error) },
          });
          return of(null);
        }),
      );
    }

    return {
      load: rxMethod<void>(
        pipe(
          tap(() => patchState(store, { ...INITIAL, loading: true })),
          switchMap(() =>
            api.getEnvironment(environments.name()).pipe(
              tap((environment) => patchState(store, { environment, loading: false })),
              catchError((error: unknown) => {
                patchState(store, { ...INITIAL, error: toConcordatError(error) });
                return of(null);
              }),
            ),
          ),
        ),
      ),

      update: rxMethod<EnvironmentChanges>(
        pipe(mergeMap((changes) => change('settings', (n) => api.updateEnvironment(n, changes)))),
      ),

      addBroker: rxMethod<{ displayName: string; uri: string; virtualHost: string }>(
        pipe(mergeMap((broker) => change('add-broker', (n) => api.addBroker(n, broker)))),
      ),

      checkBroker: rxMethod<string>(
        pipe(mergeMap((id) => change(`check:${id}`, (n) => api.checkBroker(n, id)))),
      ),

      setCredential: rxMethod<{ brokerId: string; username: string; password: string }>(
        pipe(
          mergeMap(({ brokerId, ...credential }) =>
            change(`credentials:${brokerId}`, (n) =>
              api.setBrokerCredential(n, brokerId, credential),
            ),
          ),
        ),
      ),

      removeCredential: rxMethod<string>(
        pipe(
          mergeMap((id) =>
            change(`remove-credentials:${id}`, (n) => api.removeBrokerCredential(n, id)),
          ),
        ),
      ),

      removeBroker: rxMethod<string>(
        pipe(mergeMap((id) => change(`remove:${id}`, (n) => api.removeBroker(n, id)))),
      ),
    };
  }),
);
