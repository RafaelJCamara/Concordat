import { computed, inject } from '@angular/core';
import { patchState, signalStore, withComputed, withMethods, withState } from '@ngrx/signals';
import { rxMethod } from '@ngrx/signals/rxjs-interop';
import { catchError, of, pipe, switchMap, tap } from 'rxjs';
import { ActiveEnvironmentStore } from '../../../core/config/active-environment-store';
import { ConcordatError, toConcordatError } from '../../../core/http/problem-details';
import type { SchemaDocument } from '../../../domain/registry/schema';
import type { SchemaVersion, Subject } from '../../../domain/registry/subject';
import { SchemasApi } from '../data-access/schemas-api';
import { SubjectsApi } from '../data-access/subjects-api';

/** What the screen was asked to show. */
export interface VersionRef {
  readonly subject: string;
  /** The ordinal, or the literal `latest` — which the server resolves through the gate. */
  readonly ordinal: string;
}

interface VersionDetailState {
  readonly subject: Subject | null;
  readonly version: SchemaVersion | null;
  readonly document: SchemaDocument | null;
  /** The metadata request — who registered what, and when. */
  readonly loading: boolean;
  /**
   * The schema document request, tracked separately.
   *
   * Two flags rather than one because the screen has two halves and they arrive at
   * different times. Holding the header back until the document lands would keep the whole
   * page under a skeleton for the slower of two requests, when everything above the
   * document is already known and worth reading.
   */
  readonly loadingDocument: boolean;
  readonly error: ConcordatError | null;
}

const INITIAL: VersionDetailState = {
  subject: null,
  version: null,
  document: null,
  loading: false,
  loadingDocument: false,
  error: null,
};

/**
 * One version, its metadata and its schema document.
 *
 * **Two requests, in sequence, because the second needs the first's answer.** A version
 * carries a schema *id*, never the schema text (`VersionResponse` in `Contracts.cs`), so
 * the document has to be fetched from `/v1/schemas/{id}` once the id is known. That is not
 * an oversight in the API: schemas are deduplicated by content and shared across subjects,
 * so inlining the text into every version response would send the same document once per
 * version that points at it.
 *
 * The subject fetch also carries every sibling version, which is what lets the screen offer
 * "compare with the previous" without a third request.
 */
export const VersionDetailStore = signalStore(
  withState<VersionDetailState>(INITIAL),
  withComputed(({ subject, version }) => ({
    /**
     * The version immediately before this one, or null when this is the first.
     *
     * By ordinal rather than by position in the array: ordinals are contiguous and
     * monotonic, so `ordinal - 1` is the previous version, whereas the array's order is the
     * server's and not something this screen should depend on.
     */
    previous: computed(() => {
      const current = version();
      const value = subject();

      if (current === null || value === null) {
        return null;
      }

      return value.versions.find((v) => v.ordinal === current.ordinal - 1) ?? null;
    }),

    /** Whether this version is the one the gated `latest` pointer resolves to. */
    isLatest: computed(() => {
      const current = version();
      const value = subject();

      return current !== null && value !== null && value.latest === current.ordinal;
    }),
  })),
  withMethods((store) => {
    const subjects = inject(SubjectsApi);
    const schemas = inject(SchemasApi);
    const environments = inject(ActiveEnvironmentStore);

    return {
      load: rxMethod<VersionRef>(
        pipe(
          tap(() => patchState(store, { loading: true, loadingDocument: true, error: null })),
          switchMap((ref) =>
            subjects.getSubject(environments.name(), ref.subject).pipe(
              switchMap((subject) => {
                const version = resolve(subject, ref.ordinal);

                if (version === null) {
                  patchState(store, {
                    subject,
                    version: null,
                    document: null,
                    loading: false,
                    loadingDocument: false,
                    // Carries the code the server would have used, so the template renders
                    // this through the same path as a real 404 and a caller branching on
                    // the code does not need a second case for "we worked it out locally".
                    // `status: 404` rather than 0: the subject request did reach the
                    // registry, and it is this ordinal that does not exist.
                    error: new ConcordatError({
                      status: 404,
                      code: 'version_not_found',
                      detail: `No version '${ref.ordinal}' on subject '${ref.subject}'.`,
                    }),
                  });
                  return of(null);
                }

                // Published before the document is asked for, so the header renders against
                // what is already known rather than waiting on a second round trip.
                patchState(store, { subject, version, document: null, loading: false });

                return schemas.getSchema(version.schemaId).pipe(
                  tap((document) => patchState(store, { document, loadingDocument: false })),
                  catchError((error: unknown) => {
                    // The metadata stays. A reader can still see who registered what and
                    // when, which is more useful than an empty screen reporting that one
                    // fetch of two failed.
                    patchState(store, {
                      document: null,
                      loadingDocument: false,
                      error: toConcordatError(error),
                    });
                    return of(null);
                  }),
                );
              }),
              catchError((error: unknown) => {
                patchState(store, { ...INITIAL, error: toConcordatError(error) });
                return of(null);
              }),
            ),
          ),
        ),
      ),
    };
  }),
);

/**
 * Finds the requested version within a subject.
 *
 * `latest` resolves through the subject's gated pointer rather than by taking the highest
 * ordinal (ADR-017) — a version awaiting approval has a higher ordinal and is deliberately
 * not what `latest` means. Deriving it here as `max(ordinal)` would reintroduce exactly the
 * defect the gate exists to prevent, on the one screen that shows the schema in full.
 */
function resolve(subject: Subject, ordinal: string): SchemaVersion | null {
  if (ordinal.toLowerCase() === 'latest') {
    return subject.latest === null
      ? null
      : (subject.versions.find((v) => v.ordinal === subject.latest) ?? null);
  }

  const parsed = Number(ordinal);

  if (!Number.isInteger(parsed)) {
    return null;
  }

  return subject.versions.find((v) => v.ordinal === parsed) ?? null;
}
