import { TestBed } from '@angular/core/testing';
import { Subject as ResponseStream } from 'rxjs';
import { beforeEach, describe, expect, it } from 'vitest';
import { provideConcordatConfig } from '../../../core/config/app-config';
import { ConcordatError } from '../../../core/http/problem-details';
import type { VersionDiff } from '../../../domain/registry/diff';
import type { SchemaVersion, Subject } from '../../../domain/registry/subject';
import { SubjectsApi } from '../data-access/subjects-api';
import { DiffStore } from './diff-store';

// What matters here is that the two requests are one screen's worth of state: a verdict must
// never render with its ends still unlabelled, and a bad URL must be refused locally with the
// code the server would have used rather than sent as a request that 404s without a body.

function version(ordinal: number, status: SchemaVersion['status'] = 'ACTIVE'): SchemaVersion {
  return {
    ordinal,
    schemaId: `schema-${ordinal}`,
    semanticVersion: `1.${ordinal}.0`,
    status,
    changelog: null,
    registeredAt: new Date('2026-08-13T09:00:00Z'),
    registeredBy: 'ci',
    deprecated: false,
  };
}

const subject: Subject = {
  name: 'orders.created',
  format: 'json',
  owner: 'orders-team',
  reviewers: [],
  lifecycle: 'ACTIVE',
  contentModel: 'OPEN',
  compatibilityPolicy: { mode: 'BACKWARD', surface: 'WIRE_JSON' },
  latest: 1,
  versions: [version(1), version(2, 'AWAITING_APPROVAL')],
};

const diff: VersionDiff = {
  from: 1,
  to: 2,
  fromSchemaId: 'schema-1',
  toSchemaId: 'schema-2',
  identical: false,
  policy: { mode: 'BACKWARD', surface: 'WIRE_JSON' },
  divergences: [],
};

function fakeSubjects() {
  const subjectStreams: ResponseStream<Subject>[] = [];
  const diffStreams: ResponseStream<VersionDiff>[] = [];
  const diffs: { environment: string; name: string; from: number; to: number }[] = [];

  return {
    diffs,
    subjectStreams,
    diffStreams,
    getSubject() {
      const stream = new ResponseStream<Subject>();
      subjectStreams.push(stream);
      return stream.asObservable();
    },
    diffVersions(environment: string, name: string, from: number, to: number) {
      diffs.push({ environment, name, from, to });
      const stream = new ResponseStream<VersionDiff>();
      diffStreams.push(stream);
      return stream.asObservable();
    },
  };
}

describe('DiffStore', () => {
  let api: ReturnType<typeof fakeSubjects>;
  let store: InstanceType<typeof DiffStore>;

  beforeEach(() => {
    api = fakeSubjects();

    TestBed.configureTestingModule({
      providers: [
        provideConcordatConfig({ defaultEnvironment: 'dev' }),
        { provide: SubjectsApi, useValue: api },
        DiffStore,
      ],
    });

    store = TestBed.inject(DiffStore);
  });

  function answer(): void {
    api.subjectStreams.at(-1)!.next(subject);
    api.diffStreams.at(-1)!.next(diff);
  }

  it('starts idle rather than loading', () => {
    expect(store.loading()).toBe(false);
    expect(store.diff()).toBeNull();
    expect(store.error()).toBeNull();
  });

  it('asks for the pair in the order it was given, in the active environment', () => {
    store.load({ subject: 'orders.created', from: '1', to: '2' });

    expect(api.diffs).toEqual([{ environment: 'dev', name: 'orders.created', from: 1, to: 2 }]);
  });

  it('does not publish the verdict until both requests have answered', () => {
    // A verdict whose ends are unlabelled reads as a fact about nothing in particular. Whether
    // v2 is the live contract or a proposal at the gate is what gives the divergences meaning.
    store.load({ subject: 'orders.created', from: '1', to: '2' });
    api.diffStreams[0]!.next(diff);

    expect(store.diff()).toBeNull();
    expect(store.loading()).toBe(true);

    api.subjectStreams[0]!.next(subject);
    api.diffStreams[0]!.complete();
    api.subjectStreams[0]!.complete();

    expect(store.diff()).toEqual(diff);
    expect(store.loading()).toBe(false);
  });

  it('labels each end from the subject, so a held proposal is visibly one', () => {
    store.load({ subject: 'orders.created', from: '1', to: '2' });
    answer();
    api.diffStreams[0]!.complete();
    api.subjectStreams[0]!.complete();

    expect(store.fromVersion()?.status).toBe('ACTIVE');
    expect(store.toVersion()?.status).toBe('AWAITING_APPROVAL');
  });

  describe('refusing a bad address', () => {
    it.each(['abc', '', '0', '-1', '1.5', '1e1', 'latest'])(
      'refuses %j as a version number without sending anything',
      (bad) => {
        // `latest` is deliberately not accepted: a comparison against "whatever is current"
        // would change meaning under the reader after the next approval.
        store.load({ subject: 'orders.created', from: bad, to: '2' });

        expect(store.error()).toBeInstanceOf(ConcordatError);
        expect(store.error()?.code).toBe('version_not_found');
        expect(store.error()?.status).toBe(404);
        expect(store.loading()).toBe(false);
        expect(api.diffs).toEqual([]);
        expect(api.subjectStreams).toEqual([]);
      },
    );

    it('refuses a bad second ordinal as well as a bad first', () => {
      store.load({ subject: 'orders.created', from: '1', to: 'abc' });

      expect(store.error()?.code).toBe('version_not_found');
      expect(api.diffs).toEqual([]);
    });
  });

  describe('failure', () => {
    it('surfaces the registry refusal and clears any earlier verdict', () => {
      store.load({ subject: 'orders.created', from: '1', to: '2' });
      answer();
      api.diffStreams[0]!.complete();
      api.subjectStreams[0]!.complete();

      store.load({ subject: 'orders.created', from: '1', to: '9' });
      api.diffStreams[1]!.error(
        new ConcordatError({ status: 404, code: 'version_not_found', detail: 'no version 9' }),
      );

      // A stale verdict left on screen under a fresh error would show one comparison under
      // the heading of another.
      expect(store.diff()).toBeNull();
      expect(store.error()?.code).toBe('version_not_found');
      expect(store.loading()).toBe(false);
    });

    it('keeps working after a failure, rather than tearing the subscription down', () => {
      store.load({ subject: 'orders.created', from: '1', to: '2' });
      api.diffStreams[0]!.error(new Error('offline'));

      store.load({ subject: 'orders.created', from: '1', to: '2' });
      answer();
      api.diffStreams[1]!.complete();
      api.subjectStreams[1]!.complete();

      expect(store.error()).toBeNull();
      expect(store.diff()).toEqual(diff);
    });
  });

  it('lets the newest comparison win over a slower earlier one', () => {
    // Swapping direction navigates between two comparisons of the same subject; the slow
    // first answer must not overwrite the second.
    store.load({ subject: 'orders.created', from: '1', to: '2' });
    store.load({ subject: 'orders.created', from: '2', to: '1' });

    // Completed as well as answered: `forkJoin` waits for completion, as real HTTP does.
    api.subjectStreams[1]!.next(subject);
    api.subjectStreams[1]!.complete();
    api.diffStreams[1]!.next({ ...diff, from: 2, to: 1 });
    api.diffStreams[1]!.complete();
    api.subjectStreams[0]!.next(subject);
    api.subjectStreams[0]!.complete();
    api.diffStreams[0]!.next(diff);
    api.diffStreams[0]!.complete();

    expect(store.diff()?.from).toBe(2);
  });
});
