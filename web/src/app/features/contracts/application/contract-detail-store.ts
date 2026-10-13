import { inject } from '@angular/core';
import { patchState, signalStore, withMethods, withState } from '@ngrx/signals';
import { rxMethod } from '@ngrx/signals/rxjs-interop';
import { catchError, exhaustMap, of, pipe, switchMap, tap } from 'rxjs';
import { ActiveEnvironmentStore } from '../../../core/config/active-environment-store';
import { toConcordatError, type ConcordatError } from '../../../core/http/problem-details';
import type { Contract, EnforcementMode } from '../../../domain/contracts/contract';
import {
  ContractsApi,
  type NewConsumeBinding,
  type NewPublishBinding,
} from '../data-access/contracts-api';

/** The things this screen can change about a contract. */
export type ContractAction = 'enforcement' | 'publish' | 'consume';

interface ContractDetailState {
  readonly contract: Contract | null;
  readonly loading: boolean;
  readonly error: ConcordatError | null;
  /** Which change is in flight, or null. One at a time: they all rewrite the same contract. */
  readonly saving: ContractAction | null;
  /** Why the last change of each kind was refused, kept apart from `error` so the contract stays on screen. */
  readonly actionErrors: Readonly<Partial<Record<ContractAction, ConcordatError>>>;
  /** What last succeeded, so the screen can say so and a form knows when to clear itself. */
  readonly lastSaved: ContractAction | null;
}

const INITIAL: ContractDetailState = {
  contract: null,
  loading: false,
  error: null,
  saving: null,
  actionErrors: {},
  lastSaved: null,
};

/**
 * One contract, its bindings, and the three ways it can be changed.
 *
 * <b>Every change answers with the contract as it now stands, and that replaces the copy on
 * screen.</b> A binding can be refused for what it overlaps — a conflict only the registry can
 * see, since overlap is the intersection of patterns and not their text — so the screen never
 * adds a binding it was not told was stored.
 *
 * <b>One write at a time</b>, across all three, because they rewrite the same aggregate: a
 * second, concurrent change would be computed against a contract that the first had already
 * altered, and whichever finished last would silently win.
 *
 * <b>A refusal is kept per action, not on the screen.</b> Adding a binding that overlaps an
 * existing one is an ordinary, expected answer; it must leave the contract and the other two
 * forms exactly where they were.
 */
export const ContractDetailStore = signalStore(
  withState<ContractDetailState>(INITIAL),
  withMethods((store) => {
    const api = inject(ContractsApi);
    const environments = inject(ActiveEnvironmentStore);

    /** Runs one change, and settles the state whichever way it goes. */
    function change(
      action: ContractAction,
      run: (environment: string, name: string) => ReturnType<ContractsApi['getContract']>,
    ) {
      const contract = store.contract();

      // Refused here rather than queued: each of the three methods has its own `exhaustMap`,
      // which only guards against itself, so this is what makes "one write at a time" true
      // across all of them.
      if (contract === null || store.saving() !== null) {
        return of(null);
      }

      patchState(store, {
        saving: action,
        lastSaved: null,
        actionErrors: { ...store.actionErrors(), [action]: undefined },
      });

      return run(environments.name(), contract.name).pipe(
        tap((updated) => patchState(store, { contract: updated, saving: null, lastSaved: action })),
        catchError((error: unknown) => {
          patchState(store, {
            saving: null,
            actionErrors: { ...store.actionErrors(), [action]: toConcordatError(error) },
          });
          return of(null);
        }),
      );
    }

    return {
      load: rxMethod<string>(
        pipe(
          tap(() => patchState(store, { ...INITIAL, loading: true })),
          switchMap((name) =>
            api.getContract(environments.name(), name).pipe(
              tap((contract) => patchState(store, { contract, loading: false })),
              catchError((error: unknown) => {
                patchState(store, { ...INITIAL, error: toConcordatError(error) });
                return of(null);
              }),
            ),
          ),
        ),
      ),

      setEnforcement: rxMethod<EnforcementMode>(
        pipe(
          exhaustMap((enforcement) =>
            change('enforcement', (env, name) => api.setEnforcement(env, name, enforcement)),
          ),
        ),
      ),

      addPublishBinding: rxMethod<NewPublishBinding>(
        pipe(
          exhaustMap((binding) =>
            change('publish', (env, name) => api.addPublishBinding(env, name, binding)),
          ),
        ),
      ),

      addConsumeBinding: rxMethod<NewConsumeBinding>(
        pipe(
          exhaustMap((binding) =>
            change('consume', (env, name) => api.addConsumeBinding(env, name, binding)),
          ),
        ),
      ),
    };
  }),
);
