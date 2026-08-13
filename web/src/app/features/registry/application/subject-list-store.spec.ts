import { TestBed } from '@angular/core/testing';
import { Subject as ResponseStream, throwError } from 'rxjs';
import { beforeEach, describe, expect, it } from 'vitest';
import { ActiveEnvironmentStore } from '../../../core/config/active-environment-store';
import { provideConcordatConfig } from '../../../core/config/app-config';
import { ConcordatError } from '../../../core/http/problem-details';
import type { Subject } from '../../../domain/registry/subject';
import { SubjectsApi } from '../data-access/subjects-api';
import { SubjectListStore } from './subject-list-store';

// This is the reference store shape for M4.3, so the properties pinned here are the ones
// every feature store copied from it will inherit — including the two that are easy to get
// wrong: a failure has to become state rather than an exception, and the store has to still
// work afterwards.

function subject(name: string): Subject {
  return {
    name,
    format: 'json',
    owner: 'orders-team',
    lifecycle: 'ACTIVE',
    contentModel: 'OPEN',
    compatibilityPolicy: { mode: 'BACKWARD', surface: 'WIRE_JSON' },
    latest: null,
    versions: [],
  };
}

/** A stand-in for `SubjectsApi` whose responses this file decides when to deliver. */
function fakeApi() {
  const streams: ResponseStream<readonly Subject[]>[] = [];
  const asked: string[] = [];

  return {
    asked,
    streams,
    /** The most recent in-flight response, so a test can complete it on its own terms. */
    latest() {
      return streams[streams.length - 1]!;
    },
    listSubjects(environment: string) {
      asked.push(environment);
      const stream = new ResponseStream<readonly Subject[]>();
      streams.push(stream);
      return stream.asObservable();
    },
  };
}

describe('SubjectListStore', () => {
  let api: ReturnType<typeof fakeApi>;
  let store: InstanceType<typeof SubjectListStore>;
  let environments: InstanceType<typeof ActiveEnvironmentStore>;

  beforeEach(() => {
    api = fakeApi();
    TestBed.configureTestingModule({
      providers: [
        provideConcordatConfig({ defaultEnvironment: 'dev' }),
        { provide: SubjectsApi, useValue: api },
        SubjectListStore,
      ],
    });

    store = TestBed.inject(SubjectListStore);
    environments = TestBed.inject(ActiveEnvironmentStore);
  });

  it('starts idle rather than loading', () => {
    // A store that started `loading: true` would make every screen render a skeleton before
    // anything had asked for anything.
    expect(store.loading()).toBe(false);
    expect(store.loaded()).toBe(false);
    expect(store.error()).toBeNull();
    expect(store.subjects()).toEqual([]);
  });

  describe('isEmpty', () => {
    it('is false before anything has been asked for', () => {
      // "No subjects" and "nobody has looked yet" are different screens.
      expect(store.isEmpty()).toBe(false);
    });

    it('is false while a load is in flight', () => {
      // The specific flicker this computed prevents: an empty list plus a pending request
      // renders "no subjects in this environment" for as long as the request takes, and
      // then replaces it with rows.
      store.load();

      expect(store.loading()).toBe(true);
      expect(store.isEmpty()).toBe(false);
    });

    it('is true once a load has finished and returned nothing', () => {
      store.load();
      api.latest().next([]);

      expect(store.isEmpty()).toBe(true);
    });

    it('is false when the load returned rows', () => {
      store.load();
      api.latest().next([subject('orders.created')]);

      expect(store.isEmpty()).toBe(false);
    });
  });

  describe('load', () => {
    it('asks for the active environment', () => {
      environments.select('prod');

      store.load();

      expect(api.asked).toEqual(['prod']);
    });

    it('publishes the subjects and stops loading', () => {
      store.load();
      api.latest().next([subject('orders.created'), subject('orders.shipped')]);

      expect(store.subjects().map((s) => s.name)).toEqual(['orders.created', 'orders.shipped']);
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
      // The template renders the error. Letting it propagate would surface it as an
      // unhandled rejection in the console and leave the screen showing a spinner.
      store.load();
      api.latest().error(new Error('offline'));

      expect(store.error()).toBeInstanceOf(ConcordatError);
      expect(store.loading()).toBe(false);
      expect(store.loaded()).toBe(true);
    });

    it('normalises whatever was thrown into a ConcordatError', () => {
      store.load();
      api.latest().error('something odd');

      expect(store.error()?.code).toBe('registry_refused');
    });

    it('keeps a ConcordatError intact rather than re-wrapping it', () => {
      // The interceptor has already mapped it, and the code is the only thing a screen can
      // branch on to say something more useful than "it failed".
      const refusal = new ConcordatError({
        status: 404,
        code: 'subject_not_found',
        detail: 'No such environment.',
      });

      store.load();
      api.latest().error(refusal);

      expect(store.error()).toBe(refusal);
    });

    it('clears the rows it was showing', () => {
      // Stale rows next to an error banner read as "here is the data, and also a problem",
      // which is the opposite of what happened.
      store.load();
      api.latest().next([subject('orders.created')]);

      store.load();
      api.latest().error(new Error('offline'));

      expect(store.subjects()).toEqual([]);
    });

    it('can still load afterwards', () => {
      // The reason `catchError` sits inside the `switchMap` and swallows. Placed outside,
      // the failure tears down the `rxMethod`'s own subscription and every later `load()`
      // does nothing at all — a screen whose retry button is permanently dead, with no
      // error to show for it.
      store.load();
      api.latest().error(new Error('offline'));

      store.load();
      api.latest().next([subject('orders.created')]);

      expect(store.subjects().map((s) => s.name)).toEqual(['orders.created']);
      expect(store.error()).toBeNull();
    });

    it('survives a source that fails synchronously', () => {
      TestBed.resetTestingModule();
      const failing = {
        listSubjects: () => throwError(() => new Error('config missing')),
      };
      TestBed.configureTestingModule({
        providers: [
          provideConcordatConfig(),
          { provide: SubjectsApi, useValue: failing },
          SubjectListStore,
        ],
      });

      const failed = TestBed.inject(SubjectListStore);
      failed.load();

      expect(failed.error()).toBeInstanceOf(ConcordatError);
      expect(failed.loading()).toBe(false);
    });
  });

  it('discards a slow response that a newer request has superseded', () => {
    // Why `switchMap` and not `mergeMap`. Switching environment twice in quick succession
    // must not let the first environment's subjects land last and sit there labelled as the
    // second environment's.
    environments.select('dev');
    store.load();
    const slow = api.streams[0]!;

    environments.select('prod');
    store.load();
    const fast = api.streams[1]!;

    fast.next([subject('prod.only')]);
    slow.next([subject('dev.only')]);

    expect(store.subjects().map((s) => s.name)).toEqual(['prod.only']);
  });
});
