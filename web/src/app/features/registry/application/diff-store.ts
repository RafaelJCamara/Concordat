import { inject } from '@angular/core';
import { patchState, signalStore, withMethods, withState } from '@ngrx/signals';
import { rxMethod } from '@ngrx/signals/rxjs-interop';
import { catchError, forkJoin, of, pipe, switchMap, tap } from 'rxjs';
import { ActiveEnvironmentStore } from '../../../core/config/active-environment-store';
import { ConcordatError, toConcordatError } from '../../../core/http/problem-details';
import type { VersionDiff } from '../../../domain/registry/diff';
import type { SchemaVersion, Subject } from '../../../domain/registry/subject';
import { SubjectsApi } from '../data-access/subjects-api';

/** What the screen was asked to compare. Strings, because they come out of a URL. */
export interface DiffRef {
  readonly subject: string;
  readonly from: string;
  readonly to: string;
}

interface DiffState {
  readonly subject: Subject | null;
  readonly diff: VersionDiff | null;
  /** The two ends as the subject holds them, for their status and who registered them. */
  readonly fromVersion: SchemaVersion | null;
  readonly toVersion: SchemaVersion | null;
  readonly loading: boolean;
  readonly error: ConcordatError | null;
}

const INITIAL: DiffState = {
  subject: null,
  diff: null,
  fromVersion: null,
  toVersion: null,
  loading: false,
  error: null,
};

/**
 * Two versions of a subject, compared.
 *
 * <b>The subject is fetched alongside the diff and not instead of it.</b> The diff says
 * what changed; it does not say what either end *is*. Whether `v3` is the live contract or
 * a proposal waiting at the gate is the first thing a reader needs in order to know what
 * the comparison means, and it lives on the subject. Both requests are issued together —
 * neither needs the other's answer — and the screen renders once, so it never shows a
 * verdict whose ends are still unlabelled.
 *
 * <b>There is no `latest` here.</b> The version screen accepts it because one version
 * resolves to one document. A comparison between "whatever is current" and another version
 * would change meaning under the reader after the next approval, in a screen whose whole
 * job is to be precise about which two things are being compared.
 */
export const DiffStore = signalStore(
  withState<DiffState>(INITIAL),
  withMethods((store) => {
    const subjects = inject(SubjectsApi);
    const environments = inject(ActiveEnvironmentStore);

    return {
      load: rxMethod<DiffRef>(
        pipe(
          tap(() => patchState(store, { ...INITIAL, loading: true })),
          switchMap((ref) => {
            const from = ordinalOf(ref.from);
            const to = ordinalOf(ref.to);

            if (from === null || to === null) {
              // Decided locally, with the code the server would have used for an ordinal that
              // does not exist, so the template renders it through one path. Nothing is sent:
              // `/versions/abc/diff/2` would fail route matching on the server as a bare 404
              // with no problem body, which reads as a missing page rather than a bad link.
              patchState(store, {
                loading: false,
                error: new ConcordatError({
                  status: 404,
                  code: 'version_not_found',
                  detail: `'${ref.from}' and '${ref.to}' are not both version numbers.`,
                }),
              });
              return of(null);
            }

            return forkJoin({
              subject: subjects.getSubject(environments.name(), ref.subject),
              diff: subjects.diffVersions(environments.name(), ref.subject, from, to),
            }).pipe(
              tap(({ subject, diff }) =>
                patchState(store, {
                  subject,
                  diff,
                  fromVersion: subject.versions.find((v) => v.ordinal === diff.from) ?? null,
                  toVersion: subject.versions.find((v) => v.ordinal === diff.to) ?? null,
                  loading: false,
                }),
              ),
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

/** A positive integer, or null. `Number('')` is 0 and `Number('1e1')` is 10; neither is a version. */
function ordinalOf(value: string): number | null {
  return /^[1-9]\d*$/.test(value) ? Number(value) : null;
}
