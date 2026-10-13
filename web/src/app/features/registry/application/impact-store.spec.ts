import { TestBed } from '@angular/core/testing';
import { Subject as ResponseStream } from 'rxjs';
import { beforeEach, describe, expect, it } from 'vitest';
import { provideConcordatConfig } from '../../../core/config/app-config';
import { ConcordatError } from '../../../core/http/problem-details';
import type { ImpactReport } from '../../../domain/registry/impact';
import { ImpactApi } from '../data-access/impact-api';
import { ImpactStore } from './impact-store';

// `latest` is the one input with three possible outcomes: no parameter, a parameter, or a
// request that is never sent. Conflating the first and last would send a malformed address
// to the server as though it meant the latest version.

const report: ImpactReport = {
  subject: 'orders.created',
  candidateOrdinal: 2,
  candidateSchemaId: 'abc',
  breakingCount: 0,
  consumers: [],
  contracts: [],
};

function fakeApi() {
  const asked: { environment: string; subject: string; version: number | null }[] = [];
  const streams: ResponseStream<ImpactReport>[] = [];

  return {
    asked,
    streams,
    getImpact(environment: string, subject: string, version: number | null) {
      asked.push({ environment, subject, version });
      const stream = new ResponseStream<ImpactReport>();
      streams.push(stream);
      return stream.asObservable();
    },
  };
}

describe('ImpactStore', () => {
  let api: ReturnType<typeof fakeApi>;
  let store: InstanceType<typeof ImpactStore>;

  beforeEach(() => {
    api = fakeApi();

    TestBed.configureTestingModule({
      providers: [
        provideConcordatConfig({ defaultEnvironment: 'dev' }),
        { provide: ImpactApi, useValue: api },
        ImpactStore,
      ],
    });

    store = TestBed.inject(ImpactStore);
  });

  it('starts idle rather than loading', () => {
    expect(store.loading()).toBe(false);
    expect(store.report()).toBeNull();
    expect(store.error()).toBeNull();
  });

  it('asks for the numbered version in the active environment', () => {
    store.load({ subject: 'orders.created', ordinal: '2' });

    expect(api.asked).toEqual([{ environment: 'dev', subject: 'orders.created', version: 2 }]);
  });

  it.each(['latest', 'LATEST', 'Latest'])('sends no version for %s', (word) => {
    store.load({ subject: 'orders.created', ordinal: word });

    expect(api.asked[0]?.version).toBeNull();
  });

  it.each(['abc', '', '0', '-1', '1.5', '1e1'])(
    'refuses %j without sending anything, rather than reading it as latest',
    (bad) => {
      store.load({ subject: 'orders.created', ordinal: bad });

      expect(store.error()).toBeInstanceOf(ConcordatError);
      expect(store.error()?.code).toBe('version_not_found');
      expect(store.error()?.status).toBe(404);
      expect(store.loading()).toBe(false);
      expect(api.asked).toEqual([]);
    },
  );

  it('publishes the report and stops loading', () => {
    store.load({ subject: 'orders.created', ordinal: '2' });
    expect(store.loading()).toBe(true);

    api.streams[0]!.next(report);

    expect(store.report()).toEqual(report);
    expect(store.loading()).toBe(false);
  });

  it('surfaces a refusal and clears the earlier report', () => {
    store.load({ subject: 'orders.created', ordinal: '2' });
    api.streams[0]!.next(report);

    store.load({ subject: 'orders.created', ordinal: '9' });
    api.streams[1]!.error(
      new ConcordatError({ status: 404, code: 'version_not_found', detail: 'no version 9' }),
    );

    // A stale report under a fresh error would show one version's consumers under another's
    // heading.
    expect(store.report()).toBeNull();
    expect(store.error()?.code).toBe('version_not_found');
  });

  it('keeps working after a failure', () => {
    store.load({ subject: 'orders.created', ordinal: '2' });
    api.streams[0]!.error(new Error('offline'));

    store.load({ subject: 'orders.created', ordinal: '2' });
    api.streams[1]!.next(report);

    expect(store.error()).toBeNull();
    expect(store.report()).toEqual(report);
  });

  it('lets the newest request win over a slower earlier one', () => {
    store.load({ subject: 'orders.created', ordinal: '1' });
    store.load({ subject: 'orders.created', ordinal: '2' });

    api.streams[1]!.next({ ...report, candidateOrdinal: 2 });
    api.streams[0]!.next({ ...report, candidateOrdinal: 1 });

    expect(store.report()?.candidateOrdinal).toBe(2);
  });
});
