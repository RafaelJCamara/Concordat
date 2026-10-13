import { computed, inject } from '@angular/core';
import { patchState, signalStore, withComputed, withMethods, withState } from '@ngrx/signals';
import { rxMethod } from '@ngrx/signals/rxjs-interop';
import { catchError, mergeMap, of, pipe, switchMap, tap } from 'rxjs';
import { SessionStore } from '../../../core/auth/session-store';
import { ActiveEnvironmentStore } from '../../../core/config/active-environment-store';
import { toConcordatError, type ConcordatError } from '../../../core/http/problem-details';
import {
  latestVersion,
  pendingVersions,
  type SchemaVersion,
  type Subject,
} from '../../../domain/registry/subject';
import { SubjectsApi } from '../data-access/subjects-api';

/** One version waiting on a decision, with what it would replace. */
export interface PendingApproval {
  readonly subject: string;
  readonly version: SchemaVersion;
  /** The version consumers resolve today, or null when nothing has been approved yet. */
  readonly baseline: SchemaVersion | null;
}

/** What was asked of the registry. */
export interface Decision {
  readonly subject: string;
  readonly ordinal: number;
  readonly action: 'approve' | 'reject';
}

/** What the screen last did to one version, keyed by {@link keyOf}. */
export type DecisionState =
  | { readonly state: 'deciding'; readonly action: Decision['action'] }
  | {
      readonly state: 'failed';
      readonly action: Decision['action'];
      readonly error: ConcordatError;
    };

interface PendingApprovalsState {
  readonly subjects: readonly Subject[];
  readonly loading: boolean;
  readonly loaded: boolean;
  readonly error: ConcordatError | null;
  readonly decisions: Readonly<Record<string, DecisionState>>;
  /** The most recent success, so the screen can say what just happened to a row it removed. */
  readonly lastDecided: Decision | null;
}

const INITIAL: PendingApprovalsState = {
  subjects: [],
  loading: false,
  loaded: false,
  error: null,
  decisions: {},
  lastDecided: null,
};

/** The key a version is tracked under. */
export function keyOf(subject: string, ordinal: number): string {
  return `${subject}@${ordinal}`;
}

/**
 * Every version in the environment that is waiting on a decision, and the act of deciding.
 *
 * <b>The queue is derived from the subject list, like the dashboard's counts</b>, rather than
 * from an endpoint that does not exist. Honest at this size, and bound to the same limit: the
 * day that list is paginated, a pending version on page two would silently not be in the
 * queue — which, for the screen whose only job is to show what is waiting, is the worst thing
 * it could get wrong.
 *
 * <b>A decision replaces the subject with the one the registry returned</b> rather than
 * removing the row locally. Approving moves `latest`, which changes the baseline of every
 * other pending version on that subject; patching one row would leave the rest comparing
 * against a pointer that has moved.
 *
 * <b>Decisions run concurrently across versions and never twice for one.</b> Two different
 * versions can be in flight at once, but a second click on a version already being decided is
 * ignored — an approve and a reject racing for the same version would otherwise both be sent,
 * and whichever the server saw second would be refused for a reason the reader never caused.
 */
export const PendingApprovalsStore = signalStore(
  withState<PendingApprovalsState>(INITIAL),
  withComputed(({ subjects, loading, loaded }) => ({
    /** Every held version across the environment, oldest first: the longest wait leads. */
    pending: computed<readonly PendingApproval[]>(() =>
      subjects()
        .flatMap((subject) =>
          pendingVersions(subject).map((version) => ({
            subject: subject.name,
            version,
            baseline: latestVersion(subject),
          })),
        )
        .sort((a, b) => a.version.registeredAt.getTime() - b.version.registeredAt.getTime()),
    ),

    /** True only once a load has completed and found nothing waiting — never while loading. */
    isEmpty: computed(
      () =>
        loaded() &&
        !loading() &&
        subjects().every((subject) => pendingVersions(subject).length === 0),
    ),
  })),
  withMethods((store) => {
    const api = inject(SubjectsApi);
    const environments = inject(ActiveEnvironmentStore);
    const session = inject(SessionStore);

    return {
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
                return of(null);
              }),
            ),
          ),
        ),
      ),

      decide: rxMethod<Decision>(
        pipe(
          // Filtered before the request, not after: `mergeMap` would otherwise send both.
          mergeMap((decision) => {
            const key = keyOf(decision.subject, decision.ordinal);

            if (store.decisions()[key]?.state === 'deciding') {
              return of(null);
            }

            patchState(store, {
              decisions: {
                ...store.decisions(),
                [key]: { state: 'deciding', action: decision.action },
              },
            });

            return api
              .decideVersion(
                environments.name(),
                decision.subject,
                decision.ordinal,
                decision.action,
                session.actor() ?? 'web',
              )
              .pipe(
                tap((updated) => {
                  const { [key]: _settled, ...rest } = store.decisions();

                  patchState(store, {
                    subjects: store.subjects().map((s) => (s.name === updated.name ? updated : s)),
                    decisions: rest,
                    lastDecided: decision,
                  });
                }),
                catchError((error: unknown) => {
                  // Kept on the row, not in the list-level error: the queue is fine and one
                  // decision was refused, and blanking the screen for that would hide every
                  // other version the reader can still act on.
                  patchState(store, {
                    decisions: {
                      ...store.decisions(),
                      [key]: {
                        state: 'failed',
                        action: decision.action,
                        error: toConcordatError(error),
                      },
                    },
                  });
                  return of(null);
                }),
              );
          }),
        ),
      ),
    };
  }),
);
