import { TestBed } from '@angular/core/testing';
import { Subject as ResponseStream } from 'rxjs';
import { beforeEach, describe, expect, it } from 'vitest';
import { provideConcordatConfig } from '../../../core/config/app-config';
import { ConcordatError } from '../../../core/http/problem-details';
import type { Contract, EnforcementMode } from '../../../domain/contracts/contract';
import {
  ContractsApi,
  type NewConsumeBinding,
  type NewPublishBinding,
} from '../data-access/contracts-api';
import { ContractDetailStore } from './contract-detail-store';

// What matters here is that the contract on screen is only ever one the registry said it
// stored, that one write runs at a time across all three kinds, and that a refusal is kept with
// the action that caused it rather than blanking a contract that is perfectly fine.

function contract(overrides: Partial<Contract> = {}): Contract {
  return {
    name: 'payments',
    enforcement: 'MONITOR',
    createdAt: new Date('2026-08-13T09:00:00Z'),
    publishes: [],
    consumes: [],
    ...overrides,
  };
}

const publish: NewPublishBinding = {
  exchange: 'orders',
  routingKeyPattern: 'orders.*',
  subjects: [{ subject: 'orders.created', selector: 'latest' }],
  virtualHost: '',
  precedence: null,
};

const consume: NewConsumeBinding = {
  queue: 'billing.orders',
  subjects: [{ subject: 'orders.created', selector: 'latest' }],
  virtualHost: '',
};

function fakeApi() {
  const gets: ResponseStream<Contract>[] = [];
  const writes: { kind: string; args: unknown[]; stream: ResponseStream<Contract> }[] = [];

  function write(kind: string, ...args: unknown[]) {
    const stream = new ResponseStream<Contract>();
    writes.push({ kind, args, stream });
    return stream.asObservable();
  }

  return {
    gets,
    writes,
    getContract() {
      const stream = new ResponseStream<Contract>();
      gets.push(stream);
      return stream.asObservable();
    },
    setEnforcement: (...args: unknown[]) => write('enforcement', ...args),
    addPublishBinding: (...args: unknown[]) => write('publish', ...args),
    addConsumeBinding: (...args: unknown[]) => write('consume', ...args),
    replacePublishBinding: (...args: unknown[]) => write('changePublish', ...args),
    removePublishBinding: (...args: unknown[]) => write('removePublish', ...args),
    replaceConsumeBinding: (...args: unknown[]) => write('changeConsume', ...args),
    removeConsumeBinding: (...args: unknown[]) => write('removeConsume', ...args),
  };
}

describe('ContractDetailStore', () => {
  let api: ReturnType<typeof fakeApi>;
  let store: InstanceType<typeof ContractDetailStore>;

  beforeEach(() => {
    api = fakeApi();

    TestBed.configureTestingModule({
      providers: [
        provideConcordatConfig({ defaultEnvironment: 'dev' }),
        { provide: ContractsApi, useValue: api },
        ContractDetailStore,
      ],
    });

    store = TestBed.inject(ContractDetailStore);
  });

  function loaded(value: Contract = contract()): void {
    store.load('payments');
    api.gets[0]!.next(value);
  }

  describe('loading', () => {
    it('shows the contract once it arrives', () => {
      loaded();

      expect(store.contract()?.name).toBe('payments');
      expect(store.loading()).toBe(false);
    });

    it('reports a failure with no contract', () => {
      store.load('payments');
      api.gets[0]!.error(
        new ConcordatError({ status: 404, code: 'contract_not_found', detail: 'no such' }),
      );

      expect(store.contract()).toBeNull();
      expect(store.error()?.code).toBe('contract_not_found');
    });
  });

  describe('changing it', () => {
    it('sends the new mode for this contract in the active environment', () => {
      loaded();
      store.setEnforcement('ENFORCE');

      expect(api.writes[0]).toMatchObject({
        kind: 'enforcement',
        args: ['dev', 'payments', 'ENFORCE'],
      });
    });

    it('replaces the contract with the one the registry returned', () => {
      loaded();
      store.setEnforcement('ENFORCE');
      api.writes[0]!.stream.next(contract({ enforcement: 'ENFORCE' }));

      expect(store.contract()?.enforcement).toBe('ENFORCE');
      expect(store.lastSaved()).toBe('enforcement');
      expect(store.saving()).toBeNull();
    });

    it('shows a binding only once the registry has stored it', () => {
      loaded();
      store.addPublishBinding(publish);

      expect(store.contract()?.publishes).toEqual([]);
      expect(store.saving()).toBe('publish');

      api.writes[0]!.stream.next(
        contract({
          publishes: [
            {
              id: 'p1',
              exchange: 'orders',
              routingKeyPattern: 'orders.*',
              brokerId: null,
              virtualHost: '/',
              precedence: null,
              subjects: publish.subjects,
            },
          ],
        }),
      );

      expect(store.contract()?.publishes).toHaveLength(1);
    });

    it('adds a consume binding the same way', () => {
      loaded();
      store.addConsumeBinding(consume);

      expect(api.writes[0]).toMatchObject({
        kind: 'consume',
        args: ['dev', 'payments', consume],
      });
    });

    it('does nothing before a contract has loaded', () => {
      store.setEnforcement('ENFORCE');

      expect(api.writes).toEqual([]);
    });
  });

  describe('changing and removing a binding', () => {
    const subjects = [{ subject: 'orders.created', selector: '2' }];

    it('sends a publish change to the binding’s id, in this contract and environment', () => {
      loaded();
      store.replacePublishBinding({ id: 'p1', subjects, precedence: 3 });

      expect(api.writes[0]).toMatchObject({
        kind: 'changePublish',
        args: ['dev', 'payments', 'p1', { subjects, precedence: 3 }],
      });
      expect(store.saving()).toBe('change');
    });

    it('sends the other three the same way', () => {
      loaded();
      store.removePublishBinding('p1');
      api.writes[0]!.stream.next(contract());
      store.replaceConsumeBinding({ id: 'c1', subjects });
      api.writes[1]!.stream.next(contract());
      store.removeConsumeBinding('c1');

      expect(api.writes.map((w) => w.kind)).toEqual([
        'removePublish',
        'changeConsume',
        'removeConsume',
      ]);
      expect(api.writes[0]!.args).toEqual(['dev', 'payments', 'p1']);
      expect(api.writes[1]!.args).toEqual(['dev', 'payments', 'c1', { subjects }]);
      expect(api.writes[2]!.args).toEqual(['dev', 'payments', 'c1']);
    });

    it('shows the change only once the registry has answered, and says which kind it was', () => {
      loaded();
      store.replacePublishBinding({ id: 'p1', subjects, precedence: 1 });

      expect(store.contract()?.publishes).toEqual([]);

      api.writes[0]!.stream.next(contract({ enforcement: 'ENFORCE' }));

      expect(store.contract()?.enforcement).toBe('ENFORCE');
      expect(store.lastSaved()).toBe('change');
      expect(store.saving()).toBeNull();
    });

    it('says a removal was a removal', () => {
      loaded();
      store.removeConsumeBinding('c1');
      api.writes[0]!.stream.next(contract());

      expect(store.lastSaved()).toBe('remove');
    });

    it('keeps the registry’s refusal of a change with that action and leaves the contract', () => {
      // The refusal an overlap produces is an ordinary answer, and has to leave the binding and
      // everything else on screen exactly where it was.
      loaded();
      store.replacePublishBinding({ id: 'p1', subjects, precedence: 1 });
      api.writes[0]!.stream.error(
        new ConcordatError({ status: 409, code: 'binding_conflict', detail: 'overlaps orders.*' }),
      );

      expect(store.actionErrors().change?.detail).toBe('overlaps orders.*');
      expect(store.actionErrors().remove).toBeUndefined();
      expect(store.contract()?.name).toBe('payments');
      expect(store.lastSaved()).toBeNull();
      expect(store.saving()).toBeNull();
    });

    it('keeps a refused removal with the removal', () => {
      loaded();
      store.removePublishBinding('p1');
      api.writes[0]!.stream.error(
        new ConcordatError({ status: 404, code: 'binding_not_found', detail: 'gone' }),
      );

      expect(store.actionErrors().remove?.code).toBe('binding_not_found');
      expect(store.actionErrors().change).toBeUndefined();
    });

    it('is one write at a time with everything else', () => {
      loaded();
      store.removePublishBinding('p1');
      store.replaceConsumeBinding({ id: 'c1', subjects });
      store.setEnforcement('OFF');

      expect(api.writes).toHaveLength(1);
    });

    it('does nothing before a contract has loaded', () => {
      store.removePublishBinding('p1');

      expect(api.writes).toEqual([]);
    });
  });

  describe('one write at a time', () => {
    it('ignores a second change of a different kind while the first is in flight', () => {
      // Each of the three methods has its own exhaustMap, which only guards against itself.
      // Without the shared check, a second write would be computed against a contract the
      // first had already altered, and whichever finished last would silently win.
      loaded();
      store.setEnforcement('ENFORCE');
      store.addPublishBinding(publish);
      store.addConsumeBinding(consume);

      expect(api.writes).toHaveLength(1);
      expect(store.saving()).toBe('enforcement');
    });

    it('accepts the next change once the first has settled', () => {
      loaded();
      store.setEnforcement('OFF');
      api.writes[0]!.stream.next(contract({ enforcement: 'OFF' }));

      store.addPublishBinding(publish);

      expect(api.writes).toHaveLength(2);
    });
  });

  describe('a refusal', () => {
    it('is kept with the action that caused it, and the contract stays', () => {
      loaded();
      store.addPublishBinding(publish);
      api.writes[0]!.stream.error(
        new ConcordatError({ status: 409, code: 'binding_overlaps', detail: 'overlaps orders.*' }),
      );

      expect(store.actionErrors().publish?.detail).toBe('overlaps orders.*');
      expect(store.actionErrors().consume).toBeUndefined();
      expect(store.error()).toBeNull();
      expect(store.contract()?.name).toBe('payments');
      expect(store.saving()).toBeNull();
    });

    it('is cleared when the same action is tried again', () => {
      loaded();
      store.addPublishBinding(publish);
      api.writes[0]!.stream.error(new Error('offline'));

      store.addPublishBinding(publish);

      expect(store.actionErrors().publish).toBeUndefined();
    });

    it('does not claim success', () => {
      loaded();
      store.setEnforcement('ENFORCE');
      api.writes[0]!.stream.error(new Error('offline'));

      expect(store.lastSaved()).toBeNull();
      expect(store.contract()?.enforcement).toBe('MONITOR');
    });

    it('leaves the store able to try again', () => {
      loaded();
      store.setEnforcement('ENFORCE');
      api.writes[0]!.stream.error(new Error('offline'));

      store.setEnforcement('ENFORCE');

      expect(api.writes).toHaveLength(2);
    });
  });

  it('forgets what last succeeded when something new starts', () => {
    loaded();
    store.setEnforcement('OFF' as EnforcementMode);
    api.writes[0]!.stream.next(contract({ enforcement: 'OFF' }));
    expect(store.lastSaved()).toBe('enforcement');

    store.addConsumeBinding(consume);

    expect(store.lastSaved()).toBeNull();
  });
});
