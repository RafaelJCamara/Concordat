import { inject } from '@angular/core';
import { patchState, signalStore, withMethods, withState } from '@ngrx/signals';
import { rxMethod } from '@ngrx/signals/rxjs-interop';
import { catchError, of, pipe, switchMap, tap } from 'rxjs';
import { ActiveEnvironmentStore } from '../../../core/config/active-environment-store';
import { ConcordatError, toConcordatError } from '../../../core/http/problem-details';
import type { ImpactReport } from '../../../domain/registry/impact';
import { ImpactApi } from '../data-access/impact-api';

/** What the screen was asked to analyse. */
export interface ImpactRef {
  readonly subject: string;
  /** An ordinal, or the literal `latest`. A string because it comes out of a URL. */
  readonly ordinal: string;
}

interface ImpactState {
  readonly report: ImpactReport | null;
  readonly loading: boolean;
  readonly error: ConcordatError | null;
}

const INITIAL: ImpactState = { report: null, loading: false, error: null };

/**
 * Who breaks if a registered version is the one consumers meet.
 *
 * `latest` is accepted here, unlike on the diff screen, because the report names the ordinal
 * it resolved — a pasted `…/latest/impact` link shows which version it meant, where a
 * comparison between "whatever is current" and another version would not say which two things
 * were compared.
 */
export const ImpactStore = signalStore(
  withState<ImpactState>(INITIAL),
  withMethods((store) => {
    const api = inject(ImpactApi);
    const environments = inject(ActiveEnvironmentStore);

    return {
      load: rxMethod<ImpactRef>(
        pipe(
          tap(() => patchState(store, { ...INITIAL, loading: true })),
          switchMap((ref) => {
            const version = ordinalOf(ref.ordinal);

            if (version === undefined) {
              // Decided locally, with the code the server would use for a version that does
              // not exist, so the template renders it through one path.
              patchState(store, {
                loading: false,
                error: new ConcordatError({
                  status: 404,
                  code: 'version_not_found',
                  detail: `'${ref.ordinal}' is not a version number.`,
                }),
              });
              return of(null);
            }

            return api.getImpact(environments.name(), ref.subject, version).pipe(
              tap((report) => patchState(store, { report, loading: false })),
              catchError((error: unknown) => {
                patchState(store, { ...INITIAL, error: toConcordatError(error) });
                return of(null);
              }),
            );
          }),
        ),
      ),
    };
  }),
);

/**
 * A positive integer, `null` for the literal `latest`, or `undefined` for anything else.
 *
 * Three outcomes because "no version" and "a bad version" must not be the same value: the
 * first is a request without the parameter, the second is a request that is never sent.
 */
function ordinalOf(value: string): number | null | undefined {
  if (value.toLowerCase() === 'latest') {
    return null;
  }

  return /^[1-9]\d*$/.test(value) ? Number(value) : undefined;
}
