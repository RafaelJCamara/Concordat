import { TestBed } from '@angular/core/testing';
import { Subject as ResponseStream } from 'rxjs';
import { beforeEach, describe, expect, it } from 'vitest';
import { provideConcordatConfig } from '../../../core/config/app-config';
import { ConcordatError } from '../../../core/http/problem-details';
import type { PurgedSubject, Subject } from '../../../domain/registry/subject';
import { SubjectsApi } from '../data-access/subjects-api';
import { SubjectDetailStore } from './subject-detail-store';
import { SubjectSettingsStore } from './subject-settings-store';

// Retiring and deleting a subject for good (ADR-046). Two things carry the weight: what the screen
// reports is what the registry *answered*, and a deletion is not followed by a read, because there is
// nothing left to read and a 404 would be reported as a failure of a success.

function subject(overrides: Partial<Subject> = {}): Subject {
  return {
    name: 'orders.created',
    format: 'json',
    owner: 'orders-team',
    reviewers: [],
    lifecycle: 'ACTIVE',
    contentModel: 'OPEN',
    compatibilityPolicy: { mode: null, surface: null },
    latest: null,
    versions: [],
    ...overrides,
  };
}

function fakeApi() {
  const retires: { args: unknown[]; stream: ResponseStream<Subject> }[] = [];
  const purges: { args: unknown[]; stream: ResponseStream<PurgedSubject> }[] = [];
  const reads: { args: unknown[]; stream: ResponseStream<Subject> }[] = [];

  return {
    retires,
    purges,
    reads,
    retire(...args: unknown[]) {
      const stream = new ResponseStream<Subject>();
      retires.push({ args, stream });
      return stream.asObservable();
    },
    purge(...args: unknown[]) {
      const stream = new ResponseStream<PurgedSubject>();
      purges.push({ args, stream });
      return stream.asObservable();
    },
    getSubject(...args: unknown[]) {
      const stream = new ResponseStream<Subject>();
      reads.push({ args, stream });
      return stream.asObservable();
    },
  };
}

function answer<T>(calls: { stream: ResponseStream<T> }[], value: T): void {
  const { stream } = calls[calls.length - 1]!;
  stream.next(value);
  stream.complete();
}

const refusal = (code: string, status: number) =>
  new ConcordatError({ status, code, detail: `The registry said ${code}.` });

describe('SubjectSettingsStore, ending a subject', () => {
  let api: ReturnType<typeof fakeApi>;
  let store: InstanceType<typeof SubjectSettingsStore>;
  let detail: InstanceType<typeof SubjectDetailStore>;

  beforeEach(() => {
    api = fakeApi();
    TestBed.configureTestingModule({
      providers: [
        provideConcordatConfig({ defaultEnvironment: 'dev' }),
        { provide: SubjectsApi, useValue: api },
        SubjectDetailStore,
        SubjectSettingsStore,
      ],
    });

    detail = TestBed.inject(SubjectDetailStore);
    store = TestBed.inject(SubjectSettingsStore);

    detail.load('orders.created');
    answer(api.reads, subject());
    api.reads.length = 0;
  });

  it('starts idle', () => {
    expect(store.lifecycleSaving()).toBe(false);
    expect(store.lifecycleError()).toBeNull();
  });

  describe('retiring', () => {
    it('asks the registry for the active environment, and is saving until it answers', () => {
      store.retire({ subject: 'orders.created' });

      expect(api.retires).toHaveLength(1);
      expect(api.retires[0]!.args).toEqual(['dev', 'orders.created']);
      expect(store.lifecycleSaving()).toBe(true);
    });

    it('reports the subject the registry named, and reads it again so the page shows its account', () => {
      store.retire({ subject: 'orders.created' });
      answer(api.retires, subject({ lifecycle: 'RETIRED' }));

      expect(store.lifecycleSaving()).toBe(false);
      expect(store.outcome()).toEqual({ kind: 'retired', subject: 'orders.created' });
      expect(api.reads).toHaveLength(1);
    });

    it('keeps a refusal as the registry worded it, and reports no outcome', () => {
      store.retire({ subject: 'orders.created' });
      api.retires[0]!.stream.error(refusal('lifecycle_transition_invalid', 409));

      expect(store.lifecycleSaving()).toBe(false);
      expect(store.lifecycleError()?.detail).toBe(
        'The registry said lifecycle_transition_invalid.',
      );
      expect(store.outcome()).toBeNull();
      expect(api.reads).toHaveLength(0);
    });

    it('does not start a second retirement while one is on its way', () => {
      // Cancelling the first would abandon only the browser's half of a write the registry still
      // processes, so a double click must not race it.
      store.retire({ subject: 'orders.created' });
      store.retire({ subject: 'orders.created' });

      expect(api.retires).toHaveLength(1);
    });

    it('can be tried again after a refusal', () => {
      store.retire({ subject: 'orders.created' });
      api.retires[0]!.stream.error(refusal('insufficient_scope', 403));

      store.retire({ subject: 'orders.created' });

      expect(api.retires).toHaveLength(2);
      expect(store.lifecycleError()).toBeNull();
    });
  });

  describe('deleting for good', () => {
    it('asks the registry, and reports how many versions it said went', () => {
      store.purge({ subject: 'orders.created' });

      expect(api.purges[0]!.args).toEqual(['dev', 'orders.created']);
      expect(store.lifecycleSaving()).toBe(true);

      answer(api.purges, { subject: 'orders.created', versionsRemoved: 3 });

      expect(store.lifecycleSaving()).toBe(false);
      expect(store.outcome()).toEqual({
        kind: 'purged',
        subject: 'orders.created',
        versionsRemoved: 3,
      });
    });

    it('does not read the subject afterwards: it is gone, and a 404 would report a success as a failure', () => {
      store.purge({ subject: 'orders.created' });
      answer(api.purges, { subject: 'orders.created', versionsRemoved: 1 });

      expect(api.reads).toHaveLength(0);
    });

    it('keeps the registry’s refusal, saying what to remove first, and reports no outcome', () => {
      store.purge({ subject: 'orders.created' });
      api.purges[0]!.stream.error(refusal('subject_in_use', 409));

      expect(store.lifecycleSaving()).toBe(false);
      expect(store.lifecycleError()?.detail).toBe('The registry said subject_in_use.');
      expect(store.outcome()).toBeNull();
    });

    it('does not start a second deletion while one is on its way', () => {
      store.purge({ subject: 'orders.created' });
      store.purge({ subject: 'orders.created' });

      expect(api.purges).toHaveLength(1);
    });
  });

  it('clears a refusal when an editor opens, with the others', () => {
    store.purge({ subject: 'orders.created' });
    api.purges[0]!.stream.error(refusal('subject_referenced', 409));

    store.reset();

    expect(store.lifecycleError()).toBeNull();
  });

  it('clears the last answer when the next write starts', () => {
    store.retire({ subject: 'orders.created' });
    answer(api.retires, subject({ lifecycle: 'RETIRED' }));
    expect(store.outcome()).not.toBeNull();

    store.purge({ subject: 'orders.created' });

    expect(store.outcome()).toBeNull();
  });
});
