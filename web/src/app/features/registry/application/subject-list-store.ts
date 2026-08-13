import { computed, inject } from '@angular/core';
import { patchState, signalStore, withComputed, withMethods, withState } from '@ngrx/signals';
import { rxMethod } from '@ngrx/signals/rxjs-interop';
import { catchError, of, pipe, switchMap, tap } from 'rxjs';
import { ActiveEnvironmentStore } from '../../../core/config/active-environment-store';
import { toConcordatError, type ConcordatError } from '../../../core/http/problem-details';
import type { Subject } from '../../../domain/registry/subject';
import { SubjectsApi } from '../data-access/subjects-api';

interface SubjectListState {
  readonly subjects: readonly Subject[];
  readonly loading: boolean;
  readonly error: ConcordatError | null;
  /** Whether a load has finished at least once, successfully or not. */
  readonly loaded: boolean;
}

const INITIAL: SubjectListState = {
  subjects: [],
  loading: false,
  error: null,
  loaded: false,
};

/**
 * The subject list, as one screen needs it.
 *
 * Not `providedIn: 'root'`: this store is provided by the route that uses it, so leaving
 * the page drops the state. Registry data goes stale the moment somebody else registers a
 * version, and a root-provided list would show yesterday's subjects instantly on the way
 * back in and never say so. Per-route is the honest default; caching is a decision to take
 * deliberately, per screen, once there is a reason.
 *
 * The reference shape for the feature stores M4.3 adds: state, a computed view of it, and
 * one `rxMethod` per thing the screen can ask for.
 */
export const SubjectListStore = signalStore(
  withState<SubjectListState>(INITIAL),
  withComputed(({ subjects, loading, loaded }) => ({
    /** True only once a load has completed and returned nothing — never while loading. */
    isEmpty: computed(() => loaded() && !loading() && subjects().length === 0),
  })),
  withMethods((store) => {
    const api = inject(SubjectsApi);
    const environments = inject(ActiveEnvironmentStore);

    return {
      /**
       * Loads the subjects for the active environment.
       *
       * `switchMap` rather than `mergeMap` deliberately: switching environment twice in
       * quick succession must not leave the slower first response overwriting the second,
       * which would show one environment's subjects under another's name.
       */
      load: rxMethod<void>(
        pipe(
          tap(() => patchState(store, { loading: true, error: null })),
          switchMap(() =>
            api.listSubjects(environments.name()).pipe(
              tap((subjects) => patchState(store, { subjects, loading: false, loaded: true })),
              catchError((error: unknown) => {
                patchState(store, {
                  subjects: [],
                  loading: false,
                  loaded: true,
                  error: toConcordatError(error),
                });
                // Swallowed on purpose: the failure is now state the template renders. Letting
                // it propagate would also tear down the rxMethod's subscription, so the next
                // `load()` would do nothing at all.
                return of(null);
              }),
            ),
          ),
        ),
      ),
    };
  }),
);
