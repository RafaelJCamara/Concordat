import { computed, inject } from '@angular/core';
import { patchState, signalStore, withComputed, withMethods, withState } from '@ngrx/signals';
import { rxMethod } from '@ngrx/signals/rxjs-interop';
import { catchError, exhaustMap, of, pipe, tap } from 'rxjs';
import { ActiveEnvironmentStore } from '../../../core/config/active-environment-store';
import { toConcordatError, type ConcordatError } from '../../../core/http/problem-details';
import type { RegistrationOutcome } from '../../../domain/registry/registration';
import type { Subject } from '../../../domain/registry/subject';
import { SubjectsApi } from '../data-access/subjects-api';

/** What the form submits. */
export interface RegistrationRequest {
  readonly subject: string;
  readonly schema: string;
  readonly semanticVersion: string | null;
  readonly changelog: string | null;
}

interface NewVersionState {
  /** The subject being registered against, for the form's own context. */
  readonly subject: Subject | null;
  readonly loading: boolean;
  readonly submitting: boolean;
  readonly outcome: RegistrationOutcome | null;
  readonly error: ConcordatError | null;
}

const INITIAL: NewVersionState = {
  subject: null,
  loading: false,
  submitting: false,
  outcome: null,
  error: null,
};

/**
 * The registration form's state.
 *
 * <b>`exhaustMap`, not `switchMap`.</b> Every other store here switches, because a second
 * read supersedes the first. A second *write* does not: cancelling an in-flight POST
 * abandons the browser's side of a request the registry is still going to process, so a
 * double-clicked submit could allocate two ordinals while the UI showed one. `exhaustMap`
 * ignores the second click until the first has answered, which is the behaviour a submit
 * button needs and the opposite of the one a search box needs.
 */
export const NewVersionStore = signalStore(
  withState<NewVersionState>(INITIAL),
  withComputed(({ subject, outcome }) => ({
    /**
     * The ordinal this registration would take, for the form to show before submitting.
     *
     * Derived from the highest ordinal present rather than from `latest`, and that is right
     * here where it would be wrong elsewhere: this is "what number comes next", not "what is
     * current" — a version awaiting approval has already taken its ordinal, so the next one
     * has to clear it.
     */
    nextOrdinal: computed(() => {
      const value = subject();

      if (value === null) {
        return null;
      }

      return value.versions.reduce((highest, v) => Math.max(highest, v.ordinal), 0) + 1;
    }),

    /** Whether the registry accepted the schema but held it at the approval gate. */
    held: computed(() => outcome()?.status === 'AWAITING_APPROVAL'),
  })),
  withMethods((store) => {
    const api = inject(SubjectsApi);
    const environments = inject(ActiveEnvironmentStore);

    return {
      /** Loads the subject the form registers against, for its heading and next ordinal. */
      loadSubject: rxMethod<string>(
        pipe(
          tap(() => patchState(store, { loading: true, error: null })),
          exhaustMap((name) =>
            api.getSubject(environments.name(), name).pipe(
              tap((subject) => patchState(store, { subject, loading: false })),
              catchError((error: unknown) => {
                patchState(store, { loading: false, error: toConcordatError(error) });
                return of(null);
              }),
            ),
          ),
        ),
      ),

      submit: rxMethod<RegistrationRequest>(
        pipe(
          tap(() => patchState(store, { submitting: true, error: null, outcome: null })),
          exhaustMap((request) =>
            api
              .registerVersion(environments.name(), request.subject, {
                schema: request.schema,
                semanticVersion: request.semanticVersion,
                changelog: request.changelog,
              })
              .pipe(
                // A breaking change lands here, not in `catchError`: the registry accepted
                // it and held it at the gate, which is a success with a consequence.
                tap((outcome) => patchState(store, { outcome, submitting: false })),
                catchError((error: unknown) => {
                  patchState(store, { submitting: false, error: toConcordatError(error) });
                  return of(null);
                }),
              ),
          ),
        ),
      ),

      /** Clears a previous answer so the form can be edited and sent again. */
      reset(): void {
        patchState(store, { outcome: null, error: null });
      },
    };
  }),
);
