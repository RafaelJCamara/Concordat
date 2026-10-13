import { computed, inject } from '@angular/core';
import { patchState, signalStore, withComputed, withMethods, withState } from '@ngrx/signals';
import { rxMethod } from '@ngrx/signals/rxjs-interop';
import { catchError, of, pipe, switchMap, tap } from 'rxjs';
import { ActiveEnvironmentStore } from '../../../core/config/active-environment-store';
import { toConcordatError, type ConcordatError } from '../../../core/http/problem-details';
import { latestVersion, pendingVersions, type Subject } from '../../../domain/registry/subject';
import { SubjectsApi } from '../data-access/subjects-api';

interface SubjectDetailState {
  readonly subject: Subject | null;
  readonly loading: boolean;
  readonly error: ConcordatError | null;
  /**
   * Why the subject could not be read back after a change that *did* land, or null.
   *
   * Kept apart from `error`: that one replaces the whole screen with a failure, which would be a
   * lie after a write that succeeded. This one says the change is in and the page is not yet
   * showing it.
   */
  readonly refreshError: ConcordatError | null;
}

const INITIAL: SubjectDetailState = {
  subject: null,
  loading: false,
  error: null,
  refreshError: null,
};

/**
 * One subject and its versions, as the detail screen needs it.
 *
 * Per-route like `SubjectListStore`, and for the same reason: leaving the page drops the
 * state, so coming back re-reads rather than showing whatever was true last time. On this
 * screen that matters more than on the list — a version registered by someone else between
 * two visits is exactly the thing a reader is here to find out about.
 *
 * `load` takes the subject name rather than reading it from the router. A store that
 * injects `ActivatedRoute` can only ever be used by the one route whose parameters it
 * happens to know, and it cannot be tested without a router at all.
 */
export const SubjectDetailStore = signalStore(
  withState<SubjectDetailState>(INITIAL),
  withComputed(({ subject }) => ({
    /** The version the gated `latest` pointer resolves to (ADR-017), or null. */
    latest: computed(() => {
      const value = subject();
      return value === null ? null : latestVersion(value);
    }),

    /** Versions held at the approval gate, newest first. */
    pending: computed(() => {
      const value = subject();
      return value === null ? [] : pendingVersions(value);
    }),

    /**
     * Versions newest-first, for display.
     *
     * The API returns them in registration order, which is the right order to store and the
     * wrong one to read: someone opening a subject wants the most recent change first.
     */
    versions: computed(() => {
      const value = subject();
      return value === null ? [] : [...value.versions].sort((a, b) => b.ordinal - a.ordinal);
    }),
  })),
  withMethods((store) => {
    const api = inject(SubjectsApi);
    const environments = inject(ActiveEnvironmentStore);

    return {
      /**
       * Loads one subject by name.
       *
       * `switchMap` for the same reason as the list: navigating between two subjects quickly
       * must not leave the slower first response painting the wrong subject's versions under
       * the second one's heading.
       */
      load: rxMethod<string>(
        pipe(
          tap(() => patchState(store, { loading: true, error: null, refreshError: null })),
          switchMap((name) =>
            api.getSubject(environments.name(), name).pipe(
              tap((subject) => patchState(store, { subject, loading: false })),
              catchError((error: unknown) => {
                patchState(store, {
                  subject: null,
                  loading: false,
                  error: toConcordatError(error),
                });
                // Swallowed on purpose — see `SubjectListStore`. Letting it propagate tears
                // down the rxMethod's subscription and the next `load()` does nothing at all.
                return of(null);
              }),
            ),
          ),
        ),
      ),

      /**
       * Reads the subject again after somebody changed it, without blanking the page.
       *
       * <b>Not `load`.</b> `load` raises the loading flag and the screen swaps itself for a
       * skeleton, which after a save reads as the page having lost what it just did. This replaces
       * the subject in place, so the owner or policy on screen is the registry's own account of it
       * and not a prediction made from what was sent (the registry trims an owner, and reads
       * `backward` as `BACKWARD`).
       *
       * A failure here is *not* a failed save, and is kept apart (`refreshError`): the change is
       * in, and the page cannot yet show it.
       *
       * A late answer for another subject is dropped. This store lives as long as the page does,
       * and the page is reused when somebody follows a link from one subject to another.
       */
      refresh: rxMethod<string>(
        pipe(
          switchMap((name) =>
            api.getSubject(environments.name(), name).pipe(
              tap((subject) => {
                if (store.subject()?.name === subject.name) {
                  patchState(store, { subject, refreshError: null });
                }
              }),
              catchError((error: unknown) => {
                patchState(store, { refreshError: toConcordatError(error) });

                return of(null);
              }),
            ),
          ),
        ),
      ),
    };
  }),
);
