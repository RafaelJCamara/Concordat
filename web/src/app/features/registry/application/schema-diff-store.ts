import { inject } from '@angular/core';
import { patchState, signalStore, withMethods, withState } from '@ngrx/signals';
import { rxMethod } from '@ngrx/signals/rxjs-interop';
import { catchError, forkJoin, of, pipe, switchMap, tap } from 'rxjs';
import { toConcordatError } from '../../../core/http/problem-details';
import type { Divergence } from '../../../domain/registry/compatibility';
import type { SchemaDiffResult } from '../../../domain/registry/schema-diff';
import { SchemasApi } from '../data-access/schemas-api';
import { SchemaDiffFailure, SchemaDiffer } from '../data-access/schema-differ';

/** What the line diff was asked to compare. */
export interface SchemaDiffRef {
  /** The baseline's schema id. */
  readonly from: string;
  /** The held version's schema id. */
  readonly to: string;
  /**
   * The registry's findings for the pair. A finding's id, for the page to find it by, is its
   * index in this list.
   */
  readonly divergences: readonly Divergence[];
}

/** Why the diff is not on screen, in words the page can show. */
export interface SchemaDiffProblem {
  /** `load`: a schema could not be fetched. The others are {@link SchemaDiffFailure}'s. */
  readonly reason: 'load' | 'timeout' | 'unavailable';
  readonly detail: string;
}

interface SchemaDiffState {
  readonly loading: boolean;
  readonly result: SchemaDiffResult | null;
  readonly problem: SchemaDiffProblem | null;
}

const INITIAL: SchemaDiffState = { loading: false, result: null, problem: null };

/**
 * The held version's schema against the baseline's, as lines.
 *
 * <b>It fetches the two documents itself, by schema id.</b> The version diff the review already
 * holds says which two schemas are being compared and what the compatibility engine made of them;
 * the documents are `GET /v1/schemas/{id}`, which is the one endpoint that returns a schema's text
 * (ADR-015), so no new registry call is needed. The two requests go out together and the diff is
 * made only when both are back.
 *
 * <b>It is only asked for when the reviewer has chosen the detailed view</b>, so a review left on
 * the summary fetches nothing and starts no worker.
 *
 * <b>Every outcome other than a diff is a state, not an exception.</b> Too large to show is the
 * worker's answer (`result.status === 'too-large'`); a schema that could not be fetched, and a
 * worker that would not start or took too long, are `problem`. The page turns any of them into
 * one sentence and the summary, which does not need the line diff, stays available.
 */
export const SchemaDiffStore = signalStore(
  withState<SchemaDiffState>(INITIAL),
  withMethods((store) => {
    const schemas = inject(SchemasApi);
    const differ = inject(SchemaDiffer);

    return {
      load: rxMethod<SchemaDiffRef>(
        pipe(
          tap(() => patchState(store, { ...INITIAL, loading: true })),
          switchMap((ref) =>
            forkJoin({
              before: schemas.getSchema(ref.from),
              after: schemas.getSchema(ref.to),
            }).pipe(
              switchMap(({ before, after }) =>
                differ.compare({
                  format: after.format,
                  before: before.text,
                  after: after.text,
                  findings: ref.divergences.map((divergence, id) => ({
                    id,
                    path: divergence.path,
                  })),
                }),
              ),
              tap((result) => patchState(store, { loading: false, result })),
              catchError((error: unknown) => {
                patchState(store, { ...INITIAL, problem: problemOf(error) });
                return of(null);
              }),
            ),
          ),
        ),
      ),
    };
  }),
);

function problemOf(error: unknown): SchemaDiffProblem {
  if (error instanceof SchemaDiffFailure) {
    return { reason: error.reason, detail: error.message };
  }

  return { reason: 'load', detail: toConcordatError(error).detail };
}
