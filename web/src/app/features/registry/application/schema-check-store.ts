import { inject } from '@angular/core';
import { patchState, signalStore, withMethods, withState } from '@ngrx/signals';
import { rxMethod } from '@ngrx/signals/rxjs-interop';
import { catchError, of, pipe, switchMap, tap } from 'rxjs';
import type { SchemaCheckRequest, SchemaCheckResult } from '../../../domain/registry/schema-check';
import { SchemaChecker, SchemaCheckFailure } from '../data-access/schema-checker';

interface SchemaCheckState {
  readonly checking: boolean;
  /** What the answer is an answer *to*, so the page can tell when it has gone stale. */
  readonly request: SchemaCheckRequest | null;
  readonly result: SchemaCheckResult | null;
  /** Why no answer came back, kept apart from a verdict: a check that could not run says nothing. */
  readonly failure: string | null;
}

const INITIAL: SchemaCheckState = { checking: false, request: null, result: null, failure: null };

/**
 * The state of the client-side check (`ajv`).
 *
 * `switchMap`, unlike the registration store's `exhaustMap`: this is a read, so a second ask
 * supersedes the first rather than queuing behind it — and the one that is abandoned may be the
 * one that is hung on an expensive pattern, which is exactly the one to stop waiting for.
 *
 * <b>Nothing here ever blocks a registration.</b> A failure to check is a failure to check, not
 * a finding: it is recorded as such and the page says the registry will still judge the schema.
 */
export const SchemaCheckStore = signalStore(
  withState<SchemaCheckState>(INITIAL),
  withMethods((store) => {
    const checker = inject(SchemaChecker);

    return {
      run: rxMethod<SchemaCheckRequest>(
        pipe(
          tap((request) =>
            patchState(store, { checking: true, request, result: null, failure: null }),
          ),
          switchMap((request) =>
            checker.check(request).pipe(
              tap((result) => patchState(store, { checking: false, result })),
              catchError((error: unknown) => {
                patchState(store, {
                  checking: false,
                  failure:
                    error instanceof SchemaCheckFailure
                      ? error.message
                      : 'The check could not be run.',
                });
                return of(null);
              }),
            ),
          ),
        ),
      ),

      /** Forgets the last answer. */
      clear(): void {
        patchState(store, INITIAL);
      },
    };
  }),
);
