import { computed, inject } from '@angular/core';
import { patchState, signalStore, withComputed, withMethods, withState } from '@ngrx/signals';
import { rxMethod } from '@ngrx/signals/rxjs-interop';
import { catchError, exhaustMap, of, pipe, switchMap, tap } from 'rxjs';
import { ActiveEnvironmentStore } from '../../../core/config/active-environment-store';
import { toConcordatError, type ConcordatError } from '../../../core/http/problem-details';
import type { Contract, EnforcementMode } from '../../../domain/contracts/contract';
import { ContractsApi } from '../data-access/contracts-api';

interface ContractListState {
  readonly contracts: readonly Contract[];
  readonly loading: boolean;
  readonly loaded: boolean;
  readonly error: ConcordatError | null;
  readonly creating: boolean;
  /** Why the last create was refused, kept apart from `error` so a refusal does not blank the list. */
  readonly createError: ConcordatError | null;
  /** The contract the last successful create made, so the screen can say so. */
  readonly created: Contract | null;
}

const INITIAL: ContractListState = {
  contracts: [],
  loading: false,
  loaded: false,
  error: null,
  creating: false,
  createError: null,
  created: null,
};

/** What a person asks for when creating a contract. `null` lets the registry choose. */
export interface NewContract {
  readonly name: string;
  readonly enforcement: EnforcementMode | null;
}

/**
 * The environment's contracts, and creating one.
 *
 * <b>Creation uses `exhaustMap`.</b> This store writes, and cancelling an in-flight `POST`
 * (what `switchMap` would do on a double click) abandons only the browser's half of a request
 * the registry still processes — and the second click would then be refused as a duplicate of
 * a contract that was in fact created.
 *
 * <b>A new contract is inserted in name order from the registry's answer</b> rather than the
 * list being refetched: the response is the contract as stored, and a refetch is a second
 * request to learn what the first already said.
 */
export const ContractListStore = signalStore(
  withState<ContractListState>(INITIAL),
  withComputed(({ loaded, loading, contracts }) => ({
    /** True only once a load has completed and found nothing — never while loading. */
    isEmpty: computed(() => loaded() && !loading() && contracts().length === 0),
  })),
  withMethods((store) => {
    const api = inject(ContractsApi);
    const environments = inject(ActiveEnvironmentStore);

    return {
      load: rxMethod<void>(
        pipe(
          tap(() => patchState(store, { loading: true, error: null })),
          switchMap(() =>
            api.listContracts(environments.name()).pipe(
              tap((contracts) =>
                patchState(store, { contracts: byName(contracts), loading: false, loaded: true }),
              ),
              catchError((error: unknown) => {
                patchState(store, {
                  contracts: [],
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

      create: rxMethod<NewContract>(
        pipe(
          tap(() => patchState(store, { creating: true, createError: null, created: null })),
          exhaustMap((request) =>
            api.createContract(environments.name(), request.name, request.enforcement).pipe(
              tap((created) =>
                patchState(store, {
                  contracts: byName([...store.contracts(), created]),
                  creating: false,
                  created,
                }),
              ),
              catchError((error: unknown) => {
                patchState(store, { creating: false, createError: toConcordatError(error) });
                return of(null);
              }),
            ),
          ),
        ),
      ),
    };
  }),
);

function byName(contracts: readonly Contract[]): readonly Contract[] {
  return [...contracts].sort((a, b) => a.name.localeCompare(b.name));
}
