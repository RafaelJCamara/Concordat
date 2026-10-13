import { inject } from '@angular/core';
import { patchState, signalStore, withMethods, withState } from '@ngrx/signals';
import { rxMethod } from '@ngrx/signals/rxjs-interop';
import { catchError, exhaustMap, of, pipe, tap } from 'rxjs';
import { ActiveEnvironmentStore } from '../../../core/config/active-environment-store';
import { toConcordatError, type ConcordatError } from '../../../core/http/problem-details';
import type { NewSubject } from '../../../domain/registry/new-subject';
import type { Subject } from '../../../domain/registry/subject';
import { SubjectsApi } from '../data-access/subjects-api';

interface NewSubjectState {
  readonly submitting: boolean;
  /**
   * The subject as the registry answered, which is the only thing that says it exists.
   *
   * Nothing is predicted ahead of it: the screen does not show a created subject, a count or a
   * link until this is set, and what it links to is the name *in* this, because the registry
   * trims what it is sent and the stored name is the one that can be opened.
   */
  readonly created: Subject | null;
  /**
   * What the registry said when it refused, kept with the name that was sent.
   *
   * The name matters for one answer: a 409 means the name is taken, and the way out is to open the
   * subject that holds it, which needs the name the refused request carried.
   */
  readonly error: { readonly error: ConcordatError; readonly attempted: string } | null;
}

const INITIAL: NewSubjectState = { submitting: false, created: null, error: null };

/**
 * Creating a subject.
 *
 * <b>`exhaustMap`, not `switchMap`, for the reason `NewVersionStore` gives:</b> cancelling an
 * in-flight POST abandons only the browser's half of a request the registry still processes, so a
 * double-clicked submit would create the subject while the screen showed a second attempt
 * failing with a 409 it had caused itself. A second submit while one is in flight is ignored
 * until the first answers.
 *
 * Not `providedIn: 'root'`, like every feature store here: leaving the page drops the answer, so
 * coming back to create another does not show the last one.
 */
export const NewSubjectStore = signalStore(
  withState<NewSubjectState>(INITIAL),
  withMethods((store) => {
    const api = inject(SubjectsApi);
    const environments = inject(ActiveEnvironmentStore);

    return {
      create: rxMethod<NewSubject>(
        pipe(
          tap(() => patchState(store, { submitting: true, error: null, created: null })),
          exhaustMap((subject) =>
            api.createSubject(environments.name(), subject).pipe(
              tap((created) => patchState(store, { created, submitting: false })),
              catchError((error: unknown) => {
                patchState(store, {
                  submitting: false,
                  error: { error: toConcordatError(error), attempted: subject.name },
                });

                // Swallowed on purpose, as in every store here: the failure is now state the
                // template renders, and letting it propagate would tear the rxMethod down so
                // the next attempt did nothing at all.
                return of(null);
              }),
            ),
          ),
        ),
      ),

      /** Clears a refusal so the form can be corrected and sent again. */
      reset(): void {
        patchState(store, { error: null, created: null });
      },
    };
  }),
);
