import { inject } from '@angular/core';
import { patchState, signalStore, withMethods, withState } from '@ngrx/signals';
import { rxMethod } from '@ngrx/signals/rxjs-interop';
import { catchError, exhaustMap, of, pipe, tap } from 'rxjs';
import { ActiveEnvironmentStore } from '../../../core/config/active-environment-store';
import { toConcordatError, type ConcordatError } from '../../../core/http/problem-details';
import type { CompatibilityPolicy } from '../../../domain/registry/compatibility';
import { SubjectsApi } from '../data-access/subjects-api';
import { SubjectDetailStore } from './subject-detail-store';

/** What the registry said it did, in its own words and not the request's. */
export type SettingsOutcome =
  | { readonly kind: 'owner'; readonly subject: string; readonly owner: string }
  | { readonly kind: 'policy'; readonly subject: string; readonly policy: CompatibilityPolicy }
  | { readonly kind: 'reviewers'; readonly subject: string; readonly reviewers: readonly string[] };

interface SubjectSettingsState {
  readonly ownerSaving: boolean;
  readonly ownerError: ConcordatError | null;
  readonly policySaving: boolean;
  readonly policyError: ConcordatError | null;
  readonly reviewersSaving: boolean;
  readonly reviewersError: ConcordatError | null;
  /**
   * The registry's answer to the last write that landed, or null.
   *
   * <b>Nothing is applied ahead of it.</b> The owner here is the one in the registry's response
   * (it trims what it is sent), and the policy is the one in *its* response (`PUT` answers with a
   * `PolicyResponse`, which reads `backward` as `BACKWARD`). The subject on screen is replaced by
   * a fresh read afterwards, never by this.
   */
  readonly outcome: SettingsOutcome | null;
}

const INITIAL: SubjectSettingsState = {
  ownerSaving: false,
  ownerError: null,
  policySaving: false,
  policyError: null,
  reviewersSaving: false,
  reviewersError: null,
  outcome: null,
};

/**
 * Changing a subject's owner, its compatibility policy and who may decide its held versions.
 *
 * <b>Three writes, each followed by a read.</b> Not every answer is the subject the page shows: `PATCH`
 * answers with one but a `PUT` of the policy answers with a policy, and treating them differently
 * would mean one control predicting state and the other reporting it. So after any lands, the detail
 * store reads the subject again and the screen shows that.
 *
 * <b>`exhaustMap` for both, for the reason `NewSubjectStore` gives:</b> cancelling an in-flight
 * request abandons only the browser's half of a write the registry still processes, so a
 * double-clicked confirmation would otherwise show a second attempt racing the first. They are
 * independent of each other — an owner change does not wait on a policy change — so each has its
 * own flags.
 *
 * Provided by the page beside `SubjectDetailStore`, which it re-reads through; not
 * `providedIn: 'root'`, so leaving the page drops the last answer and coming back shows none.
 */
export const SubjectSettingsStore = signalStore(
  withState<SubjectSettingsState>(INITIAL),
  withMethods((store) => {
    const api = inject(SubjectsApi);
    const environments = inject(ActiveEnvironmentStore);
    const detail = inject(SubjectDetailStore);

    return {
      changeOwner: rxMethod<{ readonly subject: string; readonly owner: string }>(
        pipe(
          tap(() => patchState(store, { ownerSaving: true, ownerError: null, outcome: null })),
          exhaustMap(({ subject, owner }) =>
            api.changeOwner(environments.name(), subject, owner).pipe(
              tap((changed) => {
                patchState(store, {
                  ownerSaving: false,
                  outcome: { kind: 'owner', subject: changed.name, owner: changed.owner },
                });
                detail.refresh(changed.name);
              }),
              catchError((error: unknown) => {
                patchState(store, { ownerSaving: false, ownerError: toConcordatError(error) });

                // Swallowed on purpose, as in every store here: the failure is state the editor
                // renders, and letting it propagate would tear the rxMethod down so the next
                // attempt did nothing at all.
                return of(null);
              }),
            ),
          ),
        ),
      ),

      setPolicy: rxMethod<{ readonly subject: string; readonly policy: CompatibilityPolicy }>(
        pipe(
          tap(() => patchState(store, { policySaving: true, policyError: null, outcome: null })),
          exhaustMap(({ subject, policy }) =>
            api.setPolicy(environments.name(), subject, policy).pipe(
              tap((stored) => {
                patchState(store, {
                  policySaving: false,
                  outcome: { kind: 'policy', subject, policy: stored },
                });
                detail.refresh(subject);
              }),
              catchError((error: unknown) => {
                patchState(store, { policySaving: false, policyError: toConcordatError(error) });

                return of(null);
              }),
            ),
          ),
        ),
      ),

      setReviewers: rxMethod<{ readonly subject: string; readonly reviewers: readonly string[] }>(
        pipe(
          tap(() =>
            patchState(store, { reviewersSaving: true, reviewersError: null, outcome: null }),
          ),
          exhaustMap(({ subject, reviewers }) =>
            api.setReviewers(environments.name(), subject, reviewers).pipe(
              tap((changed) => {
                // The set the registry stored (a repeat dropped), not the one that was sent.
                patchState(store, {
                  reviewersSaving: false,
                  outcome: {
                    kind: 'reviewers',
                    subject: changed.name,
                    reviewers: changed.reviewers,
                  },
                });
                detail.refresh(changed.name);
              }),
              catchError((error: unknown) => {
                patchState(store, {
                  reviewersSaving: false,
                  reviewersError: toConcordatError(error),
                });

                return of(null);
              }),
            ),
          ),
        ),
      ),

      /** Clears the last answer and any refusal, as an editor opens. */
      reset(): void {
        patchState(store, {
          ownerError: null,
          policyError: null,
          reviewersError: null,
          outcome: null,
        });
      },
    };
  }),
);
