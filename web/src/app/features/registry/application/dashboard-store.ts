import { computed, inject } from '@angular/core';
import { patchState, signalStore, withComputed, withMethods, withState } from '@ngrx/signals';
import { rxMethod } from '@ngrx/signals/rxjs-interop';
import { catchError, of, pipe, switchMap, tap } from 'rxjs';
import { ActiveEnvironmentStore } from '../../../core/config/active-environment-store';
import { toConcordatError, type ConcordatError } from '../../../core/http/problem-details';
import type { EnvironmentSummary } from '../../../domain/registry/environment-summary';
import { SummaryApi } from '../data-access/summary-api';

interface DashboardState {
  readonly summary: EnvironmentSummary | null;
  readonly loading: boolean;
  readonly error: ConcordatError | null;
  /** Whether a load has finished at least once, successfully or not. */
  readonly loaded: boolean;
}

const INITIAL: DashboardState = {
  summary: null,
  loading: false,
  error: null,
  loaded: false,
};

/**
 * The dashboard's overview, as the registry computes it (ADR-037).
 *
 * Provided by the route that uses it, for the reason `SubjectListStore` is: registry data is stale
 * the moment somebody else registers a version, and a root-provided overview would show yesterday's
 * numbers instantly on the way back in and never say so.
 *
 * It replaced the dashboard's reading of the subject list, and nothing derives these numbers
 * client-side any more: a count the browser computed from a list that is later paginated would
 * silently start under-counting.
 */
export const DashboardStore = signalStore(
  withState<DashboardState>(INITIAL),
  withComputed(({ summary, loading, loaded }) => ({
    /**
     * True only once a load has completed and the environment has no subjects, never while loading.
     *
     * Subjects, and not "nothing at all": an environment can hold contracts and violations before it
     * holds a subject, and the empty state is the offer to create the first one.
     */
    isEmpty: computed(() => loaded() && !loading() && summary()?.subjects.total === 0),
  })),
  withMethods((store) => {
    const api = inject(SummaryApi);
    const environments = inject(ActiveEnvironmentStore);

    return {
      /**
       * Loads the overview of the active environment.
       *
       * `switchMap` rather than `mergeMap`, as in the subject list: switching environment twice in
       * quick succession must not let the slower first answer land last and sit under the second
       * environment's name.
       */
      load: rxMethod<void>(
        pipe(
          tap(() => patchState(store, { loading: true, error: null })),
          switchMap(() =>
            api.getSummary(environments.name()).pipe(
              tap((summary) => patchState(store, { summary, loading: false, loaded: true })),
              catchError((error: unknown) => {
                patchState(store, {
                  summary: null,
                  loading: false,
                  loaded: true,
                  error: toConcordatError(error),
                });
                // Swallowed on purpose, as in the subject list: the failure is now state the
                // template renders, and letting it propagate would end the rxMethod's
                // subscription so that the retry button did nothing.
                return of(null);
              }),
            ),
          ),
        ),
      ),
    };
  }),
);
