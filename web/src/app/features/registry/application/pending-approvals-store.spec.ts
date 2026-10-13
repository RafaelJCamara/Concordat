import { TestBed } from '@angular/core/testing';
import { Subject as ResponseStream } from 'rxjs';
import { beforeEach, describe, expect, it } from 'vitest';
import { SessionStore } from '../../../core/auth/session-store';
import { provideConcordatConfig } from '../../../core/config/app-config';
import { ConcordatError } from '../../../core/http/problem-details';
import type { SchemaVersion, Subject } from '../../../domain/registry/subject';
import { SubjectsApi } from '../data-access/subjects-api';
import { keyOf, PendingApprovalsStore } from './pending-approvals-store';

// What this store is for is the queue being right: every held version in the environment, and
// none that is not held. And that a decision replaces the subject with what the registry
// returned, because approving moves `latest` and that changes every other row's baseline.

function version(
  ordinal: number,
  status: SchemaVersion['status'] = 'ACTIVE',
  registeredAt = '2026-08-13T09:00:00Z',
): SchemaVersion {
  return {
    ordinal,
    schemaId: `schema-${ordinal}`,
    semanticVersion: null,
    status,
    changelog: null,
    registeredAt: new Date(registeredAt),
    registeredBy: 'ci',
    deprecated: false,
  };
}

function subject(name: string, versions: readonly SchemaVersion[], latest: number | null): Subject {
  return {
    name,
    format: 'json',
    owner: 'team',
    lifecycle: 'ACTIVE',
    contentModel: 'OPEN',
    compatibilityPolicy: { mode: 'BACKWARD', surface: 'WIRE_JSON' },
    latest,
    versions,
  };
}

function fakeApi() {
  const lists: ResponseStream<readonly Subject[]>[] = [];
  const decisions: {
    args: [string, string, number, 'approve' | 'reject', string];
    stream: ResponseStream<Subject>;
  }[] = [];

  return {
    lists,
    decisions,
    listSubjects() {
      const stream = new ResponseStream<readonly Subject[]>();
      lists.push(stream);
      return stream.asObservable();
    },
    decideVersion(
      environment: string,
      name: string,
      ordinal: number,
      decision: 'approve' | 'reject',
      decidedBy: string,
    ) {
      const stream = new ResponseStream<Subject>();
      decisions.push({ args: [environment, name, ordinal, decision, decidedBy], stream });
      return stream.asObservable();
    },
  };
}

describe('PendingApprovalsStore', () => {
  let api: ReturnType<typeof fakeApi>;
  let store: InstanceType<typeof PendingApprovalsStore>;

  const orders = subject(
    'orders.created',
    [version(1), version(2, 'AWAITING_APPROVAL', '2026-08-14T09:00:00Z')],
    1,
  );
  const payments = subject(
    'payments.taken',
    [version(1), version(2, 'AWAITING_APPROVAL', '2026-08-12T09:00:00Z'), version(3, 'REJECTED')],
    1,
  );
  const quiet = subject('quiet.subject', [version(1)], 1);

  beforeEach(() => {
    api = fakeApi();

    TestBed.configureTestingModule({
      providers: [
        provideConcordatConfig({ defaultEnvironment: 'dev' }),
        { provide: SubjectsApi, useValue: api },
        PendingApprovalsStore,
      ],
    });

    store = TestBed.inject(PendingApprovalsStore);
  });

  function loaded(subjects: readonly Subject[]): void {
    store.load();
    api.lists[0]!.next(subjects);
  }

  describe('the queue', () => {
    it('holds only versions waiting on a decision', () => {
      loaded([orders, payments, quiet]);

      expect(store.pending().map((p) => `${p.subject}@${p.version.ordinal}`)).toEqual([
        'payments.taken@2',
        'orders.created@2',
      ]);
    });

    it('puts the longest wait first', () => {
      // payments.taken v2 was registered two days before orders.created v2.
      loaded([orders, payments]);

      expect(store.pending()[0]?.subject).toBe('payments.taken');
    });

    it('names the version each would replace, through the gated pointer', () => {
      loaded([orders]);

      expect(store.pending()[0]?.baseline?.ordinal).toBe(1);
    });

    it('has no baseline when nothing has been approved yet', () => {
      loaded([subject('fresh', [version(1, 'AWAITING_APPROVAL')], null)]);

      expect(store.pending()[0]?.baseline).toBeNull();
    });

    it('is empty only after a load that found nothing, never while loading', () => {
      expect(store.isEmpty()).toBe(false);

      store.load();
      expect(store.isEmpty()).toBe(false);

      api.lists[0]!.next([quiet]);
      expect(store.isEmpty()).toBe(true);
    });

    it('is not empty when a held version exists', () => {
      loaded([orders]);

      expect(store.isEmpty()).toBe(false);
    });

    it('reports a failed load, and an empty queue is not a verdict then', () => {
      store.load();
      api.lists[0]!.error(new ConcordatError({ status: 500, code: 'x', detail: 'down' }));

      expect(store.error()?.detail).toBe('down');
      expect(store.loaded()).toBe(true);
    });
  });

  describe('deciding', () => {
    function decide(action: 'approve' | 'reject' = 'approve'): void {
      store.decide({ subject: 'orders.created', ordinal: 2, action });
    }

    it('asks the registry in the active environment, as the signed-in actor', () => {
      TestBed.inject(SessionStore).signIn({ credential: 'c', actor: 'alice', scopes: [] });
      loaded([orders]);

      decide('reject');

      expect(api.decisions[0]?.args).toEqual(['dev', 'orders.created', 2, 'reject', 'alice']);
    });

    it('marks the version as being decided until the registry answers', () => {
      loaded([orders]);
      decide();

      expect(store.decisions()[keyOf('orders.created', 2)]).toEqual({
        state: 'deciding',
        action: 'approve',
      });
    });

    it('ignores a second decision for a version already in flight', () => {
      // An approve and a reject racing for one version would both be sent, and whichever the
      // server saw second would be refused for a reason the reader never caused.
      loaded([orders]);
      decide('approve');
      decide('reject');

      expect(api.decisions).toHaveLength(1);
    });

    it('lets two different versions be decided at once', () => {
      loaded([orders, payments]);
      decide();
      store.decide({ subject: 'payments.taken', ordinal: 2, action: 'reject' });

      expect(api.decisions).toHaveLength(2);
    });

    it('replaces the subject with the one the registry returned, so the row leaves the queue', () => {
      loaded([orders, payments]);
      decide();

      api.decisions[0]!.stream.next(subject('orders.created', [version(1), version(2)], 2));

      expect(store.pending().map((p) => p.subject)).toEqual(['payments.taken']);
      expect(store.subjects().find((s) => s.name === 'orders.created')?.latest).toBe(2);
    });

    it('moves every other pending version on that subject onto the new baseline', () => {
      // Approving v2 moves `latest`, which changes what v3 would replace. Patching one row
      // would leave v3 comparing against a pointer that has moved.
      const twoHeld = subject(
        'orders.created',
        [
          version(1),
          version(2, 'AWAITING_APPROVAL', '2026-08-13T09:00:00Z'),
          version(3, 'AWAITING_APPROVAL', '2026-08-14T09:00:00Z'),
        ],
        1,
      );
      loaded([twoHeld]);
      decide();

      api.decisions[0]!.stream.next(
        subject('orders.created', [version(1), version(2), twoHeld.versions[2]!], 2),
      );

      expect(store.pending()).toHaveLength(1);
      expect(store.pending()[0]?.baseline?.ordinal).toBe(2);
    });

    it('records what was decided, so the screen can say what happened to a row it removed', () => {
      loaded([orders]);
      decide('reject');

      api.decisions[0]!.stream.next(
        subject('orders.created', [version(1), version(2, 'REJECTED')], 1),
      );

      expect(store.lastDecided()).toEqual({
        subject: 'orders.created',
        ordinal: 2,
        action: 'reject',
      });
      expect(store.decisions()).toEqual({});
    });

    it('keeps a refusal on the row, not on the whole queue', () => {
      loaded([orders, payments]);
      decide();

      api.decisions[0]!.stream.error(
        new ConcordatError({ status: 403, code: 'insufficient_scope', detail: 'not an admin' }),
      );

      const state = store.decisions()[keyOf('orders.created', 2)];
      expect(state?.state).toBe('failed');
      expect(state?.state === 'failed' && state.error.code).toBe('insufficient_scope');
      expect(store.error()).toBeNull();
      expect(store.pending()).toHaveLength(2);
    });

    it('allows a retry after a failure', () => {
      loaded([orders]);
      decide();
      api.decisions[0]!.stream.error(new Error('offline'));

      decide();

      expect(api.decisions).toHaveLength(2);
      expect(store.decisions()[keyOf('orders.created', 2)]?.state).toBe('deciding');
    });
  });
});
