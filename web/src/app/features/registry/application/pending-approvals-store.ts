import { computed, inject } from '@angular/core';
import { patchState, signalStore, withComputed, withMethods, withState } from '@ngrx/signals';
import { rxMethod } from '@ngrx/signals/rxjs-interop';
import { catchError, exhaustMap, mergeMap, of, pipe, switchMap, tap } from 'rxjs';
import { SessionStore } from '../../../core/auth/session-store';
import { ActiveEnvironmentStore } from '../../../core/config/active-environment-store';
import { toConcordatError, type ConcordatError } from '../../../core/http/problem-details';
import type { PendingApproval } from '../../../domain/registry/approval-queue';
import { ApprovalsApi } from '../data-access/approvals-api';
import { SubjectsApi } from '../data-access/subjects-api';

/** What was asked of the registry. */
export interface Decision {
  readonly subject: string;
  readonly ordinal: number;
  readonly action: 'approve' | 'reject';
}

/** What the screen last did to one version, keyed by {@link keyOf}. */
export type DecisionState =
  | { readonly state: 'deciding'; readonly action: Decision['action'] }
  | {
      readonly state: 'failed';
      readonly action: Decision['action'];
      readonly error: ConcordatError;
    };

interface PendingApprovalsState {
  /** The oldest proposals first: the queue as far as it has been read. */
  readonly items: readonly PendingApproval[];
  /** How many versions the registry says are waiting, which can exceed `items`. */
  readonly total: number;
  readonly loading: boolean;
  readonly loaded: boolean;
  readonly loadingMore: boolean;
  readonly error: ConcordatError | null;
  /** A page after the first failed: what is shown is true, and there is more it could not read. */
  readonly moreError: ConcordatError | null;
  /** Reading the queue again after a decision failed: what is shown may be out of date. */
  readonly refreshFailed: boolean;
  readonly decisions: Readonly<Record<string, DecisionState>>;
  /** The most recent success, so the screen can say what just happened to a row it removed. */
  readonly lastDecided: Decision | null;
}

const INITIAL: PendingApprovalsState = {
  items: [],
  total: 0,
  loading: false,
  loaded: false,
  loadingMore: false,
  error: null,
  moreError: null,
  refreshFailed: false,
  decisions: {},
  lastDecided: null,
};

/** How many the screen asks for at a time. */
export const PAGE_SIZE = 50;

/** The most the registry will give in one request (`ListPendingApprovalsQuery.MaxLimit`). */
const MAX_PAGE = 200;

/** The key a version is tracked under. */
export function keyOf(subject: string, ordinal: number): string {
  return `${subject}@${ordinal}`;
}

/**
 * The versions in the environment that are waiting on a decision, and the act of deciding.
 *
 * <b>The queue is the registry's</b> (ADR-042), a page at a time, oldest proposal first, with the
 * length of the whole queue beside it. It used to be derived from the subject list, which put a
 * pending version on page two of a paged list out of the one screen whose only job is to show what
 * is waiting. `total` is what keeps the screen honest about how much of the queue it is showing.
 *
 * <b>A decision removes its row at once and then reads the queue again</b> rather than trusting
 * the removal. Approving moves `latest`, which changes the baseline of every other pending version
 * on that subject, and the registry is the one that knows the new pointer; patching one row would
 * leave the rest comparing against a pointer that has moved. The re-read asks for as many as are on
 * screen (at most one request's worth), so a decision does not collapse a list the reader paged.
 *
 * <b>Decisions run concurrently across versions and never twice for one.</b> Two different
 * versions can be in flight at once, but a second click on a version already being decided is
 * ignored — an approve and a reject racing for the same version would otherwise both be sent,
 * and whichever the server saw second would be refused for a reason the reader never caused.
 */
export const PendingApprovalsStore = signalStore(
  withState<PendingApprovalsState>(INITIAL),
  withComputed(({ items, total, loading, loaded, error }) => ({
    /** The queue as far as it has been read, oldest first: the longest wait leads. */
    pending: computed<readonly PendingApproval[]>(() => items()),

    /** True only once a load has completed and found nothing waiting — never while loading. */
    isEmpty: computed(() => loaded() && !loading() && error() === null && total() === 0),

    /** How many waiting versions the registry has that the screen has not read yet. */
    unread: computed(() => Math.max(total() - items().length, 0)),
  })),
  withMethods((store) => {
    const approvals = inject(ApprovalsApi);
    const subjects = inject(SubjectsApi);
    const environments = inject(ActiveEnvironmentStore);
    const session = inject(SessionStore);

    // Bumped whenever the list is replaced, so a page that was asked for before the replacement
    // and answered after it is dropped instead of being appended to a different list.
    let epoch = 0;

    const refresh = rxMethod<void>(
      pipe(
        tap(() => (epoch += 1)),
        switchMap(() =>
          approvals
            .getQueue(environments.name(), {
              limit: Math.min(Math.max(store.items().length, PAGE_SIZE), MAX_PAGE),
            })
            .pipe(
              tap((queue) =>
                patchState(store, {
                  items: queue.items,
                  total: queue.total,
                  refreshFailed: false,
                  moreError: null,
                }),
              ),
              catchError(() => {
                // Kept, not blanked: the queue was read a moment ago and one decision was just
                // made, and an empty screen would hide every other version the reader can act on.
                patchState(store, { refreshFailed: true });
                return of(null);
              }),
            ),
        ),
      ),
    );

    return {
      load: rxMethod<void>(
        pipe(
          tap(() => {
            epoch += 1;
            patchState(store, {
              loading: true,
              error: null,
              moreError: null,
              refreshFailed: false,
            });
          }),
          switchMap(() =>
            approvals.getQueue(environments.name(), { limit: PAGE_SIZE }).pipe(
              tap((queue) =>
                patchState(store, {
                  items: queue.items,
                  total: queue.total,
                  loading: false,
                  loaded: true,
                }),
              ),
              catchError((error: unknown) => {
                patchState(store, {
                  items: [],
                  total: 0,
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

      /** Reads the next page, after what is already on screen. Ignored while one is in flight. */
      loadMore: rxMethod<void>(
        pipe(
          exhaustMap(() => {
            if (!store.loaded() || store.loading() || store.unread() === 0) {
              return of(null);
            }

            const asked = epoch;
            patchState(store, { loadingMore: true, moreError: null });

            return approvals
              .getQueue(environments.name(), { limit: PAGE_SIZE, offset: store.items().length })
              .pipe(
                tap((queue) => {
                  if (asked !== epoch) {
                    patchState(store, { loadingMore: false });
                    return;
                  }

                  const known = new Set(store.items().map((i) => keyOf(i.subject, i.ordinal)));

                  patchState(store, {
                    items: [
                      ...store.items(),
                      ...queue.items.filter((i) => !known.has(keyOf(i.subject, i.ordinal))),
                    ],
                    total: queue.total,
                    loadingMore: false,
                  });
                }),
                catchError((error: unknown) => {
                  patchState(store, { loadingMore: false, moreError: toConcordatError(error) });
                  return of(null);
                }),
              );
          }),
        ),
      ),

      /** Reads the queue again, keeping what is shown if that fails. */
      refresh,

      decide: rxMethod<Decision>(
        pipe(
          // Filtered before the request, not after: `mergeMap` would otherwise send both.
          mergeMap((decision) => {
            const key = keyOf(decision.subject, decision.ordinal);

            if (store.decisions()[key]?.state === 'deciding') {
              return of(null);
            }

            patchState(store, {
              decisions: {
                ...store.decisions(),
                [key]: { state: 'deciding', action: decision.action },
              },
            });

            return subjects
              .decideVersion(
                environments.name(),
                decision.subject,
                decision.ordinal,
                decision.action,
                session.actor() ?? 'web',
              )
              .pipe(
                tap(() => {
                  const { [key]: _settled, ...rest } = store.decisions();

                  patchState(store, {
                    items: store.items().filter((i) => keyOf(i.subject, i.ordinal) !== key),
                    total: Math.max(store.total() - 1, 0),
                    decisions: rest,
                    lastDecided: decision,
                  });

                  refresh();
                }),
                catchError((error: unknown) => {
                  // Kept on the row, not in the list-level error: the queue is fine and one
                  // decision was refused, and blanking the screen for that would hide every
                  // other version the reader can still act on.
                  patchState(store, {
                    decisions: {
                      ...store.decisions(),
                      [key]: {
                        state: 'failed',
                        action: decision.action,
                        error: toConcordatError(error),
                      },
                    },
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
