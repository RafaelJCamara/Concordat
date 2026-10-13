import { computed, inject } from '@angular/core';
import { patchState, signalStore, withComputed, withMethods, withState } from '@ngrx/signals';
import { rxMethod } from '@ngrx/signals/rxjs-interop';
import { catchError, exhaustMap, forkJoin, mergeMap, of, pipe, switchMap, tap } from 'rxjs';
import { ActiveEnvironmentStore } from '../../../core/config/active-environment-store';
import { toConcordatError, type ConcordatError } from '../../../core/http/problem-details';
import type { OutboxDepth, Subscription } from '../../../domain/notifications/subscription';
import { NotificationsApi, type NewSubscription } from '../data-access/notifications-api';

interface NotificationsState {
  readonly subscriptions: readonly Subscription[];
  readonly outbox: OutboxDepth | null;
  readonly loading: boolean;
  readonly loaded: boolean;
  readonly error: ConcordatError | null;
  readonly creating: boolean;
  readonly createError: ConcordatError | null;
  /** The subscription the last create made, so a form can empty itself and the screen can say so. */
  readonly created: Subscription | null;
  /**
   * The webhook signing secret from the subscription just created, until dismissed.
   *
   * <b>The only place it is ever held</b>: in memory, never in the list, cleared by `dismiss()`
   * and by the next create. The registry stores only an encrypted reference and cannot show it
   * again.
   */
  readonly signingSecret: { readonly endpoint: string; readonly secret: string } | null;
  /** Which subscriptions have a change in flight. */
  readonly busy: readonly string[];
  readonly rowErrors: Readonly<Record<string, ConcordatError>>;
}

const INITIAL: NotificationsState = {
  subscriptions: [],
  outbox: null,
  loading: false,
  loaded: false,
  error: null,
  creating: false,
  createError: null,
  created: null,
  signingSecret: null,
  busy: [],
  rowErrors: {},
};

/**
 * An environment's notification subscriptions, and the outbox that delivers them.
 *
 * <b>The outbox depth is read beside them, because it is the only place a failing subscriber
 * shows.</b> A webhook whose receiver is down is retried and then parked, quietly, and nothing
 * else in the product says so — so the `parked` count is surfaced here rather than left for an
 * operator to know to ask. A failure to read it does not fail the list: the subscriptions are
 * what the screen is for, and the depth is a warning about them.
 *
 * <b>Everything is applied from the registry's answer.</b> A mute changes `enabled` and a
 * delete removes the row, only once the registry has said so.
 *
 * <b>Creating uses `exhaustMap`</b>, because a second `POST` would be a second subscription —
 * and for a webhook a second signing secret nobody asked for.
 */
export const NotificationsStore = signalStore(
  withState<NotificationsState>(INITIAL),
  withComputed(({ loaded, loading, subscriptions }) => ({
    isEmpty: computed(() => loaded() && !loading() && subscriptions().length === 0),
  })),
  withMethods((store) => {
    const api = inject(NotificationsApi);
    const environments = inject(ActiveEnvironmentStore);

    function begin(id: string): boolean {
      if (store.busy().includes(id)) {
        return false;
      }

      const { [id]: _cleared, ...remaining } = store.rowErrors();
      patchState(store, { busy: [...store.busy(), id], rowErrors: remaining });

      return true;
    }

    function settle(id: string, error?: unknown): void {
      patchState(store, {
        busy: store.busy().filter((x) => x !== id),
        ...(error === undefined
          ? {}
          : { rowErrors: { ...store.rowErrors(), [id]: toConcordatError(error) } }),
      });
    }

    return {
      load: rxMethod<void>(
        pipe(
          tap(() => patchState(store, { loading: true, error: null })),
          switchMap(() =>
            forkJoin({
              subscriptions: api.listSubscriptions(environments.name()),
              // Caught on its own: a warning about the subscriptions must not take them down.
              outbox: api.outboxDepth().pipe(catchError(() => of(null))),
            }).pipe(
              tap(({ subscriptions, outbox }) =>
                patchState(store, { subscriptions, outbox, loading: false, loaded: true }),
              ),
              catchError((error: unknown) => {
                patchState(store, {
                  subscriptions: [],
                  outbox: null,
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

      create: rxMethod<NewSubscription>(
        pipe(
          tap(() =>
            patchState(store, {
              creating: true,
              createError: null,
              created: null,
              signingSecret: null,
            }),
          ),
          exhaustMap((request) =>
            api.createSubscription(environments.name(), request).pipe(
              tap((created) =>
                patchState(store, {
                  subscriptions: [...store.subscriptions(), created.subscription],
                  creating: false,
                  created: created.subscription,
                  signingSecret:
                    created.signingSecret === null
                      ? null
                      : { endpoint: created.subscription.endpoint, secret: created.signingSecret },
                }),
              ),
              catchError((error: unknown) => {
                patchState(store, { creating: false, createError: toConcordatError(error) });
                return of(null);
              }),
            ),
          ),
        ),
      ),

      /** Forgets the signing secret. After this the only record of it is wherever the person put it. */
      dismiss(): void {
        patchState(store, { signingSecret: null });
      },

      setEnabled: rxMethod<{ id: string; enabled: boolean }>(
        pipe(
          mergeMap(({ id, enabled }) => {
            if (!begin(id)) {
              return of(null);
            }

            return api.setEnabled(environments.name(), id, enabled).pipe(
              tap((updated) => {
                patchState(store, {
                  subscriptions: store.subscriptions().map((s) => (s.id === id ? updated : s)),
                });
                settle(id);
              }),
              catchError((error: unknown) => {
                settle(id, error);
                return of(null);
              }),
            );
          }),
        ),
      ),

      remove: rxMethod<string>(
        pipe(
          mergeMap((id) => {
            if (!begin(id)) {
              return of(null);
            }

            return api.deleteSubscription(environments.name(), id).pipe(
              tap(() => {
                patchState(store, {
                  subscriptions: store.subscriptions().filter((s) => s.id !== id),
                });
                settle(id);
              }),
              catchError((error: unknown) => {
                settle(id, error);
                return of(null);
              }),
            );
          }),
        ),
      ),
    };
  }),
);
