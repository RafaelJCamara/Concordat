import { TestBed } from '@angular/core/testing';
import { Subject as ResponseStream } from 'rxjs';
import { beforeEach, describe, expect, it } from 'vitest';
import { provideConcordatConfig } from '../../../core/config/app-config';
import { ConcordatError } from '../../../core/http/problem-details';
import type { AuditEntry, AuditQuery } from '../../../domain/governance/audit';
import { AuditApi } from '../data-access/audit-api';
import { AuditStore, DEFAULT_AUDIT_QUERY } from './audit-store';

// What this store adds over a fetch is what it does *not* claim: that a full page is a complete
// log. `truncated` is the one fact a reader cannot see for themselves, and an empty result must
// never be shown while a load is still in flight.

function entry(n: number): AuditEntry {
  return {
    at: new Date(Date.UTC(2026, 7, 13, 9, n)),
    action: 'VERSION_APPROVED',
    actor: 'alice',
    target: 'orders.created',
    detail: `version ${n}`,
  };
}

function fakeApi() {
  const asked: { environment: string; query: AuditQuery }[] = [];
  const streams: ResponseStream<readonly AuditEntry[]>[] = [];

  return {
    asked,
    streams,
    getAudit(environment: string, query: AuditQuery) {
      asked.push({ environment, query });
      const stream = new ResponseStream<readonly AuditEntry[]>();
      streams.push(stream);
      return stream.asObservable();
    },
  };
}

describe('AuditStore', () => {
  let api: ReturnType<typeof fakeApi>;
  let store: InstanceType<typeof AuditStore>;

  beforeEach(() => {
    api = fakeApi();

    TestBed.configureTestingModule({
      providers: [
        provideConcordatConfig({ defaultEnvironment: 'dev' }),
        { provide: AuditApi, useValue: api },
        AuditStore,
      ],
    });

    store = TestBed.inject(AuditStore);
  });

  it('starts idle, and not empty', () => {
    expect(store.loading()).toBe(false);
    expect(store.isEmpty()).toBe(false);
    expect(store.truncated()).toBe(false);
  });

  it('opens on this environment, everything, the newest hundred', () => {
    expect(DEFAULT_AUDIT_QUERY).toEqual({
      scope: 'environment',
      action: null,
      target: '',
      since: null,
      until: null,
      limit: 100,
    });
  });

  it('asks in the active environment with the query it was given', () => {
    const query = { ...DEFAULT_AUDIT_QUERY, action: 'VERSION_APPROVED' as const };
    store.load(query);

    expect(api.asked).toEqual([{ environment: 'dev', query }]);
  });

  it('is loading, and not empty, until the registry answers', () => {
    store.load(DEFAULT_AUDIT_QUERY);

    expect(store.loading()).toBe(true);
    expect(store.isEmpty()).toBe(false);
  });

  it('is empty only after a load that found nothing', () => {
    store.load(DEFAULT_AUDIT_QUERY);
    api.streams[0]!.next([]);

    expect(store.isEmpty()).toBe(true);
    expect(store.loading()).toBe(false);
  });

  describe('truncation', () => {
    it('says the answer was cut off when it fills the limit', () => {
      store.load({ ...DEFAULT_AUDIT_QUERY, limit: 2 });
      api.streams[0]!.next([entry(1), entry(2)]);

      expect(store.truncated()).toBe(true);
    });

    it('does not when it falls short, which is how a complete answer looks', () => {
      store.load({ ...DEFAULT_AUDIT_QUERY, limit: 3 });
      api.streams[0]!.next([entry(1), entry(2)]);

      expect(store.truncated()).toBe(false);
    });

    it('judges against the limit that was asked, not the current form', () => {
      // `asked` is recorded with the entries, so changing the limit in the form without
      // applying it cannot make the previous answer look cut off, or whole.
      store.load({ ...DEFAULT_AUDIT_QUERY, limit: 2 });
      api.streams[0]!.next([entry(1), entry(2)]);

      expect(store.asked()?.limit).toBe(2);
    });
  });

  describe('failure', () => {
    it('keeps the refusal and shows no entries, rather than an empty log', () => {
      store.load(DEFAULT_AUDIT_QUERY);
      api.streams[0]!.error(
        new ConcordatError({ status: 400, code: 'audit_filter_invalid', detail: 'bad range' }),
      );

      expect(store.error()?.code).toBe('audit_filter_invalid');
      expect(store.entries()).toEqual([]);
    });

    it('clears the error on the next load, and keeps working', () => {
      store.load(DEFAULT_AUDIT_QUERY);
      api.streams[0]!.error(new Error('offline'));

      store.load(DEFAULT_AUDIT_QUERY);
      api.streams[1]!.next([entry(1)]);

      expect(store.error()).toBeNull();
      expect(store.entries()).toHaveLength(1);
    });
  });

  it('lets the newest request win over a slower earlier one', () => {
    store.load({ ...DEFAULT_AUDIT_QUERY, target: 'first' });
    store.load({ ...DEFAULT_AUDIT_QUERY, target: 'second' });

    api.streams[1]!.next([entry(2)]);
    api.streams[0]!.next([entry(1)]);

    expect(store.entries()[0]?.detail).toBe('version 2');
    expect(store.asked()?.target).toBe('second');
  });
});
