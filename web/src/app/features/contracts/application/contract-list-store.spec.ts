import { TestBed } from '@angular/core/testing';
import { Subject as ResponseStream } from 'rxjs';
import { beforeEach, describe, expect, it } from 'vitest';
import { provideConcordatConfig } from '../../../core/config/app-config';
import { ConcordatError } from '../../../core/http/problem-details';
import type { Contract, EnforcementMode } from '../../../domain/contracts/contract';
import { ContractsApi } from '../data-access/contracts-api';
import { ContractListStore } from './contract-list-store';

function contract(name: string, enforcement: EnforcementMode = 'MONITOR'): Contract {
  return {
    name,
    enforcement,
    createdAt: new Date('2026-08-13T09:00:00Z'),
    publishes: [],
    consumes: [],
  };
}

function fakeApi() {
  const lists: ResponseStream<readonly Contract[]>[] = [];
  const creates: {
    args: [string, string, EnforcementMode | null];
    stream: ResponseStream<Contract>;
  }[] = [];

  return {
    lists,
    creates,
    listContracts() {
      const stream = new ResponseStream<readonly Contract[]>();
      lists.push(stream);
      return stream.asObservable();
    },
    createContract(environment: string, name: string, enforcement: EnforcementMode | null) {
      const stream = new ResponseStream<Contract>();
      creates.push({ args: [environment, name, enforcement], stream });
      return stream.asObservable();
    },
  };
}

describe('ContractListStore', () => {
  let api: ReturnType<typeof fakeApi>;
  let store: InstanceType<typeof ContractListStore>;

  beforeEach(() => {
    api = fakeApi();

    TestBed.configureTestingModule({
      providers: [
        provideConcordatConfig({ defaultEnvironment: 'dev' }),
        { provide: ContractsApi, useValue: api },
        ContractListStore,
      ],
    });

    store = TestBed.inject(ContractListStore);
  });

  describe('loading', () => {
    it('is not empty until a load has found nothing', () => {
      expect(store.isEmpty()).toBe(false);

      store.load();
      expect(store.isEmpty()).toBe(false);

      api.lists[0]!.next([]);
      expect(store.isEmpty()).toBe(true);
    });

    it('lists contracts in name order, whatever order the registry used', () => {
      store.load();
      api.lists[0]!.next([contract('zeta'), contract('alpha'), contract('mid')]);

      expect(store.contracts().map((c) => c.name)).toEqual(['alpha', 'mid', 'zeta']);
    });

    it('reports a failure, and an empty list is not a verdict then', () => {
      store.load();
      api.lists[0]!.error(new ConcordatError({ status: 500, code: 'x', detail: 'down' }));

      expect(store.error()?.detail).toBe('down');
      expect(store.isEmpty()).toBe(true);
    });
  });

  describe('creating', () => {
    it('asks in the active environment, with the mode left to the registry when none was chosen', () => {
      store.create({ name: 'payments', enforcement: null });

      expect(api.creates[0]?.args).toEqual(['dev', 'payments', null]);
    });

    it('marks itself as creating until the registry answers', () => {
      store.create({ name: 'payments', enforcement: null });

      expect(store.creating()).toBe(true);

      api.creates[0]!.stream.next(contract('payments'));

      expect(store.creating()).toBe(false);
    });

    it('adds the stored contract in name order, without a second request for the list', () => {
      store.load();
      api.lists[0]!.next([contract('alpha'), contract('zeta')]);

      store.create({ name: 'mid', enforcement: null });
      api.creates[0]!.stream.next(contract('mid'));

      expect(store.contracts().map((c) => c.name)).toEqual(['alpha', 'mid', 'zeta']);
      expect(api.lists).toHaveLength(1);
    });

    it('records what was created, so the screen can say so', () => {
      store.create({ name: 'payments', enforcement: 'ENFORCE' });
      api.creates[0]!.stream.next(contract('payments', 'ENFORCE'));

      expect(store.created()?.name).toBe('payments');
    });

    it('ignores a second click while the first is in flight', () => {
      // Cancelling the first POST would abandon only the browser's half of a request the
      // registry still processes, and the second would then be refused as a duplicate of a
      // contract that was in fact created.
      store.create({ name: 'payments', enforcement: null });
      store.create({ name: 'payments', enforcement: null });

      expect(api.creates).toHaveLength(1);
    });

    it('keeps a refusal apart from the list, which stays on screen', () => {
      store.load();
      api.lists[0]!.next([contract('alpha')]);

      store.create({ name: 'alpha', enforcement: null });
      api.creates[0]!.stream.error(
        new ConcordatError({ status: 409, code: 'contract_already_exists', detail: 'exists' }),
      );

      expect(store.createError()?.code).toBe('contract_already_exists');
      expect(store.error()).toBeNull();
      expect(store.contracts()).toHaveLength(1);
      expect(store.creating()).toBe(false);
    });

    it('clears an earlier refusal and success when asked again', () => {
      store.create({ name: 'alpha', enforcement: null });
      api.creates[0]!.stream.error(new Error('offline'));

      store.create({ name: 'alpha', enforcement: null });

      expect(store.createError()).toBeNull();
      expect(store.created()).toBeNull();
    });
  });
});
