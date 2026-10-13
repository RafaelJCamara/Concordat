import { computed, inject } from '@angular/core';
import { patchState, signalStore, withComputed, withMethods, withState } from '@ngrx/signals';
import { rxMethod } from '@ngrx/signals/rxjs-interop';
import { catchError, of, pipe, switchMap, tap } from 'rxjs';
import { ActiveEnvironmentStore } from '../../../core/config/active-environment-store';
import { toConcordatError, type ConcordatError } from '../../../core/http/problem-details';
import {
  AUDIT_DEFAULT_LIMIT,
  type AuditEntry,
  type AuditQuery,
} from '../../../domain/governance/audit';
import { AuditApi } from '../data-access/audit-api';

/** The query the screen opens with: this environment, everything, the newest hundred. */
export const DEFAULT_AUDIT_QUERY: AuditQuery = {
  scope: 'environment',
  action: null,
  target: '',
  since: null,
  until: null,
  limit: AUDIT_DEFAULT_LIMIT,
};

interface AuditState {
  readonly entries: readonly AuditEntry[];
  /** What the entries on screen were asked for, so a result is never shown under another's filter. */
  readonly asked: AuditQuery | null;
  readonly loading: boolean;
  readonly loaded: boolean;
  readonly error: ConcordatError | null;
}

const INITIAL: AuditState = {
  entries: [],
  asked: null,
  loading: false,
  loaded: false,
  error: null,
};

/**
 * The audit trail, as one screen needs it.
 *
 * <b>A load is an explicit act, not a reaction to typing.</b> The filter form submits; nothing
 * here watches its fields. A request per keystroke in the target box would be five queries
 * against a table that only ever grows, to answer a question the person had not finished
 * asking.
 *
 * <b>`truncated` is the one thing a reader cannot see for themselves.</b> The registry returns
 * at most `limit` entries, newest first, with no cursor — so a full page is indistinguishable
 * from a complete one except by counting, and a log that quietly stops at a hundred reads as
 * "that is everything that happened".
 */
export const AuditStore = signalStore(
  withState<AuditState>(INITIAL),
  withComputed(({ entries, asked, loaded, loading }) => ({
    /** Whether the answer filled the limit, so older entries may exist beyond it. */
    truncated: computed(() => {
      const query = asked();
      return query !== null && entries().length >= query.limit;
    }),

    /** True only once a load has completed and returned nothing — never while loading. */
    isEmpty: computed(() => loaded() && !loading() && entries().length === 0),
  })),
  withMethods((store) => {
    const api = inject(AuditApi);
    const environments = inject(ActiveEnvironmentStore);

    return {
      /**
       * Asks the registry for the trail.
       *
       * `switchMap`: applying a second filter while the first is in flight must not let the
       * slower first answer land under the second's heading.
       */
      load: rxMethod<AuditQuery>(
        pipe(
          tap(() => patchState(store, { loading: true, error: null })),
          switchMap((query) =>
            api.getAudit(environments.name(), query).pipe(
              tap((entries) =>
                patchState(store, { entries, asked: query, loading: false, loaded: true }),
              ),
              catchError((error: unknown) => {
                patchState(store, {
                  entries: [],
                  asked: query,
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
    };
  }),
);
