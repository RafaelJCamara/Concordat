import { TestBed } from '@angular/core/testing';
import { Subject as ResponseStream, throwError } from 'rxjs';
import { beforeEach, describe, expect, it } from 'vitest';
import { ActiveEnvironmentStore } from '../../../core/config/active-environment-store';
import { provideConcordatConfig } from '../../../core/config/app-config';
import { ConcordatError } from '../../../core/http/problem-details';
import type { EnvironmentSummary } from '../../../domain/registry/environment-summary';
import { SummaryApi } from '../data-access/summary-api';
import { DashboardStore } from './dashboard-store';

// The same properties `SubjectListStore` pins, because this store copies its shape: a failure has
// to become state, the store has to still work afterwards, and a slow answer for the environment
// the person left must not land on the one they are looking at.

function summary(subjects: number): EnvironmentSummary {
  return {
    subjects: { total: subjects, active: subjects, deprecated: 0, retired: 0 },
    versions: { total: subjects, active: subjects, awaitingApproval: 0, rejected: 0, dismissed: 0 },
    newestSubjects: [],
    breakingChanges: [],
    enforcement: [
      { mode: 'OFF', contracts: 0, publishBindings: 0, consumeBindings: 0 },
      { mode: 'MONITOR', contracts: 0, publishBindings: 0, consumeBindings: 0 },
      { mode: 'ENFORCE', contracts: 0, publishBindings: 0, consumeBindings: 0 },
    ],
    violations: { distinct: 0, recent: 0, windowHours: 24 },
  };
}

function fakeApi() {
  const streams: ResponseStream<EnvironmentSummary>[] = [];
  const asked: string[] = [];

  return {
    asked,
    streams,
    latest() {
      return streams[streams.length - 1]!;
    },
    getSummary(environment: string) {
      asked.push(environment);
      const stream = new ResponseStream<EnvironmentSummary>();
      streams.push(stream);
      return stream.asObservable();
    },
  };
}

describe('DashboardStore', () => {
  let api: ReturnType<typeof fakeApi>;
  let store: InstanceType<typeof DashboardStore>;
  let environments: InstanceType<typeof ActiveEnvironmentStore>;

  beforeEach(() => {
    api = fakeApi();
    TestBed.configureTestingModule({
      providers: [
        provideConcordatConfig({ defaultEnvironment: 'dev' }),
        { provide: SummaryApi, useValue: api },
        DashboardStore,
      ],
    });

    store = TestBed.inject(DashboardStore);
    environments = TestBed.inject(ActiveEnvironmentStore);
  });

  it('starts idle, with nothing to show', () => {
    expect(store.loading()).toBe(false);
    expect(store.loaded()).toBe(false);
    expect(store.error()).toBeNull();
    expect(store.summary()).toBeNull();
  });

  describe('isEmpty', () => {
    it('is false before anything has been asked for', () => {
      expect(store.isEmpty()).toBe(false);
    });

    it('is false while a load is in flight', () => {
      store.load();

      expect(store.isEmpty()).toBe(false);
    });

    it('is true once the registry answered that there are no subjects', () => {
      store.load();
      api.latest().next(summary(0));

      expect(store.isEmpty()).toBe(true);
    });

    it('is false when there are subjects', () => {
      store.load();
      api.latest().next(summary(2));

      expect(store.isEmpty()).toBe(false);
    });

    it('is false after a failure, which is not an empty environment', () => {
      // "Could not load" and "there is nothing here" are different screens, and offering to create
      // the first subject of an environment that failed to load is the wrong one.
      store.load();
      api.latest().error(new Error('offline'));

      expect(store.isEmpty()).toBe(false);
    });
  });

  describe('load', () => {
    it('asks for the active environment', () => {
      environments.select('prod');

      store.load();

      expect(api.asked).toEqual(['prod']);
    });

    it('publishes the summary and stops loading', () => {
      store.load();
      api.latest().next(summary(3));

      expect(store.summary()?.subjects.total).toBe(3);
      expect(store.loading()).toBe(false);
      expect(store.loaded()).toBe(true);
      expect(store.error()).toBeNull();
    });

    it('clears a previous error when a new attempt starts', () => {
      store.load();
      api.latest().error(new Error('offline'));

      store.load();

      expect(store.error()).toBeNull();
      expect(store.loading()).toBe(true);
    });
  });

  describe('when the request fails', () => {
    it('captures the failure as state instead of throwing', () => {
      store.load();
      api.latest().error(new Error('offline'));

      expect(store.error()).toBeInstanceOf(ConcordatError);
      expect(store.loading()).toBe(false);
      expect(store.loaded()).toBe(true);
    });

    it('keeps a ConcordatError intact rather than re-wrapping it', () => {
      const refusal = new ConcordatError({
        status: 403,
        code: 'insufficient_scope',
        detail: 'This endpoint requires subject:read.',
      });

      store.load();
      api.latest().error(refusal);

      expect(store.error()).toBe(refusal);
    });

    it('drops the numbers it was showing', () => {
      // Stale numbers next to an error banner read as "here is the data, and also a problem".
      store.load();
      api.latest().next(summary(3));

      store.load();
      api.latest().error(new Error('offline'));

      expect(store.summary()).toBeNull();
    });

    it('can still load afterwards', () => {
      // `catchError` sits inside the `switchMap` and swallows. Placed outside, the failure ends the
      // rxMethod's own subscription and every later `load()` does nothing: a retry button that is
      // permanently dead, with no error to show for it.
      store.load();
      api.latest().error(new Error('offline'));

      store.load();
      api.latest().next(summary(1));

      expect(store.summary()?.subjects.total).toBe(1);
      expect(store.error()).toBeNull();
    });

    it('survives a source that fails synchronously', () => {
      TestBed.resetTestingModule();
      TestBed.configureTestingModule({
        providers: [
          provideConcordatConfig(),
          {
            provide: SummaryApi,
            useValue: { getSummary: () => throwError(() => new Error('x')) },
          },
          DashboardStore,
        ],
      });

      const failed = TestBed.inject(DashboardStore);
      failed.load();

      expect(failed.error()).toBeInstanceOf(ConcordatError);
      expect(failed.loading()).toBe(false);
    });
  });

  it('discards a slow response that a newer request has superseded', () => {
    // Why `switchMap` and not `mergeMap`: switching environment twice in quick succession must not
    // let the first environment's numbers land last and sit under the second one's name.
    environments.select('dev');
    store.load();
    const slow = api.streams[0]!;

    environments.select('prod');
    store.load();
    const fast = api.streams[1]!;

    fast.next(summary(7));
    slow.next(summary(1));

    expect(store.summary()?.subjects.total).toBe(7);
  });
});
