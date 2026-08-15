import { TestBed } from '@angular/core/testing';
import { Subject as ResponseStream } from 'rxjs';
import { beforeEach, describe, expect, it } from 'vitest';
import { ActiveEnvironmentStore } from '../../../core/config/active-environment-store';
import { provideConcordatConfig } from '../../../core/config/app-config';
import { ConcordatError } from '../../../core/http/problem-details';
import type { SchemaVersion, Subject } from '../../../domain/registry/subject';
import { SubjectsApi } from '../data-access/subjects-api';
import { SubjectDetailStore } from './subject-detail-store';

// The store's own work is the three computed views. Loading and failing follow
// `SubjectListStore`, which is the reference shape, so this file pins the ordering decision
// the list does not have to make: versions are stored in the server's order and read in the
// reverse of it.

function version(ordinal: number, status: SchemaVersion['status'] = 'ACTIVE'): SchemaVersion {
  return {
    ordinal,
    schemaId: `schema-${ordinal}`,
    semanticVersion: null,
    status,
    changelog: null,
    registeredAt: new Date('2026-08-13T09:00:00Z'),
    registeredBy: 'ci',
    deprecated: false,
  };
}

function subject(versions: readonly SchemaVersion[], latest: number | null): Subject {
  return {
    name: 'orders.created',
    format: 'json',
    owner: 'orders-team',
    lifecycle: 'ACTIVE',
    contentModel: 'OPEN',
    compatibilityPolicy: { mode: 'BACKWARD', surface: 'WIRE_JSON' },
    latest,
    versions,
  };
}

function fakeApi() {
  const streams: ResponseStream<Subject>[] = [];
  const asked: { environment: string; name: string }[] = [];

  return {
    asked,
    streams,
    latest: () => streams[streams.length - 1]!,
    getSubject(environment: string, name: string) {
      asked.push({ environment, name });
      const stream = new ResponseStream<Subject>();
      streams.push(stream);
      return stream.asObservable();
    },
  };
}

describe('SubjectDetailStore', () => {
  let api: ReturnType<typeof fakeApi>;
  let store: InstanceType<typeof SubjectDetailStore>;
  let environments: InstanceType<typeof ActiveEnvironmentStore>;

  beforeEach(() => {
    api = fakeApi();
    TestBed.configureTestingModule({
      providers: [
        provideConcordatConfig({ defaultEnvironment: 'dev' }),
        { provide: SubjectsApi, useValue: api },
        SubjectDetailStore,
      ],
    });

    store = TestBed.inject(SubjectDetailStore);
    environments = TestBed.inject(ActiveEnvironmentStore);
  });

  it('starts idle rather than loading', () => {
    expect(store.loading()).toBe(false);
    expect(store.subject()).toBeNull();
    expect(store.error()).toBeNull();
  });

  it('asks for the named subject in the active environment', () => {
    environments.select('prod');

    store.load('orders.created');

    expect(api.asked).toEqual([{ environment: 'prod', name: 'orders.created' }]);
  });

  describe('versions', () => {
    it('reads newest first, whatever order the server sent', () => {
      // Registration order is the right order to store and the wrong one to read: someone
      // opening a subject wants the most recent change at the top.
      store.load('orders.created');
      api.latest().next(subject([version(1), version(2), version(3)], 3));

      expect(store.versions().map((v) => v.ordinal)).toEqual([3, 2, 1]);
    });

    it('does not mutate the subject it was given', () => {
      // `sort` is in-place, so sorting the store's own array would reorder the state a
      // caller is also reading — and the copy is the only thing that stops it.
      const value = subject([version(1), version(2)], 2);
      store.load('orders.created');
      api.latest().next(value);

      store.versions();

      expect(value.versions.map((v) => v.ordinal)).toEqual([1, 2]);
    });

    it('is empty before anything has loaded', () => {
      expect(store.versions()).toEqual([]);
    });
  });

  describe('latest', () => {
    it('follows the gated pointer rather than the highest ordinal', () => {
      // v3 is awaiting approval, so the registry's pointer still says v2. Deriving this as
      // `max(ordinal)` would present an unapproved schema as the current contract.
      store.load('orders.created');
      api.latest().next(subject([version(1), version(2), version(3, 'AWAITING_APPROVAL')], 2));

      expect(store.latest()?.ordinal).toBe(2);
    });

    it('is null when no version is active', () => {
      store.load('orders.created');
      api.latest().next(subject([version(1, 'AWAITING_APPROVAL')], null));

      expect(store.latest()).toBeNull();
    });
  });

  describe('pending', () => {
    it('counts only versions held at the gate, newest first', () => {
      store.load('orders.created');
      api
        .latest()
        .next(
          subject(
            [version(1), version(2, 'AWAITING_APPROVAL'), version(3, 'AWAITING_APPROVAL')],
            1,
          ),
        );

      expect(store.pending().map((v) => v.ordinal)).toEqual([3, 2]);
    });

    it('excludes a dismissed proposal', () => {
      // `DISMISSED` is a proposal withdrawn before review. Counting it as pending would put
      // a permanent badge on a subject nobody has to act on.
      store.load('orders.created');
      api.latest().next(subject([version(1), version(2, 'DISMISSED')], 1));

      expect(store.pending()).toEqual([]);
    });
  });

  describe('when the request fails', () => {
    it('captures the failure as state instead of throwing', () => {
      store.load('orders.created');
      api.latest().error(new Error('offline'));

      expect(store.error()).toBeInstanceOf(ConcordatError);
      expect(store.loading()).toBe(false);
      expect(store.subject()).toBeNull();
    });

    it('can still load afterwards', () => {
      store.load('orders.created');
      api.latest().error(new Error('offline'));

      store.load('orders.created');
      api.latest().next(subject([version(1)], 1));

      expect(store.subject()?.name).toBe('orders.created');
      expect(store.error()).toBeNull();
    });
  });

  it('discards a slow response that a newer request has superseded', () => {
    // Navigating between two subjects quickly must not leave the first one's versions on
    // screen under the second one's heading.
    store.load('orders.created');
    const slow = api.streams[0]!;

    store.load('orders.shipped');
    const fast = api.streams[1]!;

    fast.next({ ...subject([version(9)], 9), name: 'orders.shipped' });
    slow.next(subject([version(1)], 1));

    expect(store.subject()?.name).toBe('orders.shipped');
  });
});
