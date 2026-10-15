import { TestBed } from '@angular/core/testing';
import { Subject as ResponseStream } from 'rxjs';
import { beforeEach, describe, expect, it } from 'vitest';
import { SessionStore } from '../../../core/auth/session-store';
import { provideConcordatConfig } from '../../../core/config/app-config';
import { ConcordatError } from '../../../core/http/problem-details';
import type { ApprovalQueue, PendingApproval } from '../../../domain/registry/approval-queue';
import type { Subject } from '../../../domain/registry/subject';
import { ApprovalsApi } from '../data-access/approvals-api';
import { SubjectsApi } from '../data-access/subjects-api';
import { keyOf, PAGE_SIZE, PendingApprovalsStore } from './pending-approvals-store';

// What this store is for is the queue being the registry's: a page at a time, with the length of
// the whole queue beside it, and the screen never claiming to show more than it has read. And that
// a decision takes its row away at once and then reads the queue again, because approving moves
// `latest` and the registry is the one that knows what every other row now replaces.

function held(subject: string, ordinal: number, baseline: number | null = 1): PendingApproval {
  return {
    subject,
    ordinal,
    semanticVersion: null,
    changelog: null,
    registeredAt: new Date('2026-08-13T09:00:00Z'),
    registeredBy: 'ci',
    baseline,
  };
}

function queue(total: number, ...items: PendingApproval[]): ApprovalQueue {
  return { total, items };
}

interface QueueRequest {
  readonly args: [string, { limit?: number; offset?: number }];
  readonly stream: ResponseStream<ApprovalQueue>;
}

function fakeApprovals() {
  const requests: QueueRequest[] = [];

  return {
    requests,
    getQueue(environment: string, page: { limit?: number; offset?: number } = {}) {
      const stream = new ResponseStream<ApprovalQueue>();
      requests.push({ args: [environment, page], stream });
      return stream.asObservable();
    },
  };
}

function fakeSubjects() {
  const decisions: {
    args: [string, string, number, 'approve' | 'reject', string];
    stream: ResponseStream<Subject>;
  }[] = [];

  return {
    decisions,
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

const decidedSubject = {} as Subject;

describe('PendingApprovalsStore', () => {
  let approvals: ReturnType<typeof fakeApprovals>;
  let subjects: ReturnType<typeof fakeSubjects>;
  let store: InstanceType<typeof PendingApprovalsStore>;

  const orders = held('orders.created', 2);
  const payments = held('payments.taken', 2);

  beforeEach(() => {
    approvals = fakeApprovals();
    subjects = fakeSubjects();

    TestBed.configureTestingModule({
      providers: [
        provideConcordatConfig({ defaultEnvironment: 'dev' }),
        { provide: ApprovalsApi, useValue: approvals },
        { provide: SubjectsApi, useValue: subjects },
        PendingApprovalsStore,
      ],
    });

    store = TestBed.inject(PendingApprovalsStore);
  });

  function loaded(page: ApprovalQueue): void {
    store.load();
    approvals.requests[0]!.stream.next(page);
  }

  describe('the queue', () => {
    it('is what the registry answered, in the order it answered', () => {
      loaded(queue(2, payments, orders));

      expect(store.pending().map((p) => keyOf(p.subject, p.ordinal))).toEqual([
        'payments.taken@2',
        'orders.created@2',
      ]);
      expect(store.total()).toBe(2);
    });

    it('asks the registry in the active environment, for the first page', () => {
      store.load();

      expect(approvals.requests[0]?.args).toEqual(['dev', { limit: PAGE_SIZE }]);
    });

    it('carries the baseline the registry named, and null when it named none', () => {
      loaded(queue(2, held('orders.created', 2, 1), held('fresh', 1, null)));

      expect(store.pending().map((p) => p.baseline)).toEqual([1, null]);
    });

    it('is empty only after a load that found nothing, never while loading', () => {
      expect(store.isEmpty()).toBe(false);

      store.load();
      expect(store.isEmpty()).toBe(false);

      approvals.requests[0]!.stream.next(queue(0));
      expect(store.isEmpty()).toBe(true);
    });

    it('is not empty when something is waiting', () => {
      loaded(queue(1, orders));

      expect(store.isEmpty()).toBe(false);
    });

    it('is not empty on the strength of a page that is empty while the queue is not', () => {
      // A total of 3 with nothing on this page is the registry disagreeing with itself, or a
      // page past the end; either way "Nothing is waiting" would be a claim it did not make.
      loaded(queue(3));

      expect(store.isEmpty()).toBe(false);
    });

    it('reports a failed load, and an empty queue is not a verdict then', () => {
      store.load();
      approvals.requests[0]!.stream.error(
        new ConcordatError({ status: 500, code: 'x', detail: 'down' }),
      );

      expect(store.error()?.detail).toBe('down');
      expect(store.loaded()).toBe(true);
      expect(store.isEmpty()).toBe(false);
    });

    it('can be loaded again after a failure, and the failure is cleared', () => {
      store.load();
      approvals.requests[0]!.stream.error(new Error('offline'));

      store.load();
      expect(store.error()).toBeNull();

      approvals.requests[1]!.stream.next(queue(1, orders));
      expect(store.pending()).toHaveLength(1);
    });
  });

  describe('a queue longer than one page', () => {
    it('knows how many it has not read', () => {
      loaded(queue(120, orders));

      expect(store.unread()).toBe(119);
    });

    it('has nothing unread when the page is the whole queue', () => {
      loaded(queue(1, orders));

      expect(store.unread()).toBe(0);
    });

    it('reads the next page after what is on screen, and appends it', () => {
      loaded(queue(3, orders, payments));

      store.loadMore();
      expect(approvals.requests[1]?.args).toEqual(['dev', { limit: PAGE_SIZE, offset: 2 }]);

      approvals.requests[1]!.stream.next(queue(3, held('shipping.sent', 2)));

      expect(store.pending().map((p) => p.subject)).toEqual([
        'orders.created',
        'payments.taken',
        'shipping.sent',
      ]);
      expect(store.unread()).toBe(0);
    });

    it('does not ask when everything has been read', () => {
      loaded(queue(1, orders));

      store.loadMore();

      expect(approvals.requests).toHaveLength(1);
    });

    it('does not ask before the first page has arrived', () => {
      store.load();

      store.loadMore();

      expect(approvals.requests).toHaveLength(1);
    });

    it('ignores a second request while one is in flight', () => {
      loaded(queue(120, orders));

      store.loadMore();
      store.loadMore();

      expect(approvals.requests).toHaveLength(2);
      expect(store.loadingMore()).toBe(true);
    });

    it('does not show the same version twice when the pages overlap', () => {
      // Someone decided a version between the two reads, so the second page starts one early.
      loaded(queue(3, orders, payments));
      store.loadMore();

      approvals.requests[1]!.stream.next(queue(3, payments, held('shipping.sent', 2)));

      expect(store.pending().map((p) => p.subject)).toEqual([
        'orders.created',
        'payments.taken',
        'shipping.sent',
      ]);
    });

    it('keeps what was read when a later page fails, and says so', () => {
      loaded(queue(120, orders));
      store.loadMore();

      approvals.requests[1]!.stream.error(
        new ConcordatError({ status: 500, code: 'x', detail: 'page two is down' }),
      );

      expect(store.pending()).toHaveLength(1);
      expect(store.moreError()?.detail).toBe('page two is down');
      expect(store.error()).toBeNull();
      expect(store.loadingMore()).toBe(false);
    });

    it('can ask again after a page failed', () => {
      loaded(queue(120, orders));
      store.loadMore();
      approvals.requests[1]!.stream.error(new Error('offline'));

      store.loadMore();

      expect(approvals.requests).toHaveLength(3);
      expect(store.moreError()).toBeNull();
    });

    it('drops a page that was answered after the list it was meant for was replaced', () => {
      loaded(queue(120, orders));
      store.loadMore();

      // A reload replaces the list while page two is still on its way.
      store.load();
      approvals.requests[2]!.stream.next(queue(1, payments));
      approvals.requests[1]!.stream.next(queue(120, held('stale.page', 2)));

      expect(store.pending().map((p) => p.subject)).toEqual(['payments.taken']);
      expect(store.loadingMore()).toBe(false);
    });
  });

  describe('deciding', () => {
    function decide(action: 'approve' | 'reject' = 'approve'): void {
      store.decide({ subject: 'orders.created', ordinal: 2, action });
    }

    it('asks the registry in the active environment, as the signed-in actor', () => {
      TestBed.inject(SessionStore).signIn({ credential: 'c', actor: 'alice', scopes: [] });
      loaded(queue(1, orders));

      decide('reject');

      expect(subjects.decisions[0]?.args).toEqual(['dev', 'orders.created', 2, 'reject', 'alice']);
    });

    it('marks the version as being decided until the registry answers', () => {
      loaded(queue(1, orders));
      decide();

      expect(store.decisions()[keyOf('orders.created', 2)]).toEqual({
        state: 'deciding',
        action: 'approve',
      });
    });

    it('ignores a second decision for a version already in flight', () => {
      // An approve and a reject racing for one version would both be sent, and whichever the
      // server saw second would be refused for a reason the reader never caused.
      loaded(queue(1, orders));
      decide('approve');
      decide('reject');

      expect(subjects.decisions).toHaveLength(1);
    });

    it('lets two different versions be decided at once', () => {
      loaded(queue(2, orders, payments));
      decide();
      store.decide({ subject: 'payments.taken', ordinal: 2, action: 'reject' });

      expect(subjects.decisions).toHaveLength(2);
    });

    it('takes the row away at once, and counts the queue one shorter', () => {
      loaded(queue(2, orders, payments));
      decide();

      subjects.decisions[0]!.stream.next(decidedSubject);

      expect(store.pending().map((p) => p.subject)).toEqual(['payments.taken']);
      expect(store.total()).toBe(1);
    });

    it('then reads the queue again, because approving moves what the others replace', () => {
      loaded(queue(2, orders, held('orders.created', 3, 1)));
      decide();
      subjects.decisions[0]!.stream.next(decidedSubject);

      // Approving v2 made it latest, so v3 now replaces v2 and not v1: only the registry knows.
      expect(approvals.requests).toHaveLength(2);
      approvals.requests[1]!.stream.next(queue(1, held('orders.created', 3, 2)));

      expect(store.pending()).toHaveLength(1);
      expect(store.pending()[0]?.baseline).toBe(2);
    });

    it('reads again for as many as were on screen, so a decision does not collapse the list', () => {
      const many = Array.from({ length: 80 }, (_, i) => held(`s${i}`, 2));
      loaded(queue(80, ...many.slice(0, 50)));
      store.loadMore();
      approvals.requests[1]!.stream.next(queue(80, ...many.slice(50)));

      store.decide({ subject: 's0', ordinal: 2, action: 'approve' });
      subjects.decisions[0]!.stream.next(decidedSubject);

      expect(approvals.requests[2]?.args).toEqual(['dev', { limit: 79 }]);
    });

    it('asks for no fewer than a page when less than one is on screen', () => {
      loaded(queue(1, orders));
      decide();
      subjects.decisions[0]!.stream.next(decidedSubject);

      expect(approvals.requests[1]?.args).toEqual(['dev', { limit: PAGE_SIZE }]);
    });

    it('keeps the screen, and says it may be out of date, when the re-read fails', () => {
      loaded(queue(2, orders, payments));
      decide();
      subjects.decisions[0]!.stream.next(decidedSubject);

      approvals.requests[1]!.stream.error(new Error('offline'));

      expect(store.refreshFailed()).toBe(true);
      expect(store.pending().map((p) => p.subject)).toEqual(['payments.taken']);
      expect(store.error()).toBeNull();
    });

    it('clears that warning when a re-read works', () => {
      loaded(queue(2, orders, payments));
      decide();
      subjects.decisions[0]!.stream.next(decidedSubject);
      approvals.requests[1]!.stream.error(new Error('offline'));

      store.refresh();
      approvals.requests[2]!.stream.next(queue(1, payments));

      expect(store.refreshFailed()).toBe(false);
    });

    it('records what was decided, so the screen can say what happened to a row it removed', () => {
      loaded(queue(1, orders));
      decide('reject');

      subjects.decisions[0]!.stream.next(decidedSubject);

      expect(store.lastDecided()).toEqual({
        subject: 'orders.created',
        ordinal: 2,
        action: 'reject',
      });
      expect(store.decisions()).toEqual({});
    });

    it('keeps a refusal on the row, not on the whole queue', () => {
      loaded(queue(2, orders, payments));
      decide();

      subjects.decisions[0]!.stream.error(
        new ConcordatError({ status: 403, code: 'insufficient_scope', detail: 'not an admin' }),
      );

      const state = store.decisions()[keyOf('orders.created', 2)];
      expect(state?.state).toBe('failed');
      expect(state?.state === 'failed' && state.error.code).toBe('insufficient_scope');
      expect(store.error()).toBeNull();
      expect(store.pending()).toHaveLength(2);
      expect(approvals.requests).toHaveLength(1);
    });

    it('allows a retry after a failure', () => {
      loaded(queue(1, orders));
      decide();
      subjects.decisions[0]!.stream.error(new Error('offline'));

      decide();

      expect(subjects.decisions).toHaveLength(2);
      expect(store.decisions()[keyOf('orders.created', 2)]?.state).toBe('deciding');
    });
  });
});
